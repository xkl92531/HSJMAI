#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>AI 回复的解析状态：Parsed 读到 JSON 对象；Empty 回复为空；Unparseable 没有 JSON 对象、JSON 不完整（截断）或思考块未闭合。</summary>
public enum AiReplyStatus { Parsed, Empty, Unparseable }

/// <summary>一次 AI 回复解析出的候选事实。SkippedItems：格式不对被跳过的约束与超时报警条数；
/// NonIoConstraints：信号含非 IO 点整条丢弃的约束数；NonIoAlarms：输出或到位信号含非 IO 点整条丢弃的超时报警数。</summary>
public sealed record AiCandidateResult(AiReplyStatus Status, IReadOnlyList<EvalFact> Facts, int SkippedItems, int NonIoConstraints, int NonIoAlarms)
{
    public static readonly AiCandidateResult Empty = new(AiReplyStatus.Empty, [], 0, 0, 0);
    public static readonly AiCandidateResult Unparseable = new(AiReplyStatus.Unparseable, [], 0, 0, 0);
}

/// <summary>把人工标注用的 AI 回复（candidates 命令的约定 JSON）转成最小事实。仅评测用，不进入产品流程。</summary>
public static class AiCandidateFacts
{
    public static AiCandidateResult Parse(string? content, IReadOnlyDictionary<string, string> addresses,
        string category, string block, int? network)
    {
        if (string.IsNullOrWhiteSpace(content)) return AiCandidateResult.Empty;
        if (AiReplyJson.ObjectText(content) is not { } body) return AiCandidateResult.Unparseable;
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { return AiCandidateResult.Unparseable; }
        if (root == null) return AiCandidateResult.Unparseable;

        bool IsIo(string name) => name.Length > 0 && addresses.ContainsKey(name);
        var facts = new List<EvalFact>();
        int skipped = 0, nonIoConstraints = 0, nonIoAlarms = 0;

        foreach (var c in Items(root, "constraints"))
        {
            try
            {
                var op = Str(c, "operator").Trim().ToUpperInvariant();
                var signals = Items(c, "signals").Select(x => (Name: Str(x, "name").Trim(), Role: Role(Str(x, "role")))).ToArray();
                if (op.Length == 0 || signals.Length == 0 || signals.Any(x => x.Name.Length == 0 || x.Role == null)) { skipped++; continue; }
                var groups = c!["targetGroups"] is JsonArray ga && ga.Count > 0
                    ? ga.Select(g => (IReadOnlyList<string>)Items(g).Select(y => Text(y).Trim()).ToArray()).ToArray()
                    : null;
                if (!signals.All(x => IsIo(x.Name)) || groups != null && !groups.SelectMany(g => g).All(IsIo)) { nonIoConstraints++; continue; }
                var bypass = Items(c, "bypass").Select(x => Text(x).Trim()).Where(x => x.Length > 0).ToArray();
                var delayed = string.Equals(Str(c, "timing").Trim(), "after_delay", StringComparison.OrdinalIgnoreCase);
                facts.AddRange(ConstraintFacts.FromRule(op, signals.Select(x => (x.Name, x.Role!)).ToArray(), addresses,
                    new RuleMeta(bypass, groups, Delayed: delayed), true, category, block, network, "ai"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { skipped++; }
        }
        foreach (var a in Items(root, "timeoutAlarms"))
        {
            try
            {
                var alarm = Str(a, "alarm").Trim();
                var output = Str(a, "output").Trim();
                var feedback = Items(a, "feedback").Select(x => Text(x).Trim()).ToArray();
                if (alarm.Length == 0) { skipped++; continue; }
                // 报警位本身可以是 DB 路径，输出与到位信号必须是 IO 点
                if (!IsIo(output) || !feedback.All(IsIo)) { nonIoAlarms++; continue; }
                var fb = feedback.Select(x => x.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal);
                var key = $"TimeoutAlarm|{alarm}|{output.ToUpperInvariant()}|{string.Join("&", fb)}";
                facts.Add(new EvalFact("TimeoutAlarm", key, key, category, block, network, [], "ai"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { skipped++; }
        }
        return new AiCandidateResult(AiReplyStatus.Parsed, facts, skipped, nonIoConstraints, nonIoAlarms);
    }

    static string? Role(string role) => role.Trim().ToLowerInvariant() switch
    {
        "source" => "Source", "target" => "Target", "member" => "Member", _ => null
    };

    // 字段类型可能不合约定：非对象上取属性会抛 InvalidOperationException，由调用方按单项跳过
    static IEnumerable<JsonNode?> Items(JsonNode? node, string name) => Items(node?[name]);
    static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? new JsonArray();
    static string Str(JsonNode? node, string name) => Text(node?[name]);
    static string Text(JsonNode? node) => node is JsonValue v ? v.ToString() : "";
}
