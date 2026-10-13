using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.Mcp;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public class EnergyFlowProgressContractTests
{
    [Fact]
    public async Task ProductionPublisher_EmitsTypedMonotonicStagesWithSafeMetrics()
    {
        var received = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(received);

        await progress.StartedAsync();
        await progress.StrategyReadyAsync();
        await progress.SignalsLoadedAsync(12);
        await progress.ConstraintsEvaluatedAsync(3);
        await progress.ResultReadyAsync(4);

        Assert.Equal(
            ["start", "strategy_ready", "signals_loaded", "constraints_evaluated", "result_ready"],
            received.Select(item => item.Stage));
        Assert.Equal([0f, 10f, 35f, 75f, 95f], received.Select(item => item.Progress));
        Assert.All(received, item => Assert.Equal("energy_flow", item.Operation));
        Assert.All(received, item => Assert.True(item.Force));
        Assert.Equal(12, received[2].Metrics?.SignalCount);
        Assert.Equal(3, received[3].Metrics?.IssueCount);
        Assert.Equal(4, received[4].Metrics?.IssueCount);
        Assert.Null(received[2].Metrics?.IssueCount);
        Assert.Null(received[3].Metrics?.SignalCount);
        Assert.Equal(
            "结果片段：IO 约束检查完成 | IssueCount=3",
            McpProgressMessageFormatter.Format(received[3]));
        Assert.Equal(
            "结果片段：IO 约束诊断结果已生成 | IssueCount=4",
            McpProgressMessageFormatter.Format(received[4]));
    }

    [Theory]
    [InlineData(true, "产线 IO 约束异常检测完成")]
    [InlineData(false, "产线 IO 约束异常检测结束（业务返回失败）")]
    public async Task ProductionPublisher_CompletesSuccessfulAndBusinessFailureReturns(
        bool isSuccess,
        string expectedMessage)
    {
        var received = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(received);

        await progress.CompletedAsync(isSuccess);

        var completed = Assert.Single(received);
        Assert.Equal("complete", completed.Stage);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(100f, completed.Progress);
        Assert.True(completed.Force);
        Assert.Equal(expectedMessage, completed.Message);
    }

    [Fact]
    public async Task ProductionPublisher_ReportsOnlyGenericFailureMessage()
    {
        var received = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(received);

        await progress.FailedAsync();

        var failed = Assert.Single(received);
        Assert.Equal("failed", failed.Stage);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(99f, failed.Progress);
        Assert.True(failed.Force);
        Assert.Equal("产线 IO 约束异常检测失败", failed.Message);
        Assert.Null(failed.Metrics);
    }

    [Fact]
    public async Task ProductionPublisher_PreservesCancellationAndDoesNotEmit()
    {
        var received = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(received);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => progress.StartedAsync(cancellation.Token));

        Assert.Empty(received);
    }

    [Fact]
    public async Task ProductionPublisher_DoesNotLetOrdinaryReporterFailureBreakDiagnosis()
    {
        var progress = new EnergyFlowDiagnosisProgress(
            new DelegateDiagnosisProgressReporter((_, _) =>
                throw new InvalidOperationException("transport unavailable")));

        await progress.StartedAsync();
    }

    [Fact]
    public async Task ProductionPublishers_DoNotMixConcurrentCalls()
    {
        var first = new List<DiagnosisProgressEvent>();
        var second = new List<DiagnosisProgressEvent>();
        var firstProgress = CreateProgress(first);
        var secondProgress = CreateProgress(second);

        await Task.WhenAll(
            firstProgress.SignalsLoadedAsync(11),
            secondProgress.ConstraintsEvaluatedAsync(22));

        var firstEvent = Assert.Single(first);
        Assert.Equal("signals_loaded", firstEvent.Stage);
        Assert.Equal(11, firstEvent.Metrics?.SignalCount);
        Assert.Null(firstEvent.Metrics?.IssueCount);

        var secondEvent = Assert.Single(second);
        Assert.Equal("constraints_evaluated", secondEvent.Stage);
        Assert.Equal(22, secondEvent.Metrics?.IssueCount);
        Assert.Null(secondEvent.Metrics?.SignalCount);
    }

    private static EnergyFlowDiagnosisProgress CreateProgress(List<DiagnosisProgressEvent> received) =>
        new(new DelegateDiagnosisProgressReporter((progress, _) =>
        {
            received.Add(progress);
            return Task.CompletedTask;
        }));
}
