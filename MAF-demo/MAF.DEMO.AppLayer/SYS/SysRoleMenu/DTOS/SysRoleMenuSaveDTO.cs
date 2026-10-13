using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.AppLayer.SYS.SysRoleMenu.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class SysRoleMenuSaveDTO
    {
        /// <summary>
        /// 角色ID
        /// </summary>
        public long RoleId { get; set; }

        /// <summary>
        /// 系统ID
        /// </summary>
        public long? SystemID { get; set; }

        /// <summary>
        /// 按钮
        /// </summary>
        public List<long> ButtonIdList { get; set; }
        /// <summary>
        /// 菜单列表
        /// </summary>
        public List<long> MenuList { get; set; }
    }
}
