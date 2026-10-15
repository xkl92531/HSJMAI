using System.Text.Json;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>精确读链：台账读取失败时拒读可能被改名的 DB / IO 点，其余点照常读；台账正常时不加载判定数据。</summary>
public class IgwReadChainOutageGuardTests
{
    static IgwReadChainOutageGuardTests()
    {
        CommonServiceProvider.ServiceProvider ??= new ServiceCollection().BuildServiceProvider();
    }

    private sealed class Harness
    {
        public readonly List<string> Aliases = new();
        public int GuardLoads;
        public int IoGuardLoads;
        public int DbGuardLoads;
        public readonly PLC_VariableAccessService Service;

        public Harness(long pid, IReadOnlyList<PLC_IOTagEntity> candidates, IReadOnlyList<PLC_IOTagEntity> allIo = null,
            IReadOnlyList<string> liveBlocks = null, IReadOnlyList<(string Block, string VariablePath)> blockPaths = null,
            bool ledgerFails = true, bool guardFails = false)
        {
            var project = new PLC_ProjectEntity { Id = pid, StationId = "PLC1", IotUrl = "http://127.0.0.1:48020", PlcIp = "10.0.0.1", IsDeleted = false };
            var projectService = Fake.Create<IPLC_ProjectService>((m, a) => m.Name == nameof(IPLC_ProjectService.GetById) ? project : null);
            var iot = Fake.Create<IIotService>((m, a) =>
            {
                if (m.Name != nameof(IIotService.ReadTagsAsList) && m.Name != nameof(IIotService.ReadTagsWithQualityAsync))
                {
                    return null;
                }
                var aliases = ((List<string>)a[1]).ToList();
                lock (Aliases)
                {
                    Aliases.AddRange(aliases);
                }
                var rows = aliases.Select(alias => new PLC_ReadTagResult
                {
                    TagName = alias,
                    IsGood = true,
                    Value = alias.EndsWith(".EXEC_BITS", StringComparison.Ordinal)
                        ? JsonDocument.Parse("[false,true]").RootElement.Clone()
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
                m.Name == nameof(IPLC_IgwRegistrationService.GetLedgerForReadAsync)
                    ? ledgerFails
                        ? Task.FromException<IgwLedgerSnapshot>(new InvalidOperationException("db down"))
                        : Task.FromResult(IgwLedgerSnapshot.Empty)
                    : throw new InvalidOperationException("unexpected call " + m.Name));
            var io = allIo ?? Array.Empty<PLC_IOTagEntity>();
            var live = liveBlocks ?? Array.Empty<string>();
            var paths = blockPaths ?? Array.Empty<(string, string)>();
            Service = new PLC_VariableAccessService(projectService, iot, (_, _, _) => Task.FromResult(candidates), ledger,
                fullIoTagLoader: (_, _) =>
                {
                    Interlocked.Increment(ref GuardLoads);
                    Interlocked.Increment(ref IoGuardLoads);
                    return guardFails
                        ? Task.FromException<IReadOnlyList<PLC_IOTagEntity>>(new InvalidOperationException("io table down"))
                        : Task.FromResult(io);
                },
                liveDbBlockLoader: (_, _) =>
                {
                    Interlocked.Increment(ref GuardLoads);
                    Interlocked.Increment(ref DbGuardLoads);
                    return guardFails
                        ? Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("block table down"))
                        : Task.FromResult(live);
                },
                blockPathLoader: (_, blocks, _) =>
                {
                    Interlocked.Increment(ref GuardLoads);
                    Interlocked.Increment(ref DbGuardLoads);
                    return guardFails
                        ? Task.FromException<IReadOnlyList<(string Block, string VariablePath)>>(new InvalidOperationException("interface table down"))
                        : Task.FromResult<IReadOnlyList<(string Block, string VariablePath)>>(
                            paths.Where(p => blocks.Contains(p.Block, StringComparer.OrdinalIgnoreCase)).ToList());
                });
        }
    }

    private static PLC_IOTagEntity IoTag(long pid, string table, string tag) =>
        new() { ProjectId = pid, TableName = table, TagName = tag, DataType = "Bool", IsDeleted = false };

    [Fact]
    public async Task Colliding_db_points_are_refused_while_the_ledger_is_unavailable()
    {
        const long pid = 881001;
        var h = new Harness(pid,
            new[] { PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "Flag", "Bool"), PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "Ok", "Bool") },
            liveBlocks: new[] { "DB490" },
            blockPaths: new[] { ("DB490", "Flag"), ("DB490", "flag"), ("DB490", "Ok") });

