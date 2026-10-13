

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:30:47
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 智能体-技能关系表 - 管理智能体技能授权与熟练度
    /// </summary>
    public class Agent_Skill_RelationInsertDTO
    {
            /// <summary>
        ///  关联智能体角色 (AI_Agent_Role)
        /// </summary>
        [Description("关联智能体角色 (AI_Agent_Role)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  关联技能 (AI_Skill_Definition)
        /// </summary>
        [Description("关联技能 (AI_Skill_Definition)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? SkillID {get;set;}
        
          /// <summary>
        ///  熟练度: Novice/Competent/Proficient/Expert
        /// </summary>
        [Description("熟练度: Novice/Competent/Proficient/Expert")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string ProficiencyLevel {get;set;}
        
          /// <summary>
        ///  熟练度积分(0-100)
        /// </summary>
        [Description("熟练度积分(0-100)")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? ProficiencyScore {get;set;}
        
          /// <summary>
        ///  获取时间
        /// </summary>
        [Description("获取时间")]
                  [Required]
                  public DateTime? AcquiredDate {get;set;}
        
          /// <summary>
        ///  最后使用时间
        /// </summary>
        [Description("最后使用时间")]
                 public DateTime? LastUsedDate {get;set;}
        
          /// <summary>
        ///  执行次数
        /// </summary>
        [Description("执行次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? ExecutionCount {get;set;}
        
          /// <summary>
        ///  成功次数
        /// </summary>
        [Description("成功次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? SuccessCount {get;set;}
        
          /// <summary>
        ///  失败次数
        /// </summary>
        [Description("失败次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? FailureCount {get;set;}
        
          /// <summary>
        ///  总执行时间(ms)
        /// </summary>
        [Description("总执行时间(ms)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? TotalExecutionTime {get;set;}
        
          /// <summary>
        ///  针对该智能体的特定配置覆盖JSON
        /// </summary>
        [Description("针对该智能体的特定配置覆盖JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OverrideConfig {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsEnabled {get;set;}
        
     

    }
}
