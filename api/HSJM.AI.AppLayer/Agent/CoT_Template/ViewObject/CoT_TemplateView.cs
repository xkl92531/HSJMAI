
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:23:26
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// CoT 模板表 - 定义可复用的思维链步骤模板
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class CoT_TemplateView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  模板名称(唯一标识)
        /// </summary>
          [ImporterHeader(Name = "模板名称(唯一标识)", Format = ""), ExporterHeader("模板名称(唯一标识)")]
        public string TemplateName {get;set;}
        
          /// <summary>
        ///  分类: CodeReview(代码审查)/Diagnosis(设备诊断)/Planning(任务规划)/Analysis(数据分析)/Other(其他)
        /// </summary>
          [ImporterHeader(Name = "分类: CodeReview(代码审查)/Diagnosis(设备诊断)/Planning(任务规划)/Analysis(数据分析)/Other(其他)", Format = ""), ExporterHeader("分类: CodeReview(代码审查)/Diagnosis(设备诊断)/Planning(任务规划)/Analysis(数据分析)/Other(其他)")]
        public string Category {get;set;}
        
          /// <summary>
        ///  模板描述
        /// </summary>
          [ImporterHeader(Name = "模板描述", Format = ""), ExporterHeader("模板描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  步骤定义 JSON 数组: [{stepOrder, stepName, description, stepPrompt, inputFromPrevStep, outputSchema, isMandatory, allowSkip, skipCondition}]
        /// </summary>
          [ImporterHeader(Name = "步骤定义 JSON 数组: [{stepOrder, stepName, description, stepPrompt, inputFromPrevStep, outputSchema, isMandatory, allowSkip, skipCondition}]", Format = ""), ExporterHeader("步骤定义 JSON 数组: [{stepOrder, stepName, description, stepPrompt, inputFromPrevStep, outputSchema, isMandatory, allowSkip, skipCondition}]")]
        public string StepsDefinition {get;set;}
        
          /// <summary>
        ///  适用场景描述(帮助用户选择模板)
        /// </summary>
          [ImporterHeader(Name = "适用场景描述(帮助用户选择模板)", Format = ""), ExporterHeader("适用场景描述(帮助用户选择模板)")]
        public string ApplicableScenarios {get;set;}
        
          /// <summary>
        ///  使用次数
        /// </summary>
          [ImporterHeader(Name = "使用次数", Format = ""), ExporterHeader("使用次数")]
        public int? UsageCount {get;set;}
        
          /// <summary>
        ///  平均执行时长(秒)
        /// </summary>
          [ImporterHeader(Name = "平均执行时长(秒)", Format = ""), ExporterHeader("平均执行时长(秒)")]
        public int? AvgExecutionTime {get;set;}
        
          /// <summary>
        ///  成功率(%)
        /// </summary>
          [ImporterHeader(Name = "成功率(%)", Format = ""), ExporterHeader("成功率(%)")]
        public decimal? SuccessRate {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  版本号(支持模板迭代)
        /// </summary>
          [ImporterHeader(Name = "版本号(支持模板迭代)", Format = ""), ExporterHeader("版本号(支持模板迭代)")]
        public int? Version {get;set;}
        
     

    }
}