        var results = await h.Service.ReadExactDataAsync(pid, new[]
        {
            new PLC_ExactReadRequest("flag", "DB490", "Flag"),
            new PLC_ExactReadRequest("ok", "DB490", "Ok"),
        }, CancellationToken.None);

        var refused = results.Single(r => r.RequestKey == "flag");
        Assert.Equal(PLC_ExactReadQuality.Unknown, refused.Quality);
        Assert.Equal(PLC_VariableAccessService.LedgerOutageRefusedCode, refused.ErrorCode);
        Assert.Equal(PLC_ExactReadQuality.Good, results.Single(r => r.RequestKey == "ok").Quality);
        Assert.DoesNotContain(h.Aliases, a => a.EndsWith(".DB490.Flag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Points_of_blocks_whose_root_may_be_renamed_are_refused()
    {
        const long pid = 881002;
        var h = new Harness(pid, new[] { PLC_VariableAccessService.SynthesizeCandidate(pid, "DB_A", "V", "Bool") },
            liveBlocks: new[] { "DB.A", "DB_A" }, blockPaths: new[] { ("DB_A", "V") });

        var result = Assert.Single(await h.Service.ReadExactDataAsync(pid,
            new[] { new PLC_ExactReadRequest("v", "DB_A", "V") }, CancellationToken.None));

        Assert.Equal(PLC_VariableAccessService.LedgerOutageRefusedCode, result.ErrorCode);
        Assert.Empty(h.Aliases);
    }

    [Fact]
    public async Task Colliding_io_points_are_refused_and_others_read()
    {
        const long pid = 881003;
        var candidates = new[] { IoTag(pid, "表1", "I1.0_X"), IoTag(pid, "表1", "Start") };
        var h = new Harness(pid, candidates, allIo: new[] { IoTag(pid, "表1", "I1.0_X"), IoTag(pid, "表1", "I1_0_X"), IoTag(pid, "表1", "Start") });

        var results = await h.Service.ReadExactDataAsync(pid, new[]
        {
            new PLC_ExactReadRequest("x", "表1", "I1.0_X"),
            new PLC_ExactReadRequest("start", "表1", "Start"),
        }, CancellationToken.None);

        Assert.Equal(PLC_VariableAccessService.LedgerOutageRefusedCode, results.Single(r => r.RequestKey == "x").ErrorCode);
        Assert.Equal(PLC_ExactReadQuality.Good, results.Single(r => r.RequestKey == "start").Quality);
    }

    [Fact]
    public async Task Guard_data_failure_refuses_db_and_io_but_still_reads_graph_containers()
    {
        const long pid = 881004;
        var h = new Harness(pid, new[]
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool"),
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB161_HomeGraph", "S_NO", "Int"),
        }, guardFails: true);

        var results = await h.Service.ReadExactDataAsync(pid, new[]
        {
            new PLC_ExactReadRequest("db", "DB490", "A"),
            new PLC_ExactReadRequest("sno", "DB161_HomeGraph", "S_NO"),
        }, CancellationToken.None);

        Assert.Equal(PLC_VariableAccessService.LedgerOutageRefusedCode, results.Single(r => r.RequestKey == "db").ErrorCode);
        Assert.Equal(PLC_ExactReadQuality.Good, results.Single(r => r.RequestKey == "sno").Quality);
    }

    [Fact]
    public async Task Guard_data_failure_is_cached_for_the_failed_context()
    {
        const long pid = 881006;
        var h = new Harness(pid, new[]
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool"),
            IoTag(pid, "表1", "Start"),
        }, guardFails: true);
        var requests = new[] { new PLC_ExactReadRequest("db", "DB490", "A"), new PLC_ExactReadRequest("io", "表1", "Start") };

        var first = await h.Service.ReadExactDataAsync(pid, requests, CancellationToken.None);
        var loadsAfterFirst = h.GuardLoads;
        var second = await h.Service.ReadExactDataAsync(pid, requests, CancellationToken.None);

        Assert.Equal(1, loadsAfterFirst);                  // 第一个加载器失败即停止
        Assert.Equal(loadsAfterFirst, h.GuardLoads);       // 失败上下文存续期间不再重试加载
        Assert.All(first.Concat(second), r => Assert.Equal(PLC_VariableAccessService.LedgerOutageRefusedCode, r.ErrorCode));
        Assert.Empty(h.Aliases);
    }

