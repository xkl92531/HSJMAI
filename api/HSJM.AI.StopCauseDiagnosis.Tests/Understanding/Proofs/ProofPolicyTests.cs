#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class ProofPolicyTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("I1", "%I0.0", "Bool"), new("M1", "%M0.0", "Bool"),
        new("E1", "%E0.1", "Bool")
    ];

    static PipelineRule Members(string op, string? proof, params string[] names) =>
        new(op, names.Select(n => new PipelineSignal(n, "Member")).ToArray(), "x") { Proof = proof };

    // 用户决定（2026-10-02）：不设「待确认」等级——新流程（Code:* / Review:* / AI:*）的瞬时规则满足形状守卫即判故障（XMLV2）
    [Theory]
    [InlineData(UnderstandingProofs.CodeNetwork)]
    [InlineData(UnderstandingProofs.CodeCallSite)]
    [InlineData(UnderstandingProofs.ReviewCallSiteDefaults)]
    [InlineData(UnderstandingProofs.AiNetwork)]
    [InlineData(UnderstandingProofs.AiFbContract)]
    public void NewFlowInstantOutputMutexIsActive(string proof)
    {
        var d = Original353ActivationPolicy.Classify(Members("AT_MOST_ONE", proof, "Q1", "Q2"), Tags);
        Assert.Equal(("Active", "NewFlowOutputMutex"), (d.Status, d.Reason));
        var rows = Original353LegacyRuleMapper.Map(1, 2, Members("AT_MOST_ONE", proof, "Q1", "Q2"), Tags);
        Assert.Equal("XMLV2", rows.Rule.Source);
    }

    [Theory]
    [InlineData(UnderstandingProofs.CodeNetwork)]
    [InlineData(UnderstandingProofs.AiFbContract)]
    public void NewFlowMutexInvolvingAnInputIsNotActive(string proof)
    {
        var d = Original353ActivationPolicy.Classify(Members("AT_MOST_ONE", proof, "Q1", "I1"), Tags);
        Assert.Equal(("Unknown", "NewFlowRelationNeedsTwoOutputs"), (d.Status, d.Reason));
        // 旧流程（无 Proof）的互斥分级不变
        Assert.Equal("Active", Original353ActivationPolicy.Classify(Members("AT_MOST_ONE", null, "Q1", "I1"), Tags).Status);
    }

    static PipelineRule Gate(string source, params string[] targets) =>
        new("REQUIRES_0_WHEN_FALSE", new[] { new PipelineSignal(source, "Source") }.Concat(targets.Select(t => new PipelineSignal(t, "Target"))).ToArray(), "g")
        { Proof = UnderstandingProofs.AiFbContract };

    [Fact]
    public void NewFlowGateIsActiveWhenItsSourceIsATagAndTargetsAreOutputs()
    {
        var active = Original353ActivationPolicy.Classify(Gate("M1", "Q1", "Q2"), Tags);
        Assert.Equal(("Active", "NewFlowGate"), (active.Status, active.Reason));
        Assert.Equal("Unknown", Original353ActivationPolicy.Classify(Gate("M1", "Q1", "I1"), Tags).Status);    // Target 不是 %Q
        Assert.Equal("Unknown", Original353ActivationPolicy.Classify(Gate("DB.Run", "Q1"), Tags).Status);    // Source 不在点表，诊断读不到
        // 旧流程无证明的门控分级不变
        Assert.Equal("Unknown", Original353ActivationPolicy.Classify(Gate("I1", "Q1") with { Proof = null }, Tags).Status);
    }

    // 整计划审查 I-2：%I 作 Source 的新流程门控不判故障（与含 %I 的互斥/同步同一理由：输入/输出映像刷新时刻不同，单次读取会报假故障）
    [Theory]
    [InlineData(UnderstandingProofs.AiFbContract, "I1")]
    [InlineData(UnderstandingProofs.AiNetwork, "I1")]
    [InlineData(UnderstandingProofs.CodeNetwork, "I1")]
    [InlineData(UnderstandingProofs.ReviewCallSiteDefaults, "I1")]
    [InlineData(UnderstandingProofs.AiFbContract, "E1")]   // 德文输入区
    public void NewFlowGateWithAnInputSourceIsNotActive(string proof, string source)
    {
        var d = Original353ActivationPolicy.Classify(Gate(source, "Q1") with { Proof = proof }, Tags);
        Assert.Equal(("Unknown", "NewFlowGateSourceIsInput"), (d.Status, d.Reason));
        Assert.NotEqual("XMLV2", Original353LegacyRuleMapper.Map(1, 2, Gate(source, "Q1") with { Proof = proof }, Tags).Rule.Source);
    }

    [Fact]
    public void FirstPhaseGateWithAnInputSourceIsUnchanged() =>
        Assert.Equal("Active", Original353ActivationPolicy.Classify(Gate("I1", "Q1") with { Proof = Original353FbContractRules.GateProof }, Tags).Status);

    [Fact]
    public void FirstPhaseContractMutexStaysActive() =>
        Assert.Equal("Active", Original353ActivationPolicy.Classify(Members("AT_MOST_ONE", Original353FbContractRules.MutexProof, "Q1", "Q2"), Tags).Status);

    [Fact]
    public void NewFlowSameStateBetweenOutputsIsActiveButWithAnInputIsNot()
    {
        var network = Original353ActivationPolicy.Classify(Members("SAME_STATE", UnderstandingProofs.CodeNetwork, "Q1", "Q2"), Tags);
        Assert.Equal(("Active", "NewFlowOutputSameState"), (network.Status, network.Reason));
        Assert.Equal("Active", Original353ActivationPolicy.Classify(Members("SAME_STATE", UnderstandingProofs.CodeCallSite, "Q1", "Q2"), Tags).Status);
        Assert.Equal("Active", Original353ActivationPolicy.Classify(Members("SAME_STATE", UnderstandingProofs.AiNetwork, "Q1", "Q2"), Tags).Status);
        Assert.Equal("Unknown", Original353ActivationPolicy.Classify(Members("SAME_STATE", UnderstandingProofs.CodeNetwork, "Q1", "I1"), Tags).Status);
        Assert.Equal("Unknown", Original353ActivationPolicy.Classify(Members("SAME_STATE", null, "Q1", "Q2"), Tags).Status);
    }

    static readonly PipelineTag[] BoundaryTags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("Q3", "%Q0.2", "Bool"),
        new("QW", "%QW2", "Word"), new("Dup", "%Q0.3", "Bool"), new("Dup", "%Q0.4", "Bool")
    ];

    [Fact]
    public void CodeProvenRelationNeedsUniqueBooleanMembers()   // Task 1 审查 M4
    {
        foreach (var op in new[] { "SAME_STATE", "AT_MOST_ONE" })
            foreach (var proof in new[] { UnderstandingProofs.CodeNetwork, UnderstandingProofs.AiFbContract })
                foreach (var other in new[] { "QW", "Dup", "Missing" })
                {
                    var d = Original353ActivationPolicy.Classify(Members(op, proof, "Q1", other), BoundaryTags);
                    Assert.Equal(("Unknown", "UniqueBooleanTagRequired"), (d.Status, d.Reason));
                }
    }

    [Fact]
    public void CodeProvenRelationWithThreeMembersIsNotActive()
    {
        foreach (var op in new[] { "SAME_STATE", "AT_MOST_ONE" })
            foreach (var proof in new[] { UnderstandingProofs.CodeNetwork, UnderstandingProofs.CodeCallSite, UnderstandingProofs.AiNetwork })
                Assert.NotEqual("Active", Original353ActivationPolicy.Classify(Members(op, proof, "Q1", "Q2", "Q3"), BoundaryTags).Status);
    }

    [Fact]
    public void CodeProvenRelationWithSettlingIsNotActive()
    {
        foreach (var op in new[] { "SAME_STATE", "AT_MOST_ONE" })
        {
            var aiTimed = Members(op, UnderstandingProofs.AiFbContract, "Q1", "Q2") with { Applicability = "AfterSettling", RequiresSettlingEvidence = true };
            Assert.Equal("RequiresRuntimeGuard", Original353ActivationPolicy.Classify(aiTimed, BoundaryTags).Status);
            var timed = Members(op, UnderstandingProofs.CodeNetwork, "Q1", "Q2") with { Applicability = "AfterSettling" };
            var d = Original353ActivationPolicy.Classify(timed, BoundaryTags);
            Assert.Equal(("RequiresRuntimeGuard", "TemporalOrBypassCondition"), (d.Status, d.Reason));
            var evidence = Members(op, UnderstandingProofs.CodeNetwork, "Q1", "Q2") with { RequiresSettlingEvidence = true };
            Assert.Equal("RequiresRuntimeGuard", Original353ActivationPolicy.Classify(evidence, BoundaryTags).Status);
        }
    }

    [Fact]
    public void ProofKindsAreClassified()
    {
        Assert.True(UnderstandingProofs.IsAi(UnderstandingProofs.AiNetwork));
        Assert.False(UnderstandingProofs.IsAi(UnderstandingProofs.CodeNetwork));
        Assert.True(UnderstandingProofs.IsReviewOnly(UnderstandingProofs.ReviewCallSiteDefaults));
        Assert.True(UnderstandingProofs.IsReviewOnly(UnderstandingProofs.AiFbContract));
        Assert.False(UnderstandingProofs.IsReviewOnly(UnderstandingProofs.CodeCallSite));
        Assert.True(UnderstandingProofs.IsCode(UnderstandingProofs.CodeCallSite));
        Assert.True(UnderstandingProofs.IsCode(UnderstandingProofs.CodeNetwork));
        Assert.False(UnderstandingProofs.IsCode(UnderstandingProofs.ReviewCallSiteDefaults));
        Assert.False(UnderstandingProofs.IsCode("FbContract:Mutex"));
        Assert.False(UnderstandingProofs.IsCode(null));
        Assert.True(UnderstandingProofs.IsNewFlow(UnderstandingProofs.CodeCallSite));
        Assert.True(UnderstandingProofs.IsNewFlow(UnderstandingProofs.AiNetwork));
        Assert.False(UnderstandingProofs.IsNewFlow("FbContract:Mutex"));
        Assert.False(UnderstandingProofs.IsNewFlow(null));
    }

    [Fact]
    public void TimedMustBeFalseTemplatePassesTheAiTextCheck()
    {
        var rule = new PipelineRule("MUST_BE_FALSE", [new("I1", "Target")], "x")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true, Proof = UnderstandingProofs.AiNetwork };
        var text = Original353RuleText.Render(rule, Original353ActivationPolicy.Classify(rule, Tags), Tags);
        Assert.Contains("时间", text.Description);
        Assert.True(Original353RuleText.IsValidAiText(rule, text.Name, text.Description));
    }

    [Fact]
    public void NewFlowRulesDoNotNeedAiWordingToPublish()
    {
        foreach (var proof in new[] { UnderstandingProofs.CodeNetwork, UnderstandingProofs.CodeCallSite,
                     UnderstandingProofs.ReviewCallSiteDefaults, UnderstandingProofs.AiNetwork })
        {
            var rule = Members("AT_MOST_ONE", proof, "Q1", "Q2");
            var generated = new Original353ProjectResult(1, "v", "fp", [new Original353UnitResult(2, [rule], [], [])], 1, 0, []);
            Assert.Null(Record.Exception(() =>
                Original353AiWordingPolicy.RequireComplete(generated, new Dictionary<(long, string), Original353RuleWording>())));
        }
    }

    [Fact]
    public void FirstPhaseContractRulesStillNeedAiWordingToPublish()
    {
        var rule = Members("AT_MOST_ONE", Original353FbContractRules.MutexProof, "Q1", "Q2");
        var generated = new Original353ProjectResult(1, "v", "fp", [new Original353UnitResult(2, [rule], [], [])], 1, 0, []);
        var error = Assert.Throws<PipelineException>(() =>
            Original353AiWordingPolicy.RequireComplete(generated, new Dictionary<(long, string), Original353RuleWording>()));
        Assert.Equal("AiDescriptionsIncomplete:1/1", error.Code);
    }

    [Fact]
    public void NewFlowOnlyUnitsDoNotNeedPlainLanguage()
    {
        Assert.True(Original353AiWordingPlan.NewFlowOnly([Members("AT_MOST_ONE", UnderstandingProofs.CodeNetwork, "Q1", "Q2")]));
        Assert.True(Original353AiWordingPlan.NewFlowOnly([Members("AT_MOST_ONE", UnderstandingProofs.ReviewCallSiteDefaults, "Q1", "Q2"),
            Members("SAME_STATE", UnderstandingProofs.AiNetwork, "Q1", "Q2")]));
        Assert.False(Original353AiWordingPlan.NewFlowOnly([Members("AT_MOST_ONE", UnderstandingProofs.CodeCallSite, "Q1", "Q2"),
            Members("AT_MOST_ONE", null, "Q1", "Q2")]));
        Assert.False(Original353AiWordingPlan.NewFlowOnly([]));
    }

    [Fact]
    public void NewFlowRulesAreBatchedSeparatelyAndTriedOnce()
    {
        var items = new List<(long Unit, PipelineRule Rule)>
        {
            (7, Members("AT_MOST_ONE", UnderstandingProofs.CodeNetwork, "Q1", "Q2")),
            (7, Members("AT_MOST_ONE", null, "Q1", "I1")),
            (7, Members("SAME_STATE", UnderstandingProofs.ReviewCallSiteDefaults, "Q1", "Q2")),
        };
        var batches = Original353AiWordingPlan.Batches(items, x => x.Unit, x => x.Rule);
        Assert.Equal(2, batches.Count);
        Assert.Equal((1, 2, true), (batches[0].Items.Length, batches[0].Attempts, batches[0].RetryEach));
        Assert.Null(Assert.Single(batches[0].Items).Rule.Proof);
        Assert.Equal((2, 1, false), (batches[1].Items.Length, batches[1].Attempts, batches[1].RetryEach));
        Assert.All(batches[1].Items, x => Assert.True(UnderstandingProofs.IsNewFlow(x.Rule.Proof)));
    }
}
