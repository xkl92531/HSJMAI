using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// task-5：台账行 → IgwLedgerSnapshot 的纯函数单测（无数据库，直接构造实体行喂给 Build）。
/// </summary>
public class IgwLedgerSnapshotBuilderTests
{
    private static PLC_IgwRegistrationEntity Row(
        string rootKind, string rootName, string deviceKind = null, string rootGroup = null,
        string renamesJson = null, bool isDeleted = false) =>
        new()
        {
            ProjectId = 1,
            RootKind = rootKind,
            RootName = rootName,
            DeviceKind = deviceKind,
            RootGroup = rootGroup,
            RenamesJson = renamesJson,
            IsDeleted = isDeleted,
        };

    [Fact]
    public void Empty_input_returns_empty_snapshot()
    {
        var snapshot = IgwLedgerSnapshotBuilder.Build(null);

        Assert.Empty(snapshot.DeviceKindByBlock);
        Assert.Empty(snapshot.RootGroupByBlock);
        Assert.Empty(snapshot.DbRenames);
        Assert.Empty(snapshot.IoRenames);
        Assert.Empty(snapshot.StationByDb);
    }

    [Fact]
    public void DbBlock_row_maps_device_kind_and_root_group()
    {
        var rows = new[]
        {
            Row(IgwLedgerRootKind.DbBlock, "DB490_OP30Manual", deviceKind: "S7Plus", rootGroup: "DB490_OP30Manual"),
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal(IgwDeviceKind.S7Plus, snapshot.DeviceKindByBlock["DB490_OP30Manual"]);
        Assert.Equal("DB490_OP30Manual", snapshot.RootGroupByBlock["DB490_OP30Manual"]);
    }

    [Fact]
    public void DbBlock_device_kind_lookup_is_case_insensitive_on_block_name()
    {
        var rows = new[] { Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7") };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal(IgwDeviceKind.S7, snapshot.DeviceKindByBlock["db10"]);
    }

    [Fact]
    public void DbBlock_renames_json_expands_into_db_renames_keyed_by_block_and_path()
    {
        var json = "{\"Type.Robot[2].X\":\"Type_Robot_2_X_2\",\"Status\":\"Status_2\"}";
        var rows = new[] { Row(IgwLedgerRootKind.DbBlock, "DB490", renamesJson: json) };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal("Type_Robot_2_X_2", snapshot.DbRenames[("DB490", "Type.Robot[2].X")]);
        Assert.Equal("Status_2", snapshot.DbRenames[("DB490", "Status")]);
    }

    [Fact]
    public void Io_renames_json_splits_key_on_separator_into_table_and_tag()
    {
        // 真实写入路径会用 JsonSerializer.Serialize 生成 RenamesJson，U+0001 等控制字符会被正确转义；
        // 这里同样走 Serialize 构造测试输入，而不是手拼未转义的原始控制字符（那不是合法 JSON）。
        var sep = IgwLedgerSnapshotBuilder.IoRenameKeySeparator;
        var map = new Dictionary<string, string> { [$"默认变量表{sep}Clock_2_5Hz"] = "Clock_2_5Hz_2" };
        var json = JsonSerializer.Serialize(map);
        var rows = new[] { Row(IgwLedgerRootKind.Io, "IO", renamesJson: json) };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal("Clock_2_5Hz_2", snapshot.IoRenames[("默认变量表", "Clock_2_5Hz")]);
    }

    [Fact]
    public void Io_renames_json_entry_without_separator_is_ignored()
    {
        var json = "{\"no_separator_here\":\"x\"}";
        var rows = new[] { Row(IgwLedgerRootKind.Io, "IO", renamesJson: json) };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Empty(snapshot.IoRenames);
    }

    [Theory]
    [InlineData("OP30|DB161_HomeGraph", "DB161_HomeGraph", "OP30")]
    [InlineData("_未分组|DB162_Xxx", "DB162_Xxx", "_未分组")]
    public void Graph_row_splits_root_name_into_station_and_db(string rootName, string expectedDb, string expectedStation)
    {
        var rows = new[] { Row(IgwLedgerRootKind.Graph, rootName, deviceKind: "Graph") };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal(expectedStation, snapshot.StationByDb[expectedDb]);
    }

    [Theory]
    [InlineData("OP30DB161_HomeGraph", "DB161_HomeGraph", "OP30")]
    [InlineData("OP|30DB161_HomeGraph", "DB161_HomeGraph", "OP|30")]
    [InlineData("OP|30|DB161_HomeGraph", "DB161_HomeGraph", "OP|30")] // 旧 '|' 形式：按最后一个分隔符拆
    public void Graph_row_supports_key_separator_and_splits_on_last_separator(string rootName, string expectedDb, string expectedStation)
    {
        var rows = new[] { Row(IgwLedgerRootKind.Graph, rootName, deviceKind: "Graph") };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal(expectedStation, snapshot.StationByDb[expectedDb]);
        Assert.Equal(rootName, snapshot.GraphRootNameByDb[expectedDb]);
    }

    [Fact]
    public void Graph_row_without_separator_is_ignored()
    {
        var rows = new[] { Row(IgwLedgerRootKind.Graph, "DB161_HomeGraph_no_pipe") };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Empty(snapshot.StationByDb);
    }

    [Fact]
    public void Soft_deleted_rows_are_ignored_even_if_passed_in()
    {
        var rows = new[]
        {
            Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7", isDeleted: true),
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Empty(snapshot.DeviceKindByBlock);
    }

    [Fact]
    public void Malformed_renames_json_is_skipped_without_throwing()
    {
        var rows = new[]
        {
            Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7", renamesJson: "{not valid json"),
            Row(IgwLedgerRootKind.DbBlock, "DB11", deviceKind: "S7Plus", renamesJson: "{\"A\":\"B\"}"),
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.DoesNotContain(snapshot.DbRenames.Keys, k => k.Block == "DB10");
        Assert.Equal(IgwDeviceKind.S7, snapshot.DeviceKindByBlock["DB10"]);
        Assert.Equal("B", snapshot.DbRenames[("DB11", "A")]);
    }

    [Fact]
    public void Unparsable_device_kind_leaves_block_out_of_device_kind_map_but_keeps_root_group()
    {
        var rows = new[] { Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "NotARealKind", rootGroup: "DB10") };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.False(snapshot.DeviceKindByBlock.ContainsKey("DB10"));
        Assert.Equal("DB10", snapshot.RootGroupByBlock["DB10"]);
    }

    [Fact]
    public void Later_row_for_same_block_overwrites_earlier_one()
    {
        var rows = new[]
        {
            Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7", rootGroup: "DB10"),
            Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7Plus", rootGroup: "DB10_2"),
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal(IgwDeviceKind.S7Plus, snapshot.DeviceKindByBlock["DB10"]);
        Assert.Equal("DB10_2", snapshot.RootGroupByBlock["DB10"]);
    }

    [Fact]
    public void Empty_renames_json_produces_no_entries()
    {
        var rows = new[] { Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7", renamesJson: "") };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Empty(snapshot.DbRenames);
    }

    // ---- fix round 1: 大小写不敏感比较器 + malformed 行计数 ----

    [Fact]
    public void DbRenames_lookup_is_ordinal_and_keeps_case_variants_apart()
    {
        // task-8 fix round 1：重名正由大小写变体引起，映射键必须按原始标识精确匹配
        var json = "{\"Sub.flag\":\"flag_2\",\"Sub.FLAG\":\"FLAG_3\"}";
        var rows = new[]
        {
            Row(IgwLedgerRootKind.DbBlock, "DB5", deviceKind: "S7", renamesJson: json),
            Row(IgwLedgerRootKind.DbBlock, "db5", deviceKind: "S7Plus", renamesJson: "{\"Sub.flag\":\"flag_9\"}"),
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows, out var malformed);

        Assert.Equal(0, malformed);
        Assert.Equal("flag_2", snapshot.DbRenames[("DB5", "Sub.flag")]);
        Assert.Equal("FLAG_3", snapshot.DbRenames[("DB5", "Sub.FLAG")]);
        Assert.Equal("flag_9", snapshot.DbRenames[("db5", "Sub.flag")]);
        Assert.False(snapshot.DbRenames.ContainsKey(("DB5", "Sub.Flag")));
        // 块级查找仍大小写不敏感
        Assert.True(snapshot.DeviceKindByBlock.ContainsKey("DB5"));
    }

    [Fact]
    public void IoRenames_lookup_is_ordinal_on_table_and_tag()
    {
        var sep = IgwLedgerSnapshotBuilder.IoRenameKeySeparator;
        var map = new Dictionary<string, string>
        {
            [$"DefaultTagTable{sep}clock_2_5hz"] = "clock_2_5hz_2",
            [$"DefaultTagTable{sep}CLOCK_2_5HZ"] = "CLOCK_2_5HZ_3",
        };
        var json = JsonSerializer.Serialize(map);
        var rows = new[] { Row(IgwLedgerRootKind.Io, "IO", renamesJson: json) };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal("clock_2_5hz_2", snapshot.IoRenames[("DefaultTagTable", "clock_2_5hz")]);
        Assert.Equal("CLOCK_2_5HZ_3", snapshot.IoRenames[("DefaultTagTable", "CLOCK_2_5HZ")]);
        Assert.False(snapshot.IoRenames.ContainsKey(("DefaultTagTable", "Clock_2_5Hz")));
        Assert.False(snapshot.IoRenames.ContainsKey(("defaulttagtable", "clock_2_5hz")));
    }

    [Fact]
    public void Build_reports_count_of_rows_with_malformed_renames_json()
    {
        var rows = new[]
        {
            Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7", renamesJson: "{not valid json"),
            Row(IgwLedgerRootKind.Io, "IO", renamesJson: "{also not valid"),
            Row(IgwLedgerRootKind.DbBlock, "DB11", deviceKind: "S7Plus", renamesJson: "{\"A\":\"B\"}"),
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows, out var malformedCount);

        Assert.Equal(2, malformedCount);
        Assert.Equal("B", snapshot.DbRenames[("DB11", "A")]);
    }

    [Fact]
    public void Build_without_out_param_still_works_and_defaults_malformed_count_discarded()
    {
        var rows = new[] { Row(IgwLedgerRootKind.DbBlock, "DB10", deviceKind: "S7") };

        // 单参数重载（原有调用方无需改动）
        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Equal(IgwDeviceKind.S7, snapshot.DeviceKindByBlock["DB10"]);
    }

    [Fact]
    public void Root_kinds_reflect_live_rows_only()
    {
        var snapshot = IgwLedgerSnapshotBuilder.Build(new[]
        {
            Row(IgwLedgerRootKind.Io, "IO", renamesJson: "not json"),   // RenamesJson 损坏也算「有 Io 行」
            Row(IgwLedgerRootKind.Graph, "ST1DB5", isDeleted: true),
            Row(IgwLedgerRootKind.DbBlock, ""),
        });

        Assert.True(snapshot.HasRootKind(IgwLedgerRootKind.Io));
        Assert.False(snapshot.HasRootKind(IgwLedgerRootKind.Graph));
        Assert.False(snapshot.HasRootKind(IgwLedgerRootKind.DbBlock));
        Assert.False(IgwLedgerSnapshot.Empty.HasRootKind(IgwLedgerRootKind.DbBlock));
    }
}
