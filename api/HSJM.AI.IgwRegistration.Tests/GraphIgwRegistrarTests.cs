using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// GraphIgwRegistrar（GraphStepTagRegistrationJob 的 IGW 分支核心）单测：假 IGW（HttpMessageHandler 桩）+ 内存台账，
/// 不连数据库、不连真实网关。
/// </summary>
public class GraphIgwRegistrarTests
{
    private const string Ip = "192.168.1.20";
    private const long ProjectId = 77;

    private static readonly IReadOnlyList<TimeSpan> FastRetryDelays = new[]
    {
        TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
    };

    private static string UniqueUrl() => $"http://igw-graph-{Guid.NewGuid():N}.local:48020";

    private static GraphIgwProjectInfo Project(string url, string plcIp = Ip) =>
        new(ProjectId, "项目G", "线体G", "PLC9", url, plcIp, 102);

    private static GraphIgwDbInput Db(string name, string station, int maxBit = 3, params string[] previous) =>
        new(name, station, maxBit, true, previous);

    private static GraphIgwRegistrar Registrar(FakeIgw igw, MemoryLedgerStore store, IgwRegistrationConfig config = null, List<string> logs = null) =>
        new(new IgwBulkClient(NullLogger<IgwBulkClient>.Instance, igw, FastRetryDelays), store,
            text => { logs?.Add(text); return Task.CompletedTask; }, NullLogger.Instance, config ?? new IgwRegistrationConfig());

