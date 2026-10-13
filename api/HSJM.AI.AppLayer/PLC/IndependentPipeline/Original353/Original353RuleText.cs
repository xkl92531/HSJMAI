#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353RuleWording(string Name, string Description, string Origin = "Deterministic",
    string? PlainLanguage = null);

/// <summary>Only renders a proved rule for operators; never changes its Boolean contract.</summary>
public static class Original353RuleText
{
    public static Original353RuleWording Render(PipelineRule rule, Original353ActivationDecision decision,
        IReadOnlyList<PipelineTag>? tags = null)
    {
        if (Original353ModeConflictRules.IsModeRule(rule))
        {
            var mode = rule.Signals.Any(s => s.Name.EndsWith(".AutoRunMod", StringComparison.Ordinal)) ? "自动运行" : "自动模式";
            return new(rule.DisplayLabel + mode + "与手动模式检查", $"{rule.DisplayLabel}{mode}与手动模式同时开启时，请检查模式选择。");
        }
        if (Original353RulePresentation.IsDoorSignalCheck(rule))
            return new(rule.DisplayLabel + "控制信号检查", $"若{rule.DisplayLabel}控制信号异常，请检查相关控制线路。");
        var names = rule.Signals.Select(s => s.Name).ToArray();
        var first = names.FirstOrDefault() ?? "未识别信号";
        var source = rule.Signals.FirstOrDefault(s => s.Role == "Source")?.Name ?? first;
        var targets = rule.Signals.Where(s => s.Role is "Target" or "Member").Select(s => s.Name).ToArray();
        var listed = string.Join("、", names);
        var targetList = string.Join("、", targets);
        string name, description;
        switch (rule.Operator)
        {
            case "AT_MOST_ONE" when rule.RequiresSettlingEvidence || rule.Applicability == "AfterSettling":
                name = first + "_互斥";
                description = $"{listed} 互斥，同时为 true 持续超过程序中的消抖时间即为异常，同一时刻最多允许一个信号为 true。";
                break;
            case "AT_MOST_ONE":
                name = first + "_互斥";
                description = $"{listed} 互斥，同一时刻最多允许一个信号为 true。";
                break;
            case "EXACTLY_ONE":
                var wiredOutput = rule.Signals.Select(signal => signal.Name)
                    .Where(signal => tags?.Count(tag => string.Equals(tag.Name, signal,
                        StringComparison.OrdinalIgnoreCase)
                        && tag.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) == 1)
                    .ToArray();
                name = (wiredOutput.Length == 1 ? wiredOutput[0] : first) + "_反相关系";
                description = $"{listed} 在程序段执行后的同一快照中应一真一假。";
                break;
            case "SAME_STATE":
                name = first + "_同步";
                description = $"{listed} 在程序执行条件成立时应保持相同状态。";
                break;
            case "IMPLIES_ANY":
                name = source + "_到位反馈";
                var groups = (rule.TargetGroups is { Count: > 0 } targetGroups ? targetGroups : [targets]).Select(g => string.Join("、", g)).ToArray();
                var anyBypass = rule.BypassSignals.Count > 0
                    ? "，旁路 " + string.Join("、", rule.BypassSignals) + " 关闭" : "";
                description = $"当 {source} 发出命令且超过动作等待时间{anyBypass}时，{string.Join(" 或 ", groups)} 应到位（满足其中一组即可）。";
                break;
            case "IMPLIES" when decision.Status == "RequiresRuntimeGuard":
                name = source + "_到位反馈";
                var bypass = rule.BypassSignals.Count > 0
                    ? "，旁路 " + string.Join("、", rule.BypassSignals) + " 关闭" : "";
                description = $"当 {source} 发出命令且超过动作等待时间{bypass}时，{targetList} {(targets.Length > 1 ? "应同时到位" : "应到位")}。";
                break;
            case "IMPLIES":
                name = source + "_关联条件";
                description = $"当 {source} 为 true 且程序执行条件成立时，{targetList} 应为 true。";
                break;
            case "ALL_TRUE":
                name = source + "_前置全满足";
                description = $"当 {source} 为 true 且程序执行条件成立时，{targetList} 均应为 true。";
                break;
            case "REQUIRES_0":
                name = source + "_目标应关闭";
                description = $"当 {source} 为 true 且程序执行条件成立时，{targetList} 应为 false。";
                break;
            case "REQUIRES_0_WHEN_FALSE":
                name = source + "_联锁";
                description = $"当 {source} 为 false 时，{targetList} 必须为 false。"
                    + (rule.UpstreamSignals.Count > 0 ? $"（{source} 的上游输入：{string.Join("、", rule.UpstreamSignals)}）" : "");
                break;
            case "MUST_BE_FALSE" when rule.RequiresSettlingEvidence || rule.Applicability == "AfterSettling":
                name = first + "_报警条件";
                description = $"{listed} 为 true 持续超过程序设定的时间即为异常，正常时不应为 true。";
                break;
            case "MUST_BE_FALSE":
                name = first + "_报警条件";
                description = $"报警检查条件成立时，{first} 不应为 true。";
                break;
            default:
                name = first + "_逻辑关系";
                description = $"PLC 程序中 {listed} 存在 {rule.Operator} 关系，需按程序执行条件检查。";
                break;
        }
        return new(name, description);
    }

