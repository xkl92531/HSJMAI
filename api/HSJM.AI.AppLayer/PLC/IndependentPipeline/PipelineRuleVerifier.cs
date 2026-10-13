#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Exhaustive Boolean proof at the documented LU evaluation point, never a field-accuracy estimate.</summary>
public static class PipelineRuleVerifier
{
    public const string Version = "independent-proof-3";
    public static string GenerationKey(string descriptionFingerprint,string modelVersion) =>
        PipelineHash.Of($"{descriptionFingerprint}:{modelVersion}:{Version}");
    public static PipelineValidation Validate(PipelineRule rule, PipelineDescription description)
    {
        if (description.IsTemplate) return new(false,"TemplateRequiresBinding");
        if (rule.Signals is null || rule.Signals.Count == 0) return new(false, "InvalidRoles");
        if (rule.Signals.Any(s => s is null || !description.Signals.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
            return new(false, "UnknownSignal");
        if (rule.Signals.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rule.Signals.Count)
            return new(false, "DuplicateSignal");
        var members = rule.Signals.Where(s => s.Role == "Member").ToList();
        var sources = rule.Signals.Where(s => s.Role == "Source").ToList();
        var targets = rule.Signals.Where(s => s.Role == "Target").ToList();
        var rolesValid = rule.Operator switch {
            "AT_MOST_ONE" or "EXACTLY_ONE" or "SAME_STATE" => members.Count >= 2 && members.Count == rule.Signals.Count,
            "IMPLIES" or "REQUIRES_0" => sources.Count == 1 && targets.Count >= 1 && sources.Count + targets.Count == rule.Signals.Count,
            "ALL_TRUE" => sources.Count == 1 && members.Count >= 1 && sources.Count + members.Count == rule.Signals.Count,
            "MUST_BE_FALSE" => targets.Count == 1 && rule.Signals.Count == 1,
            _ => false
        };
        if (!rolesValid) return new(false, "UnsupportedOperatorOrRoles");
        if (description.Limitations.Count > 0) return new(false, "ProofRequiresUnresolvedSemantics");
        var outputNames = description.Assignments.Select(a => a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Prior states and elapsed-time flags are independent universal variables. This over-approximates
        // reachable histories: it can reject valid temporal invariants, but cannot prove IN => TON.Q.
        var atoms = description.Assignments.SelectMany(a => PipelineTerms.Atoms(a.Expression)).ToArray();
        if (atoms.Any(a => a.Kind == "Signal" && outputNames.Contains(a.Name!))) return new(false, "SequentialDependenciesRequireProof");
        if (!rule.Signals.Any(s => outputNames.Contains(s.Name))) return new(false, "NoProgramEvidence");
        try { return new PipelineBooleanProof().Prove(rule,description) ? new(true) : new(false,"Counterexample"); }
        catch(PipelineException ex) { return new(false,ex.Code); }
    }
    public static bool Evaluate(LogicTerm term, IReadOnlyDictionary<string, bool> values) => term.Kind switch {
        "Signal" => values[term.Name!],
        "Previous" => values[PipelineTerms.PreviousKey(term.Name!)],
        "TimerElapsed" => values[PipelineTerms.TimerKey(term.Name!)],
        "Compare" => values[PipelineTerms.AtomKey(term)],
        "Constant" when term.Name is "true" or "false" => term.Name == "true",
        "And" when term.Children?.Count > 0 => term.Children.All(t => Evaluate(t, values)),
        "Or" when term.Children?.Count > 0 => term.Children.Any(t => Evaluate(t, values)),
        "Not" when term.Children?.Count == 1 => !Evaluate(term.Children[0], values),
        _ => throw new InvalidOperationException("Unsupported expression")
    };
    public static string Describe(PipelineRule rule) {
        string Names(string role) => string.Join("、", rule.Signals.Where(s => s.Role == role).Select(s => s.Name));
        return rule.Operator switch {
            "AT_MOST_ONE" => $"{Names("Member")} 最多一个成立。",
            "EXACTLY_ONE" => $"{Names("Member")} 必须恰好一个成立。",
            "SAME_STATE" => $"{Names("Member")} 状态必须一致。",
            "IMPLIES" => $"当 {Names("Source")} 成立时，{Names("Target")} 必须成立。",
            "ALL_TRUE" => $"当 {Names("Source")} 成立时，{Names("Member")} 必须全部成立。",
            "REQUIRES_0" => $"当 {Names("Source")} 成立时，{Names("Target")} 必须为 false。",
            "MUST_BE_FALSE" => $"{Names("Target")} 必须为 false。",
            _ => throw new PipelineException("UnsupportedOperator")
        };
    }
}
