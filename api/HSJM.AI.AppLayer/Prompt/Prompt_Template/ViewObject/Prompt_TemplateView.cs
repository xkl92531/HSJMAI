
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:18
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Prompt.ViewObject
{
    /// <summary>
    /// 提示词模板
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Prompt_TemplateView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  应用场景（Parsing/Diagnosis 等）
        /// </summary>
          [ImporterHeader(Name = "应用场景（Parsing/Diagnosis 等）", Format = ""), ExporterHeader("应用场景（Parsing/Diagnosis 等）")]
        public string Scenario {get;set;}
        
          /// <summary>
        ///  适用品牌
        /// </summary>
          [ImporterHeader(Name = "适用品牌", Format = ""), ExporterHeader("适用品牌")]
        public string Brand {get;set;}
        
          /// <summary>
        ///  适用型号/系列
        /// </summary>
          [ImporterHeader(Name = "适用型号/系列", Format = ""), ExporterHeader("适用型号/系列")]
        public string Model {get;set;}
        
          /// <summary>
        ///  语言（指令/自然语言）
        /// </summary>
          [ImporterHeader(Name = "语言（指令/自然语言）", Format = ""), ExporterHeader("语言（指令/自然语言）")]
        public string Language {get;set;}
        
          /// <summary>
        ///  适用块类型 OB/FB/FC/DB 等
        /// </summary>
          [ImporterHeader(Name = "适用块类型 OB/FB/FC/DB 等", Format = ""), ExporterHeader("适用块类型 OB/FB/FC/DB 等")]
        public string BlockType {get;set;}
        
          /// <summary>
        ///  操作/任务类型
        /// </summary>
          [ImporterHeader(Name = "操作/任务类型", Format = ""), ExporterHeader("操作/任务类型")]
        public string Action {get;set;}
        
          /// <summary>
        ///  模板唯一键
        /// </summary>
          [ImporterHeader(Name = "模板唯一键", Format = ""), ExporterHeader("模板唯一键")]
        public string PromptKey {get;set;}
        
          /// <summary>
        ///  模板版本号
        /// </summary>
          [ImporterHeader(Name = "模板版本号", Format = ""), ExporterHeader("模板版本号")]
        public string Version {get;set;}
        
          /// <summary>
        ///  父模板ID
        /// </summary>
          [ImporterHeader(Name = "父模板ID", Format = ""), ExporterHeader("父模板ID")]
        public long? ParentTemplateID {get;set;}
        
          /// <summary>
        ///  回退层级（数字越大越靠后）
        /// </summary>
          [ImporterHeader(Name = "回退层级（数字越大越靠后）", Format = ""), ExporterHeader("回退层级（数字越大越靠后）")]
        public int? FallbackLevel {get;set;}
        
          /// <summary>
        ///  优先级（大值优先）
        /// </summary>
          [ImporterHeader(Name = "优先级（大值优先）", Format = ""), ExporterHeader("优先级（大值优先）")]
        public int? Priority {get;set;}
        
          /// <summary>
        ///  状态 0-草稿 1-启用 2-停用
        /// </summary>
          [ImporterHeader(Name = "状态 0-草稿 1-启用 2-停用", Format = ""), ExporterHeader("状态 0-草稿 1-启用 2-停用")]
        public int? Status {get;set;}
        
          /// <summary>
        ///  提示词正文内容
        /// </summary>
          [ImporterHeader(Name = "提示词正文内容", Format = ""), ExporterHeader("提示词正文内容")]
        public string Content {get;set;}
        
          /// <summary>
        ///  约束配置 JSON
        /// </summary>
          [ImporterHeader(Name = "约束配置 JSON", Format = ""), ExporterHeader("约束配置 JSON")]
        public string ConstraintsJson {get;set;}
        
          /// <summary>
        ///  输出结构定义
        /// </summary>
          [ImporterHeader(Name = "输出结构定义", Format = ""), ExporterHeader("输出结构定义")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
          [ImporterHeader(Name = "扩展元数据 JSON", Format = ""), ExporterHeader("扩展元数据 JSON")]
        public string MetadataJson {get;set;}
        
     

    }
}
