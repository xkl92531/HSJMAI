

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:18
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 工作流/任务执行评估表
    /// </summary>
    public class Workflow_EvaluationUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  关联的工作流ID
        /// </summary>
        [Description("关联的工作流ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联的根任务ID(如果是针对单个大任务的评估)
        /// </summary>
        [Description("关联的根任务ID(如果是针对单个大任务的评估)")]
                  [Range(0, long.MaxValue)]
                  public long? RootTaskID {get;set;}
        
          /// <summary>
        ///  评估者类型: 0-人类专家, 1-AI反思者(Critic Agent), 2-系统规则
        /// </summary>
        [Description("评估者类型: 0-人类专家, 1-AI反思者(Critic Agent), 2-系统规则")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? EvaluatorType {get;set;}
        
          /// <summary>
        ///  评估者ID
        /// </summary>
        [Description("评估者ID")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string EvaluatorID {get;set;}
        
          /// <summary>
        ///  总体评分(0-100)
        /// </summary>
        [Description("总体评分(0-100)")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? OverallScore {get;set;}
        
          /// <summary>
        ///  效率评分
        /// </summary>
        [Description("效率评分")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? EfficiencyScore {get;set;}
        
          /// <summary>
        ///  准确性评分
        /// </summary>
        [Description("准确性评分")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? AccuracyScore {get;set;}
        
          /// <summary>
        ///  安全性评分
        /// </summary>
        [Description("安全性评分")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? SafetyScore {get;set;}
        
          /// <summary>
        ///  最终判定: 0-成功, 1-部分成功, 2-失败
        /// </summary>
        [Description("最终判定: 0-成功, 1-部分成功, 2-失败")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? SuccessStatus {get;set;}
        
          /// <summary>
        ///  详细点评/复盘内容
        /// </summary>
        [Description("详细点评/复盘内容")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ReviewComments {get;set;}
        
          /// <summary>
        ///  识别出的问题点JSON
        /// </summary>
        [Description("识别出的问题点JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string IdentifiedIssues {get;set;}
        
          /// <summary>
        ///  改进建议JSON
        /// </summary>
        [Description("改进建议JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ImprovementSuggestions {get;set;}
        
     

    }
}
