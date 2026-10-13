using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// task-8：读链按台账拼读别名（IgwReadAliasResolver）。覆盖 IO / 重名 IO / S7 块 / S7Plus 块 / 根组改名 /
/// 点重名 / 无台账回退 / GRAPH 有无工站，以及「规划器产出项 ⇔ 解析器别名」逐字一致。
/// </summary>
public class IgwReadAliasResolverTests
{
    private const string Ip = "192.168.1.10";

    private static IgwLedgerSnapshot Snapshot(
        Dictionary<string, IgwDeviceKind> kinds = null,
        Dictionary<string, string> roots = null,
        Dictionary<(string, string), string> dbRenames = null,
        Dictionary<(string, string), string> ioRenames = null,
        Dictionary<string, string> stations = null) =>
        new(
            new Dictionary<string, IgwDeviceKind>(kinds ?? new(), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(roots ?? new(), StringComparer.OrdinalIgnoreCase),
            dbRenames ?? new Dictionary<(string, string), string>(),
            ioRenames ?? new Dictionary<(string, string), string>(),
            new Dictionary<string, string>(stations ?? new(), StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Io_tag_uses_io_alias_on_s7_device()
    {
        var alias = IgwReadAliasResolver.ResolveIo(Ip, IgwLedgerSnapshot.Empty, "默认变量表", "I106.2_OP1080-SG01");
        Assert.Equal("通用.S7_192.168.1.10.IO.默认变量表.I106_2_OP1080-SG01", alias);
    }

    [Fact]
    public void Renamed_io_tag_uses_ledger_final_name()
    {
        var snapshot = Snapshot(ioRenames: new() { [("T", "A.1")] = "A_1_2" });
        Assert.Equal("通用.S7_192.168.1.10.IO.T.A_1_2", IgwReadAliasResolver.ResolveIo(Ip, snapshot, "T", "A.1"));
        Assert.Equal("通用.S7_192.168.1.10.IO.T.A_1", IgwReadAliasResolver.ResolveIo(Ip, snapshot, "T", "A_1"));
    }

    [Fact]
    public void S7_block_uses_s7_logical_device()
    {
        var snapshot = Snapshot(kinds: new() { ["DB_Data"] = IgwDeviceKind.S7 }, roots: new() { ["DB_Data"] = "DB_Data" });
        Assert.Equal("通用.S7_192.168.1.10.DB_Data.Flag", IgwReadAliasResolver.ResolveDb(Ip, snapshot, "DB_Data", "Flag"));
    }

    [Fact]
    public void S7Plus_block_uses_s1500_logical_device_and_normalized_path()
    {
        var snapshot = Snapshot(kinds: new() { ["DB490"] = IgwDeviceKind.S7Plus }, roots: new() { ["DB490"] = "DB490" });
        Assert.Equal("通用.S1500_192.168.1.10.DB490.Type.Robot[2].X",
            IgwReadAliasResolver.ResolveDb(Ip, snapshot, "DB490", "Type.Robot.Robot[2].X"));
    }

    [Fact]
    public void Block_root_rename_from_ledger_replaces_block_group()
    {
        var snapshot = Snapshot(kinds: new() { ["IO"] = IgwDeviceKind.S7 }, roots: new() { ["IO"] = "IO_DB" });
        Assert.Equal("通用.S7_192.168.1.10.IO_DB.Sensor[3]", IgwReadAliasResolver.ResolveDb(Ip, snapshot, "IO", "Sensor.Sensor[3]"));
    }

    [Fact]
    public void Db_tag_rename_replaces_only_last_segment()
    {
        var snapshot = Snapshot(
            kinds: new() { ["DB1"] = IgwDeviceKind.S7Plus },
            roots: new() { ["DB1"] = "DB1" },
            dbRenames: new() { [("DB1", "Grp.flag")] = "flag_2" });
        Assert.Equal("通用.S1500_192.168.1.10.DB1.Grp.flag_2", IgwReadAliasResolver.ResolveDb(Ip, snapshot, "DB1", "Grp.flag"));
    }

    [Fact]
    public void Block_without_ledger_entry_falls_back_to_s7plus_with_sanitized_root()
    {
        Assert.Equal("通用.S1500_192.168.1.10.DB_A_B.X[1].Y",
            IgwReadAliasResolver.ResolveDb(Ip, IgwLedgerSnapshot.Empty, "DB_A.B", "X.X[1].Y"));
        Assert.Equal("通用.S1500_192.168.1.10.DB_A_B.X[1].Y",
            IgwReadAliasResolver.ResolveDb(Ip, null, "DB_A.B", "X.X[1].Y"));
    }

    [Fact]
    public void Graph_uses_ledger_station_first()
    {
        var snapshot = Snapshot(stations: new() { ["DB160_MainGraph"] = "OP010" });
        Assert.Equal("通用.S7Plus_192.168.1.10.OP010.DB160_MainGraph.RT_DATA.EXEC_BITS",
            IgwReadAliasResolver.ResolveGraph(Ip, snapshot, "DB160_MainGraph", "RT_DATA.EXEC_BITS", "OP999"));
    }

    [Fact]
    public void Graph_falls_back_to_step_tag_station_then_ungrouped()
    {
        Assert.Equal("通用.S7Plus_192.168.1.10.OP020.DB161_HomeGraph.S_NO",
            IgwReadAliasResolver.ResolveGraph(Ip, IgwLedgerSnapshot.Empty, "DB161_HomeGraph", "S_NO", "OP020"));
        Assert.Equal("通用.S7Plus_192.168.1.10._未分组.DB161_HomeGraph.S_NO",
            IgwReadAliasResolver.ResolveGraph(Ip, IgwLedgerSnapshot.Empty, "DB161_HomeGraph", "S_NO", null));
    }

    [Fact]
    public void Graph_ledger_ungrouped_station_is_kept()
    {
        var snapshot = Snapshot(stations: new() { ["DB161_HomeGraph"] = IgwTagNaming.UngroupedStation });
        Assert.Equal("通用.S7Plus_192.168.1.10._未分组.DB161_HomeGraph.S_NO",
            IgwReadAliasResolver.ResolveGraph(Ip, snapshot, "DB161_HomeGraph", "S_NO", "OP020"));
    }

    [Theory]
    [InlineData("S_NO", true)]
    [InlineData("RT_DATA.EXEC_BITS", true)]
    [InlineData("rt_data.exec_bits", true)]
    [InlineData("RT_DATA.EXEC_BITS.EXEC_BITS[3]", false)]
    [InlineData("General.S_NO", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_graph_container_members(string member, bool expected) =>
        Assert.Equal(expected, IgwReadAliasResolver.IsGraphContainerMember(member));

    [Fact]
    public void Resolve_dispatches_by_kind()
    {
        Assert.Equal(IgwReadAliasResolver.ResolveIo(Ip, null, "T", "A"),
            IgwReadAliasResolver.Resolve(Ip, null, IgwReadRequestKind.IoTag, "T", "A"));
        Assert.Equal(IgwReadAliasResolver.ResolveDb(Ip, null, "DB1", "A"),
            IgwReadAliasResolver.Resolve(Ip, null, IgwReadRequestKind.DbVariable, "DB1", "A"));
        Assert.Equal(IgwReadAliasResolver.ResolveGraph(Ip, null, "DB1", "S_NO", "OP1"),
            IgwReadAliasResolver.Resolve(Ip, null, IgwReadRequestKind.Graph, "DB1", "S_NO", "OP1"));
    }

    // ---------- 规划器产出 ⇔ 解析器别名逐字一致 ----------

    [Fact]
    public void Resolver_aliases_match_planner_items_exactly()
    {
        var rows = new List<PlannerDbRow>
        {
            // S7 块（全偏移）+ 大小写重名点（flag → flag_2）
            new("DB_Data", 10, "Flag", "Bool", 0m, 0m, null),
            new("DB_Data", 10, "flag", "Bool", 0m, 1m, null),
            // S7Plus 块（解析器数组路径还原 + 含 . 的段清洗）
            new("DB490", 490, "Type.Robot.Robot[2].X", "Bool", null, null, null),
            new("DB490", 490, "Status.Clock_2.5Hz", "String[20]", null, null, null),
            // 块名与 IO 根组冲突 → IO_DB
            new("IO", 5, "Sensor.Sensor[3]", "Bool", 1m, 0m, null),
            // 清洗后同名块 → 第二个 {root}_2
            new("A.B", 6, "V", "Int", 0m, 0m, null),
            new("A_B", 7, "V", "Int", 0m, 0m, null),
        };
        var io = new List<PlannerIoTag>
        {
            new("T", "A.1", "%I0.0", "Bool", null),
            new("T", "A_1", "%I0.1", "Bool", null),
            new("默认变量表", "Q102.3", "%Q102.3", "Bool", null),
            // 大小写重名 IO 点（lamp → lamp_2）：Lamp 的读请求不得命中 lamp 的映射
            new("T", "Lamp", "%Q0.0", "Bool", null),
            new("T", "lamp", "%Q0.1", "Bool", null),
        };
        var input = new PlannerInput("线体A_PLC1", Ip, 102, rows, io, new List<string>(), new List<string>(),
            true, true, new IgwRegistrationConfig());

        var plan = IgwRegistrationPlanner.Plan(input);

        // 与生产同一路径构造快照：按 IgwRegistrationRunner.BuildLedger 的口径写台账行（RenamesJson 经 SerializeRenames），
        // 再经 IgwLedgerSnapshotBuilder.Build 折叠（重名键 Ordinal、块级键大小写不敏感）
        var snapshot = IgwLedgerSnapshotBuilder.Build(LedgerRowsLikeRunner(plan), out var malformed);
        Assert.Equal(0, malformed);

        var planned = plan.Requests
            .SelectMany(r => r.Request.Items.Select(i => IgwTagNaming.ReadAlias(r.Device, Ip, i.GroupPath, i.Name)))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
        var resolved = rows.Select(r => IgwReadAliasResolver.ResolveDb(Ip, snapshot, r.BlockName, r.VariablePath))
            .Concat(io.Select(t => IgwReadAliasResolver.ResolveIo(Ip, snapshot, t.TableName, t.TagName)))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(rows.Count + io.Count, planned.Count);
        Assert.Equal(planned, resolved);
        Assert.Contains("通用.S7_192.168.1.10.IO_DB.Sensor[3]", resolved);
        Assert.Contains("通用.S7_192.168.1.10.A_B_2.V", resolved);
        Assert.Contains("通用.S7_192.168.1.10.DB_Data.flag_2", resolved);
        Assert.Equal("通用.S7_192.168.1.10.DB_Data.Flag", IgwReadAliasResolver.ResolveDb(Ip, snapshot, "DB_Data", "Flag"));
        Assert.Equal("通用.S7_192.168.1.10.DB_Data.flag_2", IgwReadAliasResolver.ResolveDb(Ip, snapshot, "DB_Data", "flag"));
        Assert.Equal("通用.S7_192.168.1.10.IO.T.Lamp", IgwReadAliasResolver.ResolveIo(Ip, snapshot, "T", "Lamp"));
        Assert.Equal("通用.S7_192.168.1.10.IO.T.lamp_2", IgwReadAliasResolver.ResolveIo(Ip, snapshot, "T", "lamp"));
        Assert.Contains("通用.S7_192.168.1.10.IO.T.A_1_2", resolved);
    }

    [Fact]
    public void Resolver_graph_aliases_match_graph_builder_items()
    {
        var targets = new List<GraphTargetSpec>
        {
            new("DB160_MainGraph", "OP010", 42, true),
            new("DB161_HomeGraph", null, 7, true),
        };
        var build = GraphRequestBuilder.Build("线体A_PLC1", Ip, 102, targets,
            new Dictionary<string, IReadOnlyCollection<string>>(), new IgwRegistrationConfig());

        var planned = build.Requests
            .SelectMany(r => r.Request.Items.Select(i => IgwTagNaming.ReadAlias(IgwDeviceKind.Graph, Ip, i.GroupPath, i.Name)))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
        var snapshot = IgwLedgerSnapshotBuilder.Build(targets.Select(t => new PLC_IgwRegistrationEntity
        {
            ProjectId = 1,
            RootKind = IgwLedgerRootKind.Graph,
            RootName = GraphRequestBuilder.LedgerRootName(t.StationCode, t.DbName),
            RootGroup = GraphRequestBuilder.StationGroup(t.StationCode),
            DeviceKind = IgwDeviceKind.Graph.ToString(),
        }));
        var resolved = targets
            .SelectMany(t => new[] { GraphRequestBuilder.ExecBitsMember, GraphRequestBuilder.StepNoMember }
                .Select(m => IgwReadAliasResolver.ResolveGraph(Ip, snapshot, t.DbName, m, null)))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(planned, resolved);
    }

    [Fact]
    public void Graph_station_fallback_takes_latest_row_per_db()
    {
        var map = IgwReadAliasResolver.BuildGraphStationFallback(new (long, string, string)[]
        {
            (1, "DB160_MainGraph", "OP005"),   // 旧行（被重注册覆盖前的残留：仅取最新）
            (5, "DB160_MainGraph ", " OP010 "),
            (3, "db160_maingraph", "OP007"),
            (2, "DB161_HomeGraph", "OP020"),
            (4, "DB161_HomeGraph", null),      // 最新行未绑定工站 → 不入表 → _未分组
            (6, "", "OP099"),
        });

        Assert.Equal("OP010", map["DB160_MainGraph"]);
        Assert.False(map.ContainsKey("DB161_HomeGraph"));
        Assert.Single(map);
    }

    [Fact]
    public void Graph_station_fallback_matches_registered_station_group()
    {
        // 注册侧：GraphStepTagRegistrationJob 把 target.StationCode 写入该 DB 全部台账行，并注册到 StationGroup(StationCode)
        foreach (var station in new[] { "OP010", " OP.020 ", null, "" })
        {
            var map = IgwReadAliasResolver.BuildGraphStationFallback(new (long, string, string)[]
            {
                (10, "DB160_MainGraph", station), (11, "DB160_MainGraph", station),
            });
            var build = GraphRequestBuilder.Build("C", Ip, 102,
                new[] { new GraphTargetSpec("DB160_MainGraph", station, null, true) },
                new Dictionary<string, IReadOnlyCollection<string>>(), new IgwRegistrationConfig());
            var item = build.Requests.Single().Request.Items.Single();

            Assert.Equal(IgwTagNaming.ReadAlias(IgwDeviceKind.Graph, Ip, item.GroupPath, item.Name),
                IgwReadAliasResolver.ResolveGraph(Ip, IgwLedgerSnapshot.Empty, "DB160_MainGraph", "S_NO",
                    map.TryGetValue("DB160_MainGraph", out var s) ? s : null));
        }
    }

    /// <summary>按 IgwRegistrationRunner.BuildLedger（usePlanned 分支）的口径生成台账行。</summary>
    internal static List<PLC_IgwRegistrationEntity> LedgerRowsLikeRunner(PlanResult plan)
    {
        var rows = plan.Blocks.Select(block => new PLC_IgwRegistrationEntity
        {
            ProjectId = 1,
            RootKind = IgwLedgerRootKind.DbBlock,
            RootName = block.BlockName,
            DeviceKind = block.Device.ToString(),
            RootGroup = plan.BlockRootRenames.TryGetValue(block.BlockName, out var renamedRoot)
                ? renamedRoot
                : IgwTagNaming.SanitizeName(block.BlockName),
            RenamesJson = IgwRegistrationRunner.SerializeRenames(plan.DbRenames
                .Where(kv => string.Equals(kv.Key.Block, block.BlockName, StringComparison.Ordinal))
                .Select(kv => (kv.Key.VariablePath, kv.Value))),
            Status = IgwLedgerStatus.Registered,
        }).ToList();
        rows.Add(new PLC_IgwRegistrationEntity
        {
            ProjectId = 1,
            RootKind = IgwLedgerRootKind.Io,
            RootName = IgwTagNaming.IoRootGroup,
            RootGroup = IgwTagNaming.IoRootGroup,
            DeviceKind = IgwDeviceKind.S7.ToString(),
            RenamesJson = IgwRegistrationRunner.SerializeRenames(
                plan.IoRenames.Select(kv => (kv.Key.Table + IgwTagNaming.KeySeparator + kv.Key.Tag, kv.Value))),
            Status = IgwLedgerStatus.Registered,
        });
        return rows;
    }
}
