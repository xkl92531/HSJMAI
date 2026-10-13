
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:50
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Prompt.ViewObject
{
    /// <summary>
    /// 记录 Prompt 拼装过程，方便复现最终 Prompt 及所选模板/片段。
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Prompt_Assembly_LogView:ViewBaseField
    {
            /// <summary>
        ///  记录主键。
        /// </summary>
          [ImporterHeader(Name = "记录主键。", Format = ""), ExporterHeader("记录主键。")]
        public long ID {get;set;}
        
          /// <summary>
        ///  场景标识，例如 Parsing、Diagnosis、Planning。
        /// </summary>
          [ImporterHeader(Name = "场景标识，例如 Parsing、Diagnosis、Planning。", Format = ""), ExporterHeader("场景标识，例如 Parsing、Diagnosis、Planning。")]
        public string Scenario {get;set;}
        
          /// <summary>
        ///  品牌或客户筛选条件。
        /// </summary>
          [ImporterHeader(Name = "品牌或客户筛选条件。", Format = ""), ExporterHeader("品牌或客户筛选条件。")]
        public string Brand {get;set;}
        
          /// <summary>
        ///  Prompt 输出语言。
        /// </summary>
          [ImporterHeader(Name = "Prompt 输出语言。", Format = ""), ExporterHeader("Prompt 输出语言。")]
        public string Language {get;set;}
        
          /// <summary>
        ///  任务动作，例如 ParseBlock/Diagnose。
        /// </summary>
          [ImporterHeader(Name = "任务动作，例如 ParseBlock/Diagnose。", Format = ""), ExporterHeader("任务动作，例如 ParseBlock/Diagnose。")]
        public string Action {get;set;}
        
          /// <summary>
        ///  使用的模板 ID。
        /// </summary>
          [ImporterHeader(Name = "使用的模板 ID。", Format = ""), ExporterHeader("使用的模板 ID。")]
        public long? TemplateID {get;set;}
        
          /// <summary>
        ///  拼装的片段 ID 列表。
        /// </summary>
          [ImporterHeader(Name = "拼装的片段 ID 列表。", Format = ""), ExporterHeader("拼装的片段 ID 列表。")]
        public string FragmentIDs {get;set;}
        
          /// <summary>
        ///  Few-shot 示例 ID 列表。
        /// </summary>
          [ImporterHeader(Name = "Few-shot 示例 ID 列表。", Format = ""), ExporterHeader("Few-shot 示例 ID 列表。")]
        public string FewShotIDs {get;set;}
        
          /// <summary>
        ///  最终发送给模型的 Prompt 文本。
        /// </summary>
          [ImporterHeader(Name = "最终发送给模型的 Prompt 文本。", Format = ""), ExporterHeader("最终发送给模型的 Prompt 文本。")]
        public string FinalPrompt {get;set;}
        
          /// <summary>
        ///  最终 Prompt 的 token 数。
        /// </summary>
          [ImporterHeader(Name = "最终 Prompt 的 token 数。", Format = ""), ExporterHeader("最终 Prompt 的 token 数。")]
        public int? PromptTokenCount {get;set;}
        
          /// <summary>
        ///  拼装耗时（毫秒）。
        /// </summary>
          [ImporterHeader(Name = "拼装耗时（毫秒）。", Format = ""), ExporterHeader("拼装耗时（毫秒）。")]
        public int? AssemblyTimeMs {get;set;}
        
          /// <summary>
        ///  回退层级：0 精确、1 一级、2 二级。
        /// </summary>
          [ImporterHeader(Name = "回退层级：0 精确、1 一级、2 二级。", Format = ""), ExporterHeader("回退层级：0 精确、1 一级、2 二级。")]
        public int? FallbackLevel {get;set;}
        
          /// <summary>
        ///  关联的模型运行日志 ID。
        /// </summary>
          [ImporterHeader(Name = "关联的模型运行日志 ID。", Format = ""), ExporterHeader("关联的模型运行日志 ID。")]
        public long? RuntimeLogID {get;set;}
        
          /// <summary>
        ///  备注信息。
        /// </summary>
          [ImporterHeader(Name = "备注信息。", Format = ""), ExporterHeader("备注信息。")]
        public string Remark {get;set;}
        
     

    }
}