    /// <summary>Reject invented or omitted signals and dangerous instant-fault language.</summary>
    /// <summary>
    /// 去掉说明里本条规则自己的信号名后再查地址：正式项目常把地址直接当点名（如 %Q12001.1），
    /// 说明必须逐字写出信号名，照抄它不算违规；紧跟数字的不算同名（%Q12001.1 不吞掉 %Q12001.10）。
    /// </summary>
    private static string WithoutOwnSignalNames(PipelineRule rule, string description) =>
        rule.Signals.Select(s => s.Name).Concat(rule.BypassSignals).Concat(rule.UpstreamSignals)
            .Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderByDescending(n => n.Length)
            .Aggregate(description, (text, n) => Regex.Replace(text, Regex.Escape(n) + "(?![0-9])", " "));

    public static bool IsValidAiText(PipelineRule rule, string? name, string? description)
    {
        if (rule.Signals.Count == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description)
            || name.Length > 120 || description.Length > Math.Max(210, rule.Signals.Sum(s => s.Name.Length) + 130)
            || name.Contains('\n') || Regex.IsMatch(WithoutOwnSignalNames(rule, description), @"%[IQM][0-9]", RegexOptions.IgnoreCase)) return false;
        if (name.Contains('【') || name.Contains('】')) return false;
        if (Original353ModeConflictRules.IsModeRule(rule))
            return name == Render(rule, new("ModeCheck", "")).Name
                && description.Contains(rule.DisplayLabel ?? "", StringComparison.Ordinal)
                && description.Contains("自动", StringComparison.Ordinal) && description.Contains("手动", StringComparison.Ordinal)
                && description.Contains("同时", StringComparison.Ordinal) && description.Contains("检查", StringComparison.Ordinal)
                && description.Contains(rule.Signals.Any(s => s.Name.EndsWith(".AutoRunMod", StringComparison.Ordinal))
                    ? "自动运行" : "自动模式", StringComparison.Ordinal)
                && !Regex.IsMatch(description, "正常|允许同时|可以同时|必须同时|DB[0-9]|true|false|互斥|最多|或", RegexOptions.IgnoreCase)
                && description.Length <= 100;
        if (Original353RulePresentation.IsDoorSignalCheck(rule))
            return name == rule.DisplayLabel + "控制信号检查"
                && description.Contains(rule.DisplayLabel!, StringComparison.Ordinal)
                && description.Contains("控制信号", StringComparison.Ordinal)
                && description.Contains("异常", StringComparison.Ordinal) && description.Contains("检查", StringComparison.Ordinal)
                && description.Length <= 100
                && !rule.Signals.Any(signal => description.Contains(signal.Name, StringComparison.Ordinal))
                && !Regex.IsMatch(description, "一真一假|快照|反相|互斥|程序|true|false|打开|关闭|未关|锁好|锁紧|锁住|解锁", RegexOptions.IgnoreCase);
        if (description.Contains("【待执行条件】", StringComparison.Ordinal)
            || description.Contains("【待动作时序】", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(description, "要写|必须写|仅返回|JSON|提示词|Source|Target")) return false;
        if (Regex.IsMatch(description, "手动模式|手动未发命令|暂不判故障|证据不足|不报故障")) return false;
        if (!name.Contains(rule.Signals[0].Name, StringComparison.Ordinal)) return false;
        if (rule.Signals.Any(s => !description.Contains(s.Name, StringComparison.Ordinal))) return false;
        if (rule.MaskedFeedbackInputs.Any(masked => Regex.IsMatch(name + "\n" + description,
            @"(?<![A-Za-z0-9_])" + Regex.Escape(masked) + @"(?![A-Za-z0-9_])"))) return false;
        if (rule.BypassSignals.Any(s => !description.Contains(s, StringComparison.Ordinal))) return false;
        var timed = rule.RequiresSettlingEvidence || rule.Applicability == "AfterSettling";
        if (timed && !description.Contains("时间", StringComparison.Ordinal)) return false;
        if (timed && (description.Contains("立即到位", StringComparison.Ordinal)
            || description.Contains("同步到位", StringComparison.Ordinal)
            || Regex.IsMatch(description, "无需到位|不需要到位|不用到位|不必到位"))) return false;
        if (rule.Operator == "AT_MOST_ONE" && !description.Contains("最多", StringComparison.Ordinal)) return false;
        // IMPLIES_ANY 是「满足其中一组即可」，写成「同时到位」意思正好相反
        if (rule.Operator == "IMPLIES_ANY" && ((!description.Contains("或", StringComparison.Ordinal)
            && !description.Contains("其中一组", StringComparison.Ordinal))
            || description.Contains("同时到位", StringComparison.Ordinal))) return false;
        if (rule.Operator == "EXACTLY_ONE" && !description.Contains("一真一假", StringComparison.Ordinal)
            && !description.Contains("恰好一个", StringComparison.Ordinal)) return false;
        if (rule.Operator == "EXACTLY_ONE" && (!description.Contains("程序", StringComparison.Ordinal)
            || !description.Contains("执行", StringComparison.Ordinal) || !description.Contains("快照", StringComparison.Ordinal)
            || Regex.IsMatch(description, "最多|至少|始终|任一时刻"))) return false;
        if (rule.Operator == "SAME_STATE" && !description.Contains("相同", StringComparison.Ordinal)
            && !description.Contains("一致", StringComparison.Ordinal)) return false;
        if (rule.Operator == "SAME_STATE" && (!description.Contains("程序", StringComparison.Ordinal)
            || !description.Contains("执行", StringComparison.Ordinal) || Regex.IsMatch(description, "始终|任一时刻"))) return false;
        if (rule.Operator == "REQUIRES_0_WHEN_FALSE"
            && !description.Contains("false", StringComparison.OrdinalIgnoreCase)) return false;
        // 门控方向：必须写明「Source 为 false」，且不得出现「Source 为 true」（方向写反的说明会误导排查）
        if (rule.Operator == "REQUIRES_0_WHEN_FALSE"
            && rule.Signals.FirstOrDefault(s => s.Role == "Source")?.Name is { Length: > 0 } gateSource)
        {
            var named = Regex.Escape(gateSource) + @"\s*为\s*";
            if (!Regex.IsMatch(description, named + "false", RegexOptions.IgnoreCase)
                || Regex.IsMatch(description, named + "true", RegexOptions.IgnoreCase)) return false;
        }
        return true;
    }
}
