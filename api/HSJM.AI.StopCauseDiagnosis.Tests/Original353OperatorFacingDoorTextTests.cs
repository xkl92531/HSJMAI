#nullable enable
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353OperatorFacingDoorTextTests
{
    private static PipelineRule Rule() => new("EXACTLY_ONE", [
        new("DB20_SafeControl.Gate_Display_FB_Instance[1].q_Open The Door", "Member"),
        new("SafetyDoor1_Open", "Member")], "code-proven inversion, not physical door position");
    private static PipelineTag[] Tags() => [
        new("DB20_SafeControl.Gate_Display_FB_Instance[1].q_Open The Door", "", "Bool"),
        new("SafetyDoor1_Open", "%Q102.0", "Bool")];
    private static Dictionary<string, string?> Comments() => new() { ["SafetyDoor1_Open"] = "1#安全门打开" };

    [Fact]
    public void DoorDescriptionCanUseOperatorLanguageWhileKeepingItsSignalContract()
    {
        const string description = "若1号安全门控制信号异常，请检查相关控制线路。";
        var rule = Rule();
        var mapped = Original353LegacyRuleMapper.Map(1, 2, rule, Tags(), wording:
            new("1号安全门控制信号检查", description, "AIValidated", "完整白话文"), tagComments: Comments());
        Assert.Equal("1号安全门控制信号检查", mapped.Rule.RuleName);
        Assert.Equal(description, mapped.Rule.Description);
        Assert.Equal("EXACTLY_ONE", mapped.Rule.Operator);
        Assert.Equal("XMLV2_REVIEW", mapped.Rule.Source);
        Assert.Equal(2, mapped.Signals.Count);
        using var metadata = JsonDocument.Parse(mapped.Rule.ValidStates!);
        Assert.Equal(rule.Key, metadata.RootElement.GetProperty("RuleKey").GetString());
        Assert.Equal("AIValidated", metadata.RootElement.GetProperty("TextOrigin").GetString());
    }

    [Theory]
    [InlineData("1号安全门控制信号异常，请检查相关控制线路。", true)]
    [InlineData("1号安全门已打开，控制信号异常，请检查。", false)]
    [InlineData("1号安全门未锁好，控制信号异常，请检查。", false)]
    [InlineData("1号安全门控制信号在同一快照中不是一真一假，异常时请检查。", false)]
    [InlineData("2号安全门控制信号异常，请检查相关控制线路。", false)]
    public void OperatorWordingCannotInventADoorPositionOrExposePlcJargon(string description, bool accepted)
    {
        var rule = Original353RulePresentation.WithLabel(Rule(), Tags(), Comments());
        Assert.Equal(accepted, Original353RuleText.IsValidAiText(rule, "1号安全门控制信号检查", description));
        Assert.Equal(Rule().Key, rule.Key);
    }

    [Fact]
    public void DisplayCommentsDoNotInventDoorRulesOrChangeBooleanMeaning()
    {
        var rule = Rule() with { Operator = "AT_MOST_ONE" };
        Assert.Null(Original353RulePresentation.WithLabel(rule, Tags(), Comments()).DisplayLabel);
        var unlabeled = Original353RulePresentation.WithLabel(Rule(), Tags(),
            new Dictionary<string, string?> { ["SafetyDoor1_Open"] = "照明灯" });
        Assert.Null(unlabeled.DisplayLabel);
        Assert.Equal(Rule().Key, unlabeled.Key);
    }
}
