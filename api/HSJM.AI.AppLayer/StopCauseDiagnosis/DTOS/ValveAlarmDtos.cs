#nullable enable
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>阀门报警关联信号的当前值（未读到为 null）。</summary>
    public class ValveAlarmSignalValueDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Address { get; set; }
        public bool? Value { get; set; }
    }

    /// <summary>一键排查时当前激活的报警（报警位为 true）：阀门 FB 报警引脚所连报警位，以及发布时生成的报警清单
    /// （超时 / 位置矛盾 / 期望状态报警，全局 DB 里只被置位、复位写的锁存报警位）。</summary>
    public class ActiveValveAlarmDto
    {
        public string AlarmVariable { get; set; } = string.Empty;
        /// <summary>FwdError / BkdError / FwdLock / BkdLock（旧提取）；Timeout / PositionConflict / ExpectedState / Latched（报警清单）</summary>
        public string Kind { get; set; } = string.Empty;
        /// <summary>Fwd / Bkd</summary>
        public string Direction { get; set; } = string.Empty;
        /// <summary>报警文本（DB 成员注释，即 HMI 显示文字）；无注释时为 null。</summary>
        public string? Text { get; set; }
        public string ValveInstance { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public int? NetworkId { get; set; }
        public ValveAlarmSignalValueDto? OutputSignal { get; set; }
        public List<ValveAlarmSignalValueDto> Feedbacks { get; set; } = new();
        /// <summary>到位组：按到位点名字引用 Feedbacks，任一组全部为 true 即到位；空 = 视为单组（全部到位点）。</summary>
        public List<List<string>> FeedbackGroups { get; set; } = new();
    }
}
