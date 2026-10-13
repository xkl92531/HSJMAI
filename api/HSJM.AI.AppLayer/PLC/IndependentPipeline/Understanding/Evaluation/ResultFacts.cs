#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>生成结果 → 评测事实，口径与发布器入库一致：每个程序段 Rules ∪ ConditionalFeedbackRules 按 Key 去重、同 Key 优先取延时类，
/// 来源按 Original353ActivationPolicy 分级；超时报警位另成事实（归属产出它的调用点所在程序段），到位矛盾报警不计。</summary>
public static class ResultFacts
{
    public static IReadOnlyList<EvalFact> From(PipelineSnapshot source, Original353ProjectResult result,
        IReadOnlyList<UnderstandingAlarm> alarms, CategoryMap map)
    {
        var tags = source.Tags;
        var addresses = tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Address, StringComparer.OrdinalIgnoreCase);
        var byId = source.Units.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
        var facts = new List<EvalFact>();
        foreach (var unit in result.Units)
        {
            if (!byId.TryGetValue(unit.LogicUnitId, out var pu)) continue;
            var category = map.Of(pu);
            var rules = unit.Rules.Concat(unit.ConditionalFeedbackRules.Select(f => f.Rule))
                .GroupBy(r => r.Key).Select(g => g.OrderByDescending(r => r.RequiresSettlingEvidence).First());
            foreach (var rule in rules)
            {
                var delayed = rule.RequiresSettlingEvidence || rule.Applicability == "AfterSettling";
                facts.AddRange(ConstraintFacts.FromRule(rule.Operator, rule.Signals.Select(s => (s.Name, s.Role)).ToArray(), addresses,
                    new RuleMeta(rule.BypassSignals, rule.TargetGroups, delayed), true, category, pu.BlockName, pu.NetworkOrder,
                    OriginOf(rule, tags)));
            }
        }
        foreach (var located in alarms.Where(a => a.Alarm.Kind == "Timeout"))
        {
            var alarm = located.Alarm;
            var pu = byId.TryGetValue(located.UnitId, out var x) ? x : null;
            facts.Add(ConstraintFacts.FromTimeoutAlarm(alarm.AlarmSymbol, alarm.Output, alarm.Feedback, alarm.Bypass,
                map.Of(pu), pu?.BlockName ?? "?", pu?.NetworkOrder, "ALARM"));
        }
        return facts;
    }

    public static string OriginOf(PipelineRule rule, IReadOnlyList<PipelineTag> tags) =>
        Original353ActivationPolicy.Classify(rule, tags).Status switch
        {
            "ModeCheck" => "XMLV2_MODE", "Active" => "XMLV2", "RequiresRuntimeGuard" => "XMLV2_TIMED", _ => "XMLV2_REVIEW"
        };
}
