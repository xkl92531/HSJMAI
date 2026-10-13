#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class FbContractActivationTests
{
    static readonly PipelineTag[] Tags =
        [new("YV_W", "%Q1.0", "Bool"), new("YV_H", "%Q1.1", "Bool"), new("CYL_W", "%I2.1", "Bool"), new("%I5.0", "%I5.0", "Bool")];

    static PipelineRule Gate(string? proof = Original353FbContractRules.GateProof, string target2 = "YV_H", string[]? upstream = null) =>
        new("REQUIRES_0_WHEN_FALSE", [new("Mode.Estop", "Source"), new("YV_W", "Target"), new(target2, "Target")], "x")
        { Proof = proof, UpstreamSignals = upstream ?? [] };

    [Fact]
    public void ProvenGateWithDbSourceIsActive()
    {
        var decision = Original353ActivationPolicy.Classify(Gate(), Tags);
        Assert.Equal(("Active", "FbBodyProvenGate"), (decision.Status, decision.Reason));
    }

    [Fact]
    public void UnprovenGateIsNotActive()
    {
        Assert.NotEqual("Active", Original353ActivationPolicy.Classify(Gate(proof: null), Tags).Status);
    }

    [Fact]
    public void GateWithInputTargetIsNotActive()
    {
        Assert.NotEqual("Active", Original353ActivationPolicy.Classify(Gate(target2: "CYL_W"), Tags).Status);
    }

    [Fact]
    public void ProvenGateMapsToInstantXmlV2SafetyRule()
    {
        var rows = Original353LegacyRuleMapper.Map(1, 2, Gate(upstream: ["%I5.0"]), Tags);
        Assert.Equal("XMLV2", rows.Rule.Source);
        Assert.Equal("Safety", rows.Rule.RuleType);
        Assert.Contains(Original353FbContractRules.GateProof, rows.Rule.ValidStates);
        Assert.Null(rows.Signals.Single(s => s.SignalName == "Mode.Estop").SignalAddress);
    }

    [Fact]
    public void GateTemplateStatesTheFalseConditionAndUpstream()
    {
        var text = Original353RuleText.Render(Gate(upstream: ["%I5.0"]), new("Active", "FbBodyProvenGate"), Tags);
        Assert.Equal("Mode.Estop_联锁", text.Name);
        Assert.Equal("当 Mode.Estop 为 false 时，YV_W、YV_H 必须为 false。（Mode.Estop 的上游输入：%I5.0）", text.Description);
    }

    [Theory]
    [InlineData("当 Mode.Estop 为 false 时，YV_W、YV_H 必须为 false。", true)]
    [InlineData("当 Mode.Estop 为 false 时，YV_W、YV_H 必须为 false。上游输入 %I5.0。", true)]
    [InlineData("Mode.Estop 断开时 YV_W、YV_H 必须断电。", false)]
    [InlineData("当 Mode.Estop 为 false 时，YV_W、YV_H 必须为 false，参见 %I9.0。", false)]
    // F8：方向必须是「Source 为 false」，把 Source 写成 true 的说明方向反了
    [InlineData("当 Mode.Estop 为 true 时，YV_W、YV_H 必须为 false。", false)]
    [InlineData("当 Mode.Estop 为 TRUE 或为 false 时，YV_W、YV_H 必须为 false。", false)]
    [InlineData("当 Mode.Estop 为false时，YV_W、YV_H 必须为 false。", true)]
    [InlineData("YV_W、YV_H 为 false 时 Mode.Estop 无要求。", false)]
    public void GateAiTextMustStateFalseAndMayCiteOnlyKnownUpstream(string description, bool accepted)
    {
        Assert.Equal(accepted, Original353RuleText.IsValidAiText(Gate(upstream: ["%I5.0"]), "Mode.Estop_联锁", description));
    }
    // 位置矛盾是消抖后的时序规则：模板说明（即 AI 的 VerifiedMeaning 底稿）必须带「时间」，否则 AI 说明全被校验拒绝、发布中止
    [Fact]
    public void TimedContradictionTemplateTextPassesAiValidation()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("CYL_W", "Member"), new("%I5.0", "Member")], "x")
        { Proof = Original353FbContractRules.ContradictionProof, Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        var text = Original353RuleText.Render(rule, Original353ActivationPolicy.Classify(rule, Tags), Tags);
        Assert.True(Original353RuleText.IsValidAiText(rule, text.Name, text.Description), text.Description);
    }
}
