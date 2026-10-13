

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:38
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 角色-工具关联表
    /// </summary>
    public class Role_Tool_RelationUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  角色ID
        /// </summary>
        [Description("角色ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? RoleID {get;set;}
        
          /// <summary>
        ///  工具ID
        /// </summary>
        [Description("工具ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ToolID {get;set;}
        
          /// <summary>
        ///  针对该角色的特定配置覆盖(JSON)
        /// </summary>
        [Description("针对该角色的特定配置覆盖(JSON)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OverrideConfig {get;set;}
        
     

    }
}