    [Fact]
    public async Task Guard_loads_only_the_data_for_point_kinds_in_the_request()
    {
        const long pid = 881007;
        var h = new Harness(pid, new[]
        {
            PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool"),
            IoTag(pid, "表1", "Start"),
        }, liveBlocks: new[] { "DB490" }, blockPaths: new[] { ("DB490", "A") });

        var dbOnly = Assert.Single(await h.Service.ReadExactDataAsync(pid,
            new[] { new PLC_ExactReadRequest("db", "DB490", "A") }, CancellationToken.None));
        Assert.Equal(PLC_ExactReadQuality.Good, dbOnly.Quality);
        Assert.Equal(0, h.IoGuardLoads);
        Assert.Equal(2, h.DbGuardLoads);

        var ioOnly = Assert.Single(await h.Service.ReadExactDataAsync(pid,
            new[] { new PLC_ExactReadRequest("io", "表1", "Start") }, CancellationToken.None));
        Assert.Equal(PLC_ExactReadQuality.Good, ioOnly.Quality);
        Assert.Equal(1, h.IoGuardLoads);
        Assert.Equal(2, h.DbGuardLoads);                   // DB 判定数据已缓存，IO 读取不触发 DB 加载
    }

    /// <summary>
    /// Final fix round：读上下文被失效（注册写完台账）时会释放其判定数据缓存的信号量——
    /// 之后的读取必须照常工作（重建上下文、重新加载判定数据），不能因为信号量已释放而炸掉。
    /// </summary>
    [Fact]
    public async Task Read_after_cache_invalidation_still_works_and_reloads_guard_data()
    {
        const long pid = 881008;
        var h = new Harness(pid, new[] { PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool") },
            liveBlocks: new[] { "DB490" }, blockPaths: new[] { ("DB490", "A") });
        var request = new[] { new PLC_ExactReadRequest("db", "DB490", "A") };

        var first = Assert.Single(await h.Service.ReadExactDataAsync(pid, request, CancellationToken.None));
        var loadsAfterFirst = h.GuardLoads;
        PLC_VariableAccessService.InvalidateIgwAliasCache(pid);
        var second = Assert.Single(await h.Service.ReadExactDataAsync(pid, request, CancellationToken.None));

        Assert.Equal(PLC_ExactReadQuality.Good, first.Quality);
        Assert.Equal(PLC_ExactReadQuality.Good, second.Quality);
        Assert.True(h.GuardLoads > loadsAfterFirst); // 新上下文重新加载判定数据
    }

    [Fact]
    public async Task Available_ledger_never_loads_guard_data()
    {
        const long pid = 881005;
        var h = new Harness(pid, new[] { PLC_VariableAccessService.SynthesizeCandidate(pid, "DB490", "A", "Bool") }, ledgerFails: false);

        var result = Assert.Single(await h.Service.ReadExactDataAsync(pid,
            new[] { new PLC_ExactReadRequest("db", "DB490", "A") }, CancellationToken.None));

        Assert.Equal(PLC_ExactReadQuality.Good, result.Quality);
        Assert.Equal(0, h.GuardLoads);
    }
}
