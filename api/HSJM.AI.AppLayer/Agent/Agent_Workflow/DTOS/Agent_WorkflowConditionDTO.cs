

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:05
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Agent_WorkflowConditionDTO
    {
            /// <summary>
        ///  是否由 MAF 托管执行。
        /// </summary>
        public bool? UsesMAFWorkflow {get;set;}
        
           /// <summary>
        ///  MAF 侧最近 Checkpoint。
        /// </summary>
        public string MAFCheckpointId {get;set;}
        
           /// <summary>
        ///  映射的外部 MAF Workflow 主键。
        /// </summary>
        public string MAFWorkflowId {get;set;}
        
           /// <summary>
        ///  规划状态：Draft/Approved/Rejected。
        /// </summary>
        public string PlanStatus {get;set;}
        
           /// <summary>
        ///  触发方式：User/System/Schedule/SubWorkflow。
        /// </summary>
        public string TriggerType {get;set;}
        
           /// <summary>
        ///  负责规划的智能体 ID。
        /// </summary>
        public long? PlannerAgentID {get;set;}
        
           /// <summary>
        ///  分布式追踪 ID。
        /// </summary>
        public string TraceId {get;set;}
        
      

    }
}
