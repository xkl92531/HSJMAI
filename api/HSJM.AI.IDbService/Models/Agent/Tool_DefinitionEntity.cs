
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:09
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 工具/插件定义表
    /// </summary>
    [SugarTable("AI_Tool_Definition",tableDescription:"工具/插件定义表")]
    public class Tool_DefinitionEntity: BaseField
    {

       public  Tool_DefinitionEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  工具名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具名称")]
        public string ToolName {get;set;}
        
          /// <summary>
        ///  工具编码(Function Name)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具编码(Function Name)")]
        public string ToolCode {get;set;}
        
          /// <summary>
        ///  工具描述(供AI理解)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具描述(供AI理解)")]
        public string Description {get;set;}
        
          /// <summary>
        ///  工具类型: Local/Http/Mcp
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具类型: Local/Http/Mcp")]
        public string ToolType {get;set;}
        
          /// <summary>
        ///  API地址
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="API地址")]
        public string ApiUrl {get;set;}
        
          /// <summary>
        ///  请求方法(GET/POST)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="请求方法(GET/POST)")]
        public string ApiMethod {get;set;}
        
          /// <summary>
        ///  请求头JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="请求头JSON")]
        public string ApiHeaders {get;set;}
        
          /// <summary>
        ///  请求体模板JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="请求体模板JSON")]
        public string ApiBodyTemplate {get;set;}
        
          /// <summary>
        ///  关联MCP服务ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联MCP服务ID")]
        public long? McpServerID {get;set;}
        
          /// <summary>
        ///  MCP工具名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="MCP工具名称")]
        public string McpToolName {get;set;}
        
          /// <summary>
        ///  程序集
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="程序集")]
        public string LocalAssembly {get;set;}
        
          /// <summary>
        ///  类名
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="类名")]
        public string LocalClass {get;set;}
        
          /// <summary>
        ///  方法名
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="方法名")]
        public string LocalMethod {get;set;}
        
          /// <summary>
        ///  参数定义JSON Schema
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="参数定义JSON Schema")]
        public string ParametersSchema {get;set;}
        
          /// <summary>
        ///  工具返回值JSON Schema(用于结果验证)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具返回值JSON Schema(用于结果验证)")]
        public string ResponseSchema {get;set;}
        
          /// <summary>
        ///  错误处理策略: Abort(终止), Retry(重试), Fallback(降级)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="错误处理策略: Abort(终止), Retry(重试), Fallback(降级)")]
        public string ErrorHandlingStrategy {get;set;}
        
          /// <summary>
        ///  备用工具ID(当主工具失败时)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="备用工具ID(当主工具失败时)")]
        public long? FallbackToolID {get;set;}
        
          /// <summary>
        ///  工具执行超时时间(秒)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具执行超时时间(秒)")]
        public int? TimeoutSeconds {get;set;}
        
          /// <summary>
        ///  鉴权类型: 0-无, 1-APIKey, 2-OAuth
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="鉴权类型: 0-无, 1-APIKey, 2-OAuth")]
        public int? AuthType {get;set;}
        
          /// <summary>
        ///  鉴权配置JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="鉴权配置JSON")]
        public string AuthConfig {get;set;}
        
          /// <summary>
        ///  是否需要人工确认(高危操作)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否需要人工确认(高危操作)")]
        public bool? RequiresConfirmation {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsEnabled {get;set;}
        
     

    }

   
}
