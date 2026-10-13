
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:10
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 提示词运行日志
    /// </summary>
    [SugarTable("AI_Prompt_Runtime_Log",tableDescription:"提示词运行日志")]
    public class Prompt_Runtime_LogEntity: BaseField
    {

       public  Prompt_Runtime_LogEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  调用场景
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="调用场景")]
        public string Scenario {get;set;}
        
          /// <summary>
        ///  命中的模板ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="命中的模板ID")]
        public long? PromptTemplateID {get;set;}
        
          /// <summary>
        ///  提示词键
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="提示词键")]
        public string PromptKey {get;set;}
        
          /// <summary>
        ///  提示词版本号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="提示词版本号")]
        public string PromptVersion {get;set;}
        
          /// <summary>
        ///  模型名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模型名称")]
        public string ModelName {get;set;}
        
          /// <summary>
        ///  模型提供方
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模型提供方")]
        public string ModelProvider {get;set;}
        
          /// <summary>
        ///  调用链 TraceID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="调用链 TraceID")]
        public string TraceID {get;set;}
        
          /// <summary>
        ///  请求载荷
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="请求载荷")]
        public string RequestPayload {get;set;}
        
          /// <summary>
        ///  响应载荷
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="响应载荷")]
        public string ResponsePayload {get;set;}
        
          /// <summary>
        ///  输入 Token 数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输入 Token 数")]
        public int? InputTokens {get;set;}
        
          /// <summary>
        ///  输出 Token 数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出 Token 数")]
        public int? OutputTokens {get;set;}
        
          /// <summary>
        ///  延迟（毫秒）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="延迟（毫秒）")]
        public int? LatencyMs {get;set;}
        
          /// <summary>
        ///  评分/质量指标
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="评分/质量指标")]
        public decimal? Score {get;set;}
        
          /// <summary>
        ///  是否调用成功
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否调用成功")]
        public bool? IsSuccess {get;set;}
        
          /// <summary>
        ///  错误码
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误码")]
        public string ErrorCode {get;set;}
        
          /// <summary>
        ///  错误摘要
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误摘要")]
        public string ErrorMessage {get;set;}
        
     

    }

   
}
