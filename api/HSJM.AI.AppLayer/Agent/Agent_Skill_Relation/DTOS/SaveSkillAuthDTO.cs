using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 批量更新角色技能授权DTO
    /// </summary>
    public class SaveSkillAuthDTO
    {
        /// <summary>
        /// 角色ID
        /// </summary>
        public long RoleId { get; set; }

        /// <summary>
        /// 技能ID列表
        /// </summary>
        public List<long> SkillIds { get; set; }
    }
}
