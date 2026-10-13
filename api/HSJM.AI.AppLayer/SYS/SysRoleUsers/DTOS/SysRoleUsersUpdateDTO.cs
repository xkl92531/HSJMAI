

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:49
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 角色用户表
    /// </summary>
    public class SysRoleUsersUpdateDTO
    {
            /// <summary>
        ///  ID
        /// </summary>
        [Description("ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  角色ID
        /// </summary>
        [Description("角色ID")]
                  [Range(0, long.MaxValue)]
                  public long? RoleID {get;set;}
        
          /// <summary>
        ///  用户ID
        /// </summary>
        [Description("用户ID")]
                  [Range(0, long.MaxValue)]
                  public long? UserID {get;set;}
        
     

    }

    /// <summary>
    /// 权限批量分配
    /// </summary>
    public class PermissionAllocationDTO
    {
        /// <summary>
        /// 角色IDS
        /// </summary>
        public List<long> RoleIds { get; set; }

        /// <summary>
        /// 员工IDS
        /// </summary>
        public List<long> UserIds { get; set; } 
    }
}
