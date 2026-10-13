#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public static class RetirementVerdict
{
    public const string Covered = "已被新流程覆盖";
    public const string Keep = "未覆盖·标准答案有（需保留）";
    public const string SuspectWrong = "未覆盖·标准答案无（疑似旧规则本来就错）";
    public const string Undecided = "未覆盖·不在标准答案范围（待判定）";
}

public sealed record RetirementItem(string Family, EvalFact Fact, string Verdict);

/// <summary>spec §9：名字规则族独有的旧产出逐条判定「已被新流程覆盖 / 需保留 / 疑似本来就错 / 待判定」。疑似错的须由人按程序证据确认。
/// 一键排查不改（用户 2026-10-02 决定），不对照旧阀门报警提取。</summary>
public static class RetirementAudit
{
    public static IReadOnlyList<RetirementItem> Classify(string family, IReadOnlyList<EvalFact> oldAll, IReadOnlyList<EvalFact> oldWithout,
        IReadOnlyList<EvalFact> newWithout, TruthFile? truth)
    {
        var without = oldWithout.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var covered = newWithout.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var truthKeys = truth?.Facts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        // 同一 Key 可能出现在多个程序段：任一出现位置在标准答案范围内即算范围内（结论不随出现顺序变化），
        // 列出的事实取落在范围内的第一处，都不在范围内时取第一处
        return oldAll.Where(f => !without.Contains(f.Key)).GroupBy(f => f.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var shown = truth is null ? g.First() : g.FirstOrDefault(f => InScope(truth, f)) ?? g.First();
                return new RetirementItem(family, shown,
                    covered.Contains(g.Key) ? RetirementVerdict.Covered : Judge(shown, truth, truthKeys?.Contains(g.Key) == true));
            })
            .OrderBy(i => i.Fact.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>标准答案 Units 为 null 表示全项目都在范围内（测试线 PLC1 的口径）。
    /// 注意与 <see cref="LogicEvaluator.Scope"/> 不同：评测在 Units 为 null 时只取标准答案事实所在的程序段。
    /// 所以抽样标准答案若漏填 Units，这里会把全项目当范围，范围外的独有产出会被判「疑似旧规则本来就错」而不是「待判定」——
    /// 抽样标准答案必须填 Units。</summary>
    public static bool InScope(TruthFile truth, EvalFact fact) =>
        truth.Units is null || truth.Units.Any(u => u.Block == fact.Block && u.Network == fact.Network);

    static string Judge(EvalFact fact, TruthFile? truth, bool inTruth) =>
        truth is null ? RetirementVerdict.Undecided
        : inTruth ? RetirementVerdict.Keep
        : InScope(truth, fact) ? RetirementVerdict.SuspectWrong
        : RetirementVerdict.Undecided;
}
