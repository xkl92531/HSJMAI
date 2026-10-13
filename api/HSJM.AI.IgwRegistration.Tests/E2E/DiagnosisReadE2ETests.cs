using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.DbService.Service.IOT;
using HSJM.AI.IDbService;
using HSJM.AI.IgwRegistration.Tests.DiagnosisRead;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.E2E;

/// <summary>
/// 诊断读取端到端单独成一个禁止并行的集合：xunit 在所有可并行集合跑完之后才执行它，
/// 避免与 <see cref="IgwBulkE2ETests"/>（核对 summary.totalTagCount、全部设备清单）在同一临时 IGW 上互相干扰。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosisReadE2ECollection
{
    public const string Name = "DiagnosisReadE2E";
}

/// <summary>
/// D-1 端到端：临时 IGW（tools/igw_bulk_e2e/run_e2e.ps1，IGW master ≥ 1.6.78）上验证诊断读取的新接口与旧接口回退。
/// 未设置 IGW_E2E_URL 时跳过。
/// </summary>
[Trait("Category", "E2E")]
[Collection(DiagnosisReadE2ECollection.Name)]
public sealed class DiagnosisReadE2ETests
{
    private const long ProjectId = 9_100_001;
    private const string Ip = "192.0.2.31";
    private const string Table = "诊断表";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static E2EMemoryStore _store;

    private readonly ITestOutputHelper _output;

    static DiagnosisReadE2ETests()
    {
        CommonServiceProvider.ServiceProvider ??= new ServiceCollection().BuildServiceProvider();
    }

    public DiagnosisReadE2ETests(ITestOutputHelper output) => _output = output;

    private static string Url => Environment.GetEnvironmentVariable(IgwE2EFactAttribute.UrlVariable);

    private static IotService RealIot() =>
        new(new RequestToHttpHelper(new StubHttpClientFactory(new HttpClientHandler())));

