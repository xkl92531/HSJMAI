

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:25:03
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Tool_Generation_RequestConditionDTO
    {
            /// <summary>
        ///  请求生成工具的智能体ID (AI_Agent_Role)
        /// </summary>
        public long? RequestorAgentID {get;set;}
        
           /// <summary>
        ///  触发该请求的任务ID(可选, 用于追溯上下文)
        /// </summary>
        public long? RequestorTaskID {get;set;}
        
           /// <summary>
        ///  代码生成时间起始时间
        /// </summary>
        public DateTime? GenerationTimeStart {get;set;}
        
           /// <summary>
        ///  代码生成时间结束时间
        /// </summary>
        public DateTime? GenerationTimeEnd {get;set;}
        
           /// <summary>
        ///  状态: Requested(已请求)/Generating(生成中)/Generated(已生成)/Testing(测试中)/PendingApproval(待审批)/Approved(已批准)/Rejected(已拒绝)/Deployed(已部署)
        /// </summary>
        public string Status {get;set;}
        
           /// <summary>
        ///  审批人ID
        /// </summary>
        public long? ApproverID {get;set;}
        
           /// <summary>
        ///  生成后的工具ID (AI_Tool_Definition)
        /// </summary>
        public long? GeneratedToolID {get;set;}
        
           /// <summary>
        ///  生成后的技能ID (AI_Skill_Definition, 可选)
        /// </summary>
        public long? GeneratedSkillID {get;set;}
        
      

    }
}
