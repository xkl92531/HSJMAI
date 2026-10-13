
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:27:12
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 智能体角色配置表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Agent_RoleView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  角色名称
        /// </summary>
          [ImporterHeader(Name = "角色名称", Format = ""), ExporterHeader("角色名称")]
        public string RoleName {get;set;}
        
          /// <summary>
        ///  角色编码(唯一标识)
        /// </summary>
          [ImporterHeader(Name = "角色编码(唯一标识)", Format = ""), ExporterHeader("角色编码(唯一标识)")]
        public string RoleCode {get;set;}
        
          /// <summary>
        ///  角色描述
        /// </summary>
          [ImporterHeader(Name = "角色描述", Format = ""), ExporterHeader("角色描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  角色头像URL
        /// </summary>
          [ImporterHeader(Name = "角色头像URL", Format = ""), ExporterHeader("角色头像URL")]
        public string AvatarUrl {get;set;}
        
          /// <summary>
        ///  默认欢迎语
        /// </summary>
          [ImporterHeader(Name = "默认欢迎语", Format = ""), ExporterHeader("默认欢迎语")]
        public string WelcomeMessage {get;set;}
        
          /// <summary>
        ///  系统提示词(Persona)
        /// </summary>
          [ImporterHeader(Name = "系统提示词(Persona)", Format = ""), ExporterHeader("系统提示词(Persona)")]
        public string SystemPrompt {get;set;}
        
          /// <summary>
        ///  关联的大模型ID (AI_LLM_Model)
        /// </summary>
          [ImporterHeader(Name = "关联的大模型ID (AI_LLM_Model)", Format = ""), ExporterHeader("关联的大模型ID (AI_LLM_Model)")]
        public long? ModelID {get;set;}
        
          /// <summary>
        ///  模型特定参数覆盖JSON(TemperatureTopP等)
        /// </summary>
          [ImporterHeader(Name = "模型特定参数覆盖JSON(TemperatureTopP等)", Format = ""), ExporterHeader("模型特定参数覆盖JSON(TemperatureTopP等)")]
        public string ModelParams {get;set;}
        
          /// <summary>
        ///  智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)
        /// </summary>
          [ImporterHeader(Name = "智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)", Format = ""), ExporterHeader("智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)")]
        public string AgentType {get;set;}
        
          /// <summary>
        ///  智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)
        /// </summary>
          [ImporterHeader(Name = "智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)", Format = ""), ExporterHeader("智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)")]
        public string AgentConfig {get;set;}
        
          /// <summary>
        ///  代码实现类全名(用于Code类型智能体)
        /// </summary>
          [ImporterHeader(Name = "代码实现类全名(用于Code类型智能体)", Format = ""), ExporterHeader("代码实现类全名(用于Code类型智能体)")]
        public string ImplementationClass {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  是否公开(所有用户可见)
        /// </summary>
          [ImporterHeader(Name = "是否公开(所有用户可见)", Format = ""), ExporterHeader("是否公开(所有用户可见)")]
        public bool? IsPublic {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
          [ImporterHeader(Name = "版本号", Format = ""), ExporterHeader("版本号")]
        public int? Version {get;set;}
        
     

    }
}
