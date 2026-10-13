using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 带上下文信息的逻辑单元
    /// 包含 LogicUnit 本身以及其所属的 Step 和 Block 名称
    /// </summary>
    public class LogicUnitWithContext
    {
        /// <summary>
        /// 逻辑单元实体
        /// </summary>
        public PLC_LogicUnitEntity LogicUnit { get; set; }

        /// <summary>
        /// 所属步骤名称
        /// </summary>
        public string StepName { get; set; }

        /// <summary>
        /// 所属块名称
        /// </summary>
        public string BlockName { get; set; }

        /// <summary>
        /// 完整位置描述（用于日志和显示）
        /// 格式: "步骤名/块名/逻辑单元ID"
        /// </summary>
        public string FullLocation => $"{StepName}/{BlockName}/LogicUnit-{LogicUnit?.Id ?? 0}";
    }
}
