#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Networks;

public class NetworkAiReaderTests
{
    sealed class MemoryCache : INetworkAiCache
    {
        public readonly Dictionary<string, NetworkAiCacheEntry> Items = new();
        public Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct)
        {
            lock (Items) return Task.FromResult(Items.TryGetValue(key, out var e) ? e : null);
        }
        public Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct) { lock (Items) Items[entry.Key] = entry; return Task.CompletedTask; }
    }

    static readonly PipelineTag[] Tags = [new("Q1", "%Q0.0", "Bool"), new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool")];
    static readonly Dictionary<string, FbContractOutcome> NoContracts = new();
    const string Reply = """{"constraints":[{"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Member"},{"name":"I1","role":"Member"}],"evidence":["OB1/1/1"]}]}""";

    static PipelineSnapshot Project(bool twoNetworks = false)
    {
        var io = new FlgNetBuilder(); io.Series("Coil", "Q1", ("I1", false), ("I2", true));
        var call = new FlgNetBuilder(); call.CallWired("FB_X", "DB_X", ("A", "Input", "Bool", "I1"), ("O", "Output", "Bool", "Q1"));
        var fc = new FlgNetBuilder(); fc.Series("Coil", "Q1", ("I1", false), ("I2", false));
        var one = new FlgNetBuilder(); one.Series("Coil", "M1", ("I1", false));
        var other = new FlgNetBuilder(); other.Series("Coil", "Q1", ("I2", false));
        List<PipelineUnit> units =
        [
            new(1, "OB1", 101, "LAD", io.Build(), "", [], 1, "", "OB"),
            new(1, "OB1", 102, "LAD", call.Build(), "", ["FB_X"], 2, "", "OB"),
            new(1, "OB1", 103, "LAD", one.Build(), "", [], 3, "", "OB"),
            new(2, "FC1", 201, "LAD", fc.Build(), "", [], 1, "", "FC"),
        ];
        if (twoNetworks) units.Add(new(1, "OB1", 104, "LAD", other.Build(), "", [], 4, "", "OB"));
        return new PipelineSnapshot(1, units, Tags);
    }

    [Fact]
    public void SelectsOnlyCallFreeIoNetworksOutsideFc()
    {
        var source = Project();
        var translated = source.Units.Select(u => (u, UnitTranslator.Translate(u, Tags))).ToArray();
        Assert.Equal([101L], NetworkAiSelector.Select(translated, Tags).Select(x => x.Unit.Id).ToArray());
    }

    [Fact]
    public void NetworksWithAnySourceStatementOrOnlyTextAreNotSelected()   // 裁定 F6：SCL 块调用会成为不透明源码
    {
        PipelineUnit Pu(long id) => new(1, "OB1", id, "SCL", "", "", [], (int)id, "", "OB");
        IrStatement Assign(string id) => new(id, IrStatementKinds.Assign, "Q1", IrExpr.True, IrExpr.And([IrExpr.Var("I1"), IrExpr.Var("I2")]));
        var withSource = new IrUnit(1, "OB1", 1, "SCL", "StructuredText",
            [Assign("OB1/1/1"), new IrStatement("OB1/1/2", IrStatementKinds.Source, null, IrExpr.True, null, null, "\"FB_X_DB\"(A := I1);")], [], []);
        var plain = new IrUnit(2, "OB1", 2, "SCL", "StructuredText", [Assign("OB1/2/1")], [], []);
        var missing = new IrUnit(3, "OB1", 3, "SCL", "StructuredText", [Assign("OB1/3/1")], [], [], UnitKind: "MissingBody");
        var selected = NetworkAiSelector.Select([(Pu(1), withSource), (Pu(2), plain), (Pu(3) with { UnitKind = "MissingBody" }, missing)], Tags);
        Assert.Equal([2L], selected.Select(x => x.Unit.Id).ToArray());
    }

    [Fact]
    public void NetworksOfBlocksWithJumpsOrReturnsAreNotSelected()   // 合并时也不用，提前排除免得白调 AI
    {
        PipelineUnit Pu(long id, string block) => new(1, block, id, "LAD", "", "", [], (int)id, "", "FB");
        IrStatement Assign(string id) => new(id, IrStatementKinds.Assign, "Q1", IrExpr.True, IrExpr.And([IrExpr.Var("I1"), IrExpr.Var("I2")]));
        var plain = new IrUnit(1, "FB_A", 1, "LAD", "FlgNet", [Assign("FB_A/1/1")], [], []);
        var sibling = new IrUnit(2, "FB_B", 1, "LAD", "FlgNet", [Assign("FB_B/1/1")], [], []);
        var ret = new IrUnit(3, "FB_B", 2, "LAD", "FlgNet", [new IrStatement("FB_B/2/1", IrStatementKinds.Return, null, IrExpr.Var("I1"), null)], [], []);
        var selected = NetworkAiSelector.Select([(Pu(1, "FB_A"), plain), (Pu(2, "FB_B"), sibling), (Pu(3, "FB_B"), ret)], Tags);
        Assert.Equal([1L], selected.Select(x => x.Unit.Id).ToArray());
    }

    [Fact]
    public async Task ReadsCachesAndHitsTheCacheOnTheSecondRun()
    {
        var cache = new MemoryCache();
        var prompts = new List<string>();
        Task<string> Complete(string s, string u, CancellationToken ct) { lock (prompts) prompts.Add(u); return Task.FromResult(Reply); }
        var first = await new NetworkAiReader(cache, Complete, "m1").ReadAsync(Project(), NoContracts, CancellationToken.None);
        var outcome = Assert.Single(first.Values);
        Assert.Equal(NetworkAiStatus.Ok, outcome.Status);
        Assert.Equal(101, outcome.UnitId);
        Assert.Single(outcome.Reply!.Constraints);
        Assert.Contains("| I1 | %I0.0 | Bool |", prompts.Single());
        Assert.Contains("[OB1/1/1]", prompts.Single());
        var saved = Assert.Single(cache.Items.Values);
        Assert.Equal((NetworkAiStatus.Ok, Reply, "m1", NetworkAiPrompt.Version), (saved.Status, saved.ReplyJson, saved.Model, saved.PromptVersion));
        Assert.Equal(outcome.CacheKey, saved.Key);
        var second = await new NetworkAiReader(cache, Complete, "m1").ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Ok, second[101].Status);
        Assert.Single(prompts);
        // 模型指纹进缓存键：换模型重新读取
        var third = await new NetworkAiReader(cache, Complete, "m2").ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.NotEqual(outcome.CacheKey, third[101].CacheKey);
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    public async Task MaterialsCarryCallersAndTheContractOfAnInstanceReadInTheNetwork()
    {
        var mid1 = new FlgNetBuilder(); mid1.CallWithInstance("FB_Low", "Inst", ("I", "Input", "Bool"), ("O", "Output", "Bool"));
        var mid2 = new FlgNetBuilder(); mid2.Series("Coil", "Q1", ("Inst.O", false), ("I1", false));
        var ob = new FlgNetBuilder(); ob.CallWired("FB_Mid", "DB_Mid", ("MI", "Input", "Bool", "I2"));
        var iface = FlgNetBuilder.Interface(["MI"], ["MO"]);
        var source = new PipelineSnapshot(1,
        [
            new(1, "FB_Mid", 301, "LAD", mid1.Build(), "", ["FB_Low"], 1, iface, "FB"),
            new(1, "FB_Mid", 302, "LAD", mid2.Build(), "", [], 2, iface, "FB"),
            new(2, "OB1", 401, "LAD", ob.Build(), "", ["FB_Mid"], 1, "", "OB"),
        ], Tags);
        var relation = new AiContractRelation("mutex", ["O", "P"], [], null, null, null, null, ["FB_Low/1/1"], null);
        var contracts = new Dictionary<string, FbContractOutcome>(StringComparer.OrdinalIgnoreCase)
        {
            ["FB_Low"] = new("FB_Low", FbContractStatus.Ok, new ValidatedFbContract("FB_Low", [new ValidatedRelation(relation, false, null)]), "ck", null)
        };
        string? user = null;
        var result = await new NetworkAiReader(new MemoryCache(), (_, u, _) => { user = u; return Task.FromResult(Reply); }, "m1")
            .ReadAsync(source, contracts, CancellationToken.None);
        Assert.Equal([302L], result.Keys.ToArray());
        Assert.Contains("**OB1** 网络 1：[OB1/1/1] 调用 FB FB_Mid", user);
        Assert.Contains("- 实例 Inst（FB_Low）：mutex: O/P", user);
        Assert.Contains("  - MI : Bool", user);
        Assert.Contains("| Q1 | %Q0.0 | Bool |", user);
        Assert.DoesNotContain("| I2 |", user);
    }

    [Fact]
    public async Task FileCacheRoundTripsAndTreatsCorruptFilesAsMisses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "network-ai-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileNetworkAiCache(dir);
            var entry = new NetworkAiCacheEntry("k1", "OB1", 3, NetworkAiStatus.Ok, Reply, null, "m1", NetworkAiPrompt.Version);
            await cache.SaveAsync(entry, CancellationToken.None);
            Assert.Equal(entry, await cache.GetAsync("k1", CancellationToken.None));
            Assert.Null(await cache.GetAsync("k2", CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(dir, "k3.json"), "{\"Key\":");
            Assert.Null(await cache.GetAsync("k3", CancellationToken.None));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FailureIsNotCached()
    {
        var cache = new MemoryCache();
        var result = await new NetworkAiReader(cache, (_, _, _) => throw new InvalidOperationException("断了"), "m1")
            .ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Failed, result[101].Status);
        Assert.Contains("断了", result[101].Error);
        Assert.Empty(cache.Items);
        var unusable = await new NetworkAiReader(cache, (_, _, _) => Task.FromResult("<think>"), "m1")
            .ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Failed, unusable[101].Status);
        Assert.Equal("AiReplyUnusable", unusable[101].Error);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task CacheSaveFailureKeepsTheResult()
    {
        var result = await new NetworkAiReader(new ThrowingCache(), (_, _, _) => Task.FromResult(Reply), "m1")
            .ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Ok, result[101].Status);
        Assert.StartsWith("CacheSaveFailed", result[101].Error);
    }

    sealed class ThrowingCache : INetworkAiCache
    {
        public Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct) => throw new IOException("读不了");
        public Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct) => throw new IOException("写不了");
    }

    [Fact]
    public async Task TooLargeAndDeadlineDoNotCallAi()
    {
        Task<string> Never(string s, string u, CancellationToken ct) => throw new InvalidOperationException("不应调用");
        var big = await new NetworkAiReader(new MemoryCache(), Never, "m1", maxChars: 10).ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.TooLarge, big[101].Status);
        var late = await new NetworkAiReader(new MemoryCache(), Never, "m1", budget: TimeSpan.Zero).ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Deadline, late[101].Status);
        var overall = await new NetworkAiReader(new MemoryCache(), Never, "m1", readingDeadline: new CancellationToken(true))
            .ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Deadline, overall[101].Status);
    }

    [Fact]
    public async Task CacheHitsAreStillUsedAfterTheReadingDeadline()
    {
        var cache = new MemoryCache();
        await new NetworkAiReader(cache, (_, _, _) => Task.FromResult(Reply), "m1").ReadAsync(Project(), NoContracts, CancellationToken.None);
        var late = await new NetworkAiReader(cache, (_, _, _) => throw new InvalidOperationException("不应调用"), "m1",
            readingDeadline: new CancellationToken(true)).ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Ok, late[101].Status);
    }

    [Fact]
    public async Task ReadingDeadlineAbortsCallsInFlightWithoutFailingTheCaller()
    {
        using var deadline = new CancellationTokenSource();
        async Task<string> Hang(string s, string u, CancellationToken ct)
        {
            deadline.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply;
        }
        var result = await new NetworkAiReader(new MemoryCache(), Hang, "m1", readingDeadline: deadline.Token)
            .ReadAsync(Project(), NoContracts, CancellationToken.None);
        Assert.Equal(NetworkAiStatus.Deadline, result[101].Status);
    }

    [Fact]
    public async Task ParallelismLimitsConcurrentCalls()
    {
        int running = 0, peak = 0;
        async Task<string> Slow(string s, string u, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            lock (this) peak = Math.Max(peak, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return Reply;
        }
        var result = await new NetworkAiReader(new MemoryCache(), Slow, "m1", parallelism: 1)
            .ReadAsync(Project(twoNetworks: true), NoContracts, CancellationToken.None);
        Assert.Equal([101L, 104L], result.Keys.Order().ToArray());
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NetworkAiReader(new MemoryCache(), (_, _, ct) => Task.FromCanceled<string>(ct), "m1").ReadAsync(Project(), NoContracts, cts.Token));
    }
}
