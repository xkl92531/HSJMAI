#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public static class Original353AiWordingPolicy
{
    public static IReadOnlyList<PipelineRule> Rules(Original353UnitResult unit) => unit.Rules
        .Concat(unit.ConditionalFeedbackRules.Select(group => group.Rule))
        .GroupBy(rule => rule.Key)
        .Select(group => group.OrderByDescending(rule => rule.RequiresSettlingEvidence).First()).ToArray();

    public static bool IsAccepted(PipelineRule rule, Original353RuleWording? wording)
        => wording is { Origin: "AIValidated" or "AICache" }
           && !string.IsNullOrWhiteSpace(wording.PlainLanguage)
           && Original353RuleText.IsValidAiText(rule, wording.Name, wording.Description);

    public static void RequireComplete(Original353ProjectResult generated,
        IReadOnlyDictionary<(long UnitId, string RuleKey), Original353RuleWording> wording)
    {
        // 新流程规则（Proof=AI:*/Review:*/Code:*）仍会尝试 AI 说明，但缺失/不合格时由映射器退回模板，不阻断发布（spec §10）。
        var expected = generated.Units.SelectMany(unit => Rules(unit)
            .Where(rule => !UnderstandingProofs.IsNewFlow(rule.Proof))
            .Select(rule => (unit.LogicUnitId, Rule: rule))).ToArray();
        var missing = expected.Count(item => !wording.TryGetValue((item.LogicUnitId, item.Rule.Key), out var text)
            || !IsAccepted(item.Rule, text));
        if (missing > 0) throw new PipelineException($"AiDescriptionsIncomplete:{missing}/{expected.Length}");
    }
}
