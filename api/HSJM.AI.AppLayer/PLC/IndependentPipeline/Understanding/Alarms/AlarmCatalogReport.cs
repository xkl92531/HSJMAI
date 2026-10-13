#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>一个调用点（来源块、网络、实例）上的报警清单行数：超时报警行数与全部行数。Callees 为该调用点出现的被调块（去重）。</summary>
public sealed record AlarmCallSiteCount(string Block, int? Network, string Instance, IReadOnlyList<string> Callees, int Timeouts, int Total);

/// <summary>离线评测用：报警清单行 → Markdown（汇总、清单表、按调用点的超时报警核对）。只按 Kind 与调用点分组，不看任何名字。</summary>
public static class AlarmCatalogReport
{
    /// <summary>spec §13.6 第 1 条：每个阀门调用点前进、后退各一个超时报警。</summary>
    public const int ExpectedTimeoutsPerCallSite = 2;

    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    /// <summary>与 <see cref="LogicEvaluator.Scope"/> 同口径：有 Units 时按 Units，否则按标准答案事实所在的程序段（块名 + 网络号）。
    /// 按报警的 <see cref="UnderstandingAlarm.Network"/>（与离线评测产出事实同一种编号）过滤，必须在构建清单之前做——
    /// 清单行的 SourceNetwork 是库 networkid 口径，与评测编号在含空网络的块里不同。</summary>
    public static IReadOnlyList<UnderstandingAlarm> Scope(TruthFile truth, IReadOnlyList<UnderstandingAlarm> alarms)
    {
        var scope = truth.Units != null
            ? truth.Units.Select(u => (u.Block, (int?)u.Network)).ToHashSet()
            : truth.Facts.Select(f => (f.Block, f.Network)).ToHashSet();
        return alarms.Where(a => scope.Contains((a.BlockName, a.Network))).ToArray();
    }

    /// <summary>阀门调用点的报警种类（超时、位置矛盾）；期望状态报警（传感器 FB）与锁存报警位不参与调用点核对。</summary>
    static bool IsValveCallSiteKind(string kind) => kind is "Timeout" or "PositionConflict";

    /// <summary>按（来源块、网络、实例）分组（块名与实例忽略大小写），统计超时报警行数；按块、网络、实例排序。
    /// 只看阀门调用点的种类（<see cref="IsValveCallSiteKind"/>）。</summary>
    public static IReadOnlyList<AlarmCallSiteCount> TimeoutsByCallSite(IReadOnlyList<AlarmCatalogRow> rows) =>
        rows.Where(r => IsValveCallSiteKind(r.Kind)).GroupBy(r => (Block: r.SourceBlock.ToUpperInvariant(), r.SourceNetwork, Instance: r.SourceInstance.ToUpperInvariant()))
            .Select(g => new AlarmCallSiteCount(g.First().SourceBlock, g.Key.SourceNetwork, g.First().SourceInstance,
                g.Select(r => r.Callee).Distinct(C).Order(StringComparer.Ordinal).ToArray(),
                g.Count(r => r.Kind == "Timeout"), g.Count()))
            .OrderBy(s => s.Block, StringComparer.Ordinal).ThenBy(s => s.Network ?? int.MinValue)
            .ThenBy(s => s.Instance, StringComparer.Ordinal)
            .ToArray();

