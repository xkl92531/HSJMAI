

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:49
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 角色用户表
    /// </summary>
    public class SysRoleUsersInsertDTO
    {
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
}
