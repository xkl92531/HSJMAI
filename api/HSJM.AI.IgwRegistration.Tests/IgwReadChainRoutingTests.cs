using System.Reflection;
using System.Text.Json;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// task-8：PLC_VariableAccessService.ReadExactDataAsync 在新 IGW 项目上按台账路由读别名
/// （IO 表点位 / DB 变量 / GRAPH 容器 / GRAPH 工步位），台账读取失败回退，缓存失效。
/// 依赖均为 DispatchProxy 假实现，不连数据库、不连网关。
/// </summary>
public class IgwReadChainRoutingTests
{
    private const string Ip = "10.0.0.1";
    private const string IotUrl = "http://127.0.0.1:48020";

    static IgwReadChainRoutingTests()
    {
        // AbstractBaseService 基类构造从 CommonServiceProvider 取 ISqlSugarClient（此处为 null，GRAPH 工站查询会失败并回退）
        CommonServiceProvider.ServiceProvider ??= new ServiceCollection().BuildServiceProvider();
    }

    // ---------- DispatchProxy 假实现 ----------

    public class Fake : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler;

        protected override object Invoke(MethodInfo targetMethod, object[] args) =>
            Handler?.Invoke(targetMethod, args) ?? throw new NotSupportedException(targetMethod.Name);

        public static T Create<T>(Func<MethodInfo, object[], object> handler)
        {
            var proxy = DispatchProxy.Create<T, Fake>();
            ((Fake)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    private sealed class Harness
    {
        public readonly List<List<string>> ReadBatches = new();
        public Func<IgwLedgerSnapshot> Ledger = () => IgwLedgerSnapshot.Empty;
        public int LedgerLoads;
        public int LedgerDelayMs;
        public PLC_VariableAccessService Service;

        public Harness(long projectId, IReadOnlyList<PLC_IOTagEntity> candidates)
        {
            var project = new PLC_ProjectEntity { Id = projectId, StationId = "PLC1", IotUrl = IotUrl, PlcIp = Ip, IsDeleted = false };
            var projectService = Fake.Create<IPLC_ProjectService>((m, a) =>
                m.Name == nameof(IPLC_ProjectService.GetById) ? project : null);
            var iot = Fake.Create<IIotService>((m, a) =>
            {
                if (m.Name != nameof(IIotService.ReadTagsAsList) && m.Name != nameof(IIotService.ReadTagsWithQualityAsync))
                {
                    return null;
                }
                var aliases = ((List<string>)a[1]).ToList();
                ReadBatches.Add(aliases);
                var rows = aliases.Select(alias => new PLC_ReadTagResult
                {
                    TagName = alias,
                    IsGood = true,
                    Value = alias.EndsWith(".EXEC_BITS", StringComparison.Ordinal)
                        ? JsonDocument.Parse("[false,true,false]").RootElement.Clone()
                        : (object)true,
                }).ToList();
                if (m.Name == nameof(IIotService.ReadTagsWithQualityAsync))
                    return Task.FromResult(new IgwTransportResult<List<DcsTagQualityItem>> { IsHttpSuccess = true,
                        HttpStatusCode = System.Net.HttpStatusCode.OK,
                        Envelope = new HttpResponseResultModel<List<DcsTagQualityItem>> { IsSuccess = true,
                            BackResult = rows.Select(r => new DcsTagQualityItem { Key = r.TagName, Value = r.Value,
                                IsGood = r.IsGood, Status = "Good", TimestampUtc = DateTime.UtcNow }).ToList() } });
                return Task.FromResult(new HttpResponseResultModel<List<PLC_ReadTagResult>> { IsSuccess = true, BackResult = rows });
            });
            var ledger = Fake.Create<IPLC_IgwRegistrationService>((m, a) =>
            {
                if (m.Name == nameof(IPLC_IgwRegistrationService.GetLedgerForReadAsync))
                {
                    Interlocked.Increment(ref LedgerLoads);
                    return Task.Run(async () =>
                    {
                        await Task.Delay(LedgerDelayMs);
                        return Ledger();
                    });
                }
                // 读链不得走带建表 DDL 的 GetLedgerAsync / EnsureLedgerSchemaAsync
                throw new InvalidOperationException("unexpected call " + m.Name);
            });
            Service = new PLC_VariableAccessService(projectService, iot,
                (_, _, _) => Task.FromResult(candidates), ledger);
        }

        public List<string> AllAliases => ReadBatches.SelectMany(b => b).ToList();
    }

    private static PLC_IOTagEntity IoTag(long pid, string table, string tag) =>
        new() { ProjectId = pid, TableName = table, TagName = tag, DataType = "Bool", IsDeleted = false };

    private static IgwLedgerSnapshot Ledger(IgwDeviceKind db490Kind, string graphStation) =>
        new(
            new Dictionary<string, IgwDeviceKind>(StringComparer.OrdinalIgnoreCase) { ["DB490"] = db490Kind },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DB490"] = "DB490" },
            new Dictionary<(string, string), string>(),
            new Dictionary<(string, string), string> { [("默认变量表", "I1.0_X")] = "I1_0_X_2" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DB160_MainGraph"] = graphStation });

    [Fact]
    public async Task Routes_io_db_graph_container_and_step_bits_by_ledger()
    {
        const long pid = 880001;
        var candidates = new List<PLC_IOTagEntity>
        {
            IoTag(pid, "默认变量表", "I1.0_X"),
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "Type.Robot.Robot[2].X", "Bool"),
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB160_MainGraph", "S_NO", "Int"),
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB160_MainGraph", "RT_DATA.EXEC_BITS.EXEC_BITS[1]", "Bool"),
        };
        var h = new Harness(pid, candidates) { Ledger = () => Ledger(IgwDeviceKind.S7, "OP010") };

        var results = await h.Service.ReadExactDataAsync(pid, new[]
        {
            new PLC_ExactReadRequest("io", "默认变量表", "I1.0_X"),
            new PLC_ExactReadRequest("db", "DB490", "Type.Robot.Robot[2].X"),
            new PLC_ExactReadRequest("sno", "DB160_MainGraph", "S_NO"),
            new PLC_ExactReadRequest("bit", "DB160_MainGraph", "RT_DATA.EXEC_BITS.EXEC_BITS[1]"),
        }, CancellationToken.None);

        var aliases = h.AllAliases;
        Assert.Contains("通用.S7_10.0.0.1.IO.默认变量表.I1_0_X_2", aliases);
        Assert.Contains("通用.S7_10.0.0.1.DB490.Type.Robot[2].X", aliases);
        Assert.Contains("通用.S7Plus_10.0.0.1.OP010.DB160_MainGraph.S_NO", aliases);
        Assert.Contains("通用.S7Plus_10.0.0.1.OP010.DB160_MainGraph.RT_DATA.EXEC_BITS", aliases);
        Assert.All(results, r => Assert.Equal(PLC_ExactReadQuality.Good, r.Quality));
        Assert.Equal(true, results.Single(r => r.RequestKey == "bit").Value);
    }

    [Fact]
    public async Task Ledger_failure_falls_back_without_failing_the_read()
    {
        const long pid = 880002;
        var candidates = new List<PLC_IOTagEntity>
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "Type.Robot.Robot[2].X", "Bool"),
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB161_HomeGraph", "S_NO", "Int"),
        };
        var h = new Harness(pid, candidates) { Ledger = () => throw new InvalidOperationException("db down") };

        var results = await h.Service.ReadExactDataAsync(pid, new[]
        {
            new PLC_ExactReadRequest("db", "DB490", "Type.Robot.Robot[2].X"),
            new PLC_ExactReadRequest("sno", "DB161_HomeGraph", "S_NO"),
        }, CancellationToken.None);

        Assert.Contains("通用.S1500_10.0.0.1.DB490.Type.Robot[2].X", h.AllAliases);
        Assert.Contains("通用.S7Plus_10.0.0.1._未分组.DB161_HomeGraph.S_NO", h.AllAliases);
        Assert.All(results, r => Assert.Equal(PLC_ExactReadQuality.Good, r.Quality));
    }

    [Fact]
    public async Task Ledger_is_cached_until_invalidated()
    {
        const long pid = 880003;
        var candidates = new List<PLC_IOTagEntity>
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool"),
        };
        var kind = IgwDeviceKind.S7;
        var h = new Harness(pid, candidates) { Ledger = () => Ledger(kind, "OP010") };
        var request = new[] { new PLC_ExactReadRequest("db", "DB490", "A") };

        await h.Service.ReadExactDataAsync(pid, request, CancellationToken.None);
        kind = IgwDeviceKind.S7Plus;
        await h.Service.ReadExactDataAsync(pid, request, CancellationToken.None);
        IgwAliasCacheInvalidation.Invalidate(pid);
        await h.Service.ReadExactDataAsync(pid, request, CancellationToken.None);

        Assert.Equal("通用.S7_10.0.0.1.DB490.A", Assert.Single(h.ReadBatches[0]));
        Assert.Equal("通用.S7_10.0.0.1.DB490.A", Assert.Single(h.ReadBatches[1]));
        Assert.Equal("通用.S1500_10.0.0.1.DB490.A", Assert.Single(h.ReadBatches[2]));
    }

