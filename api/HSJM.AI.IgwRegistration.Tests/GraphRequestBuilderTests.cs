using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>GraphRequestBuilder（GRAPH 台账 → GRAPH 设备批量请求）纯函数单测</summary>
public class GraphRequestBuilderTests
{
    private const string Ip = "10.11.38.20";

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoPrevious =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    private static GraphBuildResult Build(IReadOnlyList<GraphTargetSpec> targets,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> previous = null, IgwRegistrationConfig config = null) =>
        GraphRequestBuilder.Build("线体A_PLC1", Ip, 0, targets, previous ?? NoPrevious, config ?? new IgwRegistrationConfig());

    [Fact]
    public void Station_bound_db_produces_exec_bits_array_and_s_no_under_station_group()
    {
        var result = Build(new[] { new GraphTargetSpec("DB161_HomeGraph", "OP010", 11, true) });

        var planned = Assert.Single(result.Requests);
        var request = planned.Request;
        Assert.Equal(new[] { "DB161_HomeGraph" }, planned.DbNames);
        Assert.Equal("线体A_PLC1", request.Channel.Name);
        Assert.Equal("Siemens.S7", request.Channel.DriverId);
        Assert.Equal("GRAPH", request.Device.Name);
        Assert.Equal($"{Ip}:102", request.Device.Endpoint);
        Assert.Equal($"S7Plus_{Ip}", request.Device.Settings["dcsLogicalDevice"]);
        Assert.Equal("S7-1500", request.Device.Settings["model"]);

        Assert.Equal(2, request.Items.Count);
        var exec = request.Items.Single(i => i.Name == "EXEC_BITS");
        Assert.Equal(new[] { "OP010", "DB161_HomeGraph", "RT_DATA" }, exec.GroupPath);
        Assert.Equal("DB161_HomeGraph.RT_DATA.EXEC_BITS", exec.Address);
        Assert.Equal("bool", exec.DataType);
        Assert.Equal("bool", exec.Array.ElementType);
        Assert.Equal(new[] { 12 }, exec.Array.Dimensions);
        Assert.Equal("readWrite", exec.Access);
        Assert.Equal(100, exec.ScanRateMs);

        var sno = request.Items.Single(i => i.Name == "S_NO");
        Assert.Equal(new[] { "OP010", "DB161_HomeGraph" }, sno.GroupPath);
        Assert.Equal("DB161_HomeGraph.S_NO", sno.Address);
        Assert.Equal("int16", sno.DataType);
        Assert.Null(sno.Array);
        Assert.Equal(100, sno.ScanRateMs);

        var replace = Assert.Single(request.ReplaceGroups);
        Assert.Equal(new[] { "OP010", "DB161_HomeGraph" }, replace);
        Assert.Equal(2, result.TagCountByDb["DB161_HomeGraph"]);
        Assert.Empty(result.SkippedDbs);
    }

