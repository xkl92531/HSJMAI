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

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class FbContractReaderTests
{
    // 一个下层 FB（一个网络：O := I），一个上层 FB 调用下层，OB 调用上层并接 IO
    static PipelineSnapshot Project()
    {
        var low = new FlgNetBuilder();
        low.Series("Coil", "O", ("I", false));
        var lowIface = FlgNetBuilder.Interface(["I"], ["O"]);
        var mid = new FlgNetBuilder();
        mid.CallWithInstance("FB_Low", "Inst", ("I", "Input", "Bool"), ("O", "Output", "Bool"));
        var midIface = FlgNetBuilder.Interface(["MI"], ["MO"]);
        var ob = new FlgNetBuilder();
        ob.CallWithInstance("FB_Mid", "DB_Mid", ("MI", "Input", "Bool"), ("MO", "Output", "Bool"));
        return new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, lowIface, "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_Low"], 1, midIface, "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_Mid"], 1, "", "OB"),
            new(13, "FB_Lock", 130, "LAD", "", "", [], 1, lowIface, "FB", UnitKind: "MissingBody"),
        ],
        [new("Q_Out", "%Q0.0", "Bool"), new("I_In", "%I0.0", "Bool")]);
    }

    sealed class MemoryCache : IFbContractCache
    {
        public readonly Dictionary<string, FbContractCacheEntry> Items = new();
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(Items.TryGetValue(key, out var e) ? e : null);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct) { Items[entry.Key] = entry; return Task.CompletedTask; }
    }

    [Fact]
    public void CallWiredProducesActuals()
    {
        var b = new FlgNetBuilder();
        b.CallWired("FB_Low", "Inst", ("I", "Input", "Bool", "I_In"), ("O", "Output", "Bool", "Q_Out"), ("X", "Input", "Bool", null));
        var unit = UnitTranslator.Translate(new PipelineUnit(1, "OB1", 1, "LAD", b.Build(), "", ["FB_Low"], 1, "", "OB"));
        var call = unit.Statements.Single(s => s.Call is not null).Call!;
        var i = call.Pins.Single(p => p.Name == "I");
        Assert.Equal(IrKinds.Var, i.Value?.Kind);
        Assert.Equal("I_In", i.Value?.Name);
        Assert.Contains("Q_Out", call.Pins.Single(p => p.Name == "O").Targets);
        var x = call.Pins.SingleOrDefault(p => p.Name == "X");
        Assert.True(x is null || (x.Value is null && x.Targets.Count == 0));
    }

    [Fact]
    public async Task ReadsLowerBlocksFirstAndPassesTheirContractsUp()
    {
        var evidence = IrBlock.Load(Project(), "FB_Low").StatementsById.Keys.Single();
        var prompts = new List<string>();
        Task<string> Complete(string s, string u, CancellationToken ct)
        {
            prompts.Add(u);
            return Task.FromResult(u.Contains("# 功能块 FB_Low")
                ? $$"""{"fb":"FB_Low","relations":[{"kind":"gate","input":"I","outputs":["O"],"evidence":["{{evidence}}"]}]}"""
                : """{"fb":"FB_Mid","relations":[]}""");
        }
        var reader = new FbContractReader(new MemoryCache(), Complete, "m1");
        var result = await reader.ReadAsync(Project(), ["FB_Mid", "FB_Low"], CancellationToken.None);
        Assert.Equal("FB_Low/1/1", evidence);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("# 功能块 FB_Low", prompts[0]);
        Assert.Contains("FB_Low：", prompts[1]);
        Assert.Equal(FbContractStatus.Ok, result["FB_Low"].Status);
        Assert.Equal(FbContractStatus.Rejected, result["FB_Mid"].Status);  // 没有通过核对的关系
    }

    [Fact]
    public async Task CachesSuccessButNotFailure()
    {
        var cache = new MemoryCache();
        var calls = 0;
        Task<string> Fail(string s, string u, CancellationToken ct) { calls++; return Task.FromResult("<think>没写完"); }
        var reader = new FbContractReader(cache, Fail, "m1");
        var first = await reader.ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        Assert.Equal(FbContractStatus.Failed, first["FB_Low"].Status);
        Assert.Empty(cache.Items);
        await reader.ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ThrowingCompletionIsFailedAndNotCached()
    {
        var cache = new MemoryCache();
        var reader = new FbContractReader(cache, (_, _, _) => throw new InvalidOperationException("网络断了"), "m1");
        var result = await reader.ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        Assert.Equal(FbContractStatus.Failed, result["FB_Low"].Status);
        Assert.Contains("网络断了", result["FB_Low"].Error);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task NoBodyIsReportedWithoutCallingAi()
    {
        var reader = new FbContractReader(new MemoryCache(), (_, _, _) => throw new InvalidOperationException("不应调用"), "m1");
        var result = await reader.ReadAsync(Project(), ["FB_Lock"], CancellationToken.None);
        Assert.Equal(FbContractStatus.NoBody, result["FB_Lock"].Status);
    }

    [Fact]
    public async Task CacheKeyChangesWithModelAndHitsAvoidAiCalls()
    {
        var cache = new MemoryCache();
        var calls = 0;
        Task<string> Ok(string s, string u, CancellationToken ct)
        { calls++; return Task.FromResult("""{"fb":"FB_Low","relations":[]}"""); }
        await new FbContractReader(cache, Ok, "m1").ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        await new FbContractReader(cache, Ok, "m1").ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        Assert.Equal(1, calls);
        await new FbContractReader(cache, Ok, "m2").ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SelectorFollowsFormalPassThroughToIo()
    {
        // 引脚都不接实参 → 没有块被选中
        Assert.Empty(RelevantFbSelector.Select(Project()));
        // OB1 把 FB_Mid 的引脚接到 IO 点；FB_Mid 把 FB_Low 的引脚接到自己的形参 → 两个都被选中
        var low = new FlgNetBuilder(); low.Series("Coil", "O", ("I", false));
        var mid = new FlgNetBuilder(); mid.CallWired("FB_Low", "Inst", ("I", "Input", "Bool", "MI"), ("O", "Output", "Bool", "MO"));
        var ob = new FlgNetBuilder(); ob.CallWired("FB_Mid", "DB_Mid", ("MI", "Input", "Bool", "I_In"), ("MO", "Output", "Bool", "Q_Out"));
        var wired = new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_Low"], 1, FlgNetBuilder.Interface(["MI"], ["MO"]), "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_Mid"], 1, "", "OB"),
        ],
        [new("Q_Out", "%Q0.0", "Bool"), new("I_In", "%I0.0", "Bool")]);
        Assert.Equal(new[] { "FB_Low", "FB_Mid" }, RelevantFbSelector.Select(wired).Order().ToArray());
    }

    /// <summary>Get 对指定键抛异常、Save 可整体抛异常的缓存（模拟缓存故障）。</summary>
    sealed class FaultyCache(string? badGetKey = null, bool saveThrows = false) : IFbContractCache
    {
        public readonly MemoryCache Inner = new();
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct)
            => key == badGetKey ? throw new InvalidOperationException("缓存读取故障") : Inner.GetAsync(key, ct);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct)
            => saveThrows ? throw new InvalidOperationException("缓存写入故障") : Inner.SaveAsync(entry, ct);
    }

    static string GateReply(string evidence) =>
        $$"""{"fb":"FB_Low","relations":[{"kind":"gate","input":"I","outputs":["O"],"evidence":["{{evidence}}"]}]}""";

    [Fact]
    public async Task UpperIsReReadWhenLowerOutcomeChanges()
    {
        var cache = new MemoryCache();
        var lowOk = false;
        var midCalls = 0;
        Task<string> Complete(string s, string u, CancellationToken ct)
        {
            if (u.Contains("# 功能块 FB_Low")) return Task.FromResult(lowOk ? GateReply("FB_Low/1/1") : "<think>没写完");
            midCalls++;
            return Task.FromResult("""{"fb":"FB_Mid","relations":[]}""");
        }
        var reader = new FbContractReader(cache, Complete, "m1");
        var first = await reader.ReadAsync(Project(), ["FB_Mid", "FB_Low"], CancellationToken.None);
        Assert.Equal(FbContractStatus.Failed, first["FB_Low"].Status);
        lowOk = true;
        var second = await reader.ReadAsync(Project(), ["FB_Mid", "FB_Low"], CancellationToken.None);
        Assert.Equal(FbContractStatus.Ok, second["FB_Low"].Status);
        Assert.Equal(2, midCalls);   // 下层由失败变成功，上层看到的材料变了 → 不得命中旧缓存
        Assert.NotEqual(first["FB_Mid"].CacheKey, second["FB_Mid"].CacheKey);
        await reader.ReadAsync(Project(), ["FB_Mid", "FB_Low"], CancellationToken.None);
        Assert.Equal(2, midCalls);   // 材料不变 → 命中
    }

    [Fact]
    public async Task CacheGetFailureOnlyFailsThatBlock()
    {
        Task<string> Complete(string s, string u, CancellationToken ct) => Task.FromResult(u.Contains("# 功能块 FB_Low")
            ? GateReply("FB_Low/1/1") : """{"fb":"FB_Mid","relations":[]}""");
        var probe = await new FbContractReader(new MemoryCache(), Complete, "m1")
            .ReadAsync(Project(), ["FB_Low"], CancellationToken.None);
        var reader = new FbContractReader(new FaultyCache(badGetKey: probe["FB_Low"].CacheKey), Complete, "m1");
        var result = await reader.ReadAsync(Project(), ["FB_Mid", "FB_Low", "FB_Lock"], CancellationToken.None);
        Assert.Equal(FbContractStatus.Failed, result["FB_Low"].Status);
        Assert.Contains("缓存读取故障", result["FB_Low"].Error);
        Assert.Equal(FbContractStatus.Rejected, result["FB_Mid"].Status);
        Assert.Equal(FbContractStatus.NoBody, result["FB_Lock"].Status);
    }

    [Fact]
    public async Task CacheSaveFailureKeepsValidatedOutcome()
    {
        Task<string> Complete(string s, string u, CancellationToken ct) => Task.FromResult(GateReply("FB_Low/1/1"));
        var reader = new FbContractReader(new FaultyCache(saveThrows: true), Complete, "m1");
        var result = await reader.ReadAsync(Project(), ["FB_Low", "FB_Lock"], CancellationToken.None);
        Assert.Equal(FbContractStatus.Ok, result["FB_Low"].Status);
        Assert.Single(result["FB_Low"].Contract!.Accepted);
        Assert.Equal(FbContractStatus.NoBody, result["FB_Lock"].Status);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        Task<string> Complete(string s, string u, CancellationToken ct) { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(""); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FbContractReader(new MemoryCache(), Complete, "m1").ReadAsync(Project(), ["FB_Low"], cts.Token));
    }

    [Fact]
    public async Task FileCacheTreatsCorruptFileAsMiss()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fbcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "k1.json"), """{"Key":"k1","FbName":""");
        await File.WriteAllTextAsync(Path.Combine(dir, "k2.json"), "null");
        var cache = new FileFbContractCache(dir);
        Assert.Null(await cache.GetAsync("k1", CancellationToken.None));
        Assert.Null(await cache.GetAsync("k2", CancellationToken.None));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SelectorReadsCallsEvenWhenCalleesListIsEmpty()
    {
        var low = new FlgNetBuilder(); low.Series("Coil", "O", ("I", false));
        var ob = new FlgNetBuilder(); ob.CallWired("FB_Low", "Inst", ("I", "Input", "Bool", "I_In"));
        var snapshot = new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", [], 1, "", "OB"),
        ],
        [new("I_In", "%I0.0", "Bool")]);
        Assert.Equal(new[] { "FB_Low" }, RelevantFbSelector.Select(snapshot).ToArray());
    }

    [Fact]
    public async Task FileCacheRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fbcache-" + Guid.NewGuid().ToString("N"));
        var cache = new FileFbContractCache(dir);
        var entry = new FbContractCacheEntry("k1", "FB_X", FbContractStatus.Ok, "{}", null, "m", FbContractPrompt.Version);
        await cache.SaveAsync(entry, CancellationToken.None);
        Assert.Equal(entry, await cache.GetAsync("k1", CancellationToken.None));
        Assert.Null(await cache.GetAsync("k2", CancellationToken.None));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task FeedbackDerivedFromALowerAlarmIsNotShownToTheUpperBlock()
    {
        // 下层：O := I；A := O ∧ ¬P（超时报警依赖输出 O 与到位输入 P）。AI 只给 timeoutAlarm，校验器派生 feedback；
        // 上层提示材料只列 AI 给出的关系 → 有无派生，上层缓存键都一样
        var n1 = new FlgNetBuilder();
        n1.Series("Coil", "O", ("I", false));
        var n2 = new FlgNetBuilder();
        n2.Series("Coil", "A", ("O", false), ("P", true));
        var lowIface = FlgNetBuilder.Interface(["I", "P"], ["O", "A"]);
        var mid = new FlgNetBuilder();
        mid.CallWithInstance("FB_Low", "Inst", ("I", "Input", "Bool"), ("P", "Input", "Bool"), ("O", "Output", "Bool"), ("A", "Output", "Bool"));
        var project = new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", n1.Build(), "", [], 1, lowIface, "FB"),
            new(10, "FB_Low", 101, "LAD", n2.Build(), "", [], 2, lowIface, "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_Low"], 1, FlgNetBuilder.Interface(["MI"], ["MO"]), "FB"),
        ], []);
        var alarmId = IrBlock.Load(project, "FB_Low").StatementsById.Keys.Single(k => k.StartsWith("FB_Low/2/"));
        var prompts = new List<string>();
        Task<string> Complete(string s, string u, CancellationToken ct)
        {
            prompts.Add(u);
            return Task.FromResult(u.Contains("# 功能块 FB_Low")
                ? $$"""{"fb":"FB_Low","relations":[{"kind":"timeoutAlarm","alarm":"A","output":"O","positionVar":"P","delayParam":"5","evidence":["{{alarmId}}"]}]}"""
                : """{"fb":"FB_Mid","relations":[]}""");
        }
        var result = await new FbContractReader(new MemoryCache(), Complete, "m1").ReadAsync(project, ["FB_Mid", "FB_Low"], CancellationToken.None);
        var low = result["FB_Low"].Contract!;
        Assert.Contains(low.Accepted, v => v.DerivedFrom == FbContractValidator.DerivedFromTimeoutAlarm);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("timeoutAlarm: A ← O", prompts[1]);
        Assert.DoesNotContain("feedback:", prompts[1]);
    }
}
