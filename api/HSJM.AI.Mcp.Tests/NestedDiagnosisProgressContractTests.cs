using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Mcp;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public class NestedDiagnosisProgressContractTests
{
    [Theory]
    [InlineData("start", 0)]
    [InlineData("constraint_start", 70)]
    [InlineData("constraint_done", 82)]
    [InlineData("llm_start", 82)]
    [InlineData("llm_done", 98)]
    [InlineData("complete", 100)]
    public void ParentEvents_MapToStableBusinessProgress(string type, float expected)
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = type,
            CompletedCount = 2,
            TotalCount = 4,
        });

        Assert.Equal(expected, mapped.Progress);
        Assert.Equal(100, mapped.Total);
    }

    [Fact]
    public void CompletedStation_MapsTwoOfFourToFortyPercent()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "station_done",
            CompletedCount = 2,
            TotalCount = 4,
        });

        Assert.Equal(40, mapped.Progress);
        Assert.Equal(2, mapped.Metrics!.CompletedCount);
        Assert.Equal(4, mapped.Metrics.TotalCount);
    }

    [Fact]
    public void StationStart_RejectsUnsafeStationCode()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "station_start",
            StationCode = "OP010 Authorization=unsafe",
            CompletedCount = 1,
            TotalCount = 2,
        });

        Assert.Equal("开始诊断工位 N/A", mapped.Message);
        Assert.DoesNotContain("Authorization", mapped.Message);
    }

    [Fact]
    public void CompletedStation_FormatsOnlyAllowlistedStationResultFragment()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "station_done",
            CompletedCount = 1,
            TotalCount = 2,
            Station = new StationDiagnosisSummaryDto
            {
                StationCode = "OP010",
                OverallStatus = "SomeFalse",
                DiagnosisStrategy = "StallPointBased",
                FalseAnchorCount = 2,
                KeyFindings =
                [
                    new StationAnchorFinding
                    {
                        VariableName = "unsafe-signal",
                        IoTagAddress = "%I1.0",
                        FailurePathSummary = "System.InvalidOperationException: raw stack",
                    },
                ],
            },
        });

        var formatted = McpProgressMessageFormatter.Format(mapped);

        Assert.StartsWith("结果片段：", formatted);
        Assert.Contains("工位=OP010", formatted);
        Assert.Contains("OverallStatus=SomeFalse", formatted);
        Assert.Contains("DiagnosisStrategy=StallPointBased", formatted);
        Assert.Contains("FalseAnchorCount=2", formatted);
        Assert.Contains("KeyFindingsCount=1", formatted);
        Assert.DoesNotContain("unsafe-signal", formatted);
        Assert.DoesNotContain("%I1.0", formatted);
        Assert.DoesNotContain("InvalidOperationException", formatted);
    }

    [Fact]
    public void AgentStep_FormatsOnlyIterationPhaseAndDecision()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "agent_step",
            ResultFragment = new LineDiagnosisResultFragmentDto
            {
                Iteration = 3,
                Phase = ReActPhase.Thought,
                Decision = ReActStepDecision.Continue,
            },
            Payload = new
            {
                Iteration = 999,
                Phase = "raw PLC %Q1.0=true",
                Decision = "token=must-not-leak",
            },
        });

        var formatted = McpProgressMessageFormatter.Format(mapped);

        Assert.Equal(
            "结果片段：Agent Iteration=3, Phase=Thought, Decision=Continue",
            formatted);
        Assert.DoesNotContain("%Q1.0", formatted);
        Assert.DoesNotContain("must-not-leak", formatted);
    }

    [Fact]
    public void AgentStep_RejectsUndefinedPhaseAndDecisionEnumValues()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "agent_step",
            ResultFragment = new LineDiagnosisResultFragmentDto
            {
                Iteration = 4,
                Phase = (ReActPhase)999,
                Decision = (ReActStepDecision)999,
            },
        });

        Assert.Equal(
            "结果片段：Agent Iteration=4, Phase=N/A, Decision=N/A",
            McpProgressMessageFormatter.Format(mapped));
    }

    [Fact]
    public void LlmDone_FormatsOnlySafeSummaryFields()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "llm_done",
            ResultFragment = new LineDiagnosisResultFragmentDto
            {
                Success = true,
                RootCauseStation = "OP020",
                AllowedStationCodes = ["OP010", "OP020"],
                Confidence = 0.875,
                SuggestedActionCount = 4,
            },
            Payload = new
            {
                Success = false,
                RootCauseStation = "%I1.0=true",
                Confidence = 99,
                SuggestedActionCount = 999,
                RootCauseDescription = "long raw description with %I1.0=true",
                RawResponse = "Authorization: Bearer must-not-leak",
            },
        });

        var formatted = McpProgressMessageFormatter.Format(mapped);

        Assert.Equal(
            "结果片段：Success=true, RootCauseStation=OP020, Confidence=0.875, SuggestedActionCount=4",
            formatted);
        Assert.DoesNotContain("description", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%I1.0", formatted);
        Assert.DoesNotContain("Bearer", formatted);
    }

    [Theory]
    [InlineData("OP999")]
    [InlineData("%I1.0")]
    public void LlmDone_RejectsRootCauseOutsideDiagnosedStationAllowlist(string rootCauseStation)
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "llm_done",
            ResultFragment = new LineDiagnosisResultFragmentDto
            {
                Success = true,
                RootCauseStation = rootCauseStation,
                AllowedStationCodes = ["OP010", "OP020"],
                Confidence = 0.75,
                SuggestedActionCount = 1,
            },
        });

        var formatted = McpProgressMessageFormatter.Format(mapped);

        Assert.Contains("RootCauseStation=N/A", formatted);
        Assert.DoesNotContain(rootCauseStation, formatted);
    }

    [Fact]
    public void UnknownParentEvent_ReturnsSafeGenericProgress()
    {
        var mapped = LineDiagnosisBusinessProgress.Map(new LineDiagnosisProgressEventDto
        {
            Type = "future_internal_event",
            Payload = new { Secret = "must-not-be-forwarded" },
        });

        Assert.Equal("line_stoppage", mapped.Operation);
        Assert.Equal("running", mapped.Stage);
        Assert.Equal("running", mapped.Status);
        Assert.Equal(5, mapped.Progress);
        Assert.Null(mapped.Metrics);
    }

    [Fact]
    public async Task EnergyFlowReporter_MapsChildProgressAndKeepsDetailedEvents()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var rootScope = new DiagnosisProgressScope(
            new DelegateDiagnosisProgressReporter((evt, _) =>
            {
                projected.Add(evt);
                return Task.CompletedTask;
            }),
            0,
            100,
            "line_stoppage");
        var progress = new LineDiagnosisBusinessProgress(
            rootScope,
            evt =>
            {
                detailed.Add(evt);
                return Task.CompletedTask;
            });
        var energyFlowReporter = progress.CreateEnergyFlowReporter("task-3");

        await energyFlowReporter.ReportAsync(EnergyFlowEvent(0, "started"));
        await energyFlowReporter.ReportAsync(EnergyFlowEvent(50, "running"));
        await energyFlowReporter.ReportAsync(EnergyFlowEvent(100, "completed"));

        Assert.Equal(new[] { 70f, 76f, 82f }, projected.Select(evt => evt.Progress));
        Assert.All(projected, evt => Assert.Equal(100, evt.Total));
        Assert.Equal(3, detailed.Count);
        Assert.All(detailed, evt =>
        {
            Assert.Equal("constraint_progress", evt.Type);
            Assert.Equal("task-3", evt.TaskId);
            Assert.IsType<DiagnosisProgressEvent>(evt.Payload);
        });
        Assert.Equal(new[] { "EnergyFlow 0", "EnergyFlow 50", "EnergyFlow 100" },
            detailed.Select(evt => evt.Message));
    }

    [Fact]
    public async Task EnergyFlowReporter_ProjectsSafeResultFragmentsInsideParentRange()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(projected);
        var energyFlowReporter = progress.CreateEnergyFlowReporter("task-result-fragments");

        await energyFlowReporter.ReportAsync(DiagnosisProgressEvent.Running(
            "energy_flow",
            "constraints_evaluated",
            75,
            "结果片段：IO 约束检查完成",
            new DiagnosisProgressMetrics { IssueCount = 3 },
            force: true));
        await energyFlowReporter.ReportAsync(DiagnosisProgressEvent.Running(
            "energy_flow",
            "result_ready",
            95,
            "结果片段：IO 约束诊断结果已生成",
            new DiagnosisProgressMetrics { IssueCount = 4 },
            force: true));

        Assert.Equal([79f, 81.4f], projected.Select(evt => evt.Progress));
        Assert.All(projected, evt => Assert.InRange(evt.Progress, 70, 82));
        Assert.Equal(
            [
                "结果片段：IO 约束检查完成 | IssueCount=3",
                "结果片段：IO 约束诊断结果已生成 | IssueCount=4",
            ],
            projected.Select(McpProgressMessageFormatter.Format));
    }

    [Fact]
    public async Task ParentReporterFailure_DoesNotPreventDetailedEventOrEscape()
    {
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var progress = CreateProgressWithFailingReporter(detailed);

        await progress.EmitAsync(new LineDiagnosisProgressEventDto
        {
            Type = "station_done",
            StationCode = "OP010",
            CompletedCount = 1,
            TotalCount = 2,
        });

        var evt = Assert.Single(detailed);
        Assert.Equal("station_done", evt.Type);
        Assert.Equal("OP010", evt.StationCode);
    }

    [Fact]
    public async Task ChildReporterFailure_DoesNotPreventConstraintProgressOrEscape()
    {
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var progress = CreateProgressWithFailingReporter(detailed);

        await progress.CreateEnergyFlowReporter("task-failure")
            .ReportAsync(EnergyFlowEvent(50, "running"));

        var evt = Assert.Single(detailed);
        Assert.Equal("constraint_progress", evt.Type);
        Assert.Equal("task-failure", evt.TaskId);
        Assert.Equal("EnergyFlow 50", evt.Message);
    }

    [Fact]
    public async Task ReporterCancellationWithoutRequestCancellation_IsBestEffort()
    {
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var progress = new LineDiagnosisBusinessProgress(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((_, _) =>
                    throw new OperationCanceledException("transport stopped")),
                0,
                100,
                "line_stoppage"),
            evt =>
            {
                detailed.Add(evt);
                return Task.CompletedTask;
            });

        await progress.EmitAsync(new LineDiagnosisProgressEventDto { Type = "start" });

        Assert.Single(detailed);
    }

    [Fact]
    public async Task CancelledRequest_PropagatesFromParentAndChildReporters()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var progress = CreateProgressWithFailingReporter(new List<LineDiagnosisProgressEventDto>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => progress.EmitAsync(
            new LineDiagnosisProgressEventDto { Type = "start" },
            cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => progress
            .CreateEnergyFlowReporter("cancelled")
            .ReportAsync(EnergyFlowEvent(50, "running"), cancellation.Token));
    }

    [Fact]
    public async Task RequestCancelledWhileReporterThrows_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var progress = new LineDiagnosisBusinessProgress(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((_, _) =>
                {
                    cancellation.Cancel();
                    throw new InvalidOperationException("transport unavailable");
                }),
                0,
                100,
                "line_stoppage"),
            evt =>
            {
                detailed.Add(evt);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => progress.EmitAsync(
            new LineDiagnosisProgressEventDto { Type = "start" },
            cancellation.Token));

        Assert.Single(detailed);
    }

    [Fact]
    public async Task RequestCancelledWhileReporterCompletes_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new LineDiagnosisBusinessProgress(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((_, _) =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }),
                0,
                100,
                "line_stoppage"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => progress.EmitAsync(
            new LineDiagnosisProgressEventDto { Type = "start" },
            cancellation.Token));
    }

    [Fact]
    public async Task ChildRequestCancelledWhileReporterCompletes_DoesNotEmitDetailedProgress()
    {
        using var cancellation = new CancellationTokenSource();
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var progress = new LineDiagnosisBusinessProgress(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((_, _) =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }),
                0,
                100,
                "line_stoppage"),
            evt =>
            {
                detailed.Add(evt);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => progress
            .CreateEnergyFlowReporter("cancelled-in-flight")
            .ReportAsync(EnergyFlowEvent(50, "running"), cancellation.Token));

        Assert.Empty(detailed);
    }

    [Fact]
    public async Task RunToTerminal_ReportsCompleteOnlyAfterFinalAssemblySucceeds()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(projected);
        var terminalObservedDuringAssembly = true;

        var result = await progress.RunToTerminalAsync(
            _ =>
            {
                terminalObservedDuringAssembly = projected.Count > 0;
                return Task.FromResult("final-result");
            },
            _ => true);

        Assert.Equal("final-result", result);
        Assert.False(terminalObservedDuringAssembly);
        Assert.Equal("complete", Assert.Single(projected).Stage);
    }

    [Fact]
    public async Task RunToTerminal_FinalAssemblyFailureReportsErrorAndPreservesException()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(projected);
        var expected = new InvalidOperationException("final assembly failed");
        var coreCompleted = false;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => progress.RunToTerminalAsync<object>(
            async _ =>
            {
                await Task.CompletedTask;
                coreCompleted = true;
                throw expected;
            },
            _ => true));

        Assert.True(coreCompleted);
        Assert.Same(expected, actual);
        Assert.Equal("error", Assert.Single(projected).Stage);
    }

    [Fact]
    public async Task RunToTerminal_FailureBeforeCoreReportsErrorAndPreservesException()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(projected);
        var expected = new InvalidOperationException("request mapping failed");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => progress.RunToTerminalAsync<object>(
            _ => Task.FromException<object>(expected),
            _ => true));

        Assert.Same(expected, actual);
        Assert.Equal("error", Assert.Single(projected).Stage);
    }

    [Fact]
    public async Task RunToTerminal_TerminalReporterFailurePreservesResultAndException()
    {
        var successful = CreateProgressWithFailingReporter(new List<LineDiagnosisProgressEventDto>());
        var failed = CreateProgressWithFailingReporter(new List<LineDiagnosisProgressEventDto>());
        var expected = new InvalidOperationException("original business failure");

        var result = await successful.RunToTerminalAsync(
            _ => Task.FromResult("original-result"),
            _ => true);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.RunToTerminalAsync<object>(
            _ => Task.FromException<object>(expected),
            _ => true));

        Assert.Equal("original-result", result);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ConcurrentTerminalCalls_ReportExactlyOnce()
    {
        var projected = new System.Collections.Concurrent.ConcurrentQueue<DiagnosisProgressEvent>();
        var progress = new LineDiagnosisBusinessProgress(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((evt, _) =>
                {
                    projected.Enqueue(evt);
                    return Task.CompletedTask;
                }),
                0,
                100,
                "line_stoppage"));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            index % 2 == 0 ? progress.CompleteAsync() : progress.ErrorAsync()));

        Assert.Single(projected);
    }

    [Fact]
    public async Task CompleteTerminal_IsReportedAtOneHundredExactlyOnce()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(projected);

        await progress.CompleteAsync();
        await progress.CompleteAsync();
        await progress.ErrorAsync();

        var terminal = Assert.Single(projected);
        Assert.Equal("complete", terminal.Stage);
        Assert.Equal("completed", terminal.Status);
        Assert.Equal(100, terminal.Progress);
        Assert.Equal("工位停机诊断完成", terminal.Message);
    }

    [Fact]
    public async Task ErrorTerminal_IsSafeAndReportedAtNinetyNineExactlyOnce()
    {
        var projected = new List<DiagnosisProgressEvent>();
        var progress = CreateProgress(projected);

        await progress.ErrorAsync();
        await progress.ErrorAsync();
        await progress.CompleteAsync();

        var terminal = Assert.Single(projected);
        Assert.Equal("error", terminal.Stage);
        Assert.Equal("failed", terminal.Status);
        Assert.Equal(99, terminal.Progress);
        Assert.Equal("工位停机诊断失败", terminal.Message);
    }

    [Fact]
    public async Task TerminalReporterFailure_DoesNotEscapeOrProduceDetailedEvents()
    {
        var detailed = new List<LineDiagnosisProgressEventDto>();
        var completed = CreateProgressWithFailingReporter(detailed);
        var failed = CreateProgressWithFailingReporter(detailed);

        await completed.CompleteAsync();
        await failed.ErrorAsync();

        Assert.Empty(detailed);
    }

    private static LineDiagnosisBusinessProgress CreateProgress(
        List<DiagnosisProgressEvent> projected) =>
        new(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((evt, _) =>
                {
                    projected.Add(evt);
                    return Task.CompletedTask;
                }),
                0,
                100,
                "line_stoppage"));

    private static LineDiagnosisBusinessProgress CreateProgressWithFailingReporter(
        List<LineDiagnosisProgressEventDto> detailed) =>
        new(
            new DiagnosisProgressScope(
                new DelegateDiagnosisProgressReporter((_, _) =>
                    throw new InvalidOperationException("transport unavailable")),
                0,
                100,
                "line_stoppage"),
            evt =>
            {
                detailed.Add(evt);
                return Task.CompletedTask;
            });

    private static DiagnosisProgressEvent EnergyFlowEvent(float progress, string status) => new()
    {
        Operation = "energy_flow",
        Stage = "test",
        Status = status,
        Progress = progress,
        Total = 100,
        Message = $"EnergyFlow {progress}",
    };
}
