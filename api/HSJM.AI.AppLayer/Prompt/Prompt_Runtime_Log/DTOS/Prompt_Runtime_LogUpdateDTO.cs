

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:10
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Prompt.DTOS
{
    /// <summary>
    /// 提示词运行日志
    /// </summary>
    public class Prompt_Runtime_LogUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  调用场景
        /// </summary>
        [Description("调用场景")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Scenario {get;set;}
        
          /// <summary>
        ///  命中的模板ID
        /// </summary>
        [Description("命中的模板ID")]
                  [Range(0, long.MaxValue)]
                  public long? PromptTemplateID {get;set;}
        
          /// <summary>
        ///  提示词键
        /// </summary>
        [Description("提示词键")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string PromptKey {get;set;}
        
          /// <summary>
        ///  提示词版本号
        /// </summary>
        [Description("提示词版本号")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string PromptVersion {get;set;}
        
          /// <summary>
        ///  模型名称
        /// </summary>
        [Description("模型名称")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string ModelName {get;set;}
        
          /// <summary>
        ///  模型提供方
        /// </summary>
        [Description("模型提供方")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ModelProvider {get;set;}
        
          /// <summary>
        ///  调用链 TraceID
        /// </summary>
        [Description("调用链 TraceID")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string TraceID {get;set;}
        
          /// <summary>
        ///  请求载荷
        /// </summary>
        [Description("请求载荷")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string RequestPayload {get;set;}
        
          /// <summary>
        ///  响应载荷
        /// </summary>
        [Description("响应载荷")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ResponsePayload {get;set;}
        
          /// <summary>
        ///  输入 Token 数
        /// </summary>
        [Description("输入 Token 数")]
                  [Range(0, int.MaxValue)]
                  public int? InputTokens {get;set;}
        
          /// <summary>
        ///  输出 Token 数
        /// </summary>
        [Description("输出 Token 数")]
                  [Range(0, int.MaxValue)]
                  public int? OutputTokens {get;set;}
        
          /// <summary>
        ///  延迟（毫秒）
        /// </summary>
        [Description("延迟（毫秒）")]
                  [Range(0, int.MaxValue)]
                  public int? LatencyMs {get;set;}
        
          /// <summary>
        ///  评分/质量指标
        /// </summary>
        [Description("评分/质量指标")]
                  [Range(0, double.MaxValue)]
                  public decimal? Score {get;set;}
        
          /// <summary>
        ///  是否调用成功
        /// </summary>
        [Description("是否调用成功")]
                  [Required]
                  public bool? IsSuccess {get;set;}
        
          /// <summary>
        ///  错误码
        /// </summary>
        [Description("错误码")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ErrorCode {get;set;}
        
          /// <summary>
        ///  错误摘要
        /// </summary>
        [Description("错误摘要")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ErrorMessage {get;set;}
        
     

    }
}
