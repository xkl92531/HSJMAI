using System.Collections.Generic;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353RuleTextTests
{
    [Fact]
    public void TimedFeedbackDescriptionOnlyStatesTheApplicableRelation()
    {
        var rule = new PipelineRule("IMPLIES", [new("OP100_YV04_H", "Source"), new("OP100_CYL04_1H", "Target")], "技术说明")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true, BypassSignals = ["Bypass_CYL[8]"] };
        var tags = new PipelineTag[] { new("OP100_YV04_H", "%Q1.0", "Bool"), new("OP100_CYL04_1H", "%I2.0", "Bool") };

        var rows = Original353LegacyRuleMapper.Map(1, 2, rule, tags);

        Assert.Equal("OP100_YV04_H_到位反馈", rows.Rule.RuleName);
        Assert.Contains("OP100_YV04_H", rows.Rule.Description);
        Assert.Contains("OP100_CYL04_1H", rows.Rule.Description);
        Assert.Contains("动作等待时间", rows.Rule.Description);
        Assert.Contains("Bypass_CYL[8]", rows.Rule.Description);
        Assert.DoesNotContain("手动模式", rows.Rule.Description);
        Assert.DoesNotContain("暂不判故障", rows.Rule.Description);
        Assert.DoesNotContain("%Q1.0", rows.Rule.Description);
        Assert.DoesNotContain("技术说明", rows.Rule.Description);
        Assert.Contains("RawGeneratorDescription", rows.Rule.ValidStates);
    }

    [Fact]
    public void MutexUsesFormalStyleWithoutTechnicalGeneratorWording()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "结构性补全");
        var rows = Original353LegacyRuleMapper.Map(1, 2, rule,
            [new("A", "%Q1.0", "Bool"), new("B", "%Q1.1", "Bool")]);
        Assert.Equal("A_互斥", rows.Rule.RuleName);
        Assert.Contains("最多允许一个", rows.Rule.Description);
        Assert.DoesNotContain("结构性补全", rows.Rule.Description);
    }

    [Fact]
    public void AiTextCannotOmitSignalsOrClaimInstantFeedback()
    {
        var rule = new PipelineRule("IMPLIES", [new("Q", "Source"), new("I", "Target")], "raw")
        { RequiresSettlingEvidence = true };
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "Q动作后I立即到位。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "动作时间后检查Q。"));
        Assert.True(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "Q发出动作命令并超过动作等待时间后，I应到位。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "【待动作时序】Q_到位反馈",
            "Q发出动作命令并超过动作等待时间后，I应到位。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "Q发出动作命令并超过动作等待时间后，I无需到位；手动模式未发命令时不判故障，缺少时序证据暂不判故障。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "【待执行条件】Q发出动作命令并超过动作等待时间后检查I；手动模式未发命令时不判故障，缺少时序证据暂不判故障。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "Q发出动作命令并超过动作等待时间后检查I；手动模式未发命令时不判故障；此处要写暂不判故障。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈", "Q发出动作命令并超过动作等待时间后，I应到位；证据不足时暂不判故障。"));
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈",
            "Q发出动作命令并超过动作等待时间后检查I；手动模式未发命令时不判故障，缺少时序证据暂不判故障。" + new string('说', 200)));
    }

    [Fact]
    public void AiPromptIncludesPlainLanguageCodeEvidenceAndFixedContract()
    {
        var rule = new PipelineRule("EXACTLY_ONE", [new("Internal", "Member"), new("Physical", "Member")], "raw");
        var prompt = Original353RuleTextAi.BuildPrompt("k1", rule, "白话文：线圈受 FB 输出反相驱动", ["Coil Q = NOT FB.Q"]);
        Assert.Contains("白话文：线圈受 FB 输出反相驱动", prompt);
        Assert.Contains("Coil Q = NOT FB.Q", prompt);
        Assert.Contains("EXACTLY_ONE", prompt);
        Assert.Contains("Internal", prompt);
        Assert.Contains("Physical", prompt);
        Assert.DoesNotContain("<FlgNet", prompt);
        Assert.Contains("不可修改", Original353RuleTextAi.SystemPrompt);
        Assert.Contains("命名和注释", Original353RuleTextAi.SystemPrompt);
    }

    [Fact]
    public void AiResponseMustHaveSameKeyAndValidDescription()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        var accepted = Original353RuleTextAi.TryParse("[{\"key\":\"k1\",\"name\":\"A_互斥\",\"description\":\"A、B互斥，最多允许一个为true。\"}]",
            new Dictionary<string, PipelineRule> { ["k1"] = rule });
        Assert.Single(accepted);
        Assert.Empty(Original353RuleTextAi.TryParse("[{\"key\":\"wrong\",\"name\":\"A_互斥\",\"description\":\"A、B互斥。\"}]",
            new Dictionary<string, PipelineRule> { ["k1"] = rule }));
    }

    [Fact]
    public void WordingOverrideChangesDescriptionButNotProvedRuleNameOrStructure()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        var tags = new PipelineTag[] { new("A", "%Q1.0", "Bool"), new("B", "%Q1.1", "Bool") };
        var original = Original353LegacyRuleMapper.Map(1, 2, rule, tags);
        var revised = Original353LegacyRuleMapper.Map(1, 2, rule, tags, wording:
            new Original353RuleWording("A_互斥检查", "A、B互斥，最多允许一个为 true。", "AIValidated", "接线白话文"));
        Assert.Equal(original.Rule.Operator, revised.Rule.Operator);
        Assert.Equal(original.Rule.Source, revised.Rule.Source);
        Assert.Equal(original.Signals.Select(s => (s.SignalName, s.SignalRole, s.SignalAddress)),
            revised.Signals.Select(s => (s.SignalName, s.SignalRole, s.SignalAddress)));
        Assert.Equal("A_互斥", revised.Rule.RuleName);
        using var evidence = System.Text.Json.JsonDocument.Parse(revised.Rule.ValidStates);
        Assert.Equal("接线白话文", evidence.RootElement.GetProperty("PlainLanguage").GetString());
    }

    [Fact]
    public void RuleNameKeepsItsProvedPurposeWhenAiReturnsOnlyASignalName()
    {
        var rule = new PipelineRule("IMPLIES", [new("Q", "Source"), new("I", "Target")], "raw")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        var rows = Original353LegacyRuleMapper.Map(1, 2, rule,
            [new("Q", "%Q1.0", "Bool"), new("I", "%I2.0", "Bool")], wording:
            new Original353RuleWording("Q", "Q发出命令且超过动作等待时间后，I应到位。", "AIValidated"));

        Assert.Equal("Q_到位反馈", rows.Rule.RuleName);
        Assert.Contains("Q发出命令", rows.Rule.Description);
    }

    [Fact]
    public void PlainLanguageCacheKeyChangesWithCodeOrIoMapping()
    {
        var unit = new PipelineUnit(1, "BlockA", 2, "FBD", "<FlgNet/>", "", []);
        var first = Original353RuleTextService.PlainLanguageKey(unit, "tags-a");
        Assert.Equal(first, Original353RuleTextService.PlainLanguageKey(unit, "tags-a"));
        Assert.NotEqual(first, Original353RuleTextService.PlainLanguageKey(unit with { RawContent = "<FlgNet changed='1'/>" }, "tags-a"));
        Assert.NotEqual(first, Original353RuleTextService.PlainLanguageKey(unit, "tags-b"));
    }

    [Fact]
    public void SafetyRelationUsesWiredPhysicalOutputForShortDisplayName()
    {
        var rule = new PipelineRule("EXACTLY_ONE", [
            new("DB20_SafeControl.Gate_Display_FB_Instance[1].q_Open The Door", "Member"),
            new("SafetyDoor1_Open", "Member")], "raw");
        var tags = new PipelineTag[] {
            new("DB20_SafeControl.Gate_Display_FB_Instance[1].q_Open The Door", "", "Bool"),
            new("SafetyDoor1_Open", "%Q102.0", "Bool")
        };
        var mapped = Original353LegacyRuleMapper.Map(1, 2, rule, tags, wording:
            new Original353RuleWording("DB20_SafeControl.Gate_Display_FB_Instance[1].q_Open The Door_安全门互斥",
                "DB20_SafeControl.Gate_Display_FB_Instance[1].q_Open The Door 与 SafetyDoor1_Open 在程序执行后的同一快照一真一假，证据不足时暂不判故障。", "AIValidated", "白话文"));
        Assert.Equal("SafetyDoor1_Open_反相关系", mapped.Rule.RuleName);
        Assert.DoesNotContain("【待执行条件】", mapped.Rule.Description);
        Assert.DoesNotContain("暂不判故障", mapped.Rule.Description);
    }

    [Fact]
    public void ExplanationUsesNameAndCommentWithoutIoAddress()
    {
        var rule = new PipelineRule("IMPLIES", [new("ValveQ", "Source"), new("FeedbackI", "Target")], "raw")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        var tags = new PipelineTag[] { new("ValveQ", "%Q10.0", "Bool"), new("FeedbackI", "%I11.0", "Bool") };
        var comments = new Dictionary<string, string?> {
            ["ValveQ"] = "气爪缩回输出",
            ["FeedbackI"] = "气爪缩回到位检测"
        };

        var mapped = Original353LegacyRuleMapper.Map(1, 2, rule, tags, tagComments: comments);

        Assert.DoesNotContain("【待动作时序】", mapped.Rule.Description);
        Assert.Contains("ValveQ（气爪缩回输出）", mapped.Rule.Description);
        Assert.Contains("FeedbackI（气爪缩回到位检测）", mapped.Rule.Description);
        Assert.DoesNotContain("%Q10.0", mapped.Rule.Description);
        Assert.DoesNotContain("%I11.0", mapped.Rule.Description);
        Assert.DoesNotContain("点位：", mapped.Rule.Description);
        Assert.DoesNotContain("手动模式", mapped.Rule.Description);
    }

    [Fact]
    public void IncorrectCommentCannotChangeIoDirectionOrRuleStructure()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("OutputA", "Member"), new("OutputB", "Member")], "raw");
        var tags = new PipelineTag[] { new("OutputA", "%Q1.0", "Bool"), new("OutputB", "%Q1.1", "Bool") };
        var mapped = Original353LegacyRuleMapper.Map(1, 2, rule, tags, tagComments:
            new Dictionary<string, string?> { ["OutputA"] = "误写为输入点" });
        Assert.Contains("OutputA（误写为输入点）", mapped.Rule.Description);
        Assert.Equal("AT_MOST_ONE", mapped.Rule.Operator);
        Assert.Equal("%Q1.0", mapped.Signals[0].SignalAddress);
    }

    [Fact]
    public void ConciseMultiFeedbackWordingNeedsOnlyCodeRelationAndGuards()
    {
        var rule = new PipelineRule("IMPLIES", [
            new("OP020_YV01_W", "Source"), new("OP020_CYL01_1W", "Target"),
            new("OP020_CYL01_2W", "Target"), new("OP020_CYL01_3W", "Target"),
            new("OP020_CYL01_4W", "Target")], "raw")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true,
            BypassSignals = ["DB210_OP020_User.Cmd.ByPass_CYL[1]"] };
        const string text = "OP020_YV01_W 发出动作命令并超过等待时间，且旁路 DB210_OP020_User.Cmd.ByPass_CYL[1] 关闭时，OP020_CYL01_1W、OP020_CYL01_2W、OP020_CYL01_3W、OP020_CYL01_4W 应同时到位。";
        Assert.True(Original353RuleText.IsValidAiText(rule, "OP020_YV01_W", text));
    }

    [Fact]
    public void PlainLanguageUsesFaithfulTranslatorWithoutAlwaysTrue()
    {
        var b = new FlgNetBuilder();
        var coil = b.Part("Coil"); b.Wire(FlgNetBuilder.Id(b.Access("Y")), FlgNetBuilder.Pin(coil, "operand"));
        var unit = new PipelineUnit(1, "BlockA", 2, "LAD", b.Build(), "", [], 4);
        var text = Original353RuleTextService.RenderPlainLanguage(unit);
        Assert.StartsWith("**BlockA** 网络 4（LAD）", text);
        Assert.Contains("Y := 〔未接线〕", text);
        Assert.DoesNotContain("始终成立", text);
    }
}
