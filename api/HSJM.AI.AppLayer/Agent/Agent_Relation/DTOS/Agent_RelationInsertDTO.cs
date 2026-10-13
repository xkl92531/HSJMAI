

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:24:25
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 智能体关系表
    /// </summary>
    public class Agent_RelationInsertDTO
    {
            /// <summary>
        ///  父智能体ID (Manager/Supervisor)
        /// </summary>
        [Description("父智能体ID (Manager/Supervisor)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ParentRoleID {get;set;}
        
          /// <summary>
        ///  子智能体ID (Worker/Subordinate)
        /// </summary>
        [Description("子智能体ID (Worker/Subordinate)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ChildRoleID {get;set;}
        
          /// <summary>
        ///  关系类型: Supervisor, Member, Delegate
        /// </summary>
        [Description("关系类型: Supervisor, Member, Delegate")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string RelationType {get;set;}
        
          /// <summary>
        ///  关系描述
        /// </summary>
        [Description("关系描述")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Description {get;set;}
        
     

    }
}
