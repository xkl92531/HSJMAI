using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>测试库发布规则的程序段归属（evaluate 与 candidates 共用）：ValidStates.LogicUnitId 能解析且是归档中的程序段就用它，
/// 否则按信号用 UnitLocator 定位；同时带出旁路、目标分组与延时标记。</summary>
static class RuleUnits
{
    public sealed record Attributed(EvalDb.RuleRow Rule, RuleMeta Meta, PipelineUnit? Unit);

    public static IReadOnlyList<Attributed> Attribute(IEnumerable<EvalDb.RuleRow> rules, IReadOnlyList<PipelineUnit> units, UnitLocator locator)
    {
        var byId = units.ToDictionary(u => u.Id);
        return rules.Select(rule =>
        {
            var (bypass, groups, luText, delayed) = EvalDb.Meta(rule.ValidStates);
            var unit = long.TryParse(luText, out var lu) && byId.TryGetValue(lu, out var found) ? found : locator.Find(rule.Signals);
            return new Attributed(rule, new RuleMeta(bypass, groups, delayed), unit);
        }).ToArray();
    }
}
