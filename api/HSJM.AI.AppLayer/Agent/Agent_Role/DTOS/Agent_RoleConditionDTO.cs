

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:27:12
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Agent_RoleConditionDTO
    {
            /// <summary>
        ///  是否公开(所有用户可见)
        /// </summary>
        public bool? IsPublic {get;set;}
        
           /// <summary>
        ///  是否启用
        /// </summary>
        public bool? IsEnabled {get;set;}
        
           /// <summary>
        ///  智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)
        /// </summary>
        public string AgentType {get;set;}
        
           /// <summary>
        ///  关联的大模型ID (AI_LLM_Model)
        /// </summary>
        public long? ModelID {get;set;}
        
           /// <summary>
        ///  系统提示词(Persona)
        /// </summary>
        public string SystemPrompt {get;set;}
        
           /// <summary>
        ///  角色描述
        /// </summary>
        public string Description {get;set;}
        
           /// <summary>
        ///  角色编码(唯一标识)
        /// </summary>
        public string RoleCode {get;set;}
        
           /// <summary>
        ///  角色名称
        /// </summary>
        public string RoleName {get;set;}
        
      

    }
}
