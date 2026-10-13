
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:24:17
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 任务 CoT 步骤表 - 存储任务执行时的步骤实例
    /// </summary>
    [SugarTable("AI_Task_CoT_Step",tableDescription:"任务 CoT 步骤表 - 存储任务执行时的步骤实例")]
    public class Task_CoT_StepEntity: BaseField
    {

       public  Task_CoT_StepEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联任务ID (AI_Agent_Task)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联任务ID (AI_Agent_Task)")]
        public long? TaskID {get;set;}
        
          /// <summary>
        ///  步骤顺序(1, 2, 3...)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤顺序(1, 2, 3...)")]
        public int? StepOrder {get;set;}
        
          /// <summary>
        ///  步骤名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤名称")]
        public string StepName {get;set;}
        
          /// <summary>
        ///  步骤说明
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤说明")]
        public string StepDescription {get;set;}
        
          /// <summary>
        ///  该步骤的 Prompt 模板(支持占位符: {taskContext.xxx}, {previousOutput})
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="该步骤的 Prompt 模板(支持占位符: {taskContext.xxx}, {previousOutput})")]
        public string StepPrompt {get;set;}
        
          /// <summary>
        ///  是否使用上一步输出作为输入
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否使用上一步输出作为输入")]
        public bool? InputFromPrevStep {get;set;}
        
          /// <summary>
        ///  输出格式要求 JSON Schema(用于验证)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出格式要求 JSON Schema(用于验证)")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  自定义验证规则 JSON(可选)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="自定义验证规则 JSON(可选)")]
        public string ValidationRule {get;set;}
        
          /// <summary>
        ///  是否强制执行(true: 失败则终止任务, false: 失败可继续)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否强制执行(true: 失败则终止任务, false: 失败可继续)")]
        public bool? IsMandatory {get;set;}
        
          /// <summary>
        ///  是否允许跳过
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否允许跳过")]
        public bool? AllowSkip {get;set;}
        
          /// <summary>
        ///  跳过条件表达式(如: syntaxErrors.Count > 10)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="跳过条件表达式(如: syntaxErrors.Count > 10)")]
        public string SkipCondition {get;set;}
        
          /// <summary>
        ///  状态: NotStarted(未开始)/InProgress(执行中)/Completed(已完成)/Skipped(已跳过)/Failed(失败)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="状态: NotStarted(未开始)/InProgress(执行中)/Completed(已完成)/Skipped(已跳过)/Failed(失败)")]
        public string Status {get;set;}
        
          /// <summary>
        ///  步骤开始时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤开始时间")]
        public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  步骤结束时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤结束时间")]
        public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  执行时长(毫秒)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="执行时长(毫秒)")]
        public int? Duration {get;set;}
        
          /// <summary>
        ///  思考过程(Agent 内部推理过程)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="思考过程(Agent 内部推理过程)")]
        public string ThinkingProcess {get;set;}
        
          /// <summary>
        ///  步骤输出 JSON(结构化结果)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤输出 JSON(结构化结果)")]
        public string StepOutput {get;set;}
        
          /// <summary>
        ///  错误信息(失败时记录)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误信息(失败时记录)")]
        public string ErrorMessage {get;set;}
        
          /// <summary>
        ///  执行该步骤的智能体角色ID(允许步骤级别指定不同角色)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="执行该步骤的智能体角色ID(允许步骤级别指定不同角色)")]
        public long? ExecutorRoleId {get;set;}
        
          /// <summary>
        ///  使用的模型名称(如: gpt-4, claude-3)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="使用的模型名称(如: gpt-4, claude-3)")]
        public string ModelUsed {get;set;}
        
          /// <summary>
        ///  消耗的 Token 数量
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="消耗的 Token 数量")]
        public int? TokensUsed {get;set;}
        
          /// <summary>
        ///  重试次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="重试次数")]
        public int? RetryCount {get;set;}
        
     

    }

   
}
