#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>报警关联信号引用；TableName 非空表示 ai_plc_iotag 中的 IO 点。</summary>
    public sealed record ValveAlarmSignalRef(string Name, string? TableName, string? Address);

    /// <summary>一条阀门报警定义（阀门 FB 某个报警输出引脚所连的报警位）。</summary>
    public sealed record ValveAlarmDefinition(
        long ProjectId,
        string AlarmVariable,
        string Kind,
        string Direction,
        string ValveInstance,
        string BlockName,
        long LogicUnitId,
        long? BlockId,
        int? NetworkId,
        ValveAlarmSignalRef? Output,
        IReadOnlyList<ValveAlarmSignalRef> Feedbacks)
    {
        public string? Text { get; init; }
        public string? StationCode { get; init; }
        /// <summary>到位组（按到位点名字引用 Feedbacks，任一组全部为 true 即到位）；空 = 视为单组（全部到位点）。
        /// 旧引脚名提取路径不带分组。</summary>
        public IReadOnlyList<IReadOnlyList<string>> FeedbackGroups { get; init; } = [];
        /// <summary>读取用的写法（「块名.解析器 VariablePath」，见 <see cref="AlarmVariablePaths"/>）；为 null 时按 AlarmVariable 读。
        /// AlarmVariable 仍是清单写法，用于显示与去重。</summary>
        public string? ReadVariable { get; init; }
    }

    /// <summary>
    /// 从 FB 调用锚点提取阀门报警：只认阀门 FB 的四个标准报警输出引脚（CLAUDE.md §4.2：按引脚结构，不按命名）。
    /// </summary>
    public static class ValveAlarmExtractor
    {
        private static readonly (string Pin, string Kind, string Direction, string OutputPin, string FeedbackPin)[] AlarmPins =
        {
            ("FwdErr", "FwdError", "Fwd", "QFwd", "IWP"),
            ("BkdErr", "BkdError", "Bkd", "QBkd", "IHP"),
            ("FwdLockErr", "FwdLock", "Fwd", "QFwd", "IWP"),
            ("BkdLockErr", "BkdLock", "Bkd", "QBkd", "IHP"),
        };

        public static List<ValveAlarmDefinition> Extract(
            long projectId,
            long logicUnitId,
            long? blockId,
            string blockName,
            int? networkId,
            IReadOnlyList<PldAnchorBlock> anchors,
            IReadOnlyDictionary<string, PLC_IOTagEntity> ioTagsByName)
        {
            var result = new List<ValveAlarmDefinition>();
            foreach (var fb in anchors.Where(a => a.Type == PldAnchorType.FbCall))
            {
                foreach (var spec in AlarmPins)
                {
                    if (!fb.OutPins.TryGetValue(spec.Pin, out var alarmVariable) || !IsDbMember(alarmVariable)) continue;

                    var output = fb.OutPins.TryGetValue(spec.OutputPin, out var q) && IsBound(q)
                        ? Ref(q, ioTagsByName)
                        : null;
                    var feedbacks = fb.InPinContacts.TryGetValue(spec.FeedbackPin, out var contacts) && contacts != null
                        ? contacts
                            .Where(c => c.Polarity == "NO")
                            .Select(c => c.Name?.Trim() ?? string.Empty)
                            .Where(n => ioTagsByName.TryGetValue(n, out var tag) && IsInput(tag.Address))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(n => Ref(n, ioTagsByName))
                            .ToList()
                        : new List<ValveAlarmSignalRef>();

                    result.Add(new ValveAlarmDefinition(projectId, alarmVariable!.Trim(), spec.Kind, spec.Direction,
                        fb.Anchor, blockName, logicUnitId, blockId, networkId, output, feedbacks));
                }
            }
            return result;
        }

        private static bool IsBound(string? value) =>
            !string.IsNullOrWhiteSpace(value) && !value.Contains("未绑定", StringComparison.Ordinal);

        /// <summary>报警位必须是 DB 成员（带块段的全局符号），局部变量 / 无块段点名不算。</summary>
        private static bool IsDbMember(string? value)
        {
            if (!IsBound(value)) return false;
            var text = value!.Trim();
            return !text.StartsWith("#", StringComparison.Ordinal) && text.IndexOf('.') > 0;
        }

        private static bool IsInput(string? address) =>
            !string.IsNullOrWhiteSpace(address) && address.TrimStart().StartsWith("%I", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 命中 IO 点时名称用 IO 点自身的写法：字典查找不区分大小写，但精确读按 TableName/TagName 逐字匹配，
        /// 沿用触点写法会在大小写 / 空白不一致时读不到。
        /// </summary>
        private static ValveAlarmSignalRef Ref(string name, IReadOnlyDictionary<string, PLC_IOTagEntity> ioTagsByName)
        {
            var trimmed = name.Trim();
            return ioTagsByName.TryGetValue(trimmed, out var tag)
                ? new ValveAlarmSignalRef(tag.TagName?.Trim() is { Length: > 0 } tagName ? tagName : trimmed, tag.TableName, tag.Address)
                : new ValveAlarmSignalRef(trimmed, null, null);
        }
    }
}
