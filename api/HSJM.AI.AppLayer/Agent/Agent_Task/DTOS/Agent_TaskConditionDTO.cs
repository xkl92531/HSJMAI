

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:22:41
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Agent_TaskConditionDTO
    {
            /// <summary>
        ///  任务类型枚举，例如 Analysis/Action/Verification。
        /// </summary>
        public string TaskType {get;set;}
        
           /// <summary>
        ///  任务标题或展示名称。
        /// </summary>
        public string TaskName {get;set;}
        
           /// <summary>
        ///  所属工作流 AI_Agent_Workflow 的主键。
        /// </summary>
        public long? WorkflowID {get;set;}
        
           /// <summary>
        ///  实际执行的智能体角色 ID。
        /// </summary>
        public long? AssignedAgentID {get;set;}
        
           /// <summary>
        ///  调度状态：0 待调度、1 已就绪、2 执行中、3 完成、4 失败、5 阻塞、6 待用户确认。
        /// </summary>
        public int? Status {get;set;}
        
           /// <summary>
        ///  是否需要人类审核。
        /// </summary>
        public bool? NeedHumanReview {get;set;}
        
           /// <summary>
        ///  是否启用双人审批。
        /// </summary>
        public bool? RequiresDualApproval {get;set;}
        
           /// <summary>
        ///  审批状态：0 无需、1 待批、2 通过、3 拒绝。
        /// </summary>
        public int? ApprovalStatus {get;set;}
        
           /// <summary>
        ///  父任务 ID，用于多级任务拆分。
        /// </summary>
        public long? ParentTaskID {get;set;}
        
           /// <summary>
        ///  拆解出的子工作流 ID。
        /// </summary>
        public long? SubWorkflowID {get;set;}
        
           /// <summary>
        ///  是否使用结构化 CoT
        /// </summary>
        public bool? IsStructuredCoT {get;set;}
        
      

    }
}
