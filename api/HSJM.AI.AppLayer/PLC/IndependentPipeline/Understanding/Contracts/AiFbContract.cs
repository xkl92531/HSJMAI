#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record AiContractRelation(string Kind, IReadOnlyList<string> Outputs, IReadOnlyList<string> Inputs,
    string? PositionVar, string? DelayParam, double? DelaySeconds, string? Alarm, IReadOnlyList<string> Evidence, string? Text)
{
    /// <summary>expectedStateAlarm：决定期望状态的配置输入形参（可为空）。其它种类为空。</summary>
    public IReadOnlyList<string> ConfigInputs { get; init; } = [];

    /// <summary>expectedStateAlarm：配置输入取值组合 → 被监视输入（Inputs[0]）正常时应有的值。其它种类为空。</summary>
    public IReadOnlyList<ExpectedStateCase> ExpectedStates { get; init; } = [];

    /// <summary>expectedStateAlarm：核对时发现 TON 输入另含的本块从未写入的 Bool 输入形参（条件输入，如 NOT DryRunMode）。
    /// 不是 AI 给的，由 ExpectedStateCheck 填写；调用点须把它们取成常数后复核期望值（审查 I-3）。</summary>
    public IReadOnlyList<string> ConditionInputs { get; init; } = [];
}

/// <summary>期望状态报警的一种配置组合：When 为每个配置输入的取值（键忽略大小写），Expected 为被监视输入正常时应有的值。</summary>
public sealed record ExpectedStateCase(IReadOnlyDictionary<string, bool> When, bool Expected);

public sealed record AiFbContract(string Fb, string Understanding, IReadOnlyList<AiContractRelation> Relations);

public enum AiContractParseStatus { Parsed, Empty, Unparseable }

/// <summary>解析 AI 给出的 FB 契约（只是「指认」，正确性由 FbContractValidator 核对）。</summary>
public static class AiFbContractParser
{
    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "mutex", "feedback", "timeoutalarm", "gate", "contradiction", "expectedstatealarm", "other" };

    public static (AiContractParseStatus Status, AiFbContract? Contract, int SkippedItems) Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (AiContractParseStatus.Empty, null, 0);
        if (AiReplyJson.ObjectText(content) is not { } body) return (AiContractParseStatus.Unparseable, null, 0);
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return (AiContractParseStatus.Unparseable, null, 0); }
        // JsonObject 对重复键是延迟报错（首次按键访问时抛 ArgumentException），根级访问统一兜住
        JsonArray items; string fb, understanding;
        try
        {
            if (root is not JsonObject rootObj || rootObj["relations"] is not JsonArray arr)
                return (AiContractParseStatus.Unparseable, null, 0);
            items = arr; fb = Str(rootObj, "fb"); understanding = Str(rootObj, "understanding");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { return (AiContractParseStatus.Unparseable, null, 0); }

        var relations = new List<AiContractRelation>();
        var skipped = 0;
        foreach (var item in items)
        {
            try
            {
                if (item is not JsonObject o) { skipped++; continue; }
                var kind = Str(o, "kind").Trim().ToLowerInvariant();
                if (!Kinds.Contains(kind)) { skipped++; continue; }
                if (kind == "timeoutalarm") kind = "timeoutAlarm";
                if (kind == "expectedstatealarm") kind = "expectedStateAlarm";
                var outputs = List(o, "outputs").Concat(Opt(o, "output")).Distinct(StringComparer.Ordinal).ToArray();
                var inputs = List(o, "inputs").Concat(Opt(o, "input")).Distinct(StringComparer.Ordinal).ToArray();
                var evidence = o["evidence"] switch
                {
                    null => Array.Empty<string>(),
                    JsonArray arr => arr.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToArray(),
                    _ => throw new FormatException("evidence")
                };
                double? delay = o["delaySeconds"] switch
                {
                    null => null,
                    JsonValue v when v.TryGetValue<double>(out var d) && double.IsFinite(d) && d >= 0 => d,
                    _ => throw new FormatException("delaySeconds")
                };
                relations.Add(new AiContractRelation(kind, outputs, inputs, NullIfEmpty(Str(o, "positionVar")),
                    NullIfEmpty(Str(o, "delayParam")), delay, NullIfEmpty(Str(o, "alarm")), evidence, NullIfEmpty(Str(o, "text")))
                {
                    ConfigInputs = List(o, "configInputs").Distinct(StringComparer.Ordinal).ToArray(),
                    ExpectedStates = ExpectedStates(o)
                });
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException) { skipped++; }
        }
        return (AiContractParseStatus.Parsed, new AiFbContract(fb, understanding, relations), skipped);
    }

    /// <summary>expectedStates：[{"when":{"配置输入":true/false,…},"expected":true/false}]；格式不对整条跳过。</summary>
    static IReadOnlyList<ExpectedStateCase> ExpectedStates(JsonObject o)
    {
        if (o["expectedStates"] is null) return [];
        if (o["expectedStates"] is not JsonArray arr) throw new FormatException("expectedStates");
        var cases = new List<ExpectedStateCase>();
        foreach (var item in arr)
        {
            if (item is not JsonObject c || c["expected"] is not JsonValue ev || !ev.TryGetValue<bool>(out var expected))
                throw new FormatException("expectedStates");
            var when = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            switch (c["when"])
            {
                case null: break;
                case JsonObject w:
                    foreach (var (k, v) in w)
                    {
                        if (v is not JsonValue bv || !bv.TryGetValue<bool>(out var b) || string.IsNullOrWhiteSpace(k) || !when.TryAdd(k.Trim(), b))
                            throw new FormatException("expectedStates.when");
                    }
                    break;
                default: throw new FormatException("expectedStates.when");
            }
            cases.Add(new ExpectedStateCase(when, expected));
        }
        return cases;
    }

    static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IEnumerable<string> Opt(JsonObject o, string name) => NullIfEmpty(Str(o, name)) is { } s ? [s] : [];
    static IEnumerable<string> List(JsonObject o, string name) => o[name] switch
    {
        null => [],
        JsonArray arr => arr.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0),
        _ => throw new FormatException(name)
    };
}
