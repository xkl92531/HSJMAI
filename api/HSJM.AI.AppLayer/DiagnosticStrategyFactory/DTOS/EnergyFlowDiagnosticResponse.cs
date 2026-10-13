using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 能量流诊断统一响应格式
    /// </summary>
    public class EnergyFlowDiagnosticResponse
    {
        /// <summary>
        /// 诊断状态
        /// - normal: 能量流正常，无异常
        /// - warning_only: 只有 WARNING 级别异常
        /// - diagnosed: 已完成诊断（含 CRITICAL 异常）
        /// - partial: 部分诊断（某些步骤失败）
        /// - error: 诊断失败
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; }

        /// <summary>
        /// 简洁的诊断摘要（Markdown 表格格式，100-200字）
        /// 仅当 status = "diagnosed" 时包含完整表格
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; }

        /// <summary>
        /// 统计信息
        /// </summary>
        [JsonPropertyName("statistics")]
        public DiagnosticStatistics Statistics { get; set; }

        /// <summary>
        /// 异常详情列表（可选，仅当需要详细信息时返回）
        /// </summary>
        [JsonPropertyName("issues")]
        public List<DiagnosticIssue> Issues { get; set; }

        /// <summary>
        /// 错误或警告信息（可选）
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; }

        /// <summary>
        /// 诊断过程中的注释/说明（可选）
        /// </summary>
        [JsonPropertyName("note")]
        public string Note { get; set; }

        /// <summary>
        /// 卡滞点列表
        /// </summary>
        [JsonPropertyName("stuckPoints")]
        public List<StuckPoint> StuckPoints { get; set; }

        /// <summary>
        /// 白话文诊断报告（简化版MD格式）
        /// </summary>
        [JsonPropertyName("simpleReport")]
        public string? SimpleReport { get; set; }
    }

    /// <summary>
    /// 诊断统计信息
    /// </summary>
    public class DiagnosticStatistics
    {
        /// <summary>
        /// 异常总数
        /// </summary>
        [JsonPropertyName("totalIssues")]
        public int TotalIssues { get; set; }

        /// <summary>
        /// CRITICAL 级别数量
        /// </summary>
        [JsonPropertyName("criticalCount")]
        public int CriticalCount { get; set; }

        /// <summary>
        /// WARNING 级别数量
        /// </summary>
        [JsonPropertyName("warningCount")]
        public int WarningCount { get; set; }

        /// <summary>
        /// 已分析的变量数量
        /// </summary>
        [JsonPropertyName("analyzedVariables")]
        public int AnalyzedVariables { get; set; }

        /// <summary>
        /// 安全问题数量
        /// </summary>
        [JsonPropertyName("safetyCount")]
        public int SafetyCount { get; set; }

        /// <summary>
        /// 约束违反数量
        /// </summary>
        [JsonPropertyName("constraintCount")]
        public int ConstraintCount { get; set; }

        /// <summary>
        /// IO点位问题数量
        /// </summary>
        [JsonPropertyName("ioTagCount")]
        public int IoTagCount { get; set; }

        /// <summary>
        /// 卡滞点数量
        /// </summary>
        [JsonPropertyName("stuckPointCount")]
        public int StuckPointCount { get; set; }
    }

    /// <summary>
    /// 卡滞点信息
    /// </summary>
    public class StuckPoint
    {
        /// <summary>
        /// 卡滞的变量名称
        /// </summary>
        [JsonPropertyName("variableName")]
        public string VariableName { get; set; }

        /// <summary>
        /// 卡滞类型
        /// - Coil: 输出线圈
        /// - Timer: 定时器
        /// - Counter: 计数器
        /// - Step: 步序/状态
        /// - Marker: 中间标志位
        /// </summary>
        [JsonPropertyName("stuckType")]
        public string StuckType { get; set; }

        /// <summary>
        /// 关联的逻辑单元ID
        /// </summary>
        [JsonPropertyName("logicUnitId")]
        public long? LogicUnitId { get; set; }

        /// <summary>
        /// 当前值
        /// </summary>
        [JsonPropertyName("currentValue")]
        public string CurrentValue { get; set; }

        /// <summary>
        /// 预期值（推断）
        /// </summary>
        [JsonPropertyName("expectedValue")]
        public string ExpectedValue { get; set; }

        /// <summary>
        /// 卡滞原因描述
        /// </summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; }
    }
}
