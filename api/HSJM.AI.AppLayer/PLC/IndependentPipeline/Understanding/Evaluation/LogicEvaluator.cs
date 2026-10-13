#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record EvalCategory(string Category, int Truth, int Found, int Produced, int Correct)
{
    public double Recall => Truth == 0 ? 0 : (double)Found / Truth;
    public double Precision => Produced == 0 ? 0 : (double)Correct / Produced;
}

public sealed record EvalResult(IReadOnlyList<EvalCategory> Categories, EvalCategory Overall,
    IReadOnlyList<EvalFact> Missed, IReadOnlyList<EvalFact> Wrong, int UncuratedPreconditions);

/// <summary>按最小事实比对：召回率 = 找到的真实事实 / 真实事实；准确率 = 正确的产出 / 产出。按类别分别统计。</summary>
public static class LogicEvaluator
{
    public static EvalResult Evaluate(IReadOnlyList<EvalFact> truth, IReadOnlyList<EvalFact> produced)
    {
        // 同一 Key 可能有多条：标准答案可有多个前提变体，产出可能重复。
        var truthByKey = truth.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EvalFact>)g.ToArray(), StringComparer.Ordinal);
        var producedByKey = produced.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EvalFact>)g.ToArray(), StringComparer.Ordinal);

        static bool Matches(EvalFact t, EvalFact p) =>
            t.Preconditions == null || (p.Preconditions != null && t.Preconditions.SequenceEqual(p.Preconditions, StringComparer.Ordinal));
        static bool MatchesAny(IReadOnlyList<EvalFact> variants, EvalFact p) => variants.Any(t => Matches(t, p));

        // 标准答案全部变体都未整理前提的 Key 数（不论是否被找到）。
        var uncurated = truthByKey.Values.Count(vs => vs.All(t => t.Preconditions == null));

        // 某 Key 算找到：标准答案里有该 Key，且产出里该 Key 的每一条都与某个标准答案变体吻合；任一条不吻合即整个 Key 算错。
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, variants) in truthByKey)
            if (producedByKey.TryGetValue(key, out var ps) && ps.All(p => MatchesAny(variants, p))) found.Add(key);

        var missed = truthByKey.Where(kv => !found.Contains(kv.Key)).Select(kv => kv.Value[0])
            .OrderBy(t => t.Category).ThenBy(t => t.Key, StringComparer.Ordinal).ToArray();
        // 错误产出优先展示不吻合的那一条。
        var wrong = producedByKey.Where(kv => !found.Contains(kv.Key))
            .Select(kv => truthByKey.TryGetValue(kv.Key, out var vs) ? kv.Value.FirstOrDefault(p => !MatchesAny(vs, p)) ?? kv.Value[0] : kv.Value[0])
            .OrderBy(p => p.Category).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();

        // 类别归属：标准答案 Key 用其首条的类别；已匹配的产出 Key 计入标准答案的类别，未匹配的产出 Key 用其首条的类别。
        string TruthCategory(string key) => truthByKey[key][0].Category;
        string ProducedCategory(string key) => found.Contains(key) ? TruthCategory(key) : producedByKey[key][0].Category;

        var categories = truthByKey.Keys.Select(TruthCategory).Concat(producedByKey.Keys.Select(ProducedCategory))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(c => new EvalCategory(c,
                truthByKey.Keys.Count(k => TruthCategory(k) == c),
                found.Count(k => TruthCategory(k) == c),
                producedByKey.Keys.Count(k => ProducedCategory(k) == c),
                found.Count(k => TruthCategory(k) == c)))
            .ToArray();
        var overall = new EvalCategory("合计", truthByKey.Count, found.Count, producedByKey.Count, found.Count);
        return new EvalResult(categories, overall, missed, wrong, uncurated);
    }

    /// <summary>只评标准答案覆盖的程序段：truth.Units 非 null 时恰为这些 (块名, 网络号)，
    /// 否则取标准答案事实所在的程序段。抽样后标注为零条约束的程序段只有列入 Units 才会把其错误产出计入准确率。</summary>
    public static IReadOnlyList<EvalFact> Scope(TruthFile truth, IReadOnlyList<EvalFact> produced)
    {
        var scope = truth.Units != null
            ? truth.Units.Select(u => (u.Block, (int?)u.Network)).ToHashSet()
            : truth.Facts.Select(f => (f.Block, f.Network)).ToHashSet();
        return produced.Where(p => scope.Contains((p.Block, p.Network))).ToArray();
    }

    /// <summary>truth.Units 非 null 时，所在程序段不在 Units 里的标准答案事实条数（这些事实仍计入召回率，只用于提醒）；否则为 0。</summary>
    public static int TruthOutsideUnits(TruthFile truth)
    {
        if (truth.Units == null) return 0;
        var scope = truth.Units.Select(u => (u.Block, (int?)u.Network)).ToHashSet();
        return truth.Facts.Count(f => !scope.Contains((f.Block, f.Network)));
    }

    public static string Report(EvalResult r, string title)
    {
        static string Pct(int n, int d) => d == 0 ? "—" : (100.0 * n / d).ToString("0.0") + "%";
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("| 类别 | 真实事实 | 找到 | 产出 | 正确 | 召回率 | 准确率 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in r.Categories.Append(r.Overall))
            sb.AppendLine($"| {c.Category} | {c.Truth} | {c.Found} | {c.Produced} | {c.Correct} | {Pct(c.Found, c.Truth)} | {Pct(c.Correct, c.Produced)} |");
        sb.AppendLine();
        sb.AppendLine($"未整理前提的标准答案条数（前提不参与比较）：{r.UncuratedPreconditions}");
        sb.AppendLine();
        sb.AppendLine($"## 漏识别（{r.Missed.Count}）");
        foreach (var m in r.Missed) sb.AppendLine($"- [{m.Category}] `{m.Key}` — {m.Display}（{m.Block} 网络 {m.Network}）");
        sb.AppendLine();
        sb.AppendLine($"## 错误或未在标准答案中的产出（{r.Wrong.Count}）");
        foreach (var w in r.Wrong) sb.AppendLine($"- [{w.Category}] `{w.Key}` — {w.Display}（{w.Block} 网络 {w.Network}，来源 {w.Origin}）");
        return sb.ToString();
    }
}
