#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>合并后的只删不增过滤（作用于全部来源，含旧流程）：①与代码证明的互斥同对的 SAME_STATE 被证伪；
/// ②含常量输出的规则无诊断价值。条件反馈同样处理；安全规则证据随被删规则一并删除。
/// 不处理 Repairs（被删规则的修复记录保留）与项目级 RequiredContextSignals：目前没有消费方要求它们与 Rules 一致。</summary>
public static class RuleFilters
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    /// <param name="provenMutex">已证明互斥的信号对与其证明来源。只有代码证明（Code:*）的才用来证伪 SAME_STATE；
    /// 依赖接口初值（Review:*）或 AI 指认（AI:*）的不删已有规则。</param>
    public static (Original353ProjectResult Result, int Contradicted, int Constant) Apply(Original353ProjectResult result,
        IReadOnlyCollection<(string A, string B, string? Proof)> provenMutex, IReadOnlySet<string> constantOutputs)
    {
        var mutex = provenMutex.Where(p => p.Proof?.StartsWith("Code:", StringComparison.Ordinal) == true).ToArray();
        int contradicted = 0, constant = 0, safetyDropped = 0;
        bool Contradicts(PipelineRule r) => r.Operator == "SAME_STATE" && mutex.Any(p =>
            !C.Equals(p.A, p.B) && r.Signals.Any(s => C.Equals(s.Name, p.A)) && r.Signals.Any(s => C.Equals(s.Name, p.B)));
        bool OnConstant(PipelineRule r) => r.Signals.Any(s => constantOutputs.Contains(s.Name));
        var units = result.Units.Select(u =>
        {
            var rules = new List<PipelineRule>();
            var dropped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in u.Rules)
            {
                if (Contradicts(r)) { contradicted++; dropped.Add(r.Key); }
                else if (OnConstant(r)) { constant++; dropped.Add(r.Key); }
                else rules.Add(r);
            }
            var feedback = new List<Original353ConditionalFeedback>();
            foreach (var f in u.ConditionalFeedbackRules)
            {
                if (Contradicts(f.Rule)) contradicted++;
                else if (OnConstant(f.Rule)) constant++;
                else feedback.Add(f);
            }
            dropped.ExceptWith(rules.Select(r => r.Key));
            var safety = u.SafetyRules.Where(s => !dropped.Contains(s.Rule.Key)).ToArray();
            safetyDropped += u.SafetyRules.Count - safety.Length;
            return u with { Rules = rules, ConditionalFeedbackRules = feedback, SafetyRules = safety };
        }).ToArray();
        return (result with
        {
            Units = units,
            RuleCount = units.Sum(u => u.Rules.Count),
            ConditionalFeedbackCount = units.Sum(u => u.ConditionalFeedbackRules.Count),
            SafetyRuleCount = result.SafetyRuleCount - safetyDropped
        }, contradicted, constant);
    }
}
