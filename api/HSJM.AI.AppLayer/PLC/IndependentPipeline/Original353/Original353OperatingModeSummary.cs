#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>Operating context must survive model omissions without replacing independent alarm evidence.</summary>
public static class Original353OperatingModeSummary
{
    public static bool AllManual(IReadOnlyList<long> projects, IReadOnlyList<Original353ManualModeRead> modes)
        => projects.Count > 0 && projects.All(id => modes.Count(m => m.ProjectId == id) == 1
            && modes.Single(m => m.ProjectId == id).Status == "Manual");

    public static bool AllSelectedHaveManual(IReadOnlyList<long> projects, IReadOnlyList<Original353ManualModeRead> modes)
        => projects.Count > 0 && projects.All(id => modes.Count(m => m.ProjectId == id) == 1
            && modes.Single(m => m.ProjectId == id).Status != "Conflict"
            && modes.Single(m => m.ProjectId == id).Conflicts.Count == 0
            && (modes.Single(m => m.ProjectId == id).Status == "Manual"
                || modes.Single(m => m.ProjectId == id).ManualGroups.Count > 0));

    public static IReadOnlyList<string> ManualNotices(IReadOnlyList<Original353ManualModeRead> modes)
        => modes.Where(m => m.Status == "Manual" || m.ManualGroups.Count > 0).Select(m => {
            var name = string.IsNullOrWhiteSpace(m.ProjectName) ? $"PLC {m.ProjectId}" : m.ProjectName;
            var scope = m.Status == "Manual" ? name : name + "的" + string.Join("、", m.ManualGroups.Select(Original353ModeConflictRules.Label));
            return $"{scope}当前处于手动模式，部分信号未到位可能与手动操作有关，请检查当前操作及相关信号。";
        }).Distinct().ToArray();

    public static string Report(IReadOnlyList<Original353ManualModeRead> modes)
    {
        var lines = ManualNotices(modes).ToList();
        foreach (var mode in modes.Where(m => m.Conflicts.Count > 0))
            lines.Add($"{mode.ProjectName}的{string.Join("、", mode.Conflicts.Select(c => Original353ModeConflictRules.Label(c.BlockName)).Distinct())}自动与手动模式同时开启，请检查模式选择。");
        if (lines.Count == 0) return "";
        return string.Join("\n", lines);
    }

    public static string ForModel(string? report, IReadOnlyList<Original353ManualModeRead> modes)
        => ManualNotices(modes).Count == 0 ? report ?? "" : (report ?? "")
            + "\n分析要求：总结必须保留上述已确认的手动模式提示。手动模式下普通到位/同步/流程条件未满足，只能提示疑似手动操作相关并建议检查，不能仅凭这些状态断言设备故障、气缸卡死或通信故障；确有独立报警或物理故障证据时可另行说明，不得掩盖。不要建议强制修改PLC信号来消除提示。";

    public static void Apply(LineDiagnosisLlmReport? report, IReadOnlyList<Original353ManualModeRead> modes)
    {
        if (report is null) return;
        report.SuggestedActions ??= [];
        foreach (var notice in ManualNotices(modes))
        {
            if (!(report.RootCauseDescription ?? "").Contains(notice, StringComparison.Ordinal))
                report.RootCauseDescription = notice + (string.IsNullOrWhiteSpace(report.RootCauseDescription) ? "" : "\n" + report.RootCauseDescription);
            if (!report.SuggestedActions.Contains(notice)) report.SuggestedActions.Insert(0, notice);
        }
    }
}
