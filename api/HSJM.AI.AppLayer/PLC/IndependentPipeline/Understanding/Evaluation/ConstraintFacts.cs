#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>把任意来源的约束规范化为最小事实，写法不同、含义相同的约束得到相同的 Key。</summary>
public static class ConstraintFacts
{
    /// <summary>时序参与比较的事实在规则为延时类时追加的 Key 后缀。</summary>
    public const string DelayedSuffix = "|延时";

    /// <summary>超时报警位事实：Key = TimeoutAlarm|报警位|输出(大写)|到位点(大写、去重、序数排序、&amp; 连接)；
    /// 前提 = 对应反馈规则的旁路（与反馈事实同一口径：去空白、大写、去重、序数排序）。</summary>
    public static EvalFact FromTimeoutAlarm(string alarmSymbol, string output, IReadOnlyList<string> feedback, IReadOnlyList<string> bypass,
        string category, string block, int? network, string origin)
    {
        static string N(string name) => name.Trim().ToUpperInvariant();
        var fb = string.Join("&", feedback.Select(N).Distinct().Order(StringComparer.Ordinal));
        var key = $"TimeoutAlarm|{alarmSymbol}|{N(output)}|{fb}";
        var pre = bypass.Select(N).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new EvalFact("TimeoutAlarm", key, key, category, block, network, pre, origin);
    }

    public static IReadOnlyList<EvalFact> FromRule(string op, IReadOnlyList<(string Name, string Role)> signals,
        IReadOnlyDictionary<string, string> addressByName, RuleMeta meta, bool preconditionsKnown,
        string category, string block, int? network, string origin)
    {
        string N(string name) => name.Trim().ToUpperInvariant();
        bool Area(string name, string prefix) => addressByName.TryGetValue(name, out var a) && a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string>? pre = preconditionsKnown ? meta.Bypass.Select(N).Distinct().Order(StringComparer.Ordinal).ToArray() : null;
        EvalFact F(string kind, string key, string display) => new(kind, key, display, category, block, network, pre, origin);

        // 延时类规则：时序参与比较的事实 Key 追加后缀，延时与瞬时互不匹配。
        // Feedback / FeedbackAny 天然带延时、不加；两成员都为 %I 的互斥（位置矛盾）时序豁免、不加。
        var delayKey = meta.Delayed ? DelayedSuffix : "";
        var delayText = meta.Delayed ? "（延时）" : "";

        // 规范名 → 原始名（首次出现），地址按原始名查。
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in signals) original.TryAdd(N(s.Name), s.Name);
        bool AreaN(string normalized, string prefix) => Area(original.TryGetValue(normalized, out var o) ? o : normalized, prefix);

        var sources = signals.Where(s => s.Role == "Source").Select(s => N(s.Name)).Distinct().ToArray();
        var targets = signals.Where(s => s.Role is "Target" or "Member").Select(s => N(s.Name)).Distinct().ToArray();
        var members = signals.Select(s => N(s.Name)).Distinct().ToArray();
        var facts = new List<EvalFact>();

        IEnumerable<(string A, string B)> Pairs(IReadOnlyList<string> xs)
        {
            var sorted = xs.Distinct().Order(StringComparer.Ordinal).ToArray();
            for (var i = 0; i < sorted.Length; i++)
                for (var j = i + 1; j < sorted.Length; j++) yield return (sorted[i], sorted[j]);
        }

        switch (op)
        {
            case "AT_MOST_ONE":
                foreach (var (a, b) in Pairs(members))
                {
                    var exempt = AreaN(a, "%I") && AreaN(b, "%I");
                    var dk = exempt ? "" : delayKey;
                    var dt = exempt ? "" : delayText;
                    facts.Add(F("Mutex", $"Mutex|{a}|{b}{dk}", $"{a} 与 {b} 互斥{dt}"));
                }
                break;
            case "SAME_STATE":
                foreach (var (a, b) in Pairs(members)) facts.Add(F("SameState", $"SameState|{a}|{b}{delayKey}", $"{a} 与 {b} 同步{delayText}"));
                break;
            case "EXACTLY_ONE":
                var set = string.Join("&", members.Order(StringComparer.Ordinal));
                facts.Add(F("ExactlyOne", $"ExactlyOne|{set}{delayKey}", $"{set} 恰好一个为真{delayText}"));
                break;
            case "IMPLIES" or "ALL_TRUE" when sources.Length == 1:
                // 逐目标判定：Source 为 %Q 且该目标为 %I → 到位反馈；否则为普通蕴含。
                var s0 = sources[0];
                var sourceIsQ = AreaN(s0, "%Q");
                foreach (var t in targets)
                    facts.Add(sourceIsQ && AreaN(t, "%I")
                        ? F("Feedback", $"Feedback|{s0}->{t}", $"{s0} 发出后 {t} 应到位")
                        : F("Implies", $"Implies|{s0}->{t}{delayKey}", $"{s0} 为真时 {t} 为真{delayText}"));
                break;
            case "IMPLIES_ANY" when sources.Length == 1 && meta.TargetGroups is { Count: > 0 }:
                var groups = meta.TargetGroups.Select(g => string.Join("&", g.Select(N).Order(StringComparer.Ordinal)))
                    .Order(StringComparer.Ordinal).ToArray();
                facts.Add(F("FeedbackAny", $"FeedbackAny|{sources[0]}->{string.Join("|", groups)}",
                    $"{sources[0]} 发出后 {string.Join(" 或 ", groups)} 应到位"));
                break;
            case "REQUIRES_0_WHEN_FALSE" when sources.Length >= 1:
                // 多个 Source 为「任一失效」的 OR 语义，等价于每个 (Source, Target) 一条门控。
                foreach (var s in sources)
                    foreach (var t in targets)
                        facts.Add(F("Gate", $"Gate|{s}->{t}{delayKey}", $"{s} 为 false 时 {t} 必须为 false{delayText}"));
                break;
            case "MUST_BE_FALSE":
                foreach (var t in members)
                    facts.Add(F("MustBeFalse", $"MustBeFalse|{t}{delayKey}", $"{t} 必须为 false{delayText}"));
                break;
            default:
                var all = string.Join("&", signals.Select(s => s.Role + ":" + N(s.Name)).Order(StringComparer.Ordinal));
                facts.Add(F("Other", $"Other|{op}|{all}", $"{op} {all}"));
                break;
        }
        return facts;
    }
}
