
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:22:41
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 承载单个智能体工作流内的任务实例，覆盖 ReAct 推理、审批、人机协同与重试治理等全生命周期信息。
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Agent_TaskView:ViewBaseField
    {
            /// <summary>
        ///  任务主键，自增或雪花 ID。
        /// </summary>
          [ImporterHeader(Name = "任务主键，自增或雪花 ID。", Format = ""), ExporterHeader("任务主键，自增或雪花 ID。")]
        public long ID {get;set;}
        
          /// <summary>
        ///  所属工作流 AI_Agent_Workflow 的主键。
        /// </summary>
          [ImporterHeader(Name = "所属工作流 AI_Agent_Workflow 的主键。", Format = ""), ExporterHeader("所属工作流 AI_Agent_Workflow 的主键。")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  任务标题或展示名称。
        /// </summary>
          [ImporterHeader(Name = "任务标题或展示名称。", Format = ""), ExporterHeader("任务标题或展示名称。")]
        public string TaskName {get;set;}
        
          /// <summary>
        ///  任务类型枚举，例如 Analysis/Action/Verification。
        /// </summary>
          [ImporterHeader(Name = "任务类型枚举，例如 Analysis/Action/Verification。", Format = ""), ExporterHeader("任务类型枚举，例如 Analysis/Action/Verification。")]
        public string TaskType {get;set;}
        
          /// <summary>
        ///  实际执行的智能体角色 ID。
        /// </summary>
          [ImporterHeader(Name = "实际执行的智能体角色 ID。", Format = ""), ExporterHeader("实际执行的智能体角色 ID。")]
        public long? AssignedAgentID {get;set;}
        
          /// <summary>
        ///  调度状态：0 待调度、1 已就绪、2 执行中、3 完成、4 失败、5 阻塞、6 待用户确认。
        /// </summary>
          [ImporterHeader(Name = "调度状态：0 待调度、1 已就绪、2 执行中、3 完成、4 失败、5 阻塞、6 待用户确认。", Format = ""), ExporterHeader("调度状态：0 待调度、1 已就绪、2 执行中、3 完成、4 失败、5 阻塞、6 待用户确认。")]
        public int? Status {get;set;}
        
          /// <summary>
        ///  结构化输入参数 JSON。
        /// </summary>
          [ImporterHeader(Name = "结构化输入参数 JSON。", Format = ""), ExporterHeader("结构化输入参数 JSON。")]
        public string InputParams {get;set;}
        
          /// <summary>
        ///  执行输出 JSON。
        /// </summary>
          [ImporterHeader(Name = "执行输出 JSON。", Format = ""), ExporterHeader("执行输出 JSON。")]
        public string OutputResult {get;set;}
        
          /// <summary>
        ///  运行日志或工具调用轨迹。
        /// </summary>
          [ImporterHeader(Name = "运行日志或工具调用轨迹。", Format = ""), ExporterHeader("运行日志或工具调用轨迹。")]
        public string ExecutionLog {get;set;}
        
          /// <summary>
        ///  异常堆栈或错误描述。
        /// </summary>
          [ImporterHeader(Name = "异常堆栈或错误描述。", Format = ""), ExporterHeader("异常堆栈或错误描述。")]
        public string ErrorDetails {get;set;}
        
          /// <summary>
        ///  是否需要人类审核。
        /// </summary>
          [ImporterHeader(Name = "是否需要人类审核。", Format = ""), ExporterHeader("是否需要人类审核。")]
        public bool? NeedHumanReview {get;set;}
        
          /// <summary>
        ///  人机交互配置（问题、表单、选项）。
        /// </summary>
          [ImporterHeader(Name = "人机交互配置（问题、表单、选项）。", Format = ""), ExporterHeader("人机交互配置（问题、表单、选项）。")]
        public string HumanInteractionData {get;set;}
        
          /// <summary>
        ///  用户反馈及审批意见。
        /// </summary>
          [ImporterHeader(Name = "用户反馈及审批意见。", Format = ""), ExporterHeader("用户反馈及审批意见。")]
        public string HumanFeedback {get;set;}
        
          /// <summary>
        ///  安全等级：1 信息、2 建议、3 半自动、4 全自动控制。
        /// </summary>
          [ImporterHeader(Name = "安全等级：1 信息、2 建议、3 半自动、4 全自动控制。", Format = ""), ExporterHeader("安全等级：1 信息、2 建议、3 半自动、4 全自动控制。")]
        public int? SafetyLevel {get;set;}
        
          /// <summary>
        ///  是否启用双人审批。
        /// </summary>
          [ImporterHeader(Name = "是否启用双人审批。", Format = ""), ExporterHeader("是否启用双人审批。")]
        public bool? RequiresDualApproval {get;set;}
        
          /// <summary>
        ///  审批人清单 JSON（人员 ID/角色）。
        /// </summary>
          [ImporterHeader(Name = "审批人清单 JSON（人员 ID/角色）。", Format = ""), ExporterHeader("审批人清单 JSON（人员 ID/角色）。")]
        public string ApproverList {get;set;}
        
          /// <summary>
        ///  审批状态：0 无需、1 待批、2 通过、3 拒绝。
        /// </summary>
          [ImporterHeader(Name = "审批状态：0 无需、1 待批、2 通过、3 拒绝。", Format = ""), ExporterHeader("审批状态：0 无需、1 待批、2 通过、3 拒绝。")]
        public int? ApprovalStatus {get;set;}
        
          /// <summary>
        ///  一次性确认令牌，用于防 CSRF。
        /// </summary>
          [ImporterHeader(Name = "一次性确认令牌，用于防 CSRF。", Format = ""), ExporterHeader("一次性确认令牌，用于防 CSRF。")]
        public string ApprovalToken {get;set;}
        
          /// <summary>
        ///  审批超时时间。
        /// </summary>
          [ImporterHeader(Name = "审批超时时间。", Format = ""), ExporterHeader("审批超时时间。")]
        public DateTime? ApprovalExpireTime {get;set;}
        
          /// <summary>
        ///  已重试次数。
        /// </summary>
          [ImporterHeader(Name = "已重试次数。", Format = ""), ExporterHeader("已重试次数。")]
        public int? RetryCount {get;set;}
        
          /// <summary>
        ///  最大允许重试次数。
        /// </summary>
          [ImporterHeader(Name = "最大允许重试次数。", Format = ""), ExporterHeader("最大允许重试次数。")]
        public int? MaxRetryCount {get;set;}
        
          /// <summary>
        ///  任务超时阈值（秒）。
        /// </summary>
          [ImporterHeader(Name = "任务超时阈值（秒）。", Format = ""), ExporterHeader("任务超时阈值（秒）。")]
        public int? TimeoutSeconds {get;set;}
        
          /// <summary>
        ///  实际开始执行时间。
        /// </summary>
          [ImporterHeader(Name = "实际开始执行时间。", Format = ""), ExporterHeader("实际开始执行时间。")]
        public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  实际结束时间。
        /// </summary>
          [ImporterHeader(Name = "实际结束时间。", Format = ""), ExporterHeader("实际结束时间。")]
        public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  父任务 ID，用于多级任务拆分。
        /// </summary>
          [ImporterHeader(Name = "父任务 ID，用于多级任务拆分。", Format = ""), ExporterHeader("父任务 ID，用于多级任务拆分。")]
        public long? ParentTaskID {get;set;}
        
          /// <summary>
        ///  拆解出的子工作流 ID。
        /// </summary>
          [ImporterHeader(Name = "拆解出的子工作流 ID。", Format = ""), ExporterHeader("拆解出的子工作流 ID。")]
        public long? SubWorkflowID {get;set;}
        
          /// <summary>
        ///  并行组编号，同组任务可并行触发。
        /// </summary>
          [ImporterHeader(Name = "并行组编号，同组任务可并行触发。", Format = ""), ExporterHeader("并行组编号，同组任务可并行触发。")]
        public int? ParallelGroup {get;set;}
        
          /// <summary>
        ///  预估执行时长（秒），用于 SLA 规划。
        /// </summary>
          [ImporterHeader(Name = "预估执行时长（秒），用于 SLA 规划。", Format = ""), ExporterHeader("预估执行时长（秒），用于 SLA 规划。")]
        public int? EstimatedDurationSec {get;set;}
        
          /// <summary>
        ///  智能体内部推理痕迹（ReAct Thought）。
        /// </summary>
          [ImporterHeader(Name = "智能体内部推理痕迹（ReAct Thought）。", Format = ""), ExporterHeader("智能体内部推理痕迹（ReAct Thought）。")]
        public string ThoughtProcess {get;set;}
        
          /// <summary>
        ///  本轮采取的 Action 描述。
        /// </summary>
          [ImporterHeader(Name = "本轮采取的 Action 描述。", Format = ""), ExporterHeader("本轮采取的 Action 描述。")]
        public string ActionTaken {get;set;}
        
          /// <summary>
        ///  环境 Observation 结果。
        /// </summary>
          [ImporterHeader(Name = "环境 Observation 结果。", Format = ""), ExporterHeader("环境 Observation 结果。")]
        public string ObservationResult {get;set;}
        
          /// <summary>
        ///  当前 ReAct 循环迭代次数。
        /// </summary>
          [ImporterHeader(Name = "当前 ReAct 循环迭代次数。", Format = ""), ExporterHeader("当前 ReAct 循环迭代次数。")]
        public int? ReActLoopCount {get;set;}
        
          /// <summary>
        ///  ReAct 循环最大迭代限制。
        /// </summary>
          [ImporterHeader(Name = "ReAct 循环最大迭代限制。", Format = ""), ExporterHeader("ReAct 循环最大迭代限制。")]
        public int? MaxLoopCount {get;set;}
        
          /// <summary>
        ///  是否使用结构化 CoT
        /// </summary>
          [ImporterHeader(Name = "是否使用结构化 CoT", Format = ""), ExporterHeader("是否使用结构化 CoT")]
        public bool? IsStructuredCoT {get;set;}
        
          /// <summary>
        ///  CoT 模板名称(关联 AI_CoT_Template.TemplateName)
        /// </summary>
          [ImporterHeader(Name = "CoT 模板名称(关联 AI_CoT_Template.TemplateName)", Format = ""), ExporterHeader("CoT 模板名称(关联 AI_CoT_Template.TemplateName)")]
        public string CoTTemplate {get;set;}
        
          /// <summary>
        ///  当前执行到第几步
        /// </summary>
          [ImporterHeader(Name = "当前执行到第几步", Format = ""), ExporterHeader("当前执行到第几步")]
        public int? CurrentCoTStep {get;set;}
        
          /// <summary>
        ///  总步骤数
        /// </summary>
          [ImporterHeader(Name = "总步骤数", Format = ""), ExporterHeader("总步骤数")]
        public int? CoTStepCount {get;set;}
        
     

    }
}
