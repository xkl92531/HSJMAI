
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:10
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Prompt.ViewObject
{
    /// <summary>
    /// 提示词运行日志
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Prompt_Runtime_LogView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  调用场景
        /// </summary>
          [ImporterHeader(Name = "调用场景", Format = ""), ExporterHeader("调用场景")]
        public string Scenario {get;set;}
        
          /// <summary>
        ///  命中的模板ID
        /// </summary>
          [ImporterHeader(Name = "命中的模板ID", Format = ""), ExporterHeader("命中的模板ID")]
        public long? PromptTemplateID {get;set;}
        
          /// <summary>
        ///  提示词键
        /// </summary>
          [ImporterHeader(Name = "提示词键", Format = ""), ExporterHeader("提示词键")]
        public string PromptKey {get;set;}
        
          /// <summary>
        ///  提示词版本号
        /// </summary>
          [ImporterHeader(Name = "提示词版本号", Format = ""), ExporterHeader("提示词版本号")]
        public string PromptVersion {get;set;}
        
          /// <summary>
        ///  模型名称
        /// </summary>
          [ImporterHeader(Name = "模型名称", Format = ""), ExporterHeader("模型名称")]
        public string ModelName {get;set;}
        
          /// <summary>
        ///  模型提供方
        /// </summary>
          [ImporterHeader(Name = "模型提供方", Format = ""), ExporterHeader("模型提供方")]
        public string ModelProvider {get;set;}
        
          /// <summary>
        ///  调用链 TraceID
        /// </summary>
          [ImporterHeader(Name = "调用链 TraceID", Format = ""), ExporterHeader("调用链 TraceID")]
        public string TraceID {get;set;}
        
          /// <summary>
        ///  请求载荷
        /// </summary>
          [ImporterHeader(Name = "请求载荷", Format = ""), ExporterHeader("请求载荷")]
        public string RequestPayload {get;set;}
        
          /// <summary>
        ///  响应载荷
        /// </summary>
          [ImporterHeader(Name = "响应载荷", Format = ""), ExporterHeader("响应载荷")]
        public string ResponsePayload {get;set;}
        
          /// <summary>
        ///  输入 Token 数
        /// </summary>
          [ImporterHeader(Name = "输入 Token 数", Format = ""), ExporterHeader("输入 Token 数")]
        public int? InputTokens {get;set;}
        
          /// <summary>
        ///  输出 Token 数
        /// </summary>
          [ImporterHeader(Name = "输出 Token 数", Format = ""), ExporterHeader("输出 Token 数")]
        public int? OutputTokens {get;set;}
        
          /// <summary>
        ///  延迟（毫秒）
        /// </summary>
          [ImporterHeader(Name = "延迟（毫秒）", Format = ""), ExporterHeader("延迟（毫秒）")]
        public int? LatencyMs {get;set;}
        
          /// <summary>
        ///  评分/质量指标
        /// </summary>
          [ImporterHeader(Name = "评分/质量指标", Format = ""), ExporterHeader("评分/质量指标")]
        public decimal? Score {get;set;}
        
          /// <summary>
        ///  是否调用成功
        /// </summary>
          [ImporterHeader(Name = "是否调用成功", Format = ""), ExporterHeader("是否调用成功")]
        public bool? IsSuccess {get;set;}
        
          /// <summary>
        ///  错误码
        /// </summary>
          [ImporterHeader(Name = "错误码", Format = ""), ExporterHeader("错误码")]
        public string ErrorCode {get;set;}
        
          /// <summary>
        ///  错误摘要
        /// </summary>
          [ImporterHeader(Name = "错误摘要", Format = ""), ExporterHeader("错误摘要")]
        public string ErrorMessage {get;set;}
        
     

    }
}
