#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory;

/// <summary>Executable caller wiring, not the flattened input list or AI description.
/// Timeout is proved by the PLC's own timer/error output, never by sleeping after a diagnosis click.</summary>
internal sealed record FeedbackLogicPlan(string Command, LogicTerm Feedback, string Alarm)
{
    public IEnumerable<string> Symbols => FullFeedbackLogic.Leaves(Feedback).Append(Command).Append(Alarm)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

internal static class FullFeedbackLogic
{
    public static IReadOnlyList<FeedbackLogicPlan> Extract(string xml, string? command = null)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        try
        {
            var root = XElement.Parse(xml);
            if (root.Name.LocalName != "FlgNet") return [];
            var net = new Original353FlgNet(root);
            var locals = root.Descendants().Where(e => e.Name.LocalName == "Access"
                && (string?)e.Attribute("Scope") == "LocalVariable")
                .Select(Original353FlgNet.Symbol).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var plans = new List<FeedbackLogicPlan>();
            foreach (var call in net.Calls)
            {
                var info = call.Elements().SingleOrDefault(e => e.Name.LocalName == "CallInfo");
                // This is the existing supported valve contract. Unrecognised FB semantics remain unknown.
                if ((string?)info?.Attribute("Name") != "FB3002_Valve"
                    || (string?)info.Attribute("BlockType") != "FB") continue;
                var id = (string?)call.Attribute("UId") ?? "";
                foreach (var (output, input, alarm) in new[] { ("QFwd", "IWP", "FwdErr"), ("QBkd", "IHP", "BkdErr") })
                {
                    bool Typed(string pin, string section) => info.Elements().Count(e => e.Name.LocalName == "Parameter"
                        && (string?)e.Attribute("Name") == pin && (string?)e.Attribute("Type") == "Bool"
                        && (string?)e.Attribute("Section") == section) == 1;
                    if (!Typed(output, "Output") || !Typed(input, "Input") || !Typed(alarm, "Output")) continue;
                    var bound = net.BoundOutput(id, output);
                    if (bound is null || command is not null && !string.Equals(bound, command, StringComparison.OrdinalIgnoreCase)
                        || net.HasOtherWrite(bound, id, output)) continue;
                    var expression = net.Pin(id, input);
                    var error = net.BoundOutput(id, alarm);
                    if (expression is null || !Supported(expression) || string.IsNullOrWhiteSpace(error)
                        || !error.Contains('.') || net.HasOtherWrite(error, id, alarm)
                        || locals.Overlaps(Leaves(expression))) continue;
                    plans.Add(new(bound, expression, error));
                }
            }
            return plans;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or ArgumentException)
        { return []; }
    }

    internal static IReadOnlyList<string>? GlobalWriters(IEnumerable<string> networks)
    {
        var writes = new List<string>();
        foreach (var xml in networks)
        {
            if (string.IsNullOrWhiteSpace(xml)) continue;
            try
            {
                var root = XElement.Parse(xml);
                if (root.Name.LocalName != "FlgNet") return null;
                // Include unsupported callers/parts as possible writers, not only successfully compiled plans.
                writes.AddRange(new Original353FlgNet(root).GlobalWrites()
                    .DistinctBy(w => (w.Symbol, w.PartId, w.Port)).Select(w => w.Symbol));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or ArgumentException)
            { return null; }
        }
        return writes;
    }

