using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>离线评测：内存里跑完整生成流程（第一期规则 + 模式规则 + FB 契约与调用点代入 + 普通网络），不写数据库。
/// 步骤见 OfflinePipeline（加载 → 旧流程按名字规则开关 → AI 读取 → 合并）。</summary>
static class OfflineEval
{
    public const long HoldoutProject = Holdout.TestProject;

    public static async Task<string> RunAsync(long projectId, string archive, TruthFile truth, CategoryMap map, OfflineAiOptions aiOptions,
        bool onlyTruthUnits, string? catalogOut = null, string? factsOut = null, bool allowHoldout = false)
    {
        var setup = await OfflinePipeline.LoadAsync(projectId, archive, allowHoldout);
        var ai = await OfflinePipeline.ReadAiAsync(setup, aiOptions);
        return await ReportAsync(setup, NameRuleSwitches.AllOn, ai, truth, map, onlyTruthUnits, catalogOut, factsOut);
    }

    /// <summary>用一份已读好的 AI 结果出评测报告（holdout-acceptance 与对照共用同一次读取）。</summary>
    public static async Task<string> ReportAsync(OfflineSetup setup, NameRuleSwitches switches, OfflineAiRead ai, TruthFile truth,
        CategoryMap map, bool onlyTruthUnits, string? catalogOut = null, string? factsOut = null)
    {
        var projectId = setup.ProjectId;
        var source = setup.Source;
        var generated = OfflinePipeline.Old(setup, switches);
        var (result, report) = OfflinePipeline.Merge(setup, generated, ai);
        var byId = source.Units.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
        var produced = ResultFacts.From(source, result, report.Alarms, map).ToList();
        if (factsOut is not null) await File.WriteAllTextAsync(factsOut, JsonSerializer.Serialize(produced, TruthJson.Options));
        var outside = onlyTruthUnits ? LogicEvaluator.TruthOutsideUnits(truth) : 0;
        if (onlyTruthUnits) produced = LogicEvaluator.Scope(truth, produced).ToList();
        var evaluation = LogicEvaluator.Evaluate(truth.Facts, produced);
        var sb = new StringBuilder(LogicEvaluator.Report(evaluation, $"离线评测（含 FB 契约）：项目 {projectId} 对照 {truth.Project}"));

        // 按来源：该来源产出的 Key 数，以及其中算对的 Key 数（Key 不在错误清单里即为正确）
        var wrongKeys = evaluation.Wrong.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        sb.AppendLine("\n## 按来源");
        sb.AppendLine("| 来源 | 产出 | 正确 |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in produced.GroupBy(p => p.Origin).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var keys = g.Select(p => p.Key).Distinct(StringComparer.Ordinal).ToArray();
            sb.AppendLine($"| {g.Key} | {keys.Length} | {keys.Count(k => !wrongKeys.Contains(k))} |");
        }

        sb.AppendLine("\n## FB 契约状态");
        foreach (var g in report.FbStatus.GroupBy(s => s.Value).OrderBy(g => g.Key))
            sb.AppendLine($"- {g.Key}：{g.Count()}（{string.Join("、", g.Select(x => x.Key).Order().Take(30))}）");
        sb.AppendLine("\n## 拒绝原因（前 20）");
        foreach (var g in report.RejectReasons.GroupBy(r => r.Value).OrderByDescending(g => g.Count()).Take(20))
            sb.AppendLine($"- {g.Key}：{g.Count()}");
        sb.AppendLine("\n## 代入说明计数");
        foreach (var n in report.NoteCounts.OrderByDescending(n => n.Value)) sb.AppendLine($"- {n.Key}：{n.Value}");
        // 新流程代码证明（Code:* / Review:*，瞬时规则直接判故障）：全项目逐条列出，不限评测范围
        var codeRules = result.Units.Where(u => byId.ContainsKey(u.LogicUnitId))
            .SelectMany(u => u.Rules.Where(r => r.Proof is { } p && (p.StartsWith("Code:", StringComparison.Ordinal) || p.StartsWith("Review:", StringComparison.Ordinal)))
                .Select(r => (Unit: byId[u.LogicUnitId], Rule: r)))
            .OrderBy(x => x.Unit.BlockName, StringComparer.Ordinal).ThenBy(x => x.Unit.NetworkOrder).ThenBy(x => x.Rule.Key, StringComparer.Ordinal).ToArray();
        sb.AppendLine($"\n## 新流程代码证明规则（全项目，{codeRules.Length}）");
        foreach (var (u, r) in codeRules)
            sb.AppendLine($"- {u.BlockName}/{u.NetworkOrder} {r.Operator} {string.Join(",", r.Signals.Select(s => s.Name))} {r.Proof}"
                + $"（依据 {string.Join("、", r.Trace?.Evidence ?? [])}）");
        sb.AppendLine($"\n## 被代码判定丢弃的新流程规则（全项目，{report.RefutedRules.Count}）");
        foreach (var line in report.RefutedRules.Order(StringComparer.Ordinal)) sb.AppendLine("- " + line);
        var conflicts = report.Alarms.Where(a => a.Alarm.Kind == "PositionConflict").ToArray();
        if (conflicts.Length > 0)
        {
            sb.AppendLine($"\n## 到位矛盾报警（{conflicts.Length}，列前 20）");
            foreach (var a in conflicts.Take(20))
                sb.AppendLine($"- `{a.Alarm.AlarmSymbol}` ← {string.Join(" 与 ", a.Alarm.Feedback)}（延时 {a.Alarm.Delay ?? "—"}；{a.BlockName} 网络 {a.DbNetwork}（库口径），调用 {a.Callee} 实例 {a.Instance}）");
        }
        if (catalogOut != null)
        {
            // 报警清单：与发布器同一构建（AlarmCatalogBuilder），只写文件不写数据库；--only-truth-units 时按同一范围过滤
            var catalog = AlarmCatalogBuilder.Build(onlyTruthUnits ? AlarmCatalogReport.Scope(truth, report.Alarms) : report.Alarms);
            await File.WriteAllTextAsync(catalogOut, AlarmCatalogReport.ToMarkdown(
                $"报警清单（离线）：项目 {projectId}" + (onlyTruthUnits ? $"，限 {truth.Project} 标准答案程序段" : ""), catalog,
                callSiteNotes: null,
                notesUnavailableReason: "UnderstandingReport 只保留全项目汇总的代入说明计数（NoteCounts），"
                    + "UnderstandingRuleSource.Merge 把每个调用点 CallSiteSubstitution.Apply 的 Notes 累加进计数后即丢弃，离线评测拿不到逐调用点的说明。",
                rawAlarmCount: onlyTruthUnits ? null : report.Alarms.Count));
            sb.AppendLine($"\n报警清单 {catalog.Count} 行写入 {catalogOut}");
        }
        sb.AppendLine("\n## 普通网络 AI 状态");
        if (ai.Networks is null && ai.NetworkError is null) sb.AppendLine("- 未读取（--no-network-ai）");
        if (report.NetworkReadError is not null) sb.AppendLine($"- 网络阶段出错：{report.NetworkReadError}");
        foreach (var g in report.NetworkStatus.GroupBy(s => s.Value).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"- {g.Key}：{g.Count()}");
        var networkNotes = report.NoteCounts.Where(n => n.Key.StartsWith("Network", StringComparison.Ordinal))
            .OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => $"{n.Key} {n.Value}").ToArray();
        sb.AppendLine($"- 核对通过并入库 {report.NoteCounts.Where(n => n.Key.StartsWith("NetworkRule:", StringComparison.Ordinal)).Sum(n => n.Value)} 条，"
            + $"核对拒绝 {report.NetworkRejectReasons.Count} 条；说明计数：{(networkNotes.Length == 0 ? "无" : string.Join("、", networkNotes))}");
        sb.AppendLine("\n## 普通网络拒绝原因（前 20）");
        foreach (var g in report.NetworkRejectReasons.GroupBy(r => r.Value.Split(':')[0]).OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal).Take(20))
            sb.AppendLine($"- {g.Key}：{g.Count()}");
        if (report.NetworkRejectReasons.Count > 0)
        {
            sb.AppendLine("\n## 普通网络拒绝明细（键：块/网络|operator|回复中的序号，前 100）");
            foreach (var (rejectKey, reason) in report.NetworkRejectReasons.OrderBy(r => r.Key, StringComparer.Ordinal).Take(100))
                sb.AppendLine($"- {rejectKey}：{reason}");
        }
        sb.AppendLine($"\n新增规则 {report.RulesAdded}，替换同键规则 {report.RulesReplaced}，报警位 {report.Alarms.Count}"
            + $"（超时 {report.Alarms.Count(a => a.Alarm.Kind == "Timeout")}，到位矛盾 {report.Alarms.Count(a => a.Alarm.Kind == "PositionConflict")}，"
            + $"期望状态 {report.Alarms.Count(a => a.Alarm.Kind == "ExpectedState")}，锁存报警位 {report.Alarms.Count(a => a.Alarm.Kind == LatchedAlarmBits.Kind)}，"
            + $"输出未过唯一写入而置空 {report.Alarms.Count(a => a.Alarm.Kind == "Timeout" && a.Alarm.Output.Length == 0)}）"
            + $"；标准答案中在 Units 之外的条数 {outside}");
        // 规则行数（入库口径：一条 IMPLIES 可含多个到位点；不含条件反馈与报警清单）
        static string ByOperator(Original353ProjectResult r) => string.Join("、", r.Units.SelectMany(u => u.Rules)
            .GroupBy(x => x.Operator).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
        sb.AppendLine($"\n规则行数：新流程合并前 {generated.Units.Sum(u => u.Rules.Count)}（{ByOperator(generated)}），"
            + $"合并后 {result.Units.Sum(u => u.Rules.Count)}（{ByOperator(result)}）");
        if (switches.GeneratorSignature.Length > 0) sb.AppendLine($"\n名字规则族已关闭：{switches.GeneratorSignature}");
        sb.AppendLine($"\n## AI 读取\n- 模型 {ai.ModelName}（指纹 {ai.Fingerprint[..12]}）：模型调用 {ai.ModelCalls} 次，"
            + $"旧缓存迁移命中 {ai.LegacyHits} 次，--no-ai 未命中 {ai.CacheMisses} 次；FB 契约读取 {Clock(ai.ContractTime)}，普通网络 {Clock(ai.NetworkTime)}");
        return sb.ToString();
    }

    static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss");
}