    [Fact]
    public async Task Concurrent_reads_load_the_ledger_once()
    {
        const long pid = 880004;
        var candidates = new List<PLC_IOTagEntity>
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool"),
        };
        var h = new Harness(pid, candidates) { Ledger = () => Ledger(IgwDeviceKind.S7, "OP010"), LedgerDelayMs = 100 };
        var request = new[] { new PLC_ExactReadRequest("db", "DB490", "A") };

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => h.Service.ReadExactDataAsync(pid, request, CancellationToken.None)));

        Assert.Equal(1, h.LedgerLoads);
        Assert.All(h.ReadBatches, b => Assert.Equal("通用.S7_10.0.0.1.DB490.A", Assert.Single(b)));
    }

    [Fact]
    public async Task Invalidate_during_load_is_not_overwritten_by_the_stale_result()
    {
        const long pid = 880005;
        var candidates = new List<PLC_IOTagEntity>
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool"),
        };
        var kind = IgwDeviceKind.S7;
        var h = new Harness(pid, candidates) { Ledger = () => Ledger(kind, "OP010"), LedgerDelayMs = 200 };
        var request = new[] { new PLC_ExactReadRequest("db", "DB490", "A") };

        var first = h.Service.ReadExactDataAsync(pid, request, CancellationToken.None);
        await Task.Delay(50);                      // 加载进行中（快照仍按 S7 生成）
        kind = IgwDeviceKind.S7Plus;
        IgwAliasCacheInvalidation.Invalidate(pid); // 失效发生在加载期间
        await first;
        h.LedgerDelayMs = 0;
        await h.Service.ReadExactDataAsync(pid, request, CancellationToken.None);

        Assert.Equal(2, h.LedgerLoads);
        Assert.Equal("通用.S1500_10.0.0.1.DB490.A", Assert.Single(h.ReadBatches[^1]));
    }
}
