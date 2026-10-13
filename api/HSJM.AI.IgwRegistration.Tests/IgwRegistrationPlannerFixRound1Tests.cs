using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>Fix round 1：DB 重名映射 / IO 根组冲突 / replaceGroups 上限 / JSON 形状</summary>
public class IgwRegistrationPlannerFixRound1Tests
{
    private const string Ip = "192.168.1.10";

    private static PlannerInput Input(
        IReadOnlyList<PlannerDbRow> rows = null,
        IReadOnlyList<PlannerIoTag> io = null,
        IReadOnlyCollection<string> changed = null,
        IReadOnlyCollection<string> deleted = null,
        bool ioRefreshed = false,
        bool full = true) =>
        new("线体A_PLC1", Ip, 102,
            rows ?? new List<PlannerDbRow>(),
            io ?? new List<PlannerIoTag>(),
            changed ?? new List<string>(),
            deleted ?? new List<string>(),
            ioRefreshed, full, new IgwRegistrationConfig());

    [Fact]
    public void Db_duplicate_renames_are_exposed()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB5", 5, "Sub.Flag", "Bool", 0m, 0m, null),
            new("DB5", 5, "Sub.flag", "Bool", 0m, 1m, null),
            new("DB5", 5, "Sub.FLAG", "Bool", 0m, 2m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        Assert.Equal(2, plan.DbRenames.Count);
        Assert.Equal("flag_2", plan.DbRenames[("DB5", "Sub.flag")]);
        Assert.Equal("FLAG_3", plan.DbRenames[("DB5", "Sub.FLAG")]);
        Assert.False(plan.DbRenames.ContainsKey(("DB5", "Sub.Flag")));
        Assert.Empty(plan.BlockRootRenames);
    }

