

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:22:41
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 承载单个智能体工作流内的任务实例，覆盖 ReAct 推理、审批、人机协同与重试治理等全生命周期信息。
    /// </summary>
    public class Agent_TaskUpdateDTO
    {
            /// <summary>
        ///  任务主键，自增或雪花 ID。
        /// </summary>
        [Description("任务主键，自增或雪花 ID。")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  所属工作流 AI_Agent_Workflow 的主键。
        /// </summary>
        [Description("所属工作流 AI_Agent_Workflow 的主键。")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  任务标题或展示名称。
        /// </summary>
        [Description("任务标题或展示名称。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(200)]
                  public string TaskName {get;set;}
        
          /// <summary>
        ///  任务类型枚举，例如 Analysis/Action/Verification。
        /// </summary>
        [Description("任务类型枚举，例如 Analysis/Action/Verification。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string TaskType {get;set;}
        
          /// <summary>
        ///  实际执行的智能体角色 ID。
        /// </summary>
        [Description("实际执行的智能体角色 ID。")]
                  [Range(0, long.MaxValue)]
                  public long? AssignedAgentID {get;set;}
        
          /// <summary>
        ///  调度状态：0 待调度、1 已就绪、2 执行中、3 完成、4 失败、5 阻塞、6 待用户确认。
        /// </summary>
        [Description("调度状态：0 待调度、1 已就绪、2 执行中、3 完成、4 失败、5 阻塞、6 待用户确认。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Status {get;set;}
        
          /// <summary>
        ///  结构化输入参数 JSON。
        /// </summary>
        [Description("结构化输入参数 JSON。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string InputParams {get;set;}
        
          /// <summary>
        ///  执行输出 JSON。
        /// </summary>
        [Description("执行输出 JSON。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OutputResult {get;set;}
        
          /// <summary>
        ///  运行日志或工具调用轨迹。
        /// </summary>
        [Description("运行日志或工具调用轨迹。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ExecutionLog {get;set;}
        
          /// <summary>
        ///  异常堆栈或错误描述。
        /// </summary>
        [Description("异常堆栈或错误描述。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ErrorDetails {get;set;}
        
          /// <summary>
        ///  是否需要人类审核。
        /// </summary>
        [Description("是否需要人类审核。")]
                  [Required]
                  public bool? NeedHumanReview {get;set;}
        
          /// <summary>
        ///  人机交互配置（问题、表单、选项）。
        /// </summary>
        [Description("人机交互配置（问题、表单、选项）。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string HumanInteractionData {get;set;}
        
          /// <summary>
        ///  用户反馈及审批意见。
        /// </summary>
        [Description("用户反馈及审批意见。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string HumanFeedback {get;set;}
        
          /// <summary>
        ///  安全等级：1 信息、2 建议、3 半自动、4 全自动控制。
        /// </summary>
        [Description("安全等级：1 信息、2 建议、3 半自动、4 全自动控制。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? SafetyLevel {get;set;}
        
          /// <summary>
        ///  是否启用双人审批。
        /// </summary>
        [Description("是否启用双人审批。")]
                  [Required]
                  public bool? RequiresDualApproval {get;set;}
        
          /// <summary>
        ///  审批人清单 JSON（人员 ID/角色）。
        /// </summary>
        [Description("审批人清单 JSON（人员 ID/角色）。")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ApproverList {get;set;}
        
          /// <summary>
        ///  审批状态：0 无需、1 待批、2 通过、3 拒绝。
        /// </summary>
        [Description("审批状态：0 无需、1 待批、2 通过、3 拒绝。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? ApprovalStatus {get;set;}
        
          /// <summary>
        ///  一次性确认令牌，用于防 CSRF。
        /// </summary>
        [Description("一次性确认令牌，用于防 CSRF。")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string ApprovalToken {get;set;}
        
          /// <summary>
        ///  审批超时时间。
        /// </summary>
        [Description("审批超时时间。")]
                 public DateTime? ApprovalExpireTime {get;set;}
        
          /// <summary>
        ///  已重试次数。
        /// </summary>
        [Description("已重试次数。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? RetryCount {get;set;}
        
          /// <summary>
        ///  最大允许重试次数。
        /// </summary>
        [Description("最大允许重试次数。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? MaxRetryCount {get;set;}
        
          /// <summary>
        ///  任务超时阈值（秒）。
        /// </summary>
        [Description("任务超时阈值（秒）。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? TimeoutSeconds {get;set;}
        
          /// <summary>
        ///  实际开始执行时间。
        /// </summary>
        [Description("实际开始执行时间。")]
                 public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  实际结束时间。
        /// </summary>
        [Description("实际结束时间。")]
                 public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  父任务 ID，用于多级任务拆分。
        /// </summary>
        [Description("父任务 ID，用于多级任务拆分。")]
                  [Range(0, long.MaxValue)]
                  public long? ParentTaskID {get;set;}
        
          /// <summary>
        ///  拆解出的子工作流 ID。
        /// </summary>
        [Description("拆解出的子工作流 ID。")]
                  [Range(0, long.MaxValue)]
                  public long? SubWorkflowID {get;set;}
        
          /// <summary>
        ///  并行组编号，同组任务可并行触发。
        /// </summary>
        [Description("并行组编号，同组任务可并行触发。")]
                  [Range(0, int.MaxValue)]
                  public int? ParallelGroup {get;set;}
        
          /// <summary>
        ///  预估执行时长（秒），用于 SLA 规划。
        /// </summary>
        [Description("预估执行时长（秒），用于 SLA 规划。")]
                  [Range(0, int.MaxValue)]
                  public int? EstimatedDurationSec {get;set;}
        
          /// <summary>
        ///  智能体内部推理痕迹（ReAct Thought）。
        /// </summary>
        [Description("智能体内部推理痕迹（ReAct Thought）。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ThoughtProcess {get;set;}
        
          /// <summary>
        ///  本轮采取的 Action 描述。
        /// </summary>
        [Description("本轮采取的 Action 描述。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ActionTaken {get;set;}
        
          /// <summary>
        ///  环境 Observation 结果。
        /// </summary>
        [Description("环境 Observation 结果。")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ObservationResult {get;set;}
        
          /// <summary>
        ///  当前 ReAct 循环迭代次数。
        /// </summary>
        [Description("当前 ReAct 循环迭代次数。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? ReActLoopCount {get;set;}
        
          /// <summary>
        ///  ReAct 循环最大迭代限制。
        /// </summary>
        [Description("ReAct 循环最大迭代限制。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? MaxLoopCount {get;set;}
        
          /// <summary>
        ///  是否使用结构化 CoT
        /// </summary>
        [Description("是否使用结构化 CoT")]
                  [Required]
                  public bool? IsStructuredCoT {get;set;}
        
          /// <summary>
        ///  CoT 模板名称(关联 AI_CoT_Template.TemplateName)
        /// </summary>
        [Description("CoT 模板名称(关联 AI_CoT_Template.TemplateName)")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string CoTTemplate {get;set;}
        
          /// <summary>
        ///  当前执行到第几步
        /// </summary>
        [Description("当前执行到第几步")]
                  [Range(0, int.MaxValue)]
                  public int? CurrentCoTStep {get;set;}
        
          /// <summary>
        ///  总步骤数
        /// </summary>
        [Description("总步骤数")]
                  [Range(0, int.MaxValue)]
                  public int? CoTStepCount {get;set;}
        
     

    }
}
