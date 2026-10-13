#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class UnderstandingPublishStepTests
{
    static readonly Original353ProjectResult Fallback = new(1, "v", "fp", [], 0, 0, []);

    [Fact]
    public async Task ExceptionsAreLoggedAndThePublishKeepsTheOriginalResult()
    {
        Exception? logged = null;
        var result = await UnderstandingPublishGuard.RunContainedAsync(
            () => throw new InvalidOperationException("模型服务不可用"), Fallback, ex => logged = ex, CancellationToken.None);
        Assert.Same(Fallback, result);
        Assert.IsType<InvalidOperationException>(logged);
    }

    [Fact]
    public async Task CancellationStillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnderstandingPublishGuard.RunContainedAsync(
            () => throw new OperationCanceledException(cts.Token), Fallback, _ => { }, cts.Token));
    }

    [Fact]
    public async Task SuccessReturnsTheNewResult()
    {
        var updated = Fallback with { RuleCount = 5 };
        var result = await UnderstandingPublishGuard.RunContainedAsync(() => Task.FromResult(updated), Fallback, _ => { }, CancellationToken.None);
        Assert.Same(updated, result);
    }

    [Fact]
    public async Task AStepPastTheDeadlineIsContainedAndFallsBack()
    {
        Exception? logged = null;
        var result = await UnderstandingPublishGuard.RunContainedAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Fallback with { RuleCount = 9 };
        }, Fallback, ex => logged = ex, CancellationToken.None, TimeSpan.FromMilliseconds(50));
        Assert.Same(Fallback, result);
        Assert.IsType<TimeoutException>(logged);
    }

    [Fact]
    public async Task CallerCancellationStillThrowsWhenADeadlineIsSet()
    {
        using var cts = new CancellationTokenSource();
        var running = UnderstandingPublishGuard.RunContainedAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Fallback;
        }, Fallback, _ => { }, cts.Token, TimeSpan.FromMinutes(5));
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AHangingModelCallBecomesATimeout()
    {
        ContractCompletion hanging = async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return ""; };
        var limited = UnderstandingPublishGuard.WithCallTimeout(hanging, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => limited("s", "u", CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellationOfAModelCallIsNotATimeout()
    {
        using var cts = new CancellationTokenSource();
        ContractCompletion hanging = async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return ""; };
        var limited = UnderstandingPublishGuard.WithCallTimeout(hanging, TimeSpan.FromMinutes(5));
        var running = limited("s", "u", cts.Token);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    static Original353ProjectResult OneRule(PipelineRule rule) => new(1, "test", "hash",
        [new Original353UnitResult(2, [rule], [], [])], 1, 0, []);

    [Fact]
    public void AiContractRulesDoNotNeedAiWordingToPublish()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw") { Proof = UnderstandingProofs.AiFbContract };
        Original353AiWordingPolicy.RequireComplete(OneRule(rule), new Dictionary<(long, string), Original353RuleWording>());
    }

    [Fact]
    public void OtherRulesStillNeedAiWordingToPublish()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw") { Proof = "FbContract:Mutex" };
        var error = Assert.Throws<PipelineException>(() =>
            Original353AiWordingPolicy.RequireComplete(OneRule(rule), new Dictionary<(long, string), Original353RuleWording>()));
        Assert.Equal("AiDescriptionsIncomplete:1/1", error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{not json")]
    public void UnusableRequestParametersAreTreatedAsEmpty(string? raw)
        => Assert.Empty(UnderstandingPublishGuard.ParseParameters(raw));

    [Fact]
    public void RequestParametersObjectIsKept()
        => Assert.Equal(0.7, (double)UnderstandingPublishGuard.ParseParameters("{\"top_p\":0.7}")["top_p"]!);

    // 整分支审查 Minor：日志列出未分析的 FB；契约缓存并发建表冲突视为已建好

    [Fact]
    public void UnanalysedFbsListFailedAndRejectedNames()
    {
        var report = new UnderstandingReport(new Dictionary<string, string>
        {
            ["FB_B"] = FbContractStatus.Failed, ["FB_A"] = FbContractStatus.Failed, ["FB_C"] = FbContractStatus.Rejected,
            ["FB_D"] = FbContractStatus.Ok, ["FB_E"] = FbContractStatus.NoBody
        }, new Dictionary<string, string>(), [], new Dictionary<string, int>(), 0, 0);
        var (failed, rejected) = UnderstandingPublishStep.UnanalysedFbs(report);
        Assert.Equal(new[] { "FB_A", "FB_B" }, failed);
        Assert.Equal(new[] { "FB_C" }, rejected);
    }

    [Theory]
    [InlineData("42P07", true)]
    [InlineData("42P06", true)]
    [InlineData("23505", true)]
    [InlineData("42501", false)]
    [InlineData(null, false)]
    public void ConcurrentCreateConflictsCountAsReady(string? state, bool expected)
        => Assert.Equal(expected, PostgresFbContractCache.IsConcurrentCreate(state));
}