    internal static bool Overlaps(string a, string b) => a == Original353FlgNet.UnknownLocation
        || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "[", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "[", StringComparison.OrdinalIgnoreCase);

    static bool Supported(LogicTerm term, int depth = 0) => depth < 64 && term.Kind switch
    {
        "Signal" => !string.IsNullOrWhiteSpace(term.Name),
        "Constant" => bool.TryParse(term.Name, out _),
        "Not" => term.Children is { Count: 1 } && Supported(term.Children[0], depth + 1),
        "And" or "Or" => term.Children is { Count: > 0 } && term.Children.All(c => Supported(c, depth + 1)),
        _ => false
    };

    internal static IEnumerable<string> Leaves(LogicTerm term) => term.Kind == "Signal" ? [term.Name!]
        : (term.Children ?? []).SelectMany(Leaves);

    internal static bool? Evaluate(LogicTerm term, Func<string, bool?> read) => term.Kind switch
    {
        "Signal" => read(term.Name!),
        "Constant" => bool.TryParse(term.Name, out var value) ? value : null,
        "Not" when term.Children is { Count: 1 } => !Evaluate(term.Children[0], read),
        "And" => Fold(term.Children, read, false),
        "Or" => Fold(term.Children, read, true),
        _ => null
    };

    static bool? Fold(IReadOnlyList<LogicTerm>? children, Func<string, bool?> read, bool decisive)
    {
        if (children is not { Count: > 0 }) return null;
        var values = children.Select(c => Evaluate(c, read)).ToArray();
        return values.Contains(decisive) ? decisive : values.Any(v => v is null) ? null : !decisive;
    }

    internal static string Format(LogicTerm term) => term.Kind switch
    {
        "Signal" or "Constant" => term.Name ?? "Unknown",
        "Not" => "NOT(" + Format(term.Children![0]) + ")",
        _ => "(" + string.Join(term.Kind == "And" ? " AND " : " OR ", term.Children!.Select(Format)) + ")"
    };

    public static RuleEvaluation Judge(PLC_IOConstraintRuleEntity rule, FeedbackLogicPlan plan,
        IReadOnlyList<PLC_ExactReadResult> reads, IReadOnlyDictionary<string, PLC_IOTagEntity> tags,
        DateTimeOffset now)
    {
        var values = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var name in plan.Symbols)
        {
            var rows = reads.Where(r => string.Equals(r.RequestKey, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var row = rows.Length == 1 ? rows[0] : null;
            var age = row?.SourceTimestamp is { } time ? now - time : (TimeSpan?)null;
            var good = row is { Quality: PLC_ExactReadQuality.Good } && string.IsNullOrEmpty(row.ErrorCode)
                && age is { } delta && delta >= TimeSpan.FromSeconds(-1) && delta <= TimeSpan.FromSeconds(5)
                && PlcLiveSignals.TryConvertToBool(row.Value, out _);
            values[name] = good && PlcLiveSignals.TryConvertToBool(row!.Value, out var value) ? value : null;
            if (values[name] is null) unknown.Add(name + "（" + (row?.ErrorCode ?? "缺值、质量异常或时间戳无效") + "）");
        }
        bool? Read(string name) => values.GetValueOrDefault(name);
        var feedback = Evaluate(plan.Feedback, Read);
        var command = Read(plan.Command);
        var alarm = Read(plan.Alarm);
        var evidence = $"完整到位逻辑：{Format(plan.Feedback)}；有效反馈={feedback?.ToString() ?? "Unknown"}；"
            + string.Join("；", values.Select(kv => $"{kv.Key}={kv.Value?.ToString() ?? "Unknown"}"));
        DiagnosticIssue Issue(string reason, string category = "CONSTRAINT") => new()
        {
            Variable = plan.Command, RelatedRule = rule.RuleName, Category = category,
            ConstraintOperator = rule.Operator, Severity = "Info", Reason = reason + "；" + evidence,
            SignalDetails = values.Select(kv => new SignalDetail { SignalName = kv.Key,
                CurrentValue = kv.Value ?? false, ReadStatus = kv.Value is null ? "未采到可信的新鲜值" : null,
                Address = tags.GetValueOrDefault(kv.Key)?.Address,
                SignalAddress = tags.GetValueOrDefault(kv.Key)?.Address }).ToList()
        };
        // An active native error remains evidence even after the command/feedback has changed (latched alarm).
        if (alarm == true)
        {
            var issue = Issue("PLC 超时报警已触发（可能锁存，需核对报警复位状态）；不能仅凭原始输入或等待信号排除故障");
            issue.Severity = "Critical";
            issue.Status = "True";
            return new() { Verdict = RuleVerdict.Violated, Issue = issue };
        }
        if (alarm == false && (command == false || command == true && feedback == true))
            return RuleEvaluation.PassedResult;
        var message = unknown.Count > 0 ? "完整反馈证据不全，未采到：" + string.Join("、", unknown)
            : "动作已发出但有效反馈未满足；PLC 超时报警未触发，缺可信超时证据，暂不判故障";
        return new() { Verdict = RuleVerdict.Undetermined, Bucket = UndeterminedBucket.Other, Issue = Issue(message) };
    }
}
