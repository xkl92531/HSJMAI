#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record AiNetworkSignal(string Name, string Role);

/// <summary>Delayed：AI 标的 timing=after_delay。只是「指认」，正确性由 NetworkConstraintValidator 核对。</summary>
public sealed record AiNetworkConstraint(string Operator, IReadOnlyList<AiNetworkSignal> Signals, bool Delayed, double? DelaySeconds,
    IReadOnlyList<string> Evidence);

public sealed record AiNetworkReply(string Understanding, IReadOnlyList<AiNetworkConstraint> Constraints);

/// <summary>解析普通网络 AI 的回复。任何输入都不抛异常：思考块（含只有结束标签的情形）、截断、重复键按整体不可解析或单条跳过处理。</summary>
public static class AiNetworkReplyParser
{
    internal static readonly IReadOnlySet<string> Operators = new HashSet<string>(StringComparer.Ordinal)
        { "AT_MOST_ONE", "SAME_STATE", "EXACTLY_ONE", "IMPLIES", "REQUIRES_0_WHEN_FALSE", "MUST_BE_FALSE" };

    static readonly Regex EvidenceSeparators = new(@"[,，、;；\s]+", RegexOptions.CultureInvariant);

    public static (AiReplyStatus Status, AiNetworkReply? Reply, int Skipped) Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (AiReplyStatus.Empty, null, 0);
        if (AiReplyJson.ObjectText(content) is not { } body) return (AiReplyStatus.Unparseable, null, 0);
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return (AiReplyStatus.Unparseable, null, 0); }
        // JsonObject 对重复键是延迟报错（首次按键访问时抛 ArgumentException），根级访问统一兜住
        JsonArray items;
        string understanding;
        try
        {
            if (root is not JsonObject o || o["constraints"] is not JsonArray arr) return (AiReplyStatus.Unparseable, null, 0);
            items = arr;
            understanding = Str(o, "understanding");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { return (AiReplyStatus.Unparseable, null, 0); }

        var list = new List<AiNetworkConstraint>();
        var skipped = 0;
        foreach (var item in items)
        {
            try
            {
                if (item is not JsonObject c) { skipped++; continue; }
                var op = Str(c, "operator").Trim().ToUpperInvariant();
                if (!Operators.Contains(op) || c["signals"] is not JsonArray signalArray) { skipped++; continue; }
                var signals = new List<AiNetworkSignal>();
                foreach (var x in signalArray)
                {
                    if (x is not JsonObject so) throw new FormatException("signal");
                    var name = Str(so, "name").Trim();
                    var role = Str(so, "role").Trim().ToLowerInvariant() switch
                    {
                        "source" => "Source", "target" => "Target", "member" => "Member", _ => null
                    };
                    if (name.Length == 0 || role is null) throw new FormatException("signal");
                    signals.Add(new(name, role));
                }
                if (signals.Count == 0) { skipped++; continue; }
                double? delay = c["delaySeconds"] switch
                {
                    null => null,
                    JsonValue v when v.TryGetValue<double>(out var d) && double.IsFinite(d) && d >= 0 => d,
                    _ => throw new FormatException("delaySeconds")
                };
                var evidence = c["evidence"] switch
                {
                    null => Array.Empty<string>(),
                    JsonArray arr => arr.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToArray(),
                    JsonValue v when v.TryGetValue<string>(out var str) =>
                        EvidenceSeparators.Split(str).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
                    _ => throw new FormatException("evidence")
                };
                var delayed = string.Equals(Str(c, "timing").Trim(), "after_delay", StringComparison.OrdinalIgnoreCase);
                list.Add(new(op, signals, delayed, delay, evidence));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException) { skipped++; }
        }
        return (AiReplyStatus.Parsed, new AiNetworkReply(understanding, list), skipped);
    }

    static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
