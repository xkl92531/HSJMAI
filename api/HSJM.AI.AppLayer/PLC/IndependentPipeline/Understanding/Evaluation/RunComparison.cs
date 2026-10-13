#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record PreconditionChange(EvalFact A, EvalFact B);

/// <summary>两份事实按 Key（约束类型 + 信号）比对；同一份里同 Key 只看第一条。</summary>
public sealed record FactDiff(IReadOnlyList<EvalFact> OnlyA, IReadOnlyList<EvalFact> OnlyB, IReadOnlyList<EvalFact> Both,
    IReadOnlyList<PreconditionChange> PreconditionsDiffer);

public static class RunComparison
{
    public static FactDiff Diff(IReadOnlyList<EvalFact> a, IReadOnlyList<EvalFact> b)
    {
        static Dictionary<string, EvalFact> ByKey(IEnumerable<EvalFact> xs)
        {
            var d = new Dictionary<string, EvalFact>(StringComparer.Ordinal);
            foreach (var x in xs) d.TryAdd(x.Key, x);
            return d;
        }
        static IReadOnlyList<EvalFact> Sorted(IEnumerable<EvalFact> xs) =>
            xs.OrderBy(f => f.Category, StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal).ToArray();
        var ka = ByKey(a);
        var kb = ByKey(b);
        var both = ka.Keys.Where(kb.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        return new(
            Sorted(ka.Where(kv => !kb.ContainsKey(kv.Key)).Select(kv => kv.Value)),
            Sorted(kb.Where(kv => !ka.ContainsKey(kv.Key)).Select(kv => kv.Value)),
            Sorted(both.Select(k => ka[k])),
            both.Where(k => !SamePreconditions(ka[k].Preconditions, kb[k].Preconditions))
                .Select(k => new PreconditionChange(ka[k], kb[k])).ToArray());
    }

    public static bool SamePreconditions(IReadOnlyList<string>? x, IReadOnlyList<string>? y) =>
        x is null ? y is null : y is not null && x.SequenceEqual(y, StringComparer.Ordinal);

    public static string Report(FactDiff d, string title, string aName, string bName, int limit = 300)
    {
        static string Line(EvalFact f) => $"- [{f.Category}] `{f.Key}` — {f.Display}（{f.Block} 网络 {f.Network}，来源 {f.Origin}）";
        static string Pre(IReadOnlyList<string>? p) => p is null ? "未整理" : p.Count == 0 ? "无" : string.Join("、", p);
        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine($"| 只在{aName} | 只在{bName} | 两者同 | 其中前提不同 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| {d.OnlyA.Count} | {d.OnlyB.Count} | {d.Both.Count} | {d.PreconditionsDiffer.Count} |");
        void Section(string head, IReadOnlyList<string> lines)
        {
            sb.AppendLine();
            sb.AppendLine($"### {head}（{lines.Count}）");
            foreach (var l in lines.Take(limit)) sb.AppendLine(l);
            if (lines.Count > limit) sb.AppendLine($"- …另有 {lines.Count - limit} 条未列出");
        }
        Section($"只在{aName}", d.OnlyA.Select(Line).ToArray());
        Section($"只在{bName}", d.OnlyB.Select(Line).ToArray());
        Section("前提不同", d.PreconditionsDiffer
            .Select(c => $"- `{c.A.Key}`：{aName} {Pre(c.A.Preconditions)} / {bName} {Pre(c.B.Preconditions)}（{c.A.Block} 网络 {c.A.Network}）").ToArray());
        return sb.ToString();
    }
}
