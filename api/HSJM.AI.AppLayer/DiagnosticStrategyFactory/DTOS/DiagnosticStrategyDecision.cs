using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 诊断策略决策结果
    /// 由AI判断决定需要执行哪些检查步骤
    /// </summary>
    public class DiagnosticStrategyDecision
    {
        /// <summary>
        /// 是否执行安全检查
        /// </summary>
        [JsonPropertyName("safety_check")]
        public bool SafetyCheck { get; set; }

        /// <summary>
        /// 是否执行约束检查
        /// </summary>
        [JsonPropertyName("constraint_check")]
        public bool ConstraintCheck { get; set; }

        /// <summary>
        /// 是否执行IO点位检查
        /// </summary>
        [JsonPropertyName("io_check")]
        public bool IoCheck { get; set; }

        /// <summary>
        /// 是否定位卡滞点
        /// </summary>
        [JsonPropertyName("stuck_point_locate")]
        public bool StuckPointLocate { get; set; }

        /// <summary>
        /// 是否进行能量流分析
        /// </summary>
        [JsonPropertyName("energy_flow_analysis")]
        public bool EnergyFlowAnalysis { get; set; }

        /// <summary>
        /// 判断理由
        /// </summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        /// <summary>
        /// 问题类型判断（如：卡滞、安全异常、IO故障等）
        /// </summary>
        [JsonPropertyName("problem_type")]
        public string ProblemType { get; set; }

        /// <summary>
        /// 建议的诊断优先级
        /// </summary>
        [JsonPropertyName("priority")]
        public string Priority { get; set; }

        /// <summary>
        /// 从用户描述中解析出的PLC名称/代码
        /// </summary>
        [JsonPropertyName("parsed_plc_name")]
        public string ParsedPlcName { get; set; }

        /// <summary>
        /// 解析出的多个PLC ID列表
        /// </summary>
        [JsonPropertyName("parsed_plc_ids")]
        public List<long> ParsedPlcIds { get; set; }
    }
}