    /// <summary>各用例共用一次注册（生产编排 + 内存台账）。</summary>
    private static async Task<E2EMemoryStore> EnsureRegisteredAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_store != null)
            {
                return _store;
            }
            var store = new E2EMemoryStore
            {
                ChannelName = "E2E_DIAG",
                IoTags = new List<PlannerIoTag>
                {
                    new(Table, "A_In", "%I0.0", "Bool", "诊断输入"),
                    new(Table, "B_Out", "%Q0.1", "Bool", "诊断输出"),
                },
            };
            using var client = new IgwBulkClient(NullLogger<IgwBulkClient>.Instance);
            var runner = new IgwRegistrationRunner(client, store, _ => Task.CompletedTask, NullLogger.Instance,
                new IgwRegistrationConfig());
            var outcome = await runner.RunAsync(
                new IgwRegistrationProjectInfo(ProjectId, "E2E_DIAG", "E2E线体", "DIAG", Url, Ip, 102),
                new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), true, true, null), ct);
            Assert.True(outcome.Status == IgwRegistrationStatus.Succeeded, outcome.Detail);
            _store = store;
            return store;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static PLC_IOTagEntity IoTag(string tag) =>
        new() { ProjectId = ProjectId, TableName = Table, TagName = tag, DataType = "Bool", IsDeleted = false };

    /// <summary>真实 IotService + 内存台账的诊断读取服务（点集合：已注册 A_In、B_Out + 未注册 Ghost）。</summary>
    private static PLC_VariableAccessService RealDiagnosticReadService(E2EMemoryStore store)
    {
        var project = new PLC_ProjectEntity { Id = ProjectId, StationId = "DIAG", IotUrl = Url, PlcIp = Ip, IsDeleted = false };
        var projectService = Fake.Create<IPLC_ProjectService>((m, a) =>
            m.Name == nameof(IPLC_ProjectService.GetById) ? project : null);
        var ledger = Fake.Create<IPLC_IgwRegistrationService>((m, a) =>
            m.Name == nameof(IPLC_IgwRegistrationService.GetLedgerForReadAsync)
                ? store.GetLedgerAsync(ProjectId)
                : throw new InvalidOperationException("unexpected call " + m.Name));
        IReadOnlyList<PLC_IOTagEntity> tags = new List<PLC_IOTagEntity> { IoTag("A_In"), IoTag("B_Out"), IoTag("Ghost") };
        return new PLC_VariableAccessService(projectService, RealIot(),
            (_, _, _) => Task.FromResult<IReadOnlyList<PLC_IOTagEntity>>(Array.Empty<PLC_IOTagEntity>()),
            ledger,
            (_, _) => Task.FromResult(tags),
            // 无库：GRAPH 工站回退表给空表（否则默认实现 GetDb() 空引用，日志里打出 NullReferenceException 告警）
            graphStationLoader: (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [IgwE2EFact]
    public async Task A_Quality_endpoint_reports_registered_points_as_bad_and_ghost_as_not_registered()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var store = await EnsureRegisteredAsync(cts.Token);
        var service = RealDiagnosticReadService(store);

        var result = await service.ReadDiagnosticSignalsAsync(ProjectId, cts.Token);

        foreach (var signal in result.Signals)
        {
            E2EResults.Write(_output, $"- 诊断读（新接口） {signal.TagName}: {signal.Status} {signal.Detail}");
        }
        var byKey = result.Signals.ToDictionary(s => s.TagName);
        Assert.Equal(PLC_SignalReadStatus.NotRegistered, byKey[Table + ".Ghost"].Status);
        foreach (var registered in new[] { Table + ".A_In", Table + ".B_Out" })
        {
            Assert.NotEqual(PLC_SignalReadStatus.Good, byKey[registered].Status);
            Assert.NotEqual(PLC_SignalReadStatus.NotRegistered, byKey[registered].Status);
            Assert.Null(byKey[registered].Value);
        }
        Assert.Equal(0, result.GoodCount);
    }

    [IgwE2EFact]
    public async Task B_Legacy_list_fallback_locates_bad_points_on_real_igw()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var store = await EnsureRegisteredAsync(cts.Token);
        var snapshot = await store.GetLedgerAsync(ProjectId);
        var aliasA = IgwReadAliasResolver.ResolveIo(Ip, snapshot, Table, "A_In");
        var aliasGhost = IgwReadAliasResolver.ResolveIo(Ip, snapshot, Table, "Ghost");
        var reader = new IgwDiagnosticReader(RealIot(), useQualityEndpoint: false);

        // Ghost 放在前面：旧接口一次只报第一个坏点。若先定位到 A（BadNotConnected = 通讯故障），读取器按
        // "设备级故障"把本批其余未定位点统一标 CommFailure、不再逐点定位（有界请求数的既定取舍），
        // Ghost 就拿不到「点位不存在」这一真实原因。
        var readings = await reader.ReadAsync(Url, new List<string> { aliasGhost, aliasA }, cts.Token);

        E2EResults.Write(_output, $"- 诊断读（旧接口回退） A: {readings[aliasA].Status} {readings[aliasA].Detail}");
        E2EResults.Write(_output, $"- 诊断读（旧接口回退） Ghost: {readings[aliasGhost].Status} {readings[aliasGhost].Detail}");
        Assert.Equal(PLC_SignalReadStatus.NotRegistered, readings[aliasGhost].Status);
        Assert.NotEqual(PLC_SignalReadStatus.Good, readings[aliasA].Status);
    }

    [IgwE2EFact]
    public async Task C_Energy_flow_report_on_real_readings_says_collect_failed_not_normal()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var store = await EnsureRegisteredAsync(cts.Token);
        var service = RealDiagnosticReadService(store);
        var h = new DiagnosisAppHarness();
        h.AddPlc(ProjectId, "E2E_DIAG");
        h.AddIoTag(ProjectId, Table, "A_In", "%I0.0");
        h.AddIoTag(ProjectId, Table, "B_Out", "%Q0.1");
        h.AddRule(ProjectId, "IMPLIES", "E2E_B_Out_驱动_A_In", ("B_Out", "Source"), ("A_In", "Target"));
        h.Reads[ProjectId] = () => service.ReadDiagnosticSignalsAsync(ProjectId, cts.Token);

        var result = await h.Build().DiagnoseEnergyFlowAsync(
            new EnergyFlowDiagnosticRequestDto { ProductionLineId = 1 }, null, cts.Token);

        foreach (var line in (result.BackResult ?? string.Empty).Split('\n').Where(l => l.Contains("采集失败") || l.Contains("PLC ")))
        {
            E2EResults.Write(_output, "- 诊断报告 " + line.Trim());
        }
        Assert.True(result.IsSuccess);
        Assert.Contains("采集失败，无法诊断", result.BackResult);
        Assert.DoesNotContain("未发现任何异常", result.BackResult);
    }
}
