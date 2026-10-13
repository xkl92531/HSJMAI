using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// AI 诊断结果 - 单个异常信息
    /// </summary>
    public class DiagnosticIssue
    {
        /// <summary>
        /// 变量全路径名称
        /// 例如: "DB140_Drive.Inovance_Parameter.SRV_Inovance[3].WorkPositonOK[1]"
        /// </summary>
        [JsonPropertyName("variable")]
        public string Variable { get; set; }

        /// <summary>
        /// 对应的 UId（在 XML 中的唯一标识）
        /// </summary>
        [JsonIgnore]
        public string Uid { get; set; }

        /// <summary>
        /// 当前实时值（"True" 或 "False" 或其他数值）
        /// 支持布尔值自动转换为字符串
        /// </summary>
        [JsonPropertyName("status")]
        [JsonConverter(typeof(BooleanOrStringConverter))]
        public string Status { get; set; }

        /// <summary>
        /// 为什么该点导致了逻辑阻断
        /// 例如："常开触点未闭合"、"互锁激活"、"急停触发"
        /// </summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        /// <summary>
        /// 严重性级别
        /// - CRITICAL: 直接原因（必须修复）
        /// - WARNING: 间接原因（次要问题）
        /// </summary>
        [JsonPropertyName("severity")]
        public string Severity { get; set; }

        /// <summary>
        /// 问题分类
        /// - SAFETY: 安全相关（急停、安全门、光幕等）
        /// - CONSTRAINT: 约束规则违反（互斥、时序、配对等）
        /// - IO_TAG: IO点位异常（传感器失灵、硬件故障等）
        /// - ENERGY_FLOW: 能量流逻辑问题（触点阻断、逻辑错误）
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; }

        /// <summary>
        /// 物理地址（IO点位相关）
        /// 例如: %I4.0, %Q8.1
        /// </summary>
        [JsonPropertyName("address")]
        public string Address { get; set; }

        /// <summary>
        /// 关联的约束规则名称（约束检查相关）
        /// </summary>
        [JsonPropertyName("relatedRule")]
        public string RelatedRule { get; set; }

        /// <summary>
        /// 约束规则类型（AT_MOST_ONE/EXACTLY_ONE/ALL_TRUE/REQUIRES_0/IMPLIES/CHAIN_ORDER）
        /// </summary>
        [JsonPropertyName("constraintOperator")]
        public string ConstraintOperator { get; set; }

        /// <summary>
        /// 所有信号及其当前值的详细列表（约束检查相关）
        /// 格式：[{signalName: "xxx", currentValue: true/false, expectedValue: "应该为true/false"}, ...]
        /// </summary>
        [JsonPropertyName("signalDetails")]
        public List<SignalDetail> SignalDetails { get; set; }

        /// <summary>
        /// 期望值描述（告诉用户应该是什么状态）
        /// </summary>
        [JsonPropertyName("expectedValue")]
        public string ExpectedValue { get; set; }

        /// <summary>
        /// 修改建议（告诉用户如何处理）
        /// </summary>
        [JsonPropertyName("suggestion")]
        public string Suggestion { get; set; }

        /// <summary>
        /// 判断依据（约束问题专用）- 详细说明违反了哪个规则、当前值数据来源
        /// </summary>
        [JsonIgnore]
        public string JudgmentBasis { get; set; }

        /// <summary>
        /// 备注信息（如二次校验结果）
        /// </summary>
        [JsonPropertyName("note")]
        public string Note { get; set; }
    }

    /// <summary>
    /// 信号详细信息
    /// </summary>
    public class SignalDetail
    {
        [JsonPropertyName("signalName")]
        public string SignalName { get; set; }

        /// <summary>
        /// 信号在 PLC 的物理地址（如 %Q1067.5 / %I1066.1 / %M0.0）。
        /// 来自 ai_plc_iotag.address，仅在 IO Tag 字典命中时填充。
        /// </summary>
        [JsonPropertyName("signalAddress")]
        public string SignalAddress { get; set; }

        [JsonPropertyName("currentValue")]
        public bool CurrentValue { get; set; }

        [JsonPropertyName("expectedValue")]
        public string ExpectedValue { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// 物理地址（如 %I1063.5、%Q1064.7），用于约束报告和停机总结定位现场点位。
        /// </summary>
        [JsonPropertyName("address")]
        public string Address { get; set; }

        /// <summary>
        /// D-1：未采到时的原因（中文，如「通讯故障」「点名不存在：IGW 未注册」）；为空表示 CurrentValue 是实时采集值。
        /// 非空时 CurrentValue 恒为 false，没有意义。
        /// </summary>
        [JsonPropertyName("readStatus")]
        public string ReadStatus { get; set; }
    }
}
