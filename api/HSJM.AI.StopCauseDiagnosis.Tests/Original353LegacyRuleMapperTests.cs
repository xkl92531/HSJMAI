using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using System.Collections.Generic;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353LegacyRuleMapperTests
{
    [Fact]
    public void TemporalFeedbackIsPersistedButNotMarkedAsInstantlyDiagnosable()
    {
        var rule = new PipelineRule("IMPLIES", [new("ValveOutput", "Source"), new("Feedback", "Target")], "feedback");
        var tags = new PipelineTag[] { new("ValveOutput", "%Q10.0", "Bool"), new("Feedback", "%I11.0", "Bool") };

        var mapped = Original353LegacyRuleMapper.Map(123, 456, rule, tags);

        Assert.Equal("XMLV2_TIMED", mapped.Rule.Source);
        Assert.Equal(2, mapped.Signals.Count);
        Assert.Contains(mapped.Signals, s => s.SignalName == "Feedback" && s.SignalAddress == "%I11.0");
        Assert.False(Original353DiagnosisGate.CanEvaluateInstantly(mapped.Rule));
    }

    [Fact]
    public void ArchiveStableUnitIdIsNotStoredAsDatabaseForeignKey()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "mutex");
        var rows = Original353LegacyRuleMapper.Map(123, 456, rule,
            [new("A", "%Q1.0", "Bool"), new("B", "%Q1.1", "Bool")], persistSourceLuId: false);
        Assert.Null(rows.Rule.SourceLuId);
    }

    [Fact]
    public void GuardMetadataSurvivesInTheTestRuleRecord()
    {
        var rule = new PipelineRule("IMPLIES", [new("ValveOutput", "Source"), new("Feedback", "Target")], "feedback")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true, BypassSignals = ["Bypass"] };
        var mapped = Original353LegacyRuleMapper.Map(123, 456, rule,
            [new("ValveOutput", "%Q10.0", "Bool"), new("Feedback", "%I11.0", "Bool")],
            persistSourceLuId: false);

        Assert.Equal("XMLV2_TIMED", mapped.Rule.Source);
        Assert.Equal("ValveOutput_到位反馈", mapped.Rule.RuleName);
        Assert.DoesNotContain('【', mapped.Rule.RuleName);
        Assert.DoesNotContain("暂不判故障", mapped.Rule.Description);
        Assert.Contains("Bypass", mapped.Rule.Description);
        Assert.Contains("AfterSettling", mapped.Rule.ValidStates);
        Assert.Contains("Bypass", mapped.Rule.ValidStates);
        Assert.Contains(rule.Key, mapped.Rule.ValidStates);
    }

    [Theory]
    [InlineData("XMLV2_REVIEW", false)]
    [InlineData("XMLV2_TIMED", false)]
    [InlineData("XMLV2", true)]
    [InlineData("AI", true)]
    public void OneFrameDiagnosisOnlySkipsVersionTwoGuardedRules(string source, bool expected)
    {
        var rule = new HSJM.AI.IDbService.PLC_IOConstraintRuleEntity { Source = source };
        Assert.Equal(expected, Original353DiagnosisGate.CanEvaluateInstantly(rule));
    }

    // 整分支审查 Minor：入库说明统一截到 480 字 +「…」（不论列类型）

    [Fact]
    public void LongDescriptionIsTruncatedTo480CharsWithAnEllipsis()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        var tags = new PipelineTag[] { new("A", "%I1.0", "Bool"), new("B", "%I1.1", "Bool") };
        var comments = new Dictionary<string, string?> { ["A"] = new string('甲', 400), ["B"] = new string('乙', 400) };
        var mapped = Original353LegacyRuleMapper.Map(1, 2, rule, tags, tagComments: comments);
        Assert.Equal(Original353LegacyRuleMapper.DescriptionMaxChars + 1, mapped.Rule.Description!.Length);
        Assert.EndsWith("…", mapped.Rule.Description);
    }

    [Fact]
    public void ShortDescriptionIsKeptAndSurrogatePairsAreNotSplit()
    {
        Assert.Equal("短说明", Original353LegacyRuleMapper.TruncateDescription("短说明"));
        var exact = new string('x', Original353LegacyRuleMapper.DescriptionMaxChars);
        Assert.Same(exact, Original353LegacyRuleMapper.TruncateDescription(exact));
        var pair = new string('x', Original353LegacyRuleMapper.DescriptionMaxChars - 1) + "😀" + "tail";
        var cut = Original353LegacyRuleMapper.TruncateDescription(pair);
        Assert.Equal(new string('x', Original353LegacyRuleMapper.DescriptionMaxChars - 1) + "…", cut);
    }
}
