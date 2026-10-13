
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:10
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 大模型配置表
    /// </summary>
    [SugarTable("AI_LLM_Model",tableDescription:"大模型配置表")]
    public class LLM_ModelEntity: BaseField
    {

       public  LLM_ModelEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  模型名称(如: GPT-4o, DeepSeek-V3)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模型名称(如: GPT-4o, DeepSeek-V3)")]
        public string ModelName {get;set;}
        
          /// <summary>
        ///  模型编码(唯一标识)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模型编码(唯一标识)")]
        public string ModelCode {get;set;}
        
          /// <summary>
        ///  模型类型: Chat, Embedding, Rerank, STT, TTS, Image
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模型类型: Chat, Embedding, Rerank, STT, TTS, Image")]
        public string ModelType {get;set;}
        
          /// <summary>
        ///  提供商: OpenAI, Azure, Ollama, DeepSeek
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="提供商: OpenAI, Azure, Ollama, DeepSeek")]
        public string Provider {get;set;}
        
          /// <summary>
        ///  API基础地址
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="API基础地址")]
        public string BaseUrl {get;set;}
        
          /// <summary>
        ///  API密钥(加密存储)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="API密钥(加密存储)")]
        public string ApiKey {get;set;}
        
          /// <summary>
        ///  上下文窗口大小(Chat用)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="上下文窗口大小(Chat用)")]
        public int? ContextWindow {get;set;}
        
          /// <summary>
        ///  最大输出Token数(Chat用)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最大输出Token数(Chat用)")]
        public int? MaxTokens {get;set;}
        
          /// <summary>
        ///  向量维度(Embedding用, 如1536)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="向量维度(Embedding用, 如1536)")]
        public int? Dimension {get;set;}
        
          /// <summary>
        ///  输入Token单价
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输入Token单价")]
        public decimal? CostPerInputToken {get;set;}
        
          /// <summary>
        ///  输出Token单价
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出Token单价")]
        public decimal? CostPerOutputToken {get;set;}
        
          /// <summary>
        ///  速率限制(Requests Per Minute)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="速率限制(Requests Per Minute)")]
        public int? RateLimitRPM {get;set;}
        
          /// <summary>
        ///  速率限制(Tokens Per Minute)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="速率限制(Tokens Per Minute)")]
        public int? RateLimitTPM {get;set;}
        
          /// <summary>
        ///  是否本地模型
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否本地模型")]
        public bool? IsLocal {get;set;}
        
          /// <summary>
        ///  是否支持流式输出
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否支持流式输出")]
        public bool? SupportStreaming {get;set;}
        
          /// <summary>
        ///  备用模型ID(主模型故障时自动切换)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="备用模型ID(主模型故障时自动切换)")]
        public long? FallbackModelID {get;set;}
        
          /// <summary>
        ///  健康检查端点
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="健康检查端点")]
        public string HealthCheckUrl {get;set;}
        
          /// <summary>
        ///  最后健康检查时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最后健康检查时间")]
        public DateTime? LastHealthCheckTime {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  描述
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  自定义请求参数(JSON格式, 如: {"temperature":0.7, "stream":true, "chat_template_kwargs":{"enable_thinking":false}})
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="自定义请求参数(JSON格式)", ColumnDataType = "text")]
        public string CustomRequestParams {get;set;}
        
     

    }

   
}
