
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 能量流诊断请求 DTO
    /// 支持两种模式：
    /// 1. ID模式：通过 ConditionDTO.Id (LogicUnitId) 从数据库加载
    /// 2. 内容模式：直接传入 RawContent + UidSymbolMap（适用于没有数据库ID的情况）
    /// </summary>
    public class EnergyFlowDiagnosticRequestDto
    {
        /// <summary>
        /// 产线ID（必传）
        /// 用于获取该产线下的所有PLC，并查询实时信号
        /// </summary>
        public long ProductionLineId { get; set; } = 1;

        /// <summary>Limit one-click diagnosis to the explicitly selected PLC projects.</summary>
        public List<long> ProjectIds { get; set; }

        /// <summary>
        /// 用户描述的问题（可选）
        /// 用于AI判断需要执行哪些检查步骤
        /// 例如："PLC2在某个动作卡住了"、"设备报警了"、"安全门打开了"
        /// </summary>
        public string UserDescription { get; set; }

        /// <summary>
        /// 是否强制深度分析（默认false）
        /// - false: 快速检查发现问题则立即返回，不进行能量流分析
        /// - true: 无论快速检查结果如何，都进行完整的能量流分析
        /// </summary>
        public bool ForceDeepAnalysis { get; set; } = false;

        /// <summary>
        /// 诊断流式ID（可选）
        /// 用于标识本次诊断，用于流式输出时关联客户端
        /// </summary>
        public long DiagnosticId { get; set; }
    }
}
