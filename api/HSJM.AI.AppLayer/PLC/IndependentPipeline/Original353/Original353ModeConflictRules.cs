#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public static class Original353ModeConflictRules
{
    public static bool IsModeRule(PipelineRule rule) => rule.Applicability == "ModeConflict"
        && rule.Operator == "AT_MOST_ONE" && rule.Signals.Count == 2 && rule.Signals.All(s => s.Role == "Member");
    public static string Label(string blockName)
    {
        var match = Regex.Match(blockName, @"_OP0*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number)
            ? "OP" + number.ToString("000") : blockName;
    }

    public static Original353ProjectResult Append(Original353ProjectResult generated, PipelineSnapshot source,
        IReadOnlyList<Original353ManualModeBinding> bindings)
    {
        var additions = new Dictionary<long, List<PipelineRule>>();
        foreach (var binding in bindings.Where(b => b.AutoRunVariablePath is not null))
        {
            // Resolve the concrete three distinct Bool InOut operands, never infer a pair from similar names.
            var matches = source.Units.Where(unit => HasBoundModeController(unit, binding)).ToArray();
            if (matches.Length != 1) continue;
            if (!additions.TryGetValue(matches[0].Id, out var rules)) additions[matches[0].Id] = rules = [];
            foreach (var auto in new[] { binding.AutoVariablePath, binding.AutoRunVariablePath! })
                rules.Add(new("AT_MOST_ONE", [new(binding.BlockName + "." + auto, "Member"),
                    new(binding.BlockName + "." + binding.VariablePath, "Member")],
                    "模式冲突诊断检查：两个独立布尔模式位不得同时开启，按FC3010_ModeControl的实际InOut接线绑定，不依赖I/Q地址或裸引脚名。") {
                    Applicability = "ModeConflict", DisplayLabel = Label(binding.BlockName)
                });
        }
        var units = generated.Units.Select(unit => additions.TryGetValue(unit.LogicUnitId, out var extra)
            ? unit with { Rules = unit.Rules.Concat(extra).DistinctBy(rule => rule.Key).ToArray() } : unit).ToArray();
        return generated with { Units = units, RuleCount = units.Sum(unit => unit.Rules.Count) };
    }

    private static bool HasBoundModeController(PipelineUnit unit, Original353ManualModeBinding binding)
    {
        if (string.IsNullOrWhiteSpace(unit.RawContent)) return false;
        XElement root;
        try { root = XElement.Parse(unit.RawContent); } catch (System.Xml.XmlException) { return false; }
        if (root.Name.LocalName != "FlgNet") return false;
        var parts = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts")?.Elements().ToArray() ?? [];
        var wires = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires")?.Elements().ToArray() ?? [];
        var access = parts.Where(e => e.Name.LocalName == "Access" && (string?)e.Attribute("Scope") == "GlobalVariable")
            .Where(e => e.Attribute("UId") is not null).ToDictionary(e => (string)e.Attribute("UId")!,
                e => string.Join('.', e.Elements().Where(x => x.Name.LocalName == "Symbol")
                    .SelectMany(x => x.Elements()).Select(x => (string?)x.Attribute("Name") ?? "")));
        foreach (var call in parts.Where(e => e.Name.LocalName == "Call"))
        {
            var info = call.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
            if ((string?)info?.Attribute("Name") != "FC3010_ModeControl") continue;
            var pairs = new[] { ("ManMode", binding.VariablePath), ("AutoMode", binding.AutoVariablePath), ("RunMode", binding.AutoRunVariablePath!) };
            bool Matches(string pin, string path)
            {
                if (!info!.Elements().Any(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Name") == pin
                    && (string?)e.Attribute("Section") == "InOut" && (string?)e.Attribute("Type") == "Bool")) return false;
                var found = wires.Where(w => w.Elements().Any(c => c.Name.LocalName == "NameCon"
                    && (string?)c.Attribute("UId") == (string?)call.Attribute("UId") && (string?)c.Attribute("Name") == pin)).ToArray();
                if (found.Length != 1 || found[0].Elements().Count() != 2) return false;
                var id = (string?)found[0].Elements().FirstOrDefault(c => c.Name.LocalName == "IdentCon")?.Attribute("UId");
                return id is not null && access.GetValueOrDefault(id) == binding.BlockName + "." + path;
            }
            if (pairs.All(pair => Matches(pair.Item1, pair.Item2))) return true;
        }
        return false;
    }

    internal static RuleEvaluation Evaluate(PLC_IOConstraintRuleEntity rule,
        IReadOnlyList<Original353ManualModeBinding> bindings, IReadOnlyDictionary<string, bool?> values)
    {
        var unknown = new RuleEvaluation { Verdict = RuleVerdict.Undetermined, Bucket = UndeterminedBucket.Other,
            Issue = new DiagnosticIssue { Variable = rule.RuleName, RelatedRule = rule.RuleName,
                Status = "Unknown", Category = "采集失败", Severity = "Info", Reason = "模式信号未完整读到，无法检查模式冲突。" } };
        if (rule.Source != "XMLV2_MODE" || rule.Operator != "AT_MOST_ONE") return unknown;
        string[] pair;
        try {
            using var doc = JsonDocument.Parse(rule.ValidStates ?? "");
            pair = doc.RootElement.GetProperty("ModeConflictSignals").EnumerateArray()
                .Select(s => s.GetProperty("Name").GetString() ?? "").ToArray();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return unknown; }
        if (pair.Length != 2 || !bindings.Any(b => pair.Contains(b.BlockName + "." + b.VariablePath)
            && (pair.Contains(b.BlockName + "." + b.AutoVariablePath)
                || b.AutoRunVariablePath is not null && pair.Contains(b.BlockName + "." + b.AutoRunVariablePath)))) return unknown;
        if (pair.Any(name => !values.TryGetValue(name, out var value) || value is null)) return unknown;
        if (!pair.All(name => values[name] == true)) return new RuleEvaluation { Verdict = RuleVerdict.Passed };
        return new RuleEvaluation { Verdict = RuleVerdict.Violated, Issue = new DiagnosticIssue {
            Variable = rule.RuleName, RelatedRule = rule.RuleName, ConstraintOperator = "AT_MOST_ONE",
            Category = "CONSTRAINT", Severity = "Warning", Status = string.Join("、", pair.Select(name => name + "=true")),
            Reason = pair.Any(name => name.EndsWith(".AutoRunMod", StringComparison.Ordinal))
                ? "自动运行与手动模式同时开启，模式状态冲突，请检查模式选择。"
                : "自动模式与手动模式同时开启，模式状态冲突，请检查模式选择。",
            ExpectedValue = "自动与手动不能同时开启"
        } };
    }
}
