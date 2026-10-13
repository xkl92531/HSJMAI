

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:05
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 描述跨任务的整体工作流，记录触发来源、规划 DAG、MAF 编排映射与执行状态。
    /// </summary>
    public class Agent_WorkflowUpdateDTO
    {
            /// <summary>
        ///  工作流主键。
        /// </summary>
        [Description("工作流主键。")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  工作流名称。
        /// </summary>
        [Description("工作流名称。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(200)]
                  public string Name {get;set;}
        
          /// <summary>
        ///  原始需求或任务描述。
        /// </summary>
        [Description("原始需求或任务描述。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  状态：0 待规划、1 规划中、2 执行中、3 完成、4 失败、5 取消。
        /// </summary>
        [Description("状态：0 待规划、1 规划中、2 执行中、3 完成、4 失败、5 取消。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Status {get;set;}
        
          /// <summary>
        ///  负责规划的智能体 ID。
        /// </summary>
        [Description("负责规划的智能体 ID。")]
                  [Range(0, long.MaxValue)]
                  public long? PlannerAgentID {get;set;}
        
          /// <summary>
        ///  分布式追踪 ID。
        /// </summary>
        [Description("分布式追踪 ID。")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string TraceId {get;set;}
        
          /// <summary>
        ///  共享上下文或黑板数据 JSON。
        /// </summary>
        [Description("共享上下文或黑板数据 JSON。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string GlobalContext {get;set;}
        
          /// <summary>
        ///  工作流最终输出摘要。
        /// </summary>
        [Description("工作流最终输出摘要。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ResultSummary {get;set;}
        
          /// <summary>
        ///  优先级，数值越大优先。
        /// </summary>
        [Description("优先级，数值越大优先。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Priority {get;set;}
        
          /// <summary>
        ///  触发方式：User/System/Schedule/SubWorkflow。
        /// </summary>
        [Description("触发方式：User/System/Schedule/SubWorkflow。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string TriggerType {get;set;}
        
          /// <summary>
        ///  外部关联 ID（会话、事件等）。
        /// </summary>
        [Description("外部关联 ID（会话、事件等）。")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string CorrelationID {get;set;}
        
          /// <summary>
        ///  父工作流 ID，支持嵌套。
        /// </summary>
        [Description("父工作流 ID，支持嵌套。")]
                  [Range(0, long.MaxValue)]
                  public long? ParentWorkflowID {get;set;}
        
          /// <summary>
        ///  触发本工作流的父任务 ID。
        /// </summary>
        [Description("触发本工作流的父任务 ID。")]
                  [Range(0, long.MaxValue)]
                  public long? TriggerTaskID {get;set;}
        
          /// <summary>
        ///  暂停原因。
        /// </summary>
        [Description("暂停原因。")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string PausedReason {get;set;}
        
          /// <summary>
        ///  恢复条件描述或表达式。
        /// </summary>
        [Description("恢复条件描述或表达式。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ResumeCondition {get;set;}
        
          /// <summary>
        ///  推理模式：ReAct/TreeOfThought/PlanExecute 等。
        /// </summary>
        [Description("推理模式：ReAct/TreeOfThought/PlanExecute 等。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string ThinkingMode {get;set;}
        
          /// <summary>
        ///  规划输出的 DAG JSON。
        /// </summary>
        [Description("规划输出的 DAG JSON。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string PlanDAG {get;set;}
        
          /// <summary>
        ///  规划状态：Draft/Approved/Rejected。
        /// </summary>
        [Description("规划状态：Draft/Approved/Rejected。")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string PlanStatus {get;set;}
        
          /// <summary>
        ///  预计耗时（秒）。
        /// </summary>
        [Description("预计耗时（秒）。")]
                  [Range(0, int.MaxValue)]
                  public int? EstimatedDuration {get;set;}
        
          /// <summary>
        ///  实际耗时（秒）。
        /// </summary>
        [Description("实际耗时（秒）。")]
                  [Range(0, int.MaxValue)]
                  public int? ActualDuration {get;set;}
        
          /// <summary>
        ///  映射的外部 MAF Workflow 主键。
        /// </summary>
        [Description("映射的外部 MAF Workflow 主键。")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string MAFWorkflowId {get;set;}
        
          /// <summary>
        ///  MAF 侧最近 Checkpoint。
        /// </summary>
        [Description("MAF 侧最近 Checkpoint。")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string MAFCheckpointId {get;set;}
        
          /// <summary>
        ///  下发到 MAF 执行器的配置 JSON。
        /// </summary>
        [Description("下发到 MAF 执行器的配置 JSON。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string MAFExecutorConfig {get;set;}
        
          /// <summary>
        ///  MAF 运行态快照。
        /// </summary>
        [Description("MAF 运行态快照。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string MAFStateSnapshot {get;set;}
        
          /// <summary>
        ///  是否由 MAF 托管执行。
        /// </summary>
        [Description("是否由 MAF 托管执行。")]
                  [Required]
                  public bool? UsesMAFWorkflow {get;set;}
        
     

    }
}
