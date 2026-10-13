#nullable enable
using System.Linq;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class RuleModelTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"),
        new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool"), new("I3", "%I0.2", "Bool")
    ];

    static PipelineRule Mutex(string? proof) =>
        new("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "m") { Proof = proof };

    [Fact]
    public void TargetGroupsAndTraceDoNotChangeTheKey()
    {
        var plain = new PipelineRule("IMPLIES_ANY", [new("Q1", "Source"), new("I1", "Target"), new("I2", "Target")], "x");
        var rich = plain with
        {
            TargetGroups = [["I1"], ["I2"]],
            Trace = new PipelineRuleTrace("AI:FbContract", "m", ["FB/5/1"], "k")
        };
        Assert.Equal(plain.Key, rich.Key);
    }

    [Fact]
    public void AiIdentifiedInstantMutexIsActive()   // 用户决定（2026-10-02）：新流程瞬时规则直接判故障，不设待确认
    {
        Assert.Equal("Active", Original353ActivationPolicy.Classify(Mutex(null), Tags).Status);
        var ai = Original353ActivationPolicy.Classify(Mutex(UnderstandingProofs.AiFbContract), Tags);
        Assert.Equal("Active", ai.Status);
        Assert.Equal("NewFlowOutputMutex", ai.Reason);
    }

    [Fact]
    public void ImpliesAnyIsAlwaysTimed()
    {
        var rule = new PipelineRule("IMPLIES_ANY", [new("Q1", "Source"), new("I1", "Target"), new("I2", "Target")], "x")
        { TargetGroups = [["I1"], ["I2"]], Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        Assert.Equal("RequiresRuntimeGuard", Original353ActivationPolicy.Classify(rule, Tags).Status);
    }

    [Fact]
    public void ImpliesAnyTemplateNamesEveryGroupAndMentionsTime()
    {
        var rule = new PipelineRule("IMPLIES_ANY",
            [new("Q1", "Source"), new("I1", "Target"), new("I2", "Target"), new("I3", "Target")], "x")
        { TargetGroups = [["I1", "I2"], ["I3"]], Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        var text = Original353RuleText.Render(rule, new("RequiresRuntimeGuard", ""), Tags);
        Assert.Contains("I1、I2", text.Description);
        Assert.Contains(" 或 ", text.Description);
        Assert.Contains("I3", text.Description);
        Assert.Contains("时间", text.Description);
        Assert.True(Original353RuleText.IsValidAiText(rule, text.Name, text.Description));
    }

    static PipelineRule AnyOfThree() => new("IMPLIES_ANY",
        [new("Q1", "Source"), new("I1", "Target"), new("I2", "Target"), new("I3", "Target")], "x")
    { TargetGroups = [["I1", "I2"], ["I3"]], Applicability = "AfterSettling", RequiresSettlingEvidence = true };

    [Fact]
    public void ImpliesAnyCanonicalTextStaysValid()
    {
        var rule = AnyOfThree();
        var text = Original353RuleText.Render(rule, new("RequiresRuntimeGuard", ""), Tags);
        Assert.True(Original353RuleText.IsValidAiText(rule, text.Name, text.Description));
    }

    [Fact]
    public void ImpliesAnyRejectsAllAtOnceWording()
    {
        Assert.False(Original353RuleText.IsValidAiText(AnyOfThree(), "Q1_到位反馈",
            "当 Q1 发出命令且超过动作等待时间时，I1、I2、I3 应同时到位。"));
    }

    [Fact]
    public void ImpliesAnyWithOrButWithoutTimeIsRejected()
    {
        Assert.False(Original353RuleText.IsValidAiText(AnyOfThree(), "Q1_到位反馈",
            "当 Q1 发出命令时，I1、I2 或 I3 应到位。"));
    }

    [Fact]
    public void ImpliesAnyEmptyTargetGroupsFallsBackToAllTargets()
    {
        var rule = AnyOfThree() with { TargetGroups = [] };
        var text = Original353RuleText.Render(rule, new("RequiresRuntimeGuard", ""), Tags);
        Assert.Contains("I1、I2、I3 应到位", text.Description);
        Assert.True(Original353RuleText.IsValidAiText(rule, text.Name, text.Description));
    }

    [Fact]
    public void MapperWritesTargetGroupsAndTraceIntoValidStates()
    {
        var rule = new PipelineRule("IMPLIES_ANY", [new("Q1", "Source"), new("I1", "Target"), new("I2", "Target")], "x")
        {
            TargetGroups = [["I1"], ["I2"]], Applicability = "AfterSettling", RequiresSettlingEvidence = true,
            Proof = UnderstandingProofs.AiFbContract, Trace = new PipelineRuleTrace("AI:FbContract", "m1", ["FB/5/1"], "ck")
        };
        var rows = Original353LegacyRuleMapper.Map(1, 2, rule, Tags);
        Assert.Equal("XMLV2_TIMED", rows.Rule.Source);
        using var doc = JsonDocument.Parse(rows.Rule.ValidStates!);
        var groups = doc.RootElement.GetProperty("TargetGroups").EnumerateArray()
            .Select(g => g.EnumerateArray().Select(x => x.GetString()).ToArray()).ToArray();
        Assert.Equal(new[] { new[] { "I1" }, new[] { "I2" } }, groups);
        Assert.Equal("ck", doc.RootElement.GetProperty("Trace").GetProperty("CacheKey").GetString());
        Assert.Equal(3, rows.Signals.Count);
    }
}