    /// <summary>callSiteNotes 为 null 表示拿不到逐调用点的代入说明，此时写 notesUnavailableReason。
    /// rawAlarmCount：去重前的报警位条数（可选，写进汇总）。</summary>
    public static string ToMarkdown(string title, IReadOnlyList<AlarmCatalogRow> rows,
        IReadOnlyDictionary<(string Block, int? Network, string Instance), IReadOnlyList<string>>? callSiteNotes,
        string? notesUnavailableReason, int? rawAlarmCount = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("## 汇总");
        sb.AppendLine();
        sb.AppendLine($"报警清单行数：{rows.Count}" + (rawAlarmCount is int raw ? $"（代入产出的报警位 {raw} 条，按报警位去重后）" : ""));
        sb.AppendLine();
        sb.AppendLine("| 种类 | 行数 |");
        sb.AppendLine("|---|---|");
        foreach (var g in rows.GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {Cell(g.Key)} | {g.Count()} |");

        sb.AppendLine();
        sb.AppendLine("## 清单");
        sb.AppendLine();
        sb.AppendLine("网络号为库口径（ai_plc_logicunit.networkid，块内第几个 CompileUnit，空网络也计），与一键排查定位用的一致。");
        sb.AppendLine();
        sb.AppendLine("| 报警位 | 种类 | 输出 | 到位分组 | 前提 | 延时 | 块/网络 | 实例 | 被调块 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| `{Cell(r.AlarmSymbol)}` | {Cell(r.Kind)} | {Cell(r.OutputSignal ?? "—")} | {Cell(Groups(r))} "
                + $"| {Cell(string.Join("、", r.Preconditions))} | {Cell(r.Delay ?? "—")} | {Cell(At(r.SourceBlock, r.SourceNetwork, " / "))} "
                + $"| {Cell(r.SourceInstance)} | {Cell(r.Callee)} |");

        var sites = TimeoutsByCallSite(rows);
        sb.AppendLine();
        sb.AppendLine("## 每个调用点的超时报警核对");
        sb.AppendLine();
        sb.AppendLine($"按（来源块、网络、实例）分组，共 {sites.Count} 个调用点；期望每个调用点 {ExpectedTimeoutsPerCallSite} 条超时报警（spec §13.6 第 1 条）。"
            + "只统计清单里出现过的调用点：一条报警都没产出的调用点不在此列。");
        sb.AppendLine();
        sb.AppendLine("超时报警行数分布：" + string.Join("，",
            sites.GroupBy(s => s.Timeouts).OrderBy(g => g.Key).Select(g => $"{g.Key} 条 × {g.Count()} 个调用点")));
        sb.AppendLine();
        sb.AppendLine("| 块 | 网络 | 实例 | 被调块 | 超时报警 | 全部报警 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in sites)
            sb.AppendLine($"| {Cell(s.Block)} | {s.Network?.ToString() ?? "—"} | {Cell(s.Instance)} | {Cell(string.Join("、", s.Callees))} "
                + $"| {s.Timeouts} | {s.Total} |");

        var mismatched = sites.Where(s => s.Timeouts != ExpectedTimeoutsPerCallSite).ToArray();
        sb.AppendLine();
        sb.AppendLine($"### 超时报警行数不等于 {ExpectedTimeoutsPerCallSite} 的调用点（{mismatched.Length} 个）");
        sb.AppendLine();
        if (callSiteNotes is null && mismatched.Length > 0)
        {
            sb.AppendLine($"> 无逐调用点说明：{notesUnavailableReason}");
            sb.AppendLine();
        }
        foreach (var s in mismatched)
        {
            sb.AppendLine($"- {At(s.Block, s.Network, " 网络 ")}，实例 `{s.Instance}`（{string.Join("、", s.Callees)}）："
                + $"超时报警 {s.Timeouts} 条，全部报警 {s.Total} 条");
            foreach (var r in rows.Where(r => C.Equals(r.SourceBlock, s.Block) && r.SourceNetwork == s.Network && C.Equals(r.SourceInstance, s.Instance)))
                sb.AppendLine($"  - `{r.AlarmSymbol}`（{r.Kind}；到位 {Groups(r)}；延时 {r.Delay ?? "—"}）");
            var notes = callSiteNotes?.FirstOrDefault(n => C.Equals(n.Key.Block, s.Block) && n.Key.Network == s.Network
                && C.Equals(n.Key.Instance, s.Instance)).Value;
            sb.AppendLine(notes is { Count: > 0 }
                ? $"  - 代入说明：{string.Join("、", notes)}"
                : callSiteNotes is null ? "  - 无逐调用点说明" : "  - 代入说明：无");
        }
        if (mismatched.Length == 0) sb.AppendLine("无。");

        var latched = rows.Where(r => r.Kind == LatchedAlarmBits.Kind).ToArray();
        sb.AppendLine();
        sb.AppendLine($"## 锁存报警位（{latched.Length}）");
        sb.AppendLine();
        sb.AppendLine("锁存报警位（LatchedAlarmBits）：全局 DB 里直接锁存且成组复位的位（块/网络为置位所在程序段），与经 FB 输出引脚接到全局 DB 的锁存报警（块/网络为调用所在程序段）；网络号为库口径，依据里的网络号是归档序号。");
        sb.AppendLine();
        foreach (var r in latched)
        {
            sb.AppendLine($"- `{r.AlarmSymbol}`（{At(r.SourceBlock, r.SourceNetwork, " 网络 ")}）");
            foreach (var e in r.Evidence) sb.AppendLine($"  - {e}");
        }
        if (latched.Length == 0) sb.AppendLine("无。");
        return sb.ToString();
    }

    /// <summary>到位分组写成「A&B | C」；没有分组时全部到位点视为单组。</summary>
    static string Groups(AlarmCatalogRow r) => r.FeedbackGroups.Count > 0
        ? string.Join(" | ", r.FeedbackGroups.Select(g => string.Join("&", g)))
        : string.Join("&", r.FeedbackSignals);

    static string At(string block, int? network, string separator) => block + separator + (network?.ToString() ?? "—");

    static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
