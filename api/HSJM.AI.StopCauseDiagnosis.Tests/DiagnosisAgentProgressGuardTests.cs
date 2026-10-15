using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class DiagnosisAgentProgressGuardTests
{
    private const string UnsupportedClaim = "OP020通信中断";

    [Theory]
    [InlineData(ReActPhase.Thought, ReActStepDecision.Complete)]
    [InlineData(ReActPhase.Thought, ReActStepDecision.CallTool)]
    [InlineData(ReActPhase.Reflection, ReActStepDecision.Complete)]
    [InlineData(ReActPhase.Reflection, ReActStepDecision.Continue)]
    [InlineData(ReActPhase.Action, ReActStepDecision.Continue)]
    [InlineData(ReActPhase.Observation, ReActStepDecision.Continue)]
    public async Task Public_progress_does_not_publish_unverified_model_or_tool_prose(
        ReActPhase phase, ReActStepDecision decision)
    {
        var source = new ReActStep
        {
            Phase = phase, Decision = decision, Iteration = 3, DurationMs = 17,
            Timestamp = DateTime.UtcNow, Success = true, Content = UnsupportedClaim,
            ErrorMessage = UnsupportedClaim, InterceptReason = UnsupportedClaim,
            ToolCall = new() { ToolName = "GetStationSummary", Arguments = UnsupportedClaim, CallId = "call-1" }
        };
        ReActStep published = null;
        var callback = DiagnosisAgentService.CreatePublicProgressCallback(step =>
        {
            published = step;
            return Task.CompletedTask;
        });
        await callback(source);

        Assert.NotNull(published);
        Assert.DoesNotContain(UnsupportedClaim, string.Join("\n", published.Content,
            published.ErrorMessage, published.InterceptReason, published.ToolCall?.Arguments));
        Assert.NotSame(source, published);
        Assert.Equal(phase, published.Phase);
        Assert.Equal(decision, published.Decision);
        Assert.Equal(source.Iteration, published.Iteration);
        Assert.Equal(source.DurationMs, published.DurationMs);
        Assert.Equal(source.Timestamp, published.Timestamp);
        Assert.Equal(source.Success, published.Success);
        Assert.Equal("GetStationSummary", published.ToolCall?.ToolName);
        Assert.Equal(UnsupportedClaim, source.Content);
        Assert.Equal(UnsupportedClaim, source.ToolCall.Arguments);

        // The engine consumes the unchanged Complete.Content after its progress callback.
        var final = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP020",
            RootCauseDescription = source.Content, RawResponse = source.Content, Confidence = .9 };
        LineDiagnosisLlmService.ApplyEvidenceArbitration(final, []);
        Assert.Null(final.RootCauseStation);
        Assert.DoesNotContain(UnsupportedClaim, final.RootCauseDescription);
        Assert.Equal(UnsupportedClaim, final.RawResponse);
    }

    [Fact]
    public void Absent_progress_subscriber_does_not_add_a_callback() =>
        Assert.Null(DiagnosisAgentService.CreatePublicProgressCallback(null));

    [Fact]
    public async Task English_progress_uses_fixed_english_stage_status()
    {
        ReActStep published = null;
        var callback = DiagnosisAgentService.CreatePublicProgressCallback(step =>
        {
            published = step;
            return Task.CompletedTask;
        }, "en-US");
        await callback(new() { Phase = ReActPhase.Thought, Decision = ReActStepDecision.Complete,
            Content = UnsupportedClaim, Success = true });
        Assert.Contains("evidence", published.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UnsupportedClaim, published.Content);
    }
}
