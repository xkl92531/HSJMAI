#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353ActivationDecision(string Status, string Reason);

/// <summary>Conservative boundary between XML recognition and legacy instant diagnosis.
/// The legacy rule table has no durable settling, bypass or source-version contract.</summary>
public static class Original353ActivationPolicy
{
    public static Original353ActivationDecision Classify(PipelineRule rule, IReadOnlyList<PipelineTag> tags)
    {
        if (rule is null || tags is null || rule.Signals is null || rule.Signals.Count == 0)
            return new("Unknown", "InvalidRuleOrTags");
        if (Original353ModeConflictRules.IsModeRule(rule))
            return new("ModeCheck", "SourceBoundModeConflictPolicy");
        if (rule.Operator == "IMPLIES_ANY")
            return new("RequiresRuntimeGuard", "AnyOfFeedbackNeedsSettling");
        if (rule.RequiresSettlingEvidence || rule.Applicability == "AfterSettling" || rule.BypassSignals.Count > 0)
            return new("RequiresRuntimeGuard", "TemporalOrBypassCondition");
        if (rule.Operator == "REQUIRES_0_WHEN_FALSE" && rule.Proof == Original353FbContractRules.GateProof)
            return ClassifyProvenGate(rule, tags);

        var mapped = new Dictionary<string, PipelineTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in rule.Signals)
        {
            if (signal is null || string.IsNullOrWhiteSpace(signal.Name)) return new("Unknown", "InvalidSignal");
            var matches = tags.Where(t => string.Equals(t.Name, signal.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].DataType, "Bool", StringComparison.OrdinalIgnoreCase))
                return new("Unknown", "UniqueBooleanTagRequired");
            mapped[signal.Name] = matches[0];
        }

        if (rule.Operator is "IMPLIES" or "ALL_TRUE")
        {
            var source = rule.Signals.SingleOrDefault(s => s.Role == "Source");
            if (source is null) return new("Unknown", "SingleSourceRequired");
            var targets = rule.Signals.Where(s => s.Role is "Target" or "Member").ToArray();
            if (targets.Length == 0) return new("Unknown", "TargetsRequired");
            if (mapped[source.Name].Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)
                && targets.Any(t => mapped[t.Name].Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)))
                return new("RequiresRuntimeGuard", "OutputToInputFeedbackNeedsSettling");
            return new("Unknown", "SourceInvariantNotProvenForInstantDiagnosis");
        }

        // 新流程（Code:* / Review:* / AI:*）的瞬时规则直接判故障（用户决定 2026-10-02：不设「待确认」等级，不靠人来确认；
        // 延时类已在上面归入 RequiresRuntimeGuard）。形状守卫：成员是点表里唯一的 Bool（上面已核对），
        // 互斥/同步恰好两个 Member 且都是 %Q（含 %I 的同步/互斥不判：输入/输出映像刷新时刻不同），
        // 门控一个 Source（点表里的点，诊断才读得到，且不是 %I——与含 %I 的互斥/同步同一理由：急停等 %I 刚变的那个周期里
        // 同一次读取拿到新的输入映像与上周期写出的输出映像，单次读取就会报假故障；第一期 FbContract:Gate 不走这里）
        // + Target 全是 %Q；其它新流程瞬时规则不判
        if (Understanding.UnderstandingProofs.IsNewFlow(rule.Proof))
        {
            bool AllQ(IEnumerable<PipelineSignal> signals) =>
                signals.All(s => mapped[s.Name].Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase));
            if (rule.Operator is "AT_MOST_ONE" or "SAME_STATE")
                return rule.Signals.Count == 2 && rule.Signals.All(s => s.Role == "Member") && AllQ(rule.Signals)
                    ? new("Active", rule.Operator == "AT_MOST_ONE" ? "NewFlowOutputMutex" : "NewFlowOutputSameState")
                    : new("Unknown", "NewFlowRelationNeedsTwoOutputs");
            if (rule.Operator == "REQUIRES_0_WHEN_FALSE")
            {
                var sources = rule.Signals.Where(s => s.Role == "Source").ToArray();
                var targets = rule.Signals.Where(s => s.Role == "Target").ToArray();
                if (sources.Length != 1 || targets.Length == 0 || sources.Length + targets.Length != rule.Signals.Count || !AllQ(targets))
                    return new("Unknown", "GateShapeInvalid");
                var sourceAddress = mapped[sources[0].Name].Address.Trim();
                if (sourceAddress.StartsWith("%I", StringComparison.OrdinalIgnoreCase) || sourceAddress.StartsWith("%E", StringComparison.OrdinalIgnoreCase))
                    return new("Unknown", "NewFlowGateSourceIsInput");
                return new("Active", "NewFlowGate");
            }
            return new("Unknown", "OperatorRequiresSeparateSourceProof");
        }

        if (rule.Operator == "AT_MOST_ONE" && rule.Signals.Count == 2
            && rule.Signals.All(s => s.Role == "Member"))
            return new("Active", "BooleanMutex");

        return new("Unknown", "OperatorRequiresSeparateSourceProof");
    }

    /// <summary>FB 程序体证明的门控：Source 可为 DB 变量（不在点表），Target 必须是唯一的 %Q Bool 点。</summary>
    static Original353ActivationDecision ClassifyProvenGate(PipelineRule rule, IReadOnlyList<PipelineTag> tags)
    {
        var sources = rule.Signals.Where(s => s.Role == "Source").ToArray();
        var targets = rule.Signals.Where(s => s.Role == "Target").ToArray();
        if (sources.Length != 1 || targets.Length == 0 || sources.Length + targets.Length != rule.Signals.Count)
            return new("Unknown", "GateShapeInvalid");
        foreach (var target in targets)
        {
            var matches = tags.Where(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].DataType, "Bool", StringComparison.OrdinalIgnoreCase)
                || !matches[0].Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase))
                return new("Unknown", "GateTargetMustBeUniqueBoolOutput");
        }
        return new("Active", "FbBodyProvenGate");
    }
}
