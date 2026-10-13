

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:23:26
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// CoT 模板表 - 定义可复用的思维链步骤模板
    /// </summary>
    public class CoT_TemplateUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  模板名称(唯一标识)
        /// </summary>
        [Description("模板名称(唯一标识)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string TemplateName {get;set;}
        
          /// <summary>
        ///  分类: CodeReview(代码审查)/Diagnosis(设备诊断)/Planning(任务规划)/Analysis(数据分析)/Other(其他)
        /// </summary>
        [Description("分类: CodeReview(代码审查)/Diagnosis(设备诊断)/Planning(任务规划)/Analysis(数据分析)/Other(其他)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string Category {get;set;}
        
          /// <summary>
        ///  模板描述
        /// </summary>
        [Description("模板描述")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  步骤定义 JSON 数组: [{stepOrder, stepName, description, stepPrompt, inputFromPrevStep, outputSchema, isMandatory, allowSkip, skipCondition}]
        /// </summary>
        [Description("步骤定义 JSON 数组: [{stepOrder, stepName, description, stepPrompt, inputFromPrevStep, outputSchema, isMandatory, allowSkip, skipCondition}]")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(-1)]
                  public string StepsDefinition {get;set;}
        
          /// <summary>
        ///  适用场景描述(帮助用户选择模板)
        /// </summary>
        [Description("适用场景描述(帮助用户选择模板)")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ApplicableScenarios {get;set;}
        
          /// <summary>
        ///  使用次数
        /// </summary>
        [Description("使用次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? UsageCount {get;set;}
        
          /// <summary>
        ///  平均执行时长(秒)
        /// </summary>
        [Description("平均执行时长(秒)")]
                  [Range(0, int.MaxValue)]
                  public int? AvgExecutionTime {get;set;}
        
          /// <summary>
        ///  成功率(%)
        /// </summary>
        [Description("成功率(%)")]
                  [Range(0, double.MaxValue)]
                  public decimal? SuccessRate {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  版本号(支持模板迭代)
        /// </summary>
        [Description("版本号(支持模板迭代)")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Version {get;set;}
        
     

    }
}
