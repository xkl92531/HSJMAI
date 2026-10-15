using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services;

/// <summary>
/// Constrain both LLM and Agent reports to independently verified input evidence.
/// Free-form prose cannot be safely filtered by fault keywords: regenerate public claims from
/// evidence. Successful model responses remain in RawResponse for audit; failed responses are
/// audited by the service before projection and are not exposed through the public failure report.
/// </summary>
internal static class DiagnosisEvidenceGuard
{
    internal static void Apply(LineDiagnosisLlmReport report,
        IReadOnlyList<LineDiagnosisLlmService.StationRootCauseEvidenceProfile> profiles,
        string language = "zh-CN")
    {
        if (report == null) return;
        report.EvidenceOnly = !report.Success || report.EvidenceOnly;
        if (report.EvidenceOnly)
        {
            report.Success = false;
            // Services log the raw response before this public projection. Failure UI must not display it.
            report.RawResponse = null;
            report.Confidence = 0;
        }
        profiles ??= Array.Empty<LineDiagnosisLlmService.StationRootCauseEvidenceProfile>();
        language = LineDiagnosisI18n.Normalize(language);
        string T(string cn, string en, string tw, string ja) => language switch
        {
            "en-US" => en, "zh-TW" => tw, "ja-JP" => ja, _ => cn
        };

        var insufficient = T(
            "现有证据不足以确认停机根因。等待条件未满足或信号未知不证明设备故障，也不能据此判定通信中断。",
            "Insufficient evidence to confirm the stop root cause. Unmet waiting conditions or unknown signals do not establish equipment failure or communication loss.",
            "現有證據不足以確認停機根因。等待條件未滿足或訊號未知不證明設備故障，也不能據此判定通訊中斷。",
            "停止の根本原因を確定する証拠が不足しています。待機条件の未成立や不明な信号だけでは、設備故障や通信断を確定できません。");
        var candidateLabel = T("故障候选，根因待确认", "Fault candidate; root cause unconfirmed",
            "故障候選，根因待確認", "故障候補（根本原因は未確定）");
        var alarmCaveat = T("PLC 报警可能为锁存状态；尚不能据此断言当前机械部件损坏。",
            "A PLC alarm may be latched; this does not establish current mechanical damage.",
            "PLC 警報可能為鎖存狀態；尚不能據此斷言目前機械零件損壞。",
            "PLC アラームはラッチ状態の可能性があり、現在の機械損傷を証明するものではありません。");

        var candidates = profiles.Where(p => p.HasConfirmedFaultEvidence && !p.AmbiguousStation)
            .GroupBy(p => p.StationCode, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        // A model preference cannot resolve multiple independent faults or project ambiguity.
        var selected = candidates.Count == 1 && !profiles.Any(p => p.HasConfirmedFaultEvidence && p.AmbiguousStation)
            ? candidates[0] : null;
        var impactPaths = BuildImpactPaths(profiles);
        string DescribePath(IReadOnlyList<string> path) => string.Join(" → ",
            Enumerable.Range(0, path.Count - 1).Select(i => T(
                $"{path[i]} 等待 {path[i + 1]}", $"{path[i]} waits for {path[i + 1]}",
                $"{path[i]} 等待 {path[i + 1]}", $"{path[i]} は {path[i + 1]} を待機")));
        var impactedSummaries = impactPaths.ToDictionary(pair => pair.Key, pair =>
        {
            var chains = string.Join("; ", pair.Value.Select(DescribePath));
            var targets = pair.Value.Select(path => path[^1]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var targetText = string.Join(", ", targets);
            var summary = T(
                $"连带受阻：{chains}。上述等待条件实际为 False，等待链指向独立故障候选 [{targetText}]；未发现本站独立故障证据。",
                $"Blocked by dependency: {chains}. These waiting conditions are FALSE and reach independent fault candidates [{targetText}]; no independent fault evidence was found at this station.",
                $"連帶受阻：{chains}。上述等待條件實際為 False，等待鏈指向獨立故障候選 [{targetText}]；未發現本站獨立故障證據。",
                $"依存先を待機して停止：{chains}。待機条件は False であり、独立した故障候補 [{targetText}] に到達します。このステーション自体の独立した故障証拠は見つかっていません。");
            if (targets.Count > 1) summary += T("存在多个候选，无法唯一归属。",
                "Multiple candidates remain; attribution is not unique.", "存在多個候選，無法唯一歸屬。", "複数の候補があり、原因を一意に特定できません。");
            return summary;
        }, StringComparer.OrdinalIgnoreCase);
        var candidateSummaries = profiles.Where(p => p.HasConfirmedFaultEvidence).Select(p =>
        {
            var identity = p.AmbiguousStation ? $"{p.StationCode} (ProjectId={p.ProjectId})" : p.StationCode;
            return $"{identity}: {candidateLabel}. {string.Join("; ", p.ConfirmedEvidence)}";
        }).Distinct().ToList();

        report.RootCauseStation = selected?.StationCode;
        report.RootCauseDescription = candidateSummaries.Count == 0 ? insufficient
            : string.Join("\n", candidateSummaries) + "\n" + alarmCaveat;
        if (selected != null && candidates.Count == 1)
            report.RootCauseDescription = T($"主要异常位于 {selected.StationCode}。",
                $"The main abnormality is at {selected.StationCode}.", $"主要異常位於 {selected.StationCode}。",
                $"主な異常は {selected.StationCode} にあります。") + "\n" + report.RootCauseDescription;
        if (profiles.Any(p => p.AmbiguousStation))
            report.RootCauseDescription += "\n" + T(
                "存在跨 PLC 项目的同名工位，当前报告仅按工位号关联，不能确认其根因归属。",
                "Station codes repeat across PLC projects; this report cannot assign root cause by station code alone.",
                "存在跨 PLC 專案的同名工位，目前報告僅按工位號關聯，不能確認其根因歸屬。",
                "複数の PLC プロジェクトに同名ステーションがあるため、コードだけでは根本原因を特定できません。");
        if (selected == null && candidateSummaries.Count > 0)
            report.RootCauseDescription += "\n" + T("存在多个独立异常候选或工位归属歧义，尚不能确定唯一主根。",
                "Multiple independent candidates or ambiguous station identities prevent selecting a unique primary root.",
                "存在多個獨立異常候選或工位歸屬歧義，尚不能確定唯一主根。", "独立した異常候補が複数あるか、ステーションの識別が曖昧なため、主原因を一意に特定できません。");
        if (report.EvidenceOnly)
            report.RootCauseDescription = T("模型分析不可用；以下为已采集 PLC 证据的确定性汇总。",
                "Model analysis is unavailable; the following is a deterministic summary of collected PLC evidence.",
                "模型分析不可用；以下為已採集 PLC 證據的確定性彙總。", "モデル分析は利用できません。以下は取得済み PLC 証拠から生成した確定的な要約です。")
                + "\n" + report.RootCauseDescription;

        // Keep observed paths to every independent fault candidate even when no unique root is selected.
        report.ImpactChain = impactPaths.Count == 0
            ? T("工位间因果关系尚未确认。", "Causal relationships between stations are unconfirmed.",
                "工位間因果關係尚未確認。", "ステーション間の因果関係は未確定です。")
            : T("指向独立故障候选的已确认等待链：", "Observed waiting paths to independent fault candidates: ",
                "指向獨立故障候選的已確認等待鏈：", "独立した故障候補への確認済み待機経路：")
                + string.Join("; ", impactPaths.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).SelectMany(p => p.Value).Select(DescribePath));
        report.Reasoning = report.RootCauseDescription + "\n" + report.ImpactChain;
        // These are conservative display caps, not calibrated fault probabilities. Never raise model confidence.
        report.Confidence = double.IsFinite(report.Confidence)
            ? Math.Clamp(report.Confidence, 0, selected == null ? 0.3 : 0.65) : 0;
        report.SecondaryCauses = impactedSummaries.Select(pair => new SecondaryCauseItem
            { StationCode = pair.Key, Description = pair.Value }).ToList();
        report.StationAnalysis = profiles.GroupBy(p => p.StationCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LineDiagnosisStationAnalysis
            {
                StationCode = g.Key,
                Role = selected != null && string.Equals(g.Key, selected.StationCode, StringComparison.OrdinalIgnoreCase)
                    ? "RootCause" : impactedSummaries.ContainsKey(g.Key) ? "Impacted" : "Unknown",
                Summary = g.Any(p => p.HasConfirmedFaultEvidence)
                    ? string.Join("\n", g.Where(p => p.HasConfirmedFaultEvidence).Select(p =>
                        $"{candidateLabel}. ProjectId={p.ProjectId}; {string.Join("; ", p.ConfirmedEvidence)}"))
                    : impactedSummaries.TryGetValue(g.Key, out var impacted) ? impacted : insufficient
            }).ToList();
        report.SuggestedActions = candidateSummaries.Count > 0
            ? new() { T("先核对下列报警/违规的当前信号及锁存复位条件，再现场确认根因：",
                "Verify current signals and alarm latch/reset conditions before confirming the root cause: ",
                "先核對下列警報/違規的目前訊號及鎖存復位條件，再現場確認根因：",
                "次の信号とアラームのラッチ・リセット条件を確認してから根本原因を現場で確認してください：")
                + string.Join("\n", candidateSummaries) }
            : new() { T("补充带读取质量和时间戳的 PLC 报警、动作命令及完整反馈条件，核对当前步骤和动作是否实际发出。",
                "Collect PLC alarms, commands and complete feedback conditions with read quality and timestamps; verify the current step and whether the action was commanded.",
                "補充帶讀取品質和時間戳記的 PLC 警報、動作命令及完整回饋條件，核對目前步驟和動作是否實際發出。",
                "読取品質と時刻付きの PLC アラーム、動作指令、完全なフィードバック条件を取得し、現在のステップと動作指令の有無を確認してください。") };
        if (impactPaths.Count > 0)
            report.SuggestedActions.Add(T(
                "优先核查上述独立故障候选的报警及反馈，再按已确认等待链复核受阻工位的条件是否解除。",
                "Check the listed independent fault candidates' alarms and feedback first, then verify dependent stations' waiting conditions along the observed paths.",
                "優先核查上述獨立故障候選的警報及回饋，再按已確認等待鏈複核受阻工位的條件是否解除。",
                "まず上記の独立した故障候補のアラームとフィードバックを確認し、確認済みの依存経路に沿って待機条件が解除されるかを確認してください。"));
    }

    private static Dictionary<string, List<IReadOnlyList<string>>> BuildImpactPaths(
        IReadOnlyList<LineDiagnosisLlmService.StationRootCauseEvidenceProfile> profiles)
    {
        var paths = new Dictionary<string, List<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);

        // The public station identity is code-only. Neither source nor intermediate/target nodes may
        // have multiple project identities; no relation is inferred from station order or model text.
        var stations = profiles.GroupBy(p => p.StationCode, StringComparer.OrdinalIgnoreCase)
            .Where(g => !g.Any(p => p.AmbiguousStation) && g.Select(p => p.ProjectId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var graph = stations.ToDictionary(pair => pair.Key, pair => pair.Value
            .SelectMany(p => p.ConfirmedWaitingTargets).Where(stations.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var independentFaults = stations.Where(pair => pair.Value.Any(p => p.HasConfirmedFaultEvidence))
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Iterative graph walks are bounded by the number of stations and avoid recursion depth risks.
        // Exclude actual cycle members even if a cycle also has an outgoing edge to the fault station.
        var cyclic = graph.Keys.Where(start => ReachesSelf(start, graph)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in graph.Keys)
        {
            if (independentFaults.Contains(source) || cyclic.Contains(source)) continue;
            var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [source] = null };
            var pending = new Queue<string>();
            pending.Enqueue(source);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var target in graph[current])
                {
                    if (cyclic.Contains(target) || parents.ContainsKey(target)) continue;
                    parents[target] = current;
                    if (independentFaults.Contains(target))
                    {
                        var path = new List<string>();
                        for (var cursor = target; cursor != null; cursor = parents[cursor]) path.Add(cursor);
                        path.Reverse();
                        if (!paths.TryGetValue(source, out var sourcePaths)) paths[source] = sourcePaths = new();
                        sourcePaths.Add(path);
                        // Stop at every independent fault; never reclassify it as a mere intermediate waiter.
                        continue;
                    }
                    pending.Enqueue(target);
                }
            }
        }
        return paths;
    }

    private static bool ReachesSelf(string start, IReadOnlyDictionary<string, string[]> graph)
    {
        var pending = new Queue<string>(graph[start]);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (string.Equals(current, start, StringComparison.OrdinalIgnoreCase)) return true;
            if (!visited.Add(current)) continue;
            foreach (var next in graph[current]) pending.Enqueue(next);
        }
        return false;
    }
}
