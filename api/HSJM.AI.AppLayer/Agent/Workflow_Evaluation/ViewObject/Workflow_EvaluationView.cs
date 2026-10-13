
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:18
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 工作流/任务执行评估表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Workflow_EvaluationView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  关联的工作流ID
        /// </summary>
          [ImporterHeader(Name = "关联的工作流ID", Format = ""), ExporterHeader("关联的工作流ID")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联的根任务ID(如果是针对单个大任务的评估)
        /// </summary>
          [ImporterHeader(Name = "关联的根任务ID(如果是针对单个大任务的评估)", Format = ""), ExporterHeader("关联的根任务ID(如果是针对单个大任务的评估)")]
        public long? RootTaskID {get;set;}
        
          /// <summary>
        ///  评估者类型: 0-人类专家, 1-AI反思者(Critic Agent), 2-系统规则
        /// </summary>
          [ImporterHeader(Name = "评估者类型: 0-人类专家, 1-AI反思者(Critic Agent), 2-系统规则", Format = ""), ExporterHeader("评估者类型: 0-人类专家, 1-AI反思者(Critic Agent), 2-系统规则")]
        public int? EvaluatorType {get;set;}
        
          /// <summary>
        ///  评估者ID
        /// </summary>
          [ImporterHeader(Name = "评估者ID", Format = ""), ExporterHeader("评估者ID")]
        public string EvaluatorID {get;set;}
        
          /// <summary>
        ///  总体评分(0-100)
        /// </summary>
          [ImporterHeader(Name = "总体评分(0-100)", Format = ""), ExporterHeader("总体评分(0-100)")]
        public int? OverallScore {get;set;}
        
          /// <summary>
        ///  效率评分
        /// </summary>
          [ImporterHeader(Name = "效率评分", Format = ""), ExporterHeader("效率评分")]
        public int? EfficiencyScore {get;set;}
        
          /// <summary>
        ///  准确性评分
        /// </summary>
          [ImporterHeader(Name = "准确性评分", Format = ""), ExporterHeader("准确性评分")]
        public int? AccuracyScore {get;set;}
        
          /// <summary>
        ///  安全性评分
        /// </summary>
          [ImporterHeader(Name = "安全性评分", Format = ""), ExporterHeader("安全性评分")]
        public int? SafetyScore {get;set;}
        
          /// <summary>
        ///  最终判定: 0-成功, 1-部分成功, 2-失败
        /// </summary>
          [ImporterHeader(Name = "最终判定: 0-成功, 1-部分成功, 2-失败", Format = ""), ExporterHeader("最终判定: 0-成功, 1-部分成功, 2-失败")]
        public int? SuccessStatus {get;set;}
        
          /// <summary>
        ///  详细点评/复盘内容
        /// </summary>
          [ImporterHeader(Name = "详细点评/复盘内容", Format = ""), ExporterHeader("详细点评/复盘内容")]
        public string ReviewComments {get;set;}
        
          /// <summary>
        ///  识别出的问题点JSON
        /// </summary>
          [ImporterHeader(Name = "识别出的问题点JSON", Format = ""), ExporterHeader("识别出的问题点JSON")]
        public string IdentifiedIssues {get;set;}
        
          /// <summary>
        ///  改进建议JSON
        /// </summary>
          [ImporterHeader(Name = "改进建议JSON", Format = ""), ExporterHeader("改进建议JSON")]
        public string ImprovementSuggestions {get;set;}
        
     

    }
}
