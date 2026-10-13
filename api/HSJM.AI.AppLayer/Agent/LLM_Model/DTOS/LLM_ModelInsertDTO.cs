

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:10
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 大模型配置表
    /// </summary>
    public class LLM_ModelInsertDTO
    {
        /// <summary>
        ///  模型名称(如: GPT-4o, DeepSeek-V3)
        /// </summary>
        [Description("模型名称(如: GPT-4o, DeepSeek-V3)")]
        [Required]
        [MinLength(0)]
        [MaxLength(100)]
        public string ModelName { get; set; }

        /// <summary>
        ///  模型编码(唯一标识)
        /// </summary>
        [Description("模型编码(唯一标识)")]
        [Required]
        [MinLength(0)]
        [MaxLength(50)]
        public string ModelCode { get; set; }

        /// <summary>
        ///  模型类型: Chat, Embedding, Rerank, STT, TTS, Image
        /// </summary>
        [Description("模型类型: Chat, Embedding, Rerank, STT, TTS, Image")]
        [Required]
        [MinLength(0)]
        [MaxLength(20)]
        public string ModelType { get; set; }

        /// <summary>
        ///  提供商: OpenAI, Azure, Ollama, DeepSeek
        /// </summary>
        [Description("提供商: OpenAI, Azure, Ollama, DeepSeek")]
        [Required]
        [MinLength(0)]
        [MaxLength(50)]
        public string Provider { get; set; }

        /// <summary>
        ///  API基础地址
        /// </summary>
        [Description("API基础地址")]
        [MinLength(0)]
        [MaxLength(200)]
        public string BaseUrl { get; set; }

        /// <summary>
        ///  API密钥(加密存储)
        /// </summary>
        [Description("API密钥(加密存储)")]
        [MinLength(0)]
        [MaxLength(500)]
        public string ApiKey { get; set; }

        /// <summary>
        ///  上下文窗口大小(Chat用)
        /// </summary>
        [Description("上下文窗口大小(Chat用)")]
        [Required]
        [Range(0, int.MaxValue)]
        public int? ContextWindow { get; set; }

        /// <summary>
        ///  最大输出Token数(Chat用)
        /// </summary>
        [Description("最大输出Token数(Chat用)")]
        [Required]
        [Range(0, int.MaxValue)]
        public int? MaxTokens { get; set; }

        /// <summary>
        ///  向量维度(Embedding用, 如1536)
        /// </summary>
        [Description("向量维度(Embedding用, 如1536)")]
        [Required]
        [Range(0, int.MaxValue)]
        public int? Dimension { get; set; }

        /// <summary>
        ///  输入Token单价
        /// </summary>
        [Description("输入Token单价")]
        [Required]
        [Range(0, double.MaxValue)]
        public decimal? CostPerInputToken { get; set; }

        /// <summary>
        ///  输出Token单价
        /// </summary>
        [Description("输出Token单价")]
        [Required]
        [Range(0, double.MaxValue)]
        public decimal? CostPerOutputToken { get; set; }

        /// <summary>
        ///  速率限制(Requests Per Minute)
        /// </summary>
        [Description("速率限制(Requests Per Minute)")]
        [Required]
        [Range(0, int.MaxValue)]
        public int? RateLimitRPM { get; set; }

        /// <summary>
        ///  速率限制(Tokens Per Minute)
        /// </summary>
        [Description("速率限制(Tokens Per Minute)")]
        [Required]
        [Range(0, int.MaxValue)]
        public int? RateLimitTPM { get; set; }

        /// <summary>
        ///  是否本地模型
        /// </summary>
        [Description("是否本地模型")]
        [Required]
        public bool? IsLocal { get; set; }

        /// <summary>
        ///  是否支持流式输出
        /// </summary>
        [Description("是否支持流式输出")]
        [Required]
        public bool? SupportStreaming { get; set; }

        /// <summary>
        ///  备用模型ID(主模型故障时自动切换)
        /// </summary>
        [Description("备用模型ID(主模型故障时自动切换)")]
        [Range(0, long.MaxValue)]
        public long? FallbackModelID { get; set; }

        /// <summary>
        ///  健康检查端点
        /// </summary>
        [Description("健康检查端点")]
        [MinLength(0)]
        [MaxLength(200)]
        public string HealthCheckUrl { get; set; }

        /// <summary>
        ///  最后健康检查时间
        /// </summary>
        [Description("最后健康检查时间")]
        public DateTime? LastHealthCheckTime { get; set; }

        /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
        [Required]
        public bool? IsEnabled { get; set; }

        /// <summary>
        ///  描述
        /// </summary>
        [Description("描述")]
        [MinLength(0)]
        [MaxLength(500)]
        public string Description { get; set; }

        /// <summary>
        ///  自定义请求参数(JSON格式)
        /// </summary>
        [Description("自定义请求参数(JSON格式)")]
        public string CustomRequestParams { get; set; }



    }
}