    [Fact]
    public async Task Registers_all_dbs_in_one_graph_request_writes_graph_ledger_and_invalidates_cache()
    {
        var igw = new FakeIgw();
        var store = new MemoryLedgerStore();
        // 静态事件被其它并行测试类触发：只记本项目，且用线程安全集合（旧写法并发 Add 会丢写入 / 与断言枚举冲突）
        var invalidated = new System.Collections.Concurrent.ConcurrentQueue<long>();
        void OnInvalidated(long id)
        {
            if (id == ProjectId)
            {
                invalidated.Enqueue(id);
            }
        }
        IgwAliasCacheInvalidation.Invalidated += OnInvalidated;
        try
        {
            var result = await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
                new[] { Db("DB161_HomeGraph", "OP010"), Db("DB162_Graph", null) }, CancellationToken.None);

            var request = Assert.Single(igw.BulkRequests);
            Assert.Equal("GRAPH", request.Device.Name);
            Assert.Equal("线体G_PLC9", request.Channel.Name);
            Assert.Equal(4, request.Items.Count);
            Assert.Equal(new[] { "线体G_PLC9" }, store.ChannelWrites);

            Assert.True(result.ByDb["DB161_HomeGraph"].Registered);
            Assert.True(result.ByDb["DB162_Graph"].Registered);

            Assert.Equal(2, store.Rows.Count);
            var row = store.Rows.Single(r => r.RootName == "OP010DB161_HomeGraph");
            Assert.Equal(IgwLedgerRootKind.Graph, row.RootKind);
            Assert.Equal("OP010", row.RootGroup);
            Assert.Equal("Graph", row.DeviceKind);
            Assert.Equal(2, row.TagCount);
            Assert.Equal(IgwLedgerStatus.Registered, row.Status);
            Assert.Contains(store.Rows, r => r.RootName == "_未分组DB162_Graph" && r.RootGroup == "_未分组");
            Assert.Contains(ProjectId, invalidated);
        }
        finally
        {
            IgwAliasCacheInvalidation.Invalidated -= OnInvalidated;
        }
    }

    [Fact]
    public async Task Station_rebind_uses_ledger_station_for_replace_groups_and_removes_old_ledger_root()
    {
        var igw = new FakeIgw();
        var store = new MemoryLedgerStore { ChannelName = "已存通道" };
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.Graph, RootName = "OP020|DB161_HomeGraph", RootGroup = "OP020",
            DeviceKind = "Graph", Status = IgwLedgerStatus.Registered,
        });

        await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010", 3, "OP030") }, CancellationToken.None);

        var request = Assert.Single(igw.BulkRequests);
        Assert.Equal("已存通道", request.Channel.Name);
        Assert.Empty(store.ChannelWrites);
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP020", "DB161_HomeGraph" }));
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP030", "DB161_HomeGraph" }));
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP010", "DB161_HomeGraph" }));
        Assert.Contains((IgwLedgerRootKind.Graph, "OP020|DB161_HomeGraph"), store.LastRemoved);
        var row = Assert.Single(store.Rows);
        Assert.Equal("OP010DB161_HomeGraph", row.RootName);
    }

    [Fact]
    public async Task Bulk_failure_marks_all_dbs_failed_and_keeps_old_ledger_station()
    {
        var igw = new FakeIgw
        {
            BulkResponder = _ => (HttpStatusCode.BadRequest, """{"error":"invalid_items","details":[{"index":0,"reason":"地址非法"}]}"""),
        };
        var store = new MemoryLedgerStore();
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.Graph, RootName = "OP020|DB161_HomeGraph", RootGroup = "OP020",
            DeviceKind = "Graph", Status = IgwLedgerStatus.Registered, TagCount = 2,
        });

        var result = await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010"), Db("DB162_Graph", "OP010") }, CancellationToken.None);

        Assert.False(result.ByDb["DB161_HomeGraph"].Registered);
        Assert.Contains("invalid_items", result.ByDb["DB161_HomeGraph"].Error);
        Assert.Contains("地址非法", result.ByDb["DB162_Graph"].Error);
        // 未写入 IGW：旧位置仍是真实状态，台账沿用旧工站键（读链继续按旧工站读）
        var kept = store.Rows.Single(r => r.RootName.EndsWith("|DB161_HomeGraph", StringComparison.Ordinal));
        Assert.Equal("OP020|DB161_HomeGraph", kept.RootName);
        Assert.Equal(IgwLedgerStatus.Failed, kept.Status);
        Assert.Equal(0, kept.TagCount);
        var fresh = store.Rows.Single(r => r.RootName == "OP010DB162_Graph");
        Assert.Equal(IgwLedgerStatus.Failed, fresh.Status);
        Assert.Empty(store.LastRemoved);
    }

    [Fact]
    public async Task Old_igw_without_bulk_endpoint_fails_every_db_with_upgrade_message()
    {
        var igw = new FakeIgw { ProbeStatus = HttpStatusCode.NotFound };
        var store = new MemoryLedgerStore();

        var result = await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None);

        Assert.False(result.ByDb["DB161_HomeGraph"].Registered);
        Assert.Contains("升级", result.ByDb["DB161_HomeGraph"].Error);
        Assert.Empty(igw.BulkRequests);
        Assert.Equal(0, store.LedgerWrites);
    }

    [Fact]
    public async Task Legacy_device_cleanup_runs_before_send_and_failure_aborts()
    {
        var igw = new FakeIgw();
        igw.LegacyDevices.Add(("dev-1", "S1500_" + Ip, "S7Plus_" + Ip, Ip + ":102"));
        var store = new MemoryLedgerStore();

        var ok = await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None);
        Assert.True(ok.ByDb["DB161_HomeGraph"].Registered);
        Assert.Contains("dev-1", igw.DeletedDeviceIds);
        var mutationIndex = igw.Requests.FindIndex(r => r.EndsWith("project/mutations", StringComparison.Ordinal));
        var bulkIndex = igw.Requests.FindLastIndex(r => r.EndsWith("bulk/tags", StringComparison.Ordinal));
        Assert.True(mutationIndex >= 0 && mutationIndex < bulkIndex);

        var failing = new FakeIgw { FailLegacyDelete = true };
        failing.LegacyDevices.Add(("dev-2", "S7Plus_" + Ip, "S7Plus_" + Ip, Ip));
        var failed = await Registrar(failing, new MemoryLedgerStore()).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None);
        Assert.False(failed.ByDb["DB161_HomeGraph"].Registered);
        Assert.Contains("清理旧设备失败", failed.ByDb["DB161_HomeGraph"].Error);
        Assert.Empty(failing.BulkRequests);
    }

    [Fact]
    public async Task Graph_cleanup_leaves_scalar_legacy_devices_to_the_db_registration()
    {
        // N-2：GRAPH 注册只清理逻辑名 S7Plus_{ip} 的旧设备；S1500_{ip}（标量）由 DB/IO 注册清理（其删除会触发全量升级）
        var igw = new FakeIgw();
        igw.LegacyDevices.Add(("dev-scalar", "S1500_" + Ip, "S1500_" + Ip, Ip + ":102"));
        igw.LegacyDevices.Add(("dev-graph", "S7Plus_" + Ip, "S7Plus_" + Ip, Ip + ":102"));

        var result = await Registrar(igw, new MemoryLedgerStore()).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None);

        Assert.True(result.ByDb["DB161_HomeGraph"].Registered);
        Assert.Equal(new HashSet<string> { "dev-graph" }, igw.DeletedDeviceIds);
    }

    [Fact]
    public async Task Missing_plc_ip_fails_without_calling_igw()
    {
        var igw = new FakeIgw();
        var result = await Registrar(igw, new MemoryLedgerStore()).RegisterAsync(Project(UniqueUrl(), plcIp: " "),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None);

        Assert.False(result.ByDb["DB161_HomeGraph"].Registered);
        Assert.Contains("plcip", result.ByDb["DB161_HomeGraph"].Error);
        Assert.Empty(igw.Requests);
    }

    [Fact]
    public async Task Split_requests_stop_after_first_failure()
    {
        var calls = 0;
        var igw = new FakeIgw
        {
            BulkResponder = _ => ++calls == 1
                ? (HttpStatusCode.OK, """{"applied":true,"revision":"r1"}""")
                : (HttpStatusCode.BadRequest, """{"error":"name_conflict","detail":"重名"}"""),
        };
        var store = new MemoryLedgerStore();
        var config = new IgwRegistrationConfig { MaxItemsPerRequest = 2 };

        var result = await Registrar(igw, store, config).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB1_Graph", "OP010"), Db("DB2_Graph", "OP010"), Db("DB3_Graph", "OP010") }, CancellationToken.None);

        Assert.Equal(2, igw.BulkRequests.Count);
        Assert.True(result.ByDb["DB1_Graph"].Registered);
        Assert.Contains("name_conflict", result.ByDb["DB2_Graph"].Error);
        Assert.Contains("未发送", result.ByDb["DB3_Graph"].Error);
        Assert.Equal(IgwLedgerStatus.Registered, store.Rows.Single(r => r.RootName == "OP010DB1_Graph").Status);
        Assert.Equal(IgwLedgerStatus.Failed, store.Rows.Single(r => r.RootName == "OP010DB3_Graph").Status);
    }

    // ------------------------------------------------------------------ 失效 GRAPH DB 清理（fix round 1）

    private static PLC_IgwRegistrationEntity GraphLedgerRow(string rootName, string station) => new()
    {
        RootKind = IgwLedgerRootKind.Graph, RootName = rootName, RootGroup = station,
        DeviceKind = "Graph", Status = IgwLedgerStatus.Registered, TagCount = 2,
    };

    [Fact]
    public async Task Full_run_clears_ledger_graph_dbs_no_longer_in_targets()
    {
        var igw = new FakeIgw();
        var store = new MemoryLedgerStore();
        store.Rows.Add(GraphLedgerRow("OP050|DB999_Unbound", "OP050"));
        store.Rows.Add(GraphLedgerRow("_未分组|DB998_Deleted", "_未分组"));
        store.Rows.Add(GraphLedgerRow("OP020|DB161_HomeGraph", "OP020"));

        await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None, removeStaleDbs: true);

        var request = Assert.Single(igw.BulkRequests);
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP050", "DB999_Unbound" }));
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "_未分组", "DB998_Deleted" }));
        // 改绑仍清旧组
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP020", "DB161_HomeGraph" }));
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP010", "DB161_HomeGraph" }));
        Assert.DoesNotContain(request.Items, i => i.GroupPath.Contains("DB999_Unbound"));

        Assert.Contains((IgwLedgerRootKind.Graph, "OP050|DB999_Unbound"), store.LastRemoved);
        Assert.Contains((IgwLedgerRootKind.Graph, "_未分组|DB998_Deleted"), store.LastRemoved);
        Assert.Contains((IgwLedgerRootKind.Graph, "OP020|DB161_HomeGraph"), store.LastRemoved);
        Assert.Equal(new[] { "OP010DB161_HomeGraph" }, store.Rows.Select(r => r.RootName));
    }

    [Fact]
    public async Task Stale_cleanup_is_skipped_for_scoped_runs_and_retained_dbs()
    {
        var igw = new FakeIgw();
        var store = new MemoryLedgerStore();
        store.Rows.Add(GraphLedgerRow("OP050|DB999_Other", "OP050"));
        store.Rows.Add(GraphLedgerRow("OP060|DB997_BuildFailed", "OP060"));

        // 范围收窄运行：其它 DB 不是"失效"，不清
        await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None);
        Assert.DoesNotContain(igw.BulkRequests[0].ReplaceGroups, g => g[1] == "DB999_Other" || g[1] == "DB997_BuildFailed");

        // 全量运行但 DB997 台账构建异常（retain）：只清 DB999
        await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None,
            removeStaleDbs: true, retainDbs: new[] { "db997_buildfailed" });
        var full = igw.BulkRequests[1];
        Assert.Contains(full.ReplaceGroups, g => g.SequenceEqual(new[] { "OP050", "DB999_Other" }));
        Assert.DoesNotContain(full.ReplaceGroups, g => g[1] == "DB997_BuildFailed");
        Assert.Contains(store.Rows, r => r.RootName == "OP060|DB997_BuildFailed");
        Assert.DoesNotContain(store.Rows, r => r.RootName == "OP050|DB999_Other");
    }

    [Fact]
    public async Task Stale_ledger_roots_are_kept_when_request_fails()
    {
        var igw = new FakeIgw { BulkResponder = _ => (HttpStatusCode.BadRequest, """{"error":"invalid_items"}""") };
        var store = new MemoryLedgerStore();
        store.Rows.Add(GraphLedgerRow("OP050|DB999_Unbound", "OP050"));

        await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            new[] { Db("DB161_HomeGraph", "OP010") }, CancellationToken.None, removeStaleDbs: true);

        Assert.Contains(store.Rows, r => r.RootName == "OP050|DB999_Unbound");
        Assert.Empty(store.LastRemoved);
    }

    [Fact]
    public async Task Zero_targets_full_run_sends_replace_only_request_and_removes_ledger_roots()
    {
        var igw = new FakeIgw();
        var store = new MemoryLedgerStore { ChannelName = "通道X" };
        store.Rows.Add(GraphLedgerRow("OP050|DB999_Unbound", "OP050"));
        store.Rows.Add(GraphLedgerRow("OP|60DB998_Pipe", "OP|60"));

        var result = await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            Array.Empty<GraphIgwDbInput>(), CancellationToken.None, removeStaleDbs: true);

        var request = Assert.Single(igw.BulkRequests);
        Assert.Empty(request.Items);
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP050", "DB999_Unbound" }));
        Assert.Contains(request.ReplaceGroups, g => g.SequenceEqual(new[] { "OP|60", "DB998_Pipe" }));
        Assert.Contains((IgwLedgerRootKind.Graph, "OP050|DB999_Unbound"), store.LastRemoved);
        Assert.Contains((IgwLedgerRootKind.Graph, "OP|60DB998_Pipe"), store.LastRemoved);
        Assert.Empty(store.Rows);
        Assert.Empty(result.ByDb);
    }

    [Fact]
    public async Task Zero_targets_without_stale_roots_makes_no_igw_calls()
    {
        var igw = new FakeIgw();
        var store = new MemoryLedgerStore();

        await Registrar(igw, store).RegisterAsync(Project(UniqueUrl()),
            Array.Empty<GraphIgwDbInput>(), CancellationToken.None, removeStaleDbs: true);

        Assert.Empty(igw.Requests);
        Assert.Equal(0, store.LedgerWrites);
    }

    // ------------------------------------------------------------------ 桩

    private sealed class MemoryLedgerStore : IIgwLedgerStore
    {
        public string ChannelName { get; set; }
        public List<string> ChannelWrites { get; } = new();
        public List<PLC_IgwRegistrationEntity> Rows { get; } = new();
        public List<(string RootKind, string RootName)> LastRemoved { get; private set; } = new();
        public int LedgerWrites { get; private set; }

        public Task<string> GetChannelNameAsync(long projectId) => Task.FromResult(ChannelName);

        public Task SetChannelNameAsync(long projectId, string channelName)
        {
            ChannelName = channelName;
            ChannelWrites.Add(channelName);
            return Task.CompletedTask;
        }

        public Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId) => Task.FromResult(IgwLedgerSnapshotBuilder.Build(Rows));

        public Task ReplaceLedgerAsync(long projectId, IReadOnlyList<PLC_IgwRegistrationEntity> rows,
            IReadOnlyList<(string RootKind, string RootName)> removedRoots)
        {
            LedgerWrites++;
            LastRemoved = removedRoots.ToList();
            var keys = rows.Select(r => (r.RootKind, r.RootName)).Concat(removedRoots).ToHashSet();
            Rows.RemoveAll(r => keys.Contains((r.RootKind, r.RootName)));
            foreach (var row in rows)
            {
                row.ProjectId = projectId;
                Rows.Add(row);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIgw : HttpMessageHandler
    {
        public HttpStatusCode ProbeStatus { get; set; } = HttpStatusCode.BadRequest;
        public Func<IgwBulkRequest, (HttpStatusCode Status, string Body)> BulkResponder { get; set; } =
            _ => (HttpStatusCode.OK, """{"applied":true,"revision":"r1"}""");
        public List<IgwBulkRequest> BulkRequests { get; } = new();
        public List<string> Requests { get; } = new();
        public List<(string Id, string Name, string Logical, string Endpoint)> LegacyDevices { get; } = new();
        public HashSet<string> DeletedDeviceIds { get; } = new();
        public bool FailLegacyDelete { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");

            if (path.EndsWith("api/v1/project/bulk/tags", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (body == "{}")
                {
                    return ProbeStatus == HttpStatusCode.BadRequest
                        ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_items"}""")
                        : new HttpResponseMessage(ProbeStatus);
                }
                var bulk = JsonSerializer.Deserialize<IgwBulkRequest>(body, IgwBulkJson.Options);
                BulkRequests.Add(bulk);
                var (status, responseBody) = BulkResponder(bulk);
                return Json(status, responseBody);
            }
            if (path.EndsWith("project/summary", StringComparison.Ordinal))
            {
                var response = Json(HttpStatusCode.OK, """{"root":{"id":"root-1","kind":"project","name":"root"}}""");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rev-1\"");
                return response;
            }
            if (path.Contains("project/tree/project/root-1/children", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, LegacyDevices.Count == 0
                    ? "[]"
                    : """[{"id":"ch-1","kind":"channel","name":"通用_S7Plus"}]""");
            }
            if (path.Contains("project/tree/channel/ch-1/children", StringComparison.Ordinal))
            {
                var items = LegacyDevices.Select(d => $$"""{"id":"{{d.Id}}","kind":"device","name":"{{d.Name}}"}""");
                return Json(HttpStatusCode.OK, "[" + string.Join(",", items) + "]");
            }
            const string entityPrefix = "project/entity/device/";
            if (path.Contains(entityPrefix, StringComparison.Ordinal))
            {
                var id = path.Substring(path.IndexOf(entityPrefix, StringComparison.Ordinal) + entityPrefix.Length);
                var device = LegacyDevices.Single(d => d.Id == id);
                return Json(HttpStatusCode.OK,
                    $$$"""{"id":"{{{id}}}","endpoint":"{{{device.Endpoint}}}","settings":{"dcsLogicalDevice":"{{{device.Logical}}}"}}""");
            }
            if (path.EndsWith("project/mutations", StringComparison.Ordinal))
            {
                if (FailLegacyDelete)
                {
                    return Json(HttpStatusCode.InternalServerError, """{"error":"persistence_failed","detail":"模拟删除失败"}""");
                }
                using var doc = JsonDocument.Parse(body);
                foreach (var el in doc.RootElement.GetProperty("entityIds").EnumerateArray())
                {
                    DeletedDeviceIds.Add(el.GetString());
                }
                return Json(HttpStatusCode.OK, "{}");
            }
            throw new InvalidOperationException($"FakeIgw 未预期的请求 {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
