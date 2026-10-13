
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:53
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 技能执行历史表 - 记录每次技能调用详情
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Skill_Execution_LogView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  关联技能
        /// </summary>
          [ImporterHeader(Name = "关联技能", Format = ""), ExporterHeader("关联技能")]
        public long? SkillID {get;set;}
        
          /// <summary>
        ///  执行的智能体角色
        /// </summary>
          [ImporterHeader(Name = "执行的智能体角色", Format = ""), ExporterHeader("执行的智能体角色")]
        public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  关联工作流
        /// </summary>
          [ImporterHeader(Name = "关联工作流", Format = ""), ExporterHeader("关联工作流")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联任务
        /// </summary>
          [ImporterHeader(Name = "关联任务", Format = ""), ExporterHeader("关联任务")]
        public long? TaskID {get;set;}
        
          /// <summary>
        ///  请求唯一ID(幂等性保障)
        /// </summary>
          [ImporterHeader(Name = "请求唯一ID(幂等性保障)", Format = ""), ExporterHeader("请求唯一ID(幂等性保障)")]
        public string RequestID {get;set;}
        
          /// <summary>
        ///  输入参数JSON
        /// </summary>
          [ImporterHeader(Name = "输入参数JSON", Format = ""), ExporterHeader("输入参数JSON")]
        public string InputParams {get;set;}
        
          /// <summary>
        ///  输出结果JSON
        /// </summary>
          [ImporterHeader(Name = "输出结果JSON", Format = ""), ExporterHeader("输出结果JSON")]
        public string OutputResult {get;set;}
        
          /// <summary>
        ///  状态: Success/Failed/Timeout/Cancelled/Pending
        /// </summary>
          [ImporterHeader(Name = "状态: Success/Failed/Timeout/Cancelled/Pending", Format = ""), ExporterHeader("状态: Success/Failed/Timeout/Cancelled/Pending")]
        public string ExecutionStatus {get;set;}
        
          /// <summary>
        ///  执行耗时(ms)
        /// </summary>
          [ImporterHeader(Name = "执行耗时(ms)", Format = ""), ExporterHeader("执行耗时(ms)")]
        public int? ExecutionTime {get;set;}
        
          /// <summary>
        ///  开始时间
        /// </summary>
          [ImporterHeader(Name = "开始时间", Format = ""), ExporterHeader("开始时间")]
        public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  结束时间
        /// </summary>
          [ImporterHeader(Name = "结束时间", Format = ""), ExporterHeader("结束时间")]
        public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  错误码
        /// </summary>
          [ImporterHeader(Name = "错误码", Format = ""), ExporterHeader("错误码")]
        public string ErrorCode {get;set;}
        
          /// <summary>
        ///  错误信息
        /// </summary>
          [ImporterHeader(Name = "错误信息", Format = ""), ExporterHeader("错误信息")]
        public string ErrorMessage {get;set;}
        
          /// <summary>
        ///  错误详情/堆栈
        /// </summary>
          [ImporterHeader(Name = "错误详情/堆栈", Format = ""), ExporterHeader("错误详情/堆栈")]
        public string ErrorDetails {get;set;}
        
          /// <summary>
        ///  父执行记录ID(复合技能子步骤)
        /// </summary>
          [ImporterHeader(Name = "父执行记录ID(复合技能子步骤)", Format = ""), ExporterHeader("父执行记录ID(复合技能子步骤)")]
        public long? ParentExecutionID {get;set;}
        
          /// <summary>
        ///  步骤序号(复合技能)
        /// </summary>
          [ImporterHeader(Name = "步骤序号(复合技能)", Format = ""), ExporterHeader("步骤序号(复合技能)")]
        public int? StepIndex {get;set;}
        
          /// <summary>
        ///  质量评分(0-5)
        /// </summary>
          [ImporterHeader(Name = "质量评分(0-5)", Format = ""), ExporterHeader("质量评分(0-5)")]
        public decimal? QualityScore {get;set;}
        
          /// <summary>
        ///  反馈来源: Auto/Human/System
        /// </summary>
          [ImporterHeader(Name = "反馈来源: Auto/Human/System", Format = ""), ExporterHeader("反馈来源: Auto/Human/System")]
        public string FeedbackSource {get;set;}
        
          /// <summary>
        ///  反馈备注
        /// </summary>
          [ImporterHeader(Name = "反馈备注", Format = ""), ExporterHeader("反馈备注")]
        public string FeedbackNotes {get;set;}
        
     

    }
}
