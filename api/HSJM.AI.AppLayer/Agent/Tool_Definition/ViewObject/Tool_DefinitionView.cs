
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:09
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 工具/插件定义表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Tool_DefinitionView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  工具名称
        /// </summary>
          [ImporterHeader(Name = "工具名称", Format = ""), ExporterHeader("工具名称")]
        public string ToolName {get;set;}
        
          /// <summary>
        ///  工具编码(Function Name)
        /// </summary>
          [ImporterHeader(Name = "工具编码(Function Name)", Format = ""), ExporterHeader("工具编码(Function Name)")]
        public string ToolCode {get;set;}
        
          /// <summary>
        ///  工具描述(供AI理解)
        /// </summary>
          [ImporterHeader(Name = "工具描述(供AI理解)", Format = ""), ExporterHeader("工具描述(供AI理解)")]
        public string Description {get;set;}
        
          /// <summary>
        ///  工具类型: Local/Http/Mcp
        /// </summary>
          [ImporterHeader(Name = "工具类型: Local/Http/Mcp", Format = ""), ExporterHeader("工具类型: Local/Http/Mcp")]
        public string ToolType {get;set;}
        
          /// <summary>
        ///  API地址
        /// </summary>
          [ImporterHeader(Name = "API地址", Format = ""), ExporterHeader("API地址")]
        public string ApiUrl {get;set;}
        
          /// <summary>
        ///  请求方法(GET/POST)
        /// </summary>
          [ImporterHeader(Name = "请求方法(GET/POST)", Format = ""), ExporterHeader("请求方法(GET/POST)")]
        public string ApiMethod {get;set;}
        
          /// <summary>
        ///  请求头JSON
        /// </summary>
          [ImporterHeader(Name = "请求头JSON", Format = ""), ExporterHeader("请求头JSON")]
        public string ApiHeaders {get;set;}
        
          /// <summary>
        ///  请求体模板JSON
        /// </summary>
          [ImporterHeader(Name = "请求体模板JSON", Format = ""), ExporterHeader("请求体模板JSON")]
        public string ApiBodyTemplate {get;set;}
        
          /// <summary>
        ///  关联MCP服务ID
        /// </summary>
          [ImporterHeader(Name = "关联MCP服务ID", Format = ""), ExporterHeader("关联MCP服务ID")]
        public long? McpServerID {get;set;}
        
          /// <summary>
        ///  MCP工具名称
        /// </summary>
          [ImporterHeader(Name = "MCP工具名称", Format = ""), ExporterHeader("MCP工具名称")]
        public string McpToolName {get;set;}
        
          /// <summary>
        ///  程序集
        /// </summary>
          [ImporterHeader(Name = "程序集", Format = ""), ExporterHeader("程序集")]
        public string LocalAssembly {get;set;}
        
          /// <summary>
        ///  类名
        /// </summary>
          [ImporterHeader(Name = "类名", Format = ""), ExporterHeader("类名")]
        public string LocalClass {get;set;}
        
          /// <summary>
        ///  方法名
        /// </summary>
          [ImporterHeader(Name = "方法名", Format = ""), ExporterHeader("方法名")]
        public string LocalMethod {get;set;}
        
          /// <summary>
        ///  参数定义JSON Schema
        /// </summary>
          [ImporterHeader(Name = "参数定义JSON Schema", Format = ""), ExporterHeader("参数定义JSON Schema")]
        public string ParametersSchema {get;set;}
        
          /// <summary>
        ///  工具返回值JSON Schema(用于结果验证)
        /// </summary>
          [ImporterHeader(Name = "工具返回值JSON Schema(用于结果验证)", Format = ""), ExporterHeader("工具返回值JSON Schema(用于结果验证)")]
        public string ResponseSchema {get;set;}
        
          /// <summary>
        ///  错误处理策略: Abort(终止), Retry(重试), Fallback(降级)
        /// </summary>
          [ImporterHeader(Name = "错误处理策略: Abort(终止), Retry(重试), Fallback(降级)", Format = ""), ExporterHeader("错误处理策略: Abort(终止), Retry(重试), Fallback(降级)")]
        public string ErrorHandlingStrategy {get;set;}
        
          /// <summary>
        ///  备用工具ID(当主工具失败时)
        /// </summary>
          [ImporterHeader(Name = "备用工具ID(当主工具失败时)", Format = ""), ExporterHeader("备用工具ID(当主工具失败时)")]
        public long? FallbackToolID {get;set;}
        
          /// <summary>
        ///  工具执行超时时间(秒)
        /// </summary>
          [ImporterHeader(Name = "工具执行超时时间(秒)", Format = ""), ExporterHeader("工具执行超时时间(秒)")]
        public int? TimeoutSeconds {get;set;}
        
          /// <summary>
        ///  鉴权类型: 0-无, 1-APIKey, 2-OAuth
        /// </summary>
          [ImporterHeader(Name = "鉴权类型: 0-无, 1-APIKey, 2-OAuth", Format = ""), ExporterHeader("鉴权类型: 0-无, 1-APIKey, 2-OAuth")]
        public int? AuthType {get;set;}
        
          /// <summary>
        ///  鉴权配置JSON
        /// </summary>
          [ImporterHeader(Name = "鉴权配置JSON", Format = ""), ExporterHeader("鉴权配置JSON")]
        public string AuthConfig {get;set;}
        
          /// <summary>
        ///  是否需要人工确认(高危操作)
        /// </summary>
          [ImporterHeader(Name = "是否需要人工确认(高危操作)", Format = ""), ExporterHeader("是否需要人工确认(高危操作)")]
        public bool? RequiresConfirmation {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
     

    }
}
