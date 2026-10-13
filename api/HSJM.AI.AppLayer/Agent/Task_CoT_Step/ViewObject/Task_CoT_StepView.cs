
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:24:17
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 任务 CoT 步骤表 - 存储任务执行时的步骤实例
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Task_CoT_StepView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  关联任务ID (AI_Agent_Task)
        /// </summary>
          [ImporterHeader(Name = "关联任务ID (AI_Agent_Task)", Format = ""), ExporterHeader("关联任务ID (AI_Agent_Task)")]
        public long? TaskID {get;set;}
        
          /// <summary>
        ///  步骤顺序(1, 2, 3...)
        /// </summary>
          [ImporterHeader(Name = "步骤顺序(1, 2, 3...)", Format = ""), ExporterHeader("步骤顺序(1, 2, 3...)")]
        public int? StepOrder {get;set;}
        
          /// <summary>
        ///  步骤名称
        /// </summary>
          [ImporterHeader(Name = "步骤名称", Format = ""), ExporterHeader("步骤名称")]
        public string StepName {get;set;}
        
          /// <summary>
        ///  步骤说明
        /// </summary>
          [ImporterHeader(Name = "步骤说明", Format = ""), ExporterHeader("步骤说明")]
        public string StepDescription {get;set;}
        
          /// <summary>
        ///  该步骤的 Prompt 模板(支持占位符: {taskContext.xxx}, {previousOutput})
        /// </summary>
          [ImporterHeader(Name = "该步骤的 Prompt 模板(支持占位符: {taskContext.xxx}, {previousOutput})", Format = ""), ExporterHeader("该步骤的 Prompt 模板(支持占位符: {taskContext.xxx}, {previousOutput})")]
        public string StepPrompt {get;set;}
        
          /// <summary>
        ///  是否使用上一步输出作为输入
        /// </summary>
          [ImporterHeader(Name = "是否使用上一步输出作为输入", Format = ""), ExporterHeader("是否使用上一步输出作为输入")]
        public bool? InputFromPrevStep {get;set;}
        
          /// <summary>
        ///  输出格式要求 JSON Schema(用于验证)
        /// </summary>
          [ImporterHeader(Name = "输出格式要求 JSON Schema(用于验证)", Format = ""), ExporterHeader("输出格式要求 JSON Schema(用于验证)")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  自定义验证规则 JSON(可选)
        /// </summary>
          [ImporterHeader(Name = "自定义验证规则 JSON(可选)", Format = ""), ExporterHeader("自定义验证规则 JSON(可选)")]
        public string ValidationRule {get;set;}
        
          /// <summary>
        ///  是否强制执行(true: 失败则终止任务, false: 失败可继续)
        /// </summary>
          [ImporterHeader(Name = "是否强制执行(true: 失败则终止任务, false: 失败可继续)", Format = ""), ExporterHeader("是否强制执行(true: 失败则终止任务, false: 失败可继续)")]
        public bool? IsMandatory {get;set;}
        
          /// <summary>
        ///  是否允许跳过
        /// </summary>
          [ImporterHeader(Name = "是否允许跳过", Format = ""), ExporterHeader("是否允许跳过")]
        public bool? AllowSkip {get;set;}
        
          /// <summary>
        ///  跳过条件表达式(如: syntaxErrors.Count > 10)
        /// </summary>
          [ImporterHeader(Name = "跳过条件表达式(如: syntaxErrors.Count > 10)", Format = ""), ExporterHeader("跳过条件表达式(如: syntaxErrors.Count > 10)")]
        public string SkipCondition {get;set;}
        
          /// <summary>
        ///  状态: NotStarted(未开始)/InProgress(执行中)/Completed(已完成)/Skipped(已跳过)/Failed(失败)
        /// </summary>
          [ImporterHeader(Name = "状态: NotStarted(未开始)/InProgress(执行中)/Completed(已完成)/Skipped(已跳过)/Failed(失败)", Format = ""), ExporterHeader("状态: NotStarted(未开始)/InProgress(执行中)/Completed(已完成)/Skipped(已跳过)/Failed(失败)")]
        public string Status {get;set;}
        
          /// <summary>
        ///  步骤开始时间
        /// </summary>
          [ImporterHeader(Name = "步骤开始时间", Format = ""), ExporterHeader("步骤开始时间")]
        public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  步骤结束时间
        /// </summary>
          [ImporterHeader(Name = "步骤结束时间", Format = ""), ExporterHeader("步骤结束时间")]
        public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  执行时长(毫秒)
        /// </summary>
          [ImporterHeader(Name = "执行时长(毫秒)", Format = ""), ExporterHeader("执行时长(毫秒)")]
        public int? Duration {get;set;}
        
          /// <summary>
        ///  思考过程(Agent 内部推理过程)
        /// </summary>
          [ImporterHeader(Name = "思考过程(Agent 内部推理过程)", Format = ""), ExporterHeader("思考过程(Agent 内部推理过程)")]
        public string ThinkingProcess {get;set;}
        
          /// <summary>
        ///  步骤输出 JSON(结构化结果)
        /// </summary>
          [ImporterHeader(Name = "步骤输出 JSON(结构化结果)", Format = ""), ExporterHeader("步骤输出 JSON(结构化结果)")]
        public string StepOutput {get;set;}
        
          /// <summary>
        ///  错误信息(失败时记录)
        /// </summary>
          [ImporterHeader(Name = "错误信息(失败时记录)", Format = ""), ExporterHeader("错误信息(失败时记录)")]
        public string ErrorMessage {get;set;}
        
          /// <summary>
        ///  执行该步骤的智能体角色ID(允许步骤级别指定不同角色)
        /// </summary>
          [ImporterHeader(Name = "执行该步骤的智能体角色ID(允许步骤级别指定不同角色)", Format = ""), ExporterHeader("执行该步骤的智能体角色ID(允许步骤级别指定不同角色)")]
        public long? ExecutorRoleId {get;set;}
        
          /// <summary>
        ///  使用的模型名称(如: gpt-4, claude-3)
        /// </summary>
          [ImporterHeader(Name = "使用的模型名称(如: gpt-4, claude-3)", Format = ""), ExporterHeader("使用的模型名称(如: gpt-4, claude-3)")]
        public string ModelUsed {get;set;}
        
          /// <summary>
        ///  消耗的 Token 数量
        /// </summary>
          [ImporterHeader(Name = "消耗的 Token 数量", Format = ""), ExporterHeader("消耗的 Token 数量")]
        public int? TokensUsed {get;set;}
        
          /// <summary>
        ///  重试次数
        /// </summary>
          [ImporterHeader(Name = "重试次数", Format = ""), ExporterHeader("重试次数")]
        public int? RetryCount {get;set;}
        
     

    }
}
