#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>AI 汇总（普通模式与 Agent 模式共用）中的「阀门报警」小节。</summary>
    public static class ValveAlarmPromptFormatter
    {
        public static string KindLabel(string kind) => kind switch
        {
            "FwdError" => "伸出未到位报警",
            "BkdError" => "缩回未到位报警",
            "FwdLock" => "伸出条件不满足警告",
            "BkdLock" => "缩回条件不满足警告",
            "Timeout" => "动作超时未到位报警",
            "PositionConflict" => "到位信号矛盾报警",
            "ExpectedState" => "期望状态报警",
            "Latched" => "锁存报警位",
            _ => "报警",
        };

        public static void AppendSection(StringBuilder sb, IEnumerable<StationDiagnosisSummaryDto> summaries)
        {
            var stations = summaries
                .Where(s => s?.ActiveAlarms != null && s.ActiveAlarms.Count > 0)
                .OrderBy(s => s.StationOrder)
                .ToList();
            if (stations.Count == 0) return;

            sb.AppendLine("## 阀门报警（PLC 实时值）");
            sb.AppendLine("以下是当前为 true 的报警位，由 PLC 程序自身置出，可作为根因证据：阀门 FB 报警输出（伸出/缩回未到位、伸出/缩回条件不满足、动作超时未到位、到位信号矛盾）、传感器期望状态报警（监视输入长时间不在期望状态），以及全局 DB 里由程序置位、复位保持、供 HMI 显示的报警位（文字为 HMI 报警文本，没有文本时为变量路径）。");
            sb.AppendLine("报警位可能需要复位才会清除：请结合每条报警后列出的输出与反馈当前值判断故障是否仍在发生（例如输出为 true 而反馈为 false，说明气缸疑似没有到位）。");
            sb.AppendLine("读取失败的报警位不在本列表中，不是已确认故障，不能作为故障点输出。");
            sb.AppendLine("工位即使在下方被列为正常/运行中，其阀门报警仍是有效证据，需要结合判断。");
            foreach (var station in stations)
            {
                sb.AppendLine($"### {station.StationCode}");
                foreach (var alarm in station.ActiveAlarms)
                {
                    var signals = new List<string>();
                    if (alarm.OutputSignal != null) signals.Add("输出 " + Format(alarm.OutputSignal));
                    // 期望状态报警的关联点是被监视的输入，不是到位反馈
                    var feedbackLabel = alarm.Kind == "ExpectedState" ? "监视输入 " : "反馈 ";
                    signals.AddRange(alarm.Feedbacks.Select(f => feedbackLabel + Format(f)));
                    var tail = signals.Count > 0 ? "；" + string.Join("，", signals) : string.Empty;
                    var text = string.IsNullOrWhiteSpace(alarm.Text) ? alarm.AlarmVariable : alarm.Text;
                    var hint = alarm.Kind == "Timeout" ? TimeoutHint(alarm) : string.Empty;
                    // 锁存报警位没有实例：位置只写块名
                    var where = string.Join("，", new[] { alarm.ValveInstance, alarm.BlockName }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    sb.AppendLine($"- [{KindLabel(alarm.Kind)}] {text}{(where.Length > 0 ? $"（{where}）" : string.Empty)}{tail}{hint}");
                }
            }
            sb.AppendLine();
        }

        /// <summary>
        /// 动作超时报警的结论后缀，只有一种：「疑似 … 未到位」。前提：
        /// 输出读到为 true，或报警没有输出（清单里输出为空）；输出为 false 或未读到时不追加——
        /// 报警可能只依赖 FB 内部命令（输出 = 命令 ∧ 联锁），输出为 false 时报警可能正在真实触发（联锁未满足、阀门未被驱动），
        /// 既不能说「锁存」，也不能把原因说成传感器未到位。
        /// 每个到位组都至少有一个为 false 的点、且组内没有未读到的点 → 列出各组为 false 的点；
        /// 其余情况不追加。不推断「已到位」：组里只记了正的到位点，同项的常闭/比较条件不在组里，组内全为 true 也不能断定程序认为已到位。
        /// 分组为空（旧清单行）时：只有 1 个到位点按单组处理，多于 1 个不点名（不知道是「与」还是「或」）。
        /// </summary>
        private static string TimeoutHint(ActiveValveAlarmDto alarm)
        {
            if (alarm.OutputSignal != null && alarm.OutputSignal.Value != true)
                return string.Empty;

            var feedbacks = alarm.Feedbacks ?? new List<ValveAlarmSignalValueDto>();
            var values = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in feedbacks)
                // 同名重复且取值不一致：按未读到处理，不下结论
                values[f.Name] = values.TryGetValue(f.Name, out var seen) && seen != f.Value ? null : f.Value;

            List<List<string>> groups;
            if (alarm.FeedbackGroups is { Count: > 0 })
                groups = alarm.FeedbackGroups.Where(g => g is { Count: > 0 }).ToList();
            else if (feedbacks.Count == 1)
                groups = new List<List<string>> { new() { feedbacks[0].Name } };
            else
                return string.Empty;
            if (groups.Count == 0) return string.Empty;

            bool? Value(string name) => values.TryGetValue(name?.Trim() ?? string.Empty, out var v) ? v : null;
            if (groups.Any(g => g.Any(n => Value(n) is null)) || !groups.All(g => g.Any(n => Value(n) == false)))
                return string.Empty;

            var missing = groups.Select(g => g.Where(n => Value(n) == false).Select(DisplayName).ToList()).ToList();
            if (missing.Count == 1)
                return $" → 疑似 {string.Join("、", missing[0])} 未到位";
            return " → 疑似未到位（任一组到位即可）："
                + string.Join("；", missing.Select((g, i) => $"组{i + 1} {string.Join("、", g)}"));

            // 用 Feedbacks 里的写法显示（分组引用可能大小写不同）
            string DisplayName(string name) =>
                feedbacks.FirstOrDefault(f => string.Equals(f.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name ?? name;
        }

        private static string Format(ValveAlarmSignalValueDto signal)
        {
            var address = string.IsNullOrWhiteSpace(signal.Address) ? string.Empty : $"({signal.Address})";
            var value = signal.Value is null ? "未读到" : signal.Value.Value ? "true" : "false";
            return $"{signal.Name}{address}={value}";
        }
    }
}