    [Fact]
    public void Block_named_io_uses_io_db_root_and_replace_group()
    {
        var rows = new List<PlannerDbRow> { new("io", 7, "A", "Int", 0m, 0m, null) };
        var tags = new List<PlannerIoTag> { new("T", "A", "%I0.0", "Bool", null) };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, tags, full: true));

        Assert.Equal("IO_DB", plan.BlockRootRenames["io"]);
        var s7 = Assert.Single(plan.Requests, r => r.Device == IgwDeviceKind.S7);
        var dbItem = Assert.Single(s7.Request.Items, i => i.Address == "DB7.DBW0");
        Assert.Equal(new[] { "IO_DB" }, dbItem.GroupPath);
        Assert.Contains(s7.Request.ReplaceGroups, g => g.SequenceEqual(new[] { "IO_DB" }));
        Assert.Contains(s7.Request.ReplaceGroups, g => g.SequenceEqual(new[] { "IO" }));
        var plus = Assert.Single(plan.Requests, r => r.Device == IgwDeviceKind.S7Plus);
        Assert.Equal(new[] { "IO_DB" }, Assert.Single(plus.Request.ReplaceGroups));
    }

    [Fact]
    public void Deleted_block_named_io_replaces_io_db_not_io()
    {
        var plan = IgwRegistrationPlanner.Plan(Input(deleted: new[] { "IO" }, full: false));

        Assert.Equal(2, plan.Requests.Count);
        Assert.All(plan.Requests, r => Assert.Equal(new[] { "IO_DB" }, Assert.Single(r.Request.ReplaceGroups)));
        Assert.Equal("IO_DB", plan.BlockRootRenames["IO"]);
    }

    [Fact]
    public void Replace_only_requests_respect_replace_groups_cap()
    {
        var deleted = Enumerable.Range(0, IgwRegistrationPlanner.MaxReplaceGroupsPerRequest + 1).Select(i => "DB" + i).ToList();

        var plan = IgwRegistrationPlanner.Plan(Input(deleted: deleted, full: false));

        foreach (var device in new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus })
        {
            var reqs = plan.Requests.Where(r => r.Device == device).ToList();
            Assert.Equal(2, reqs.Count);
            Assert.Equal(10_000, reqs[0].Request.ReplaceGroups.Count);
            Assert.Single(reqs[1].Request.ReplaceGroups);
        }
    }

    [Fact]
    public void Block_units_split_when_replace_groups_cap_reached()
    {
        var count = IgwRegistrationPlanner.MaxReplaceGroupsPerRequest + 1;
        var rows = Enumerable.Range(0, count).Select(i => new PlannerDbRow("DB" + i, i + 1, "A", "Int", 0m, 0m, null)).ToList();
        var tags = new List<PlannerIoTag> { new("T", "A", "%I0.0", "Bool", null) };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, tags, full: true));

        var s7 = plan.Requests.Where(r => r.Device == IgwDeviceKind.S7).ToList();
        Assert.All(plan.Requests, r => Assert.True(r.Request.ReplaceGroups.Count <= IgwRegistrationPlanner.MaxReplaceGroupsPerRequest));
        Assert.Equal(2, s7.Count);
        Assert.Equal(count + 1, s7.Sum(r => r.Request.ReplaceGroups.Count)); // 所有块 + IO
        Assert.Equal(count + 1, s7.Sum(r => r.Request.Items.Count));
        // 每个块的点与其 replaceGroup 在同一请求
        foreach (var r in s7)
        {
            foreach (var item in r.Request.Items.Where(i => i.GroupPath[0] != "IO"))
            {
                Assert.Contains(r.Request.ReplaceGroups, g => g[0] == item.GroupPath[0]);
            }
        }
        var plus = plan.Requests.Where(r => r.Device == IgwDeviceKind.S7Plus).ToList();
        Assert.Equal(2, plus.Count);
        Assert.Equal(count, plus.Sum(r => r.Request.ReplaceGroups.Count));
    }

    [Fact]
    public void Request_serializes_to_igw_json_shape()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB1", 1, "Sub.Speed", "Word", 0m, 0m, "速度"),
        };
        var plan = IgwRegistrationPlanner.Plan(Input(rows, full: false, changed: new[] { "DB1" }));
        var request = plan.Requests.Single(r => r.Device == IgwDeviceKind.S7).Request;

        var json = JsonSerializer.Serialize(request, IgwBulkJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("线体A_PLC1", root.GetProperty("channel").GetProperty("name").GetString());
        Assert.Equal("Siemens.S7", root.GetProperty("channel").GetProperty("driverId").GetString());
        Assert.Equal("ABCD", root.GetProperty("channel").GetProperty("settings").GetProperty("wordOrder").GetString());
        Assert.Equal("S7", root.GetProperty("device").GetProperty("name").GetString());
        Assert.Equal(Ip + ":102", root.GetProperty("device").GetProperty("endpoint").GetString());
        Assert.Equal("DB1", root.GetProperty("replaceGroups")[0][0].GetString());
        var item = root.GetProperty("items")[0];
        Assert.Equal("DB1", item.GetProperty("groupPath")[0].GetString());
        Assert.Equal("Sub", item.GetProperty("groupPath")[1].GetString());
        Assert.Equal("Speed", item.GetProperty("name").GetString());
        Assert.Equal("DB1.DBW0", item.GetProperty("address").GetString());
        Assert.Equal(JsonValueKind.String, item.GetProperty("dataType").ValueKind);
        Assert.Equal("uInt16", item.GetProperty("dataType").GetString());
        Assert.Equal("readWrite", item.GetProperty("access").GetString());
        Assert.Equal(1000, item.GetProperty("scanRateMs").GetInt32());
        Assert.Equal("速度", item.GetProperty("description").GetString());
        Assert.False(item.TryGetProperty("array", out _));        // null 不输出
        Assert.False(item.TryGetProperty("stringLength", out _));
        Assert.False(item.TryGetProperty("GroupPath", out _));    // 必须 camelCase

        var back = JsonSerializer.Deserialize<IgwBulkRequest>(json, IgwBulkJson.Options);
        Assert.Equal(request.Items[0].Address, back.Items[0].Address);
        Assert.Equal(request.Items[0].GroupPath, back.Items[0].GroupPath);
        Assert.Equal(request.ReplaceGroups[0], back.ReplaceGroups[0]);
        Assert.Equal(request.Device.Settings["dcsLogicalDevice"], back.Device.Settings["dcsLogicalDevice"]);
    }

    [Fact]
    public void Response_deserializes_from_igw_camelcase_body()
    {
        const string body = "{\"applied\":true,\"revision\":\"r-5\",\"created\":3,\"updated\":1,\"deleted\":2,\"deviceRestarted\":true,\"timing\":\"x\"}";

        var resp = JsonSerializer.Deserialize<IgwBulkResponse>(body, IgwBulkJson.Options);

        Assert.True(resp.Applied);
        Assert.Equal("r-5", resp.Revision);
        Assert.Equal(3, resp.Created);
        Assert.Equal(2, resp.Deleted);
        Assert.True(resp.DeviceRestarted);
    }
}
