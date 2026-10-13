
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:10
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 大模型配置表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class LLM_ModelView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  模型名称(如: GPT-4o, DeepSeek-V3)
        /// </summary>
          [ImporterHeader(Name = "模型名称(如: GPT-4o, DeepSeek-V3)", Format = ""), ExporterHeader("模型名称(如: GPT-4o, DeepSeek-V3)")]
        public string ModelName {get;set;}
        
          /// <summary>
        ///  模型编码(唯一标识)
        /// </summary>
          [ImporterHeader(Name = "模型编码(唯一标识)", Format = ""), ExporterHeader("模型编码(唯一标识)")]
        public string ModelCode {get;set;}
        
          /// <summary>
        ///  模型类型: Chat, Embedding, Rerank, STT, TTS, Image
        /// </summary>
          [ImporterHeader(Name = "模型类型: Chat, Embedding, Rerank, STT, TTS, Image", Format = ""), ExporterHeader("模型类型: Chat, Embedding, Rerank, STT, TTS, Image")]
        public string ModelType {get;set;}
        
          /// <summary>
        ///  提供商: OpenAI, Azure, Ollama, DeepSeek
        /// </summary>
          [ImporterHeader(Name = "提供商: OpenAI, Azure, Ollama, DeepSeek", Format = ""), ExporterHeader("提供商: OpenAI, Azure, Ollama, DeepSeek")]
        public string Provider {get;set;}
        
          /// <summary>
        ///  API基础地址
        /// </summary>
          [ImporterHeader(Name = "API基础地址", Format = ""), ExporterHeader("API基础地址")]
        public string BaseUrl {get;set;}
        
          /// <summary>
        ///  API密钥(加密存储)
        /// </summary>
          [ImporterHeader(Name = "API密钥(加密存储)", Format = ""), ExporterHeader("API密钥(加密存储)")]
        public string ApiKey {get;set;}
        
          /// <summary>
        ///  上下文窗口大小(Chat用)
        /// </summary>
          [ImporterHeader(Name = "上下文窗口大小(Chat用)", Format = ""), ExporterHeader("上下文窗口大小(Chat用)")]
        public int? ContextWindow {get;set;}
        
          /// <summary>
        ///  最大输出Token数(Chat用)
        /// </summary>
          [ImporterHeader(Name = "最大输出Token数(Chat用)", Format = ""), ExporterHeader("最大输出Token数(Chat用)")]
        public int? MaxTokens {get;set;}
        
          /// <summary>
        ///  向量维度(Embedding用, 如1536)
        /// </summary>
          [ImporterHeader(Name = "向量维度(Embedding用, 如1536)", Format = ""), ExporterHeader("向量维度(Embedding用, 如1536)")]
        public int? Dimension {get;set;}
        
          /// <summary>
        ///  输入Token单价
        /// </summary>
          [ImporterHeader(Name = "输入Token单价", Format = ""), ExporterHeader("输入Token单价")]
        public decimal? CostPerInputToken {get;set;}
        
          /// <summary>
        ///  输出Token单价
        /// </summary>
          [ImporterHeader(Name = "输出Token单价", Format = ""), ExporterHeader("输出Token单价")]
        public decimal? CostPerOutputToken {get;set;}
        
          /// <summary>
        ///  速率限制(Requests Per Minute)
        /// </summary>
          [ImporterHeader(Name = "速率限制(Requests Per Minute)", Format = ""), ExporterHeader("速率限制(Requests Per Minute)")]
        public int? RateLimitRPM {get;set;}
        
          /// <summary>
        ///  速率限制(Tokens Per Minute)
        /// </summary>
          [ImporterHeader(Name = "速率限制(Tokens Per Minute)", Format = ""), ExporterHeader("速率限制(Tokens Per Minute)")]
        public int? RateLimitTPM {get;set;}
        
          /// <summary>
        ///  是否本地模型
        /// </summary>
          [ImporterHeader(Name = "是否本地模型", Format = ""), ExporterHeader("是否本地模型")]
        public bool? IsLocal {get;set;}
        
          /// <summary>
        ///  是否支持流式输出
        /// </summary>
          [ImporterHeader(Name = "是否支持流式输出", Format = ""), ExporterHeader("是否支持流式输出")]
        public bool? SupportStreaming {get;set;}
        
          /// <summary>
        ///  备用模型ID(主模型故障时自动切换)
        /// </summary>
          [ImporterHeader(Name = "备用模型ID(主模型故障时自动切换)", Format = ""), ExporterHeader("备用模型ID(主模型故障时自动切换)")]
        public long? FallbackModelID {get;set;}
        
          /// <summary>
        ///  健康检查端点
        /// </summary>
          [ImporterHeader(Name = "健康检查端点", Format = ""), ExporterHeader("健康检查端点")]
        public string HealthCheckUrl {get;set;}
        
          /// <summary>
        ///  最后健康检查时间
        /// </summary>
          [ImporterHeader(Name = "最后健康检查时间", Format = ""), ExporterHeader("最后健康检查时间")]
        public DateTime? LastHealthCheckTime {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  描述
        /// </summary>
          [ImporterHeader(Name = "描述", Format = ""), ExporterHeader("描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  自定义请求参数(JSON格式)
        /// </summary>
          [ImporterHeader(Name = "自定义请求参数", Format = ""), ExporterHeader("自定义请求参数")]
        public string CustomRequestParams {get;set;}
        
     

    }
}
