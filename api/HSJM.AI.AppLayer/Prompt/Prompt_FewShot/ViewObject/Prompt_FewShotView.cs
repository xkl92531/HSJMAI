
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:56
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Prompt.ViewObject
{
    /// <summary>
    /// Few-shot 样例
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Prompt_FewShotView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  应用场景
        /// </summary>
          [ImporterHeader(Name = "应用场景", Format = ""), ExporterHeader("应用场景")]
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
        ///  语言
        /// </summary>
          [ImporterHeader(Name = "语言", Format = ""), ExporterHeader("语言")]
        public string Language {get;set;}
        
          /// <summary>
        ///  块类型
        /// </summary>
          [ImporterHeader(Name = "块类型", Format = ""), ExporterHeader("块类型")]
        public string BlockType {get;set;}
        
          /// <summary>
        ///  操作/任务类型
        /// </summary>
          [ImporterHeader(Name = "操作/任务类型", Format = ""), ExporterHeader("操作/任务类型")]
        public string Action {get;set;}
        
          /// <summary>
        ///  样例类型（正例/反例等）
        /// </summary>
          [ImporterHeader(Name = "样例类型（正例/反例等）", Format = ""), ExporterHeader("样例类型（正例/反例等）")]
        public string ExampleType {get;set;}
        
          /// <summary>
        ///  输入摘要
        /// </summary>
          [ImporterHeader(Name = "输入摘要", Format = ""), ExporterHeader("输入摘要")]
        public string InputSummary {get;set;}
        
          /// <summary>
        ///  输出摘要
        /// </summary>
          [ImporterHeader(Name = "输出摘要", Format = ""), ExporterHeader("输出摘要")]
        public string OutputSummary {get;set;}
        
          /// <summary>
        ///  样例 JSON 内容
        /// </summary>
          [ImporterHeader(Name = "样例 JSON 内容", Format = ""), ExporterHeader("样例 JSON 内容")]
        public string ExampleJson {get;set;}
        
          /// <summary>
        ///  Embedding 模型
        /// </summary>
          [ImporterHeader(Name = "Embedding 模型", Format = ""), ExporterHeader("Embedding 模型")]
        public string EmbeddingModel {get;set;}
        
          /// <summary>
        ///  向量库ID/标识
        /// </summary>
          [ImporterHeader(Name = "向量库ID/标识", Format = ""), ExporterHeader("向量库ID/标识")]
        public string VectorID {get;set;}
        
          /// <summary>
        ///  相似度哈希/指纹
        /// </summary>
          [ImporterHeader(Name = "相似度哈希/指纹", Format = ""), ExporterHeader("相似度哈希/指纹")]
        public string SimilarityHash {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
          [ImporterHeader(Name = "扩展元数据 JSON", Format = ""), ExporterHeader("扩展元数据 JSON")]
        public string MetadataJson {get;set;}
        
     

    }
}
