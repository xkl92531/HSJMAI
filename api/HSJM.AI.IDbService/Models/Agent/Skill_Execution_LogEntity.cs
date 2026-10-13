
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:53
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 技能执行历史表 - 记录每次技能调用详情
    /// </summary>
    [SugarTable("AI_Skill_Execution_Log",tableDescription:"技能执行历史表 - 记录每次技能调用详情")]
    public class Skill_Execution_LogEntity: BaseField
    {

       public  Skill_Execution_LogEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联技能
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联技能")]
        public long? SkillID {get;set;}
        
          /// <summary>
        ///  执行的智能体角色
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="执行的智能体角色")]
        public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  关联工作流
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联工作流")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联任务
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联任务")]
        public long? TaskID {get;set;}
        
          /// <summary>
        ///  请求唯一ID(幂等性保障)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="请求唯一ID(幂等性保障)")]
        public string RequestID {get;set;}
        
          /// <summary>
        ///  输入参数JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输入参数JSON")]
        public string InputParams {get;set;}
        
          /// <summary>
        ///  输出结果JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出结果JSON")]
        public string OutputResult {get;set;}
        
          /// <summary>
        ///  状态: Success/Failed/Timeout/Cancelled/Pending
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="状态: Success/Failed/Timeout/Cancelled/Pending")]
        public string ExecutionStatus {get;set;}
        
          /// <summary>
        ///  执行耗时(ms)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="执行耗时(ms)")]
        public int? ExecutionTime {get;set;}
        
          /// <summary>
        ///  开始时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="开始时间")]
        public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  结束时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="结束时间")]
        public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  错误码
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误码")]
        public string ErrorCode {get;set;}
        
          /// <summary>
        ///  错误信息
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误信息")]
        public string ErrorMessage {get;set;}
        
          /// <summary>
        ///  错误详情/堆栈
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误详情/堆栈")]
        public string ErrorDetails {get;set;}
        
          /// <summary>
        ///  父执行记录ID(复合技能子步骤)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="父执行记录ID(复合技能子步骤)")]
        public long? ParentExecutionID {get;set;}
        
          /// <summary>
        ///  步骤序号(复合技能)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="步骤序号(复合技能)")]
        public int? StepIndex {get;set;}
        
          /// <summary>
        ///  质量评分(0-5)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="质量评分(0-5)")]
        public decimal? QualityScore {get;set;}
        
          /// <summary>
        ///  反馈来源: Auto/Human/System
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="反馈来源: Auto/Human/System")]
        public string FeedbackSource {get;set;}
        
          /// <summary>
        ///  反馈备注
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="反馈备注")]
        public string FeedbackNotes {get;set;}
        
     

    }

   
}
