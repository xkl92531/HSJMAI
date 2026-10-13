using HSJM.AI.IDbService;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 相关逻辑数据容器
    /// 包含当前逻辑单元、相关逻辑单元列表和查询策略
    /// </summary>
    public class RelatedLogicData
    {
        /// <summary>
        /// 当前正在诊断的逻辑单元
        /// </summary>
        public PLC_LogicUnitEntity CurrentUnit { get; set; }

        /// <summary>
        /// 根据查询策略找到的相关逻辑单元列表
        /// </summary>
        public List<LogicUnitWithContext> RelatedUnits { get; set; }

        /// <summary>
        /// 使用的查询策略
        /// </summary>
        public QueryStrategy SearchStrategy { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public RelatedLogicData()
        {
            RelatedUnits = new List<LogicUnitWithContext>();
        }
    }
}
