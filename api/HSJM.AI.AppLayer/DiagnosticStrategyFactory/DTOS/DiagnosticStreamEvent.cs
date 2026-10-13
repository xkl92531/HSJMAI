using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 流式诊断进度事件
    /// </summary>
    public class DiagnosticStreamEvent
    {
        /// <summary>
        /// 事件类型
        /// </summary>
        [JsonPropertyName("event")]
        public string Event { get; set; }

        /// <summary>
        /// 事件数据
        /// </summary>
        [JsonPropertyName("data")]
        public DiagnosticStreamData Data { get; set; }
    }

    /// <summary>
    /// 事件数据
    /// </summary>
    public class DiagnosticStreamData
    {
        /// <summary>
        /// 步骤名称
        /// </summary>
        [JsonPropertyName("step")]
        public string Step { get; set; }

        /// <summary>
        /// 进度百分比
        /// </summary>
        [JsonPropertyName("progress")]
        public int Progress { get; set; }

        /// <summary>
        /// 状态描述
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; }

        /// <summary>
        /// 额外数据（JSON字符串）
        /// </summary>
        [JsonPropertyName("extra")]
        public string Extra { get; set; }

        /// <summary>
        /// 是否完成
        /// </summary>
        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    /// <summary>
    /// 诊断步骤枚举
    /// </summary>
    public enum DiagnosticStep
    {
        /// <summary>
        /// 初始化
        /// </summary>
        Init = 0,

        /// <summary>
        /// AI策略决策
        /// </summary>
        StrategyDecision = 10,

        /// <summary>
        /// 安全检查
        /// </summary>
        SafetyCheck = 20,

        /// <summary>
        /// 约束检查
        /// </summary>
        ConstraintCheck = 30,

        /// <summary>
        /// IO点位检查
        /// </summary>
        IoCheck = 40,

        /// <summary>
        /// 卡滞点定位
        /// </summary>
        StuckPointLocate = 50,

        /// <summary>
        /// 能量流分析
        /// </summary>
        EnergyFlowAnalysis = 70,

        /// <summary>
        /// 完成
        /// </summary>
        Complete = 100
    }
}
