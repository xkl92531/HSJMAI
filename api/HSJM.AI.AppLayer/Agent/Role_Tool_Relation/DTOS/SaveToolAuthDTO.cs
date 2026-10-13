using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 批量更新角色工具授权DTO
    /// </summary>
    public class SaveToolAuthDTO
    {
        /// <summary>
        /// 角色ID
        /// </summary>
        public long RoleId { get; set; }

        /// <summary>
        /// 工具ID列表
        /// </summary>
        public List<long> ToolIds { get; set; }
    }
}
