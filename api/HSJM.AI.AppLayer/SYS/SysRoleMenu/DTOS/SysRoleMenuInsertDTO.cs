

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:27
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 角色菜单表
    /// </summary>
    public class SysRoleMenuInsertDTO
    {
        /// <summary>
        ///  角色ID
        /// </summary>
        [Description("角色ID")]
        [Range(0, long.MaxValue)]
        public long? RoleID { get; set; }
        /// <summary>
        ///  系统ID
        /// </summary>
        [Description("系统ID")]
        [Range(0, long.MaxValue)]
        public long? SystemID { get; set; }

        /// <summary>
        ///  菜单ID
        /// </summary>
        [Description("菜单ID")]
        [Range(0, long.MaxValue)]
        public long? MenuID { get; set; }

                /// <summary>
        /// 按钮权限
        /// </summary>
        public string MenuButtonCodes { get; set; }

    }
}
