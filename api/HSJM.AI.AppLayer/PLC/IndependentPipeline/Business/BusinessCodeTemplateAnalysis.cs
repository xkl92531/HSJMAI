#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Pure source analysis. Stable Boolean slices may be instantiated; a recognized numeric
/// monitor is evidence of wiring only, not a proof of a normal-operation or elapsed-time expectation.</summary>
public static class BusinessCodeTemplateAnalysis
{
    public const string Version = "business-code-template-1:stable-slices3:monitor-unknown1";
    private static readonly HashSet<string> BooleanParts = new(StringComparer.Ordinal)
        { "Contact", "Coil", "A", "AND", "And", "O", "OR", "Or", "NOT", "Not" };
    private static readonly HashSet<string> OtherKnownParts = new(StringComparer.Ordinal)
        { "SCoil", "RCoil", "Add", "Sub", "Mul", "Div", "Move", "Ge", "Gt", "Le", "Lt", "Eq", "Ne", "PContact", "NContact", "PBox", "NBox" };

    public static BusinessTemplateAnalysis Analyze(BusinessDescription template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<BusinessDecision>();
        BusinessTemplateAnalysis Finish() => new(template, Version,
            "纯代码模板分析；仅单写普通线圈稳定快照关系可确证，不调用模型。" +
            (unresolved.Count == 0 ? " 已完成当前普通布尔模板分析；不等于调用点绑定或全部运行时覆盖。" :
                " 未解决（Unknown）：" + string.Join("; ", unresolved.Order(StringComparer.Ordinal))),
            decisions, unresolved.Count == 0 ? "AnalyzedNotInstantiated" : "PartiallyAnalyzedNotInstantiated");

        var sources = template.Evidence.Where(e => e.Kind == "SourceStructure").ToArray();
        if (template.Status != "TemplateRendered" || sources.Length == 0)
        {
            unresolved.Add("MissingRequiredSource:" + template.Status);
            return Finish();
        }
        // The existing slice engine consumes selected rendered equations. Missing source or a
        // truncated projection must never certify prose as a complete writer inventory.
        if (sources.Any(e => !template.ModelText.Contains("[" + e.Id + "] " + e.Text, StringComparison.Ordinal)))
        {
            unresolved.Add("MissingRequiredSource:SourceProjectionIncomplete");
            return Finish();
        }
        foreach (var limitation in template.Limitations) unresolved.Add("SourceLimitation:" + limitation);
        foreach (var callee in template.Callees) unresolved.Add("InterproceduralSemanticsUnknown:" + callee);
        var sourceCoils = 0;
        foreach (var source in sources)
        {
            var start = source.Text.IndexOf('<');
            var end = source.Text.LastIndexOf('>');
            if (start < 0 || end < start) { unresolved.Add("MissingRequiredSource:" + source.Id); return Finish(); }
            try
            {
                using var reader = XmlReader.Create(new StringReader(source.Text[start..(end + 1)]),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
                var xml = XElement.Load(reader);
                sourceCoils += xml.Descendants().Count(e => e.Name.LocalName == "Part" && (string?)e.Attribute("Name") == "Coil");
                if (xml.Name.LocalName != "FlgNet") unresolved.Add("UnsupportedSourceLanguage:" + xml.Name.LocalName);
                if (xml.Descendants().Any(e => e.Name.LocalName == "Call")) unresolved.Add("InterproceduralSemanticsUnknown:" + source.Id);
                foreach (var kind in xml.Descendants().Where(e => e.Name.LocalName == "Part").Select(e => (string?)e.Attribute("Name") ?? ""))
                    if (!BooleanParts.Contains(kind)) unresolved.Add((OtherKnownParts.Contains(kind) ? "StateOrNumericSemanticsUnknown:" : "UnknownInstruction:") + kind);
            }
            catch (XmlException) { unresolved.Add("MalformedSource:" + source.Id); return Finish(); }
        }
        var flow = template.Evidence.Where(e => e.Kind == "ControlFlow").ToArray();
        if (flow.Length == 0) unresolved.Add("NoBooleanEquations");
        if (sourceCoils != flow.Count(e => Regex.IsMatch(e.Text, @"^[PS]\d+ := .+ \(Coil\) \[source")))
            unresolved.Add("BooleanProjectionIncomplete");
        foreach (var fact in flow)
        {
            var match = Regex.Match(fact.Text, @"^[PS]\d+ := (?<expr>.+) \(Coil\) \[source");
            if (!template.ModelText.Contains("[" + fact.Id + "]", StringComparison.Ordinal) || !match.Success ||
                Regex.Matches(match.Groups["expr"].Value, @"[A-Za-z_][A-Za-z_0-9]*").Any(m =>
                    !Regex.IsMatch(m.Value, @"^[PS]\d+$") && m.Value is not ("AND" or "OR" or "NOT" or "TRUE" or "FALSE" or "Bool" or "true" or "false")))
                unresolved.Add("UnknownBooleanSemantics:" + fact.Id);
        }
        var symbols = (template.Symbols ?? []).Where(s => s.BlockId == template.BlockId).ToArray();
        var allowed = template.SignalAliases.Concat(symbols.Where(s => s.Section is "Input" or "Output" or "InOut")
                .Where(s => !s.Path.Contains('.') && !s.Path.Contains('[')).Select(s => new KeyValuePair<string, string>(s.Alias, "formal:" + s.Alias)))
            .GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var view = template with { SignalAliases = allowed };
        using var inventory = JsonDocument.Parse(BusinessTemplateInference.StableAssignmentSlices(template,
            symbols.Where(s => s.Section == "Output" && s.DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase)).Select(s => s.Alias).ToArray()));
        if (!inventory.RootElement.TryGetProperty("equationTargets", out var targets))
            unresolved.Add("WriterInventoryUnknown:" + inventory.RootElement.GetProperty("scope").GetString());
        else
        {
            var eligible = targets.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
            foreach (var fact in flow)
            {
                var target = Regex.Match(fact.Text, @"^(?<n>[PS]\d+) := ");
                if (target.Success && !eligible.Contains(target.Groups["n"].Value)) unresolved.Add("AssignmentNotProven:" + target.Groups["n"].Value);
            }
        }
        foreach (var candidate in BusinessTemplateInference.StableSliceCandidates(template))
        {
            var decision = BusinessCandidateValidator.Validate(view, candidate);
            if (candidate.Signals.Any(s => !symbols.Any(p => p.Alias == s.Name &&
                p.DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase) && p.Section is "Input" or "Output" or "InOut") &&
                (!s.Name.StartsWith("S", StringComparison.Ordinal) || !template.SignalTypes.TryGetValue(s.Name, out var type) || !type.Equals("Bool", StringComparison.OrdinalIgnoreCase))))
                decision = new(candidate, "Unknown", "NonPublicBooleanTemplateSignal");
            if (decision.Status == "Validated") decision = decision with { Code = "TemplateCodeDerivedStableSlice" };
            else unresolved.Add("StableSliceNotProven:" + decision.Code);
            decisions.Add(decision);
        }
        var numeric = BusinessNumericEvidence.Build(template);
        foreach (var add in numeric.Where(f => f.Instruction == "Add"))
        {
            var counters = add.Outputs.Where(o => o.WriteKind == "EnabledNumericWrite").Select(o => o.Target).ToHashSet(StringComparer.Ordinal);
            if (counters.Count == 0) continue;
            unresolved.Add("TemplateMonitorInitialRangeUnknown:" + add.EvidenceId + ":" + add.Uid);
            if (add.DataType is "Real" or "LReal") unresolved.Add("TemplateMonitorFloatingPointProgressUnknown:" + add.EvidenceId + ":" + add.Uid);
            if (add.Enable.Contains("EDGE(", StringComparison.Ordinal)) unresolved.Add("TemplateMonitorClockProgressUnknown:" + add.EvidenceId + ":" + add.Uid);
            foreach (var threshold in numeric.Where(f => f.BlockRef == add.BlockRef && f.Instruction is "Ge" or "Gt" &&
                f.Inputs.TryGetValue("in1", out var counter) && counters.Contains(counter)))
                if (!threshold.Inputs.TryGetValue("in2", out var value) || !Regex.IsMatch(value, @"^[A-Za-z]+\([+-]?[0-9]+(?:\.[0-9]+)?\)$"))
                    unresolved.Add("TemplateMonitorDynamicThresholdUnknown:" + threshold.EvidenceId + ":" + threshold.Uid);
            foreach (var write in numeric.Where(f => f.BlockRef == add.BlockRef && f.Instruction == "Move" && f.Outputs.Any(o => counters.Contains(o.Target))))
                unresolved.Add("TemplateMonitorResetRecurrenceUnknown:" + write.EvidenceId + ":" + write.Uid);
        }
        // Do not use existence of a chain as a liveness proof. In particular stable input values
        // neither entail recurring clock edges nor exclude NaN/negative/overflowed initial counters.
        var chains = BusinessTemplateInference.BuildMonitorChains(template, numeric);
        if (chains.Count > 32) unresolved.Add("TemplateMonitorBudgetExceeded");
        foreach (var chain in chains.Take(32))
        {
            var reason = "TemplateMonitorNormalExpectationUnknown:ProgressAndNormalOperationNotProven";
            unresolved.Add(reason + ":" + chain.Counter);
            foreach (var reset in chain.ResetBehaviors)
                unresolved.Add("TemplateMonitorEdgeResetOnlyStructureProven:" + reset.Uid + ":NoRepeatedEdgeInFixedPendingPhase");
            var candidate = new BusinessCandidate("IMPLIES", [new(chain.ActivityOutput, "Source"), new(chain.CompletionOutput, "Target")],
                "接线锁定活动/完成及监视路径；初值范围、数值前进性、足够监视次数和正常运行前提尚未证明。", chain.EvidenceIds,
                chain.Conditions, "StableDiagnosticSnapshot");
            decisions.Add(new(candidate, "Unknown", reason));
        }
        return Finish();
    }
}
