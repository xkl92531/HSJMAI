

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:27:12
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 智能体角色配置表
    /// </summary>
    public class Agent_RoleInsertDTO
    {
            /// <summary>
        ///  角色名称
        /// </summary>
        [Description("角色名称")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string RoleName {get;set;}
        
          /// <summary>
        ///  角色编码(唯一标识)
        /// </summary>
        [Description("角色编码(唯一标识)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string RoleCode {get;set;}
        
          /// <summary>
        ///  角色描述
        /// </summary>
        [Description("角色描述")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  角色头像URL
        /// </summary>
        [Description("角色头像URL")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string AvatarUrl {get;set;}
        
          /// <summary>
        ///  默认欢迎语
        /// </summary>
        [Description("默认欢迎语")]
                  [MinLength(0)]
                   [MaxLength(1000)]
                  public string WelcomeMessage {get;set;}
        
          /// <summary>
        ///  系统提示词(Persona)
        /// </summary>
        [Description("系统提示词(Persona)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string SystemPrompt {get;set;}
        
          /// <summary>
        ///  关联的大模型ID (AI_LLM_Model)
        /// </summary>
        [Description("关联的大模型ID (AI_LLM_Model)")]
                  [Range(0, long.MaxValue)]
                  public long? ModelID {get;set;}
        
          /// <summary>
        ///  模型特定参数覆盖JSON(TemperatureTopP等)
        /// </summary>
        [Description("模型特定参数覆盖JSON(TemperatureTopP等)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ModelParams {get;set;}
        
          /// <summary>
        ///  智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)
        /// </summary>
        [Description("智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string AgentType {get;set;}
        
          /// <summary>
        ///  智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)
        /// </summary>
        [Description("智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string AgentConfig {get;set;}
        
          /// <summary>
        ///  代码实现类全名(用于Code类型智能体)
        /// </summary>
        [Description("代码实现类全名(用于Code类型智能体)")]
                  [MinLength(0)]
                   [MaxLength(255)]
                  public string ImplementationClass {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  是否公开(所有用户可见)
        /// </summary>
        [Description("是否公开(所有用户可见)")]
                  [Required]
                  public bool? IsPublic {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
        [Description("版本号")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Version {get;set;}
        
     

    }
}
