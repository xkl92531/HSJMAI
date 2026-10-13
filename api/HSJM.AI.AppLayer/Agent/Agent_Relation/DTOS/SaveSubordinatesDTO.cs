using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 批量更新Planner下属智能体DTO
    /// </summary>
    public class SaveSubordinatesDTO
    {
        /// <summary>
        /// Planner角色ID
        /// </summary>
        public long PlannerRoleId { get; set; }

        /// <summary>
        /// 下属智能体角色ID列表
        /// </summary>
        public List<long> SubordinateRoleIds { get; set; }
    }
}
