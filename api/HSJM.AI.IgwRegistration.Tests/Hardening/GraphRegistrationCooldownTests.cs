using System.Net;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>N-6：GRAPH 注册写台账前失败 → 记失败时间；DB/IO 注册自动重请求在冷却期内跳过。</summary>
public class GraphRegistrationCooldownTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0);

    private static MemoryRegistrationStore PopulatedStoreWithGraphTargets()
    {
        var store = new MemoryRegistrationStore
        {
            HasGraphTargets = true,
            Data = new IgwRegistrationData(new[] { "DB1" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0) }, new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.Io, "IO", "S7", IgwLedgerStatus.Registered);
        return store;
    }

    private static IgwRegistrationArgs ChangeDb1() => new(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

    private static MemoryProjectStateStore FailedGraphAt(DateTime at) => new()
    {
        State = new IgwRegistrationStateRow { ProjectId = ProjectId, GraphFailedAt = at, GraphFailure = "IGW 版本过低" },
    };

    [Fact]
    public async Task Graph_request_is_skipped_during_cooldown()
    {
        var graph = new List<(long, string)>();

        var outcome = await Runner(new FakeIgwServer(), PopulatedStoreWithGraphTargets(), graphRequests: graph,
            state: FailedGraphAt(Now.AddMinutes(-5)), now: () => Now).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Empty(graph);
        Assert.Contains("分钟内不自动重试", outcome.Detail);
    }

    [Fact]
    public async Task Graph_request_resumes_after_cooldown()
    {
        var graph = new List<(long, string)>();

        await Runner(new FakeIgwServer(), PopulatedStoreWithGraphTargets(), graphRequests: graph,
            state: FailedGraphAt(Now.AddMinutes(-31)), now: () => Now).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Single(graph);
    }

    [Fact]
    public async Task Explicit_full_registration_ignores_cooldown()
    {
        var graph = new List<(long, string)>();
        var full = new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), true, true, null);

        await Runner(new FakeIgwServer(), PopulatedStoreWithGraphTargets(), graphRequests: graph,
            state: FailedGraphAt(Now.AddMinutes(-1)), now: () => Now).RunAsync(Project(UniqueUrl()), full, CancellationToken.None);

        Assert.Single(graph);
    }

    [Fact]
    public async Task Removed_legacy_graph_device_ignores_cooldown()
    {
        var igw = new FakeIgwServer();
        igw.AddDevice("通用_S7Plus", "旧GRAPH", "S7Plus_" + Ip, Ip + ":102", managedBy: null);
        var graph = new List<(long, string)>();

        await Runner(igw, PopulatedStoreWithGraphTargets(), graphRequests: graph,
            state: FailedGraphAt(Now.AddMinutes(-1)), now: () => Now).RunAsync(Project(UniqueUrl()), ChangeDb1(), CancellationToken.None);

        Assert.Single(graph);
    }

    [Fact]
    public async Task Registrar_records_failure_before_ledger_and_clears_it_after_success()
    {
        var state = new MemoryProjectStateStore();
        var store = new MemoryRegistrationStore();
        var db = new GraphIgwDbInput("DB161_HomeGraph", "OP010", 3, true, Array.Empty<string>());

        var old = new FakeIgwServer { ProbeStatus = HttpStatusCode.NotFound };
        await Registrar(old, store, state, () => Now).RegisterAsync(
            new GraphIgwProjectInfo(ProjectId, "项目H", "线体A", "PLC1", UniqueUrl(), Ip, 102), new[] { db }, CancellationToken.None);

        Assert.Equal(Now, state.State.GraphFailedAt);
        Assert.Contains("版本过低", state.State.GraphFailure);

        await Registrar(new FakeIgwServer(), store, state, () => Now).RegisterAsync(
            new GraphIgwProjectInfo(ProjectId, "项目H", "线体A", "PLC1", UniqueUrl(), Ip, 102), new[] { db }, CancellationToken.None);

        Assert.Null(state.State.GraphFailedAt);
        Assert.Contains(store.Rows, r => r.RootKind == IgwLedgerRootKind.Graph);
    }

    [Fact]
    public void Graph_cooldown_defaults_to_30_minutes() => Assert.Equal(30, new IgwRegistrationConfig().GraphRetryCooldownMinutes);
}
