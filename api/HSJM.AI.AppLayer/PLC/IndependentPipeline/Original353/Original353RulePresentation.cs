#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public static class Original353RulePresentation
{
    public static bool IsDoorSignalCheck(PipelineRule rule)
        => rule.Operator == "EXACTLY_ONE" && !string.IsNullOrWhiteSpace(rule.DisplayLabel);

    public static PipelineRule WithLabel(PipelineRule rule, IReadOnlyList<PipelineTag> tags,
        IReadOnlyDictionary<string, string?>? comments)
    {
        if (rule.Operator != "EXACTLY_ONE" || comments is null) return rule;
        var physical = rule.Signals.Where(signal => tags.Count(tag => tag.Name.Equals(signal.Name,
            StringComparison.OrdinalIgnoreCase) && tag.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) == 1).ToArray();
        if (physical.Length != 1 || !comments.TryGetValue(physical[0].Name, out var comment)
            || string.IsNullOrWhiteSpace(comment) || !comment.Contains("安全门", StringComparison.Ordinal)) return rule;
        // Comments supply a display label only. The already-proved operator and signals stay intact.
        var number = Regex.Match(comment, @"(?<number>\d+)\s*(?:#|号)\s*安全门");
        if (!number.Success) number = Regex.Match(comment, @"安全门\s*(?<number>\d+)");
        return rule with { DisplayLabel = number.Success ? number.Groups["number"].Value + "号安全门" : "安全门" };
    }

    public static Original353ProjectResult WithLabels(Original353ProjectResult generated,
        IReadOnlyList<PipelineTag> tags, IReadOnlyDictionary<string, string?> comments)
        => generated with { Units = generated.Units.Select(unit => unit with {
            Rules = unit.Rules.Select(rule => WithLabel(rule, tags, comments)).ToArray(),
            SafetyRules = unit.SafetyRules.Select(item => item with { Rule = WithLabel(item.Rule, tags, comments) }).ToArray()
        }).ToArray() };
}
