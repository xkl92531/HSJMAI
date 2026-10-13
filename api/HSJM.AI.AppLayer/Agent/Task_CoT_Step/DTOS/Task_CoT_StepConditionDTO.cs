

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:24:17
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Task_CoT_StepConditionDTO
    {
            /// <summary>
        ///  关联任务ID (AI_Agent_Task)
        /// </summary>
        public long? TaskID {get;set;}
        
           /// <summary>
        ///  步骤顺序(1, 2, 3...)
        /// </summary>
        public int? StepOrder {get;set;}
        
           /// <summary>
        ///  步骤名称
        /// </summary>
        public string StepName {get;set;}
        
           /// <summary>
        ///  状态: NotStarted(未开始)/InProgress(执行中)/Completed(已完成)/Skipped(已跳过)/Failed(失败)
        /// </summary>
        public string Status {get;set;}
        
           /// <summary>
        ///  使用的模型名称(如: gpt-4, claude-3)
        /// </summary>
        public string ModelUsed {get;set;}
        
           /// <summary>
        ///  执行该步骤的智能体角色ID(允许步骤级别指定不同角色)
        /// </summary>
        public long? ExecutorRoleId {get;set;}
        
           /// <summary>
        ///  是否允许跳过
        /// </summary>
        public bool? AllowSkip {get;set;}
        
           /// <summary>
        ///  是否强制执行(true: 失败则终止任务, false: 失败可继续)
        /// </summary>
        public bool? IsMandatory {get;set;}
        
           /// <summary>
        ///  是否使用上一步输出作为输入
        /// </summary>
        public bool? InputFromPrevStep {get;set;}
        
      

    }
}