    [Fact]
    public void Unbound_db_goes_to_ungrouped_station()
    {
        var result = Build(new[] { new GraphTargetSpec("DB200_Graph", null, 0, true), new GraphTargetSpec("DB201_Graph", "  ", 3, true) });

        var request = Assert.Single(result.Requests).Request;
        Assert.All(request.Items, i => Assert.Equal(IgwTagNaming.UngroupedStation, i.GroupPath[0]));
        Assert.Equal(new[] { 1 }, request.Items.Single(i => i.Name == "EXEC_BITS" && i.GroupPath[1] == "DB200_Graph").Array.Dimensions);
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "_未分组", "DB200_Graph" }));
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "_未分组", "DB201_Graph" }));
    }

    [Fact]
    public void Station_rebind_replaces_old_and_new_station_groups_deduplicated()
    {
        var previous = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB161_HomeGraph"] = new[] { "OP020", "op020", null, "OP010" },
            ["DB162_Graph"] = new[] { "OP030" },
        };
        var result = Build(new[]
        {
            new GraphTargetSpec("DB161_HomeGraph", "OP010", 5, true),
            new GraphTargetSpec("DB162_Graph", "OP030", 5, true),
        }, previous);

        var groups = Assert.Single(result.Requests).Request.ReplaceGroups;
        Assert.Equal(4, groups.Count);
        Assert.Contains(groups, g => g.SequenceEqual(new[] { "OP020", "DB161_HomeGraph" }));
        Assert.Contains(groups, g => g.SequenceEqual(new[] { "_未分组", "DB161_HomeGraph" }));
        Assert.Contains(groups, g => g.SequenceEqual(new[] { "OP010", "DB161_HomeGraph" }));
        Assert.Contains(groups, g => g.SequenceEqual(new[] { "OP030", "DB162_Graph" }));
    }

    [Fact]
    public void Names_are_sanitized_and_match_graph_read_alias()
    {
        var result = Build(new[] { new GraphTargetSpec("DB9_Graph", " OP.050 ", 2, true) });

        var request = Assert.Single(result.Requests).Request;
        var exec = request.Items.Single(i => i.Name == "EXEC_BITS");
        var sno = request.Items.Single(i => i.Name == "S_NO");
        Assert.Equal("OP_050", exec.GroupPath[0]);

        // 物理路径 = 通用.{S7Plus_ip}.{组路径}.{点名}，必须与读别名逐字一致
        string PhysicalAlias(IgwBulkItem item) => $"通用.S7Plus_{Ip}." + string.Join(".", item.GroupPath.Append(item.Name));
        Assert.Equal(IgwTagNaming.GraphReadAlias(Ip, " OP.050 ", "DB9_Graph", GraphRequestBuilder.ExecBitsMember), PhysicalAlias(exec));
        Assert.Equal(IgwTagNaming.GraphReadAlias(Ip, " OP.050 ", "DB9_Graph", GraphRequestBuilder.StepNoMember), PhysicalAlias(sno));
        Assert.Equal(IgwTagNaming.GraphReadAlias(Ip, null, "DB9_Graph", "S_NO"),
            $"通用.S7Plus_{Ip}.{GraphRequestBuilder.StationGroup(null)}.DB9_Graph.S_NO");
        Assert.Equal("OP_050DB9_Graph", GraphRequestBuilder.LedgerRootName(" OP.050 ", "DB9_Graph"));
        Assert.Equal("_未分组DB9_Graph", GraphRequestBuilder.LedgerRootName("", "DB9_Graph"));
        // 工站名含 '|' 时台账键仍可无歧义还原
        var snapshot = IgwLedgerSnapshotBuilder.Build(new[]
        {
            new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.Graph, RootName = GraphRequestBuilder.LedgerRootName("OP|10", "DB9_Graph") },
        });
        Assert.Equal("OP|10", snapshot.StationByDb["DB9_Graph"]);
    }

    [Fact]
    public void Missing_exec_bits_or_s_no_are_omitted_and_invalid_db_is_skipped()
    {
        var result = Build(new[]
        {
            new GraphTargetSpec("DB1_Graph", "OP010", null, true),
            new GraphTargetSpec("DB2_Graph", "OP010", 4, false),
            new GraphTargetSpec("DB 3 Graph", "OP010", 4, true),
            new GraphTargetSpec("  ", "OP010", 4, true),
            new GraphTargetSpec("db1_graph", "OP010", 4, true), // 大小写重复：首个生效
        });

        var request = Assert.Single(result.Requests).Request;
        Assert.Equal(new[] { "S_NO" }, request.Items.Where(i => i.GroupPath[1] == "DB1_Graph").Select(i => i.Name));
        Assert.Equal(new[] { "EXEC_BITS" }, request.Items.Where(i => i.GroupPath[1] == "DB2_Graph").Select(i => i.Name));
        Assert.Equal(2, request.Items.Count);
        Assert.True(result.SkippedDbs.ContainsKey("DB 3 Graph"));
        Assert.Equal(new[] { "DB1_Graph", "DB2_Graph" }, Assert.Single(result.Requests).DbNames);
    }

    [Fact]
    public void Requests_are_split_on_db_boundaries_when_exceeding_max_items()
    {
        var targets = Enumerable.Range(1, 5).Select(i => new GraphTargetSpec($"DB{i}_Graph", "OP010", 3, true)).ToList();

        var result = Build(targets, config: new IgwRegistrationConfig { MaxItemsPerRequest = 4, GraphScanRateMs = 50 });

        Assert.Equal(3, result.Requests.Count);
        Assert.Equal(new[] { 4, 4, 2 }, result.Requests.Select(r => r.Request.Items.Count));
        Assert.Equal(new[] { "DB1_Graph", "DB2_Graph" }, result.Requests[0].DbNames);
        Assert.All(result.Requests, r => Assert.Equal(r.DbNames.Count, r.Request.ReplaceGroups.Count));
        Assert.All(result.Requests.SelectMany(r => r.Request.Items), i => Assert.Equal(50, i.ScanRateMs));
    }

    [Fact]
    public void Stale_roots_become_replace_only_groups()
    {
        var result = GraphRequestBuilder.Build("线体A_PLC1", Ip, 0,
            new[] { new GraphTargetSpec("DB1_Graph", "OP010", 3, true) }, NoPrevious, new IgwRegistrationConfig(),
            new[] { ("OP050", "DB9_Old"), ((string)null, "DB8_Old"), ("OP010", "db1_graph") });

        var planned = Assert.Single(result.Requests);
        Assert.Equal(new[] { "DB9_Old", "DB8_Old" }, planned.StaleDbNames);
        Assert.Contains(planned.Request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP050", "DB9_Old" }));
        Assert.Contains(planned.Request.ReplaceGroups, g => g.SequenceEqual(new[] { "_未分组", "DB8_Old" }));
        Assert.Equal(2, planned.Request.Items.Count); // 失效 DB 不生成点；与目标同名的失效项被忽略
        Assert.Equal(3, planned.Request.ReplaceGroups.Count);

        var onlyStale = GraphRequestBuilder.Build("c", Ip, 0, Array.Empty<GraphTargetSpec>(), NoPrevious,
            new IgwRegistrationConfig(), new[] { ("OP050", "DB9_Old") });
        var stalePlanned = Assert.Single(onlyStale.Requests);
        Assert.Empty(stalePlanned.Request.Items);
        Assert.Empty(stalePlanned.DbNames);
    }
}
