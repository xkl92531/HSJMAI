namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 逻辑单元查询范围
    /// </summary>
    public enum LogicUnitQueryScope
    {
        /// <summary>
        /// 同一 Block 下的所有 LogicUnit
        /// </summary>
        SameBlock,

        /// <summary>
        /// 同一 Step 下的所有 LogicUnit
        /// </summary>
        SameStep,

        /// <summary>
        /// 整个项目的所有 LogicUnit
        /// </summary>
        AllProject
    }
}
