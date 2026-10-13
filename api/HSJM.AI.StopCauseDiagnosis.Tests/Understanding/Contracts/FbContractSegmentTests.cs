#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class FbContractSegmentTests
{
    sealed class MemoryCache : IFbContractCache
    {
        public readonly Dictionary<string, FbContractCacheEntry> Items = new();
        public Func<FbContractCacheEntry, bool> FailSave = _ => false;
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Items.TryGetValue(key, out var e) ? e : null);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct)
        {
            if (FailSave(entry)) throw new InvalidOperationException("磁盘满");
            Items[entry.Key] = entry;
            return Task.CompletedTask;
        }
    }

    // FB_Big：三个网络，各一条 Ok := Ik；接口 I1..I3 → O1..O3
    static PipelineSnapshot Project(params string[] extraFbs)
    {
        var units = Enumerable.Range(1, 3).Select(k =>
        {
            var b = new FlgNetBuilder();
            b.Series("Coil", $"O{k}", ($"I{k}", false));
            return new PipelineUnit(10, "FB_Big", 100 + k, "LAD", b.Build(), "", [], k,
                FlgNetBuilder.Interface(["I1", "I2", "I3"], ["O1", "O2", "O3"]), "FB");
        }).ToList();
        foreach (var (fb, i) in extraFbs.Select((f, i) => (f, i)))
        {
            var b = new FlgNetBuilder();
            b.Series("Coil", "O", ("I", false));
            units.Add(new PipelineUnit(20 + i, fb, 200 + i, "LAD", b.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"));
        }
        return new PipelineSnapshot(1, units.ToArray(), []);
    }

    static int[] Lengths(PipelineSnapshot s) => IrBlock.Load(s, "FB_Big").Units.Select(u => IrText.Unit(u).Length).ToArray();

    static string Gate(int k) =>
        $$"""{"fb":"FB_Big","relations":[{"kind":"gate","input":"I{{k}}","outputs":["O{{k}}"],"evidence":["FB_Big/{{k}}/1"]}]}""";

    [Fact]
    public void SplitPacksNetworksInOrderAndSkipsOversizedOnes()
    {
        var block = IrBlock.Load(Project(), "FB_Big");
        var len = Lengths(Project());
        var plan = FbContractSegments.Split(block, len[0] + len[1]);
        Assert.Equal([2, 1], plan.Segments.Select(s => s.Count).ToArray());
        Assert.Empty(plan.Skipped);
        var tight = FbContractSegments.Split(block, len.Min() - 1);
        Assert.Empty(tight.Segments);
        Assert.Equal(["FB_Big/1", "FB_Big/2", "FB_Big/3"], tight.Skipped);
    }

    [Fact]
    public void BlockWithinBudgetIsASingleSegment()
    {
        var block = IrBlock.Load(Project(), "FB_Big");
        var plan = FbContractSegments.Split(block, Lengths(Project()).Sum());
        Assert.Equal([3], plan.Segments.Select(s => s.Count).ToArray());
    }

    [Fact]
    public void SegmentPromptHasWholeInterfaceAndOnlyItsNetworks()
    {
        var block = IrBlock.Load(Project(), "FB_Big");
        var user = FbContractPrompt.BuildSegmentUser(block, [block.Units[1]], 2, 3, ["FB_Big/9"],
            [new LowerContractInfo("FB_Low", "gate: I 为 false 时 O 为 false")]);
        Assert.Contains("分 3 段提供，这是第 2 段", user);
        Assert.Contains("FB_Big/9", user);
        Assert.Contains(IrText.Unit(block.Units[1]), user);
        Assert.DoesNotContain(IrText.Unit(block.Units[0]), user);
        foreach (var name in new[] { "I1", "I3", "O1", "O3" }) Assert.Contains(name, user);   // 完整接口
        Assert.Contains("FB_Low：gate", user);
    }

    [Fact]
    public async Task OversizedFbIsReadInSegmentsAndValidatedAsAWhole()
    {
        var source = Project();
        var len = Lengths(source);
        var cache = new MemoryCache();
        var prompts = new List<string>();
        Task<string> Complete(string s, string u, CancellationToken ct)
        {
            prompts.Add(u);
            return Task.FromResult(u.Contains("这是第 1 段") ? Gate(1) : Gate(3));
        }
        var reader = new FbContractReader(cache, Complete, "m1", maxBodyChars: len[0] + len[1]);
        var outcome = (await reader.ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("分 2 段提供", prompts[0]);
        Assert.Equal(["I1", "I3"], outcome.Contract!.Accepted.Select(r => r.Relation.Inputs[0]).Order().ToArray());
        Assert.Equal(["FB_Big#1/2", "FB_Big#2/2"], cache.Items.Values.Select(e => e.FbName).Order().ToArray());

        // 再读一次：各段都命中缓存，不调 AI，结果与键相同
        var again = (await new FbContractReader(cache, (_, _, _) => throw new InvalidOperationException("不应调用"), "m1",
            maxBodyChars: len[0] + len[1]).ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, again.Status);
        Assert.Equal(outcome.CacheKey, again.CacheKey);
        Assert.Equal(2, again.Contract!.Accepted.Count);
    }

    [Fact]
    public async Task EvidenceFromAnotherSegmentIsCheckedOnTheWholeBlock()
    {
        // 第 2 段的 AI 引用了第 1 段的语句：整块 IR 上核对，证据存在即可；关系本身由程序核对
        var source = Project();
        var len = Lengths(source);
        var reader = new FbContractReader(new MemoryCache(), (_, u, _) => Task.FromResult(u.Contains("这是第 1 段") ? Gate(1) : Gate(1)),
            "m1", maxBodyChars: len[0] + len[1]);
        var outcome = (await reader.ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.All(outcome.Contract!.Accepted, r => Assert.Equal("I1", r.Relation.Inputs[0]));
    }

    [Fact]
    public async Task FailedSegmentIsReportedAndNotCached()
    {
        var source = Project();
        var len = Lengths(source);
        var cache = new MemoryCache();
        Task<string> Complete(string s, string u, CancellationToken ct) =>
            u.Contains("这是第 1 段") ? Task.FromResult(Gate(1)) : throw new InvalidOperationException("超时");
        var outcome = (await new FbContractReader(cache, Complete, "m1", maxBodyChars: len[0] + len[1])
            .ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Contains("SegmentFailed", outcome.Error);
        Assert.Contains("2/2", outcome.Error);
        Assert.Single(cache.Items);

        var allFail = (await new FbContractReader(new MemoryCache(), (_, _, _) => throw new InvalidOperationException("挂了"), "m1",
            maxBodyChars: len[0] + len[1]).ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Failed, allFail.Status);
        Assert.Null(allFail.Contract);
    }

    [Fact]
    public async Task UnusableSegmentReplyIsFailedAndNotCached()
    {
        var source = Project();
        var len = Lengths(source);
        var cache = new MemoryCache();
        var outcome = (await new FbContractReader(cache, (_, u, _) => Task.FromResult(u.Contains("这是第 1 段") ? Gate(1) : "不是 JSON"), "m1",
            maxBodyChars: len[0] + len[1]).ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Contains("AiReplyUnusable", outcome.Error);
        Assert.Single(cache.Items);
    }

    [Fact]
    public async Task CacheSaveFailureOfOneSegmentDoesNotStopLaterSegmentsBeingSaved()
    {
        // F17：每段独立写缓存；一段写失败只记说明
        var source = Project();
        var len = Lengths(source);
        var cache = new MemoryCache { FailSave = e => e.FbName == "FB_Big#1/2" };
        var outcome = (await new FbContractReader(cache, (_, u, _) => Task.FromResult(u.Contains("这是第 1 段") ? Gate(1) : Gate(3)), "m1",
            maxBodyChars: len[0] + len[1]).ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Contains("CacheSaveFailed", outcome.Error);
        Assert.Equal(["FB_Big#2/2"], cache.Items.Values.Select(e => e.FbName).ToArray());
        Assert.Equal(2, outcome.Contract!.Accepted.Count);
    }

    [Fact]
    public async Task OversizedNetworkIsSkippedAndListed()
    {
        // 网络 2 串了很多触点，单独超过预算：跳过并在提示与结果里列出，其余网络照常分段
        var units = Enumerable.Range(1, 3).Select(k =>
        {
            var b = new FlgNetBuilder();
            var contacts = k == 2 ? Enumerable.Range(1, 40).Select(i => ($"I{k}", i % 2 == 0)).ToArray() : [($"I{k}", false)];
            b.Series("Coil", $"O{k}", contacts);
            return new PipelineUnit(10, "FB_Big", 100 + k, "LAD", b.Build(), "", [], k,
                FlgNetBuilder.Interface(["I1", "I2", "I3"], ["O1", "O2", "O3"]), "FB");
        }).ToArray();
        var source = new PipelineSnapshot(1, units, []);
        var len = Lengths(source);
        var budget = len[0] + len[2];
        Assert.True(len[1] > budget);
        var prompts = new List<string>();
        var reader = new FbContractReader(new MemoryCache(), (_, u, _) => { prompts.Add(u); return Task.FromResult(Gate(1)); }, "m1",
            maxBodyChars: budget);
        var outcome = (await reader.ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Contains("SegmentTooLarge: FB_Big/2", outcome.Error);
        var prompt = Assert.Single(prompts);
        Assert.Contains("单独超过长度上限、未提供：FB_Big/2", prompt);
        Assert.Contains("分 1 段提供", prompt);
    }

    [Fact]
    public async Task NothingFitsIsTooLargeWithoutCallingAi()
    {
        var source = Project();
        var cache = new MemoryCache();
        var reader = new FbContractReader(cache, (_, _, _) => throw new InvalidOperationException("不应调用"), "m1",
            maxBodyChars: Lengths(source).Min() - 1);
        var outcome = (await reader.ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.TooLarge, outcome.Status);
        Assert.Equal(FbContractStatus.TooLarge, cache.Items[outcome.CacheKey].Status);   // 原行为：按内容键写报告
    }

    [Fact]
    public async Task UnsegmentedFbKeepsItsCacheKey()
    {
        // 未超预算的 FB 走原路径：键与默认预算的读取器相同（已有缓存继续命中）
        var source = Project();
        var cache = new MemoryCache();
        var a = (await new FbContractReader(cache, (_, _, _) => Task.FromResult(Gate(1)), "m1")
            .ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        var b = (await new FbContractReader(cache, (_, _, _) => throw new InvalidOperationException("不应调用"), "m1",
            maxBodyChars: Lengths(source).Sum()).ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(a.CacheKey, b.CacheKey);
        Assert.Equal(FbContractStatus.Ok, b.Status);
        Assert.Equal("FB_Big", Assert.Single(cache.Items.Values).FbName);
    }

    [Fact]
    public async Task ReadingDeadlineDropsOnlyUnreadSegments()
    {
        // F11：读取期限到了只丢没读完的段，已读完的段照常核对使用；之后的块不再调 AI
        var source = Project("FB_Z");
        var len = Lengths(source);
        using var stop = new CancellationTokenSource();
        var prompts = new List<string>();
        Task<string> Complete(string s, string u, CancellationToken ct)
        {
            prompts.Add(u);
            stop.Cancel();
            return Task.FromResult(Gate(1));
        }
        var reader = new FbContractReader(new MemoryCache(), Complete, "m1", maxBodyChars: len[0] + len[1], readingDeadline: stop.Token);
        var result = await reader.ReadAsync(source, ["FB_Big", "FB_Z"], CancellationToken.None);
        Assert.Single(prompts);
        Assert.Equal(FbContractStatus.Ok, result["FB_Big"].Status);
        Assert.Contains("ReadingDeadline", result["FB_Big"].Error);
        Assert.Equal(["I1"], result["FB_Big"].Contract!.Accepted.Select(r => r.Relation.Inputs[0]).ToArray());
        Assert.Equal(FbContractStatus.Failed, result["FB_Z"].Status);
        Assert.Contains("ReadingDeadline", result["FB_Z"].Error);
    }

    [Fact]
    public async Task ReadingDeadlineCancelsTheCallInFlight()
    {
        var source = Project();
        var len = Lengths(source);
        using var stop = new CancellationTokenSource();
        async Task<string> Complete(string s, string u, CancellationToken ct)
        {
            if (u.Contains("这是第 1 段")) return Gate(1);
            stop.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Task.Delay(Timeout.Infinite, ct);
            return Gate(3);
        }
        var outcome = (await new FbContractReader(new MemoryCache(), Complete, "m1", maxBodyChars: len[0] + len[1], readingDeadline: stop.Token)
            .ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Contains("ReadingDeadline", outcome.Error);
    }

    [Fact]
    public async Task CacheHitsAreStillUsedAfterTheReadingDeadline()
    {
        var source = Project();
        var len = Lengths(source);
        var cache = new MemoryCache();
        await new FbContractReader(cache, (_, u, _) => Task.FromResult(u.Contains("这是第 1 段") ? Gate(1) : Gate(3)), "m1",
            maxBodyChars: len[0] + len[1]).ReadAsync(source, ["FB_Big"], CancellationToken.None);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var outcome = (await new FbContractReader(cache, (_, _, _) => throw new InvalidOperationException("不应调用"), "m1",
            maxBodyChars: len[0] + len[1], readingDeadline: stop.Token).ReadAsync(source, ["FB_Big"], CancellationToken.None))["FB_Big"];
        Assert.Equal(FbContractStatus.Ok, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.Contract!.Accepted.Count);
    }

    [Fact]
    public async Task CallerCancellationStillPropagates()
    {
        var source = Project();
        var len = Lengths(source);
        using var cts = new CancellationTokenSource();
        Task<string> Complete(string s, string u, CancellationToken ct) { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Gate(1)); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FbContractReader(new MemoryCache(), Complete, "m1", maxBodyChars: len[0] + len[1]).ReadAsync(source, ["FB_Big"], cts.Token));
    }

    [Theory]
    [InlineData(45, 40)]
    [InlineData(10, 8)]
    [InlineData(100, 95)]
    public void ReadingBudgetLeavesRoomBeforeTheStepDeadline(double deadlineMinutes, double expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UnderstandingPublishStep.ReadingBudget(TimeSpan.FromMinutes(deadlineMinutes)));
    }
}
