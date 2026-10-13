

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:09
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 工具/插件定义表
    /// </summary>
    public class Tool_DefinitionUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  工具名称
        /// </summary>
        [Description("工具名称")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string ToolName {get;set;}
        
          /// <summary>
        ///  工具编码(Function Name)
        /// </summary>
        [Description("工具编码(Function Name)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string ToolCode {get;set;}
        
          /// <summary>
        ///  工具描述(供AI理解)
        /// </summary>
        [Description("工具描述(供AI理解)")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  工具类型: Local/Http/Mcp
        /// </summary>
        [Description("工具类型: Local/Http/Mcp")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string ToolType {get;set;}
        
          /// <summary>
        ///  API地址
        /// </summary>
        [Description("API地址")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ApiUrl {get;set;}
        
          /// <summary>
        ///  请求方法(GET/POST)
        /// </summary>
        [Description("请求方法(GET/POST)")]
                  [MinLength(0)]
                   [MaxLength(10)]
                  public string ApiMethod {get;set;}
        
          /// <summary>
        ///  请求头JSON
        /// </summary>
        [Description("请求头JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ApiHeaders {get;set;}
        
          /// <summary>
        ///  请求体模板JSON
        /// </summary>
        [Description("请求体模板JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ApiBodyTemplate {get;set;}
        
          /// <summary>
        ///  关联MCP服务ID
        /// </summary>
        [Description("关联MCP服务ID")]
                  [Range(0, long.MaxValue)]
                  public long? McpServerID {get;set;}
        
          /// <summary>
        ///  MCP工具名称
        /// </summary>
        [Description("MCP工具名称")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string McpToolName {get;set;}
        
          /// <summary>
        ///  程序集
        /// </summary>
        [Description("程序集")]
                  [MinLength(0)]
                   [MaxLength(255)]
                  public string LocalAssembly {get;set;}
        
          /// <summary>
        ///  类名
        /// </summary>
        [Description("类名")]
                  [MinLength(0)]
                   [MaxLength(255)]
                  public string LocalClass {get;set;}
        
          /// <summary>
        ///  方法名
        /// </summary>
        [Description("方法名")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string LocalMethod {get;set;}
        
          /// <summary>
        ///  参数定义JSON Schema
        /// </summary>
        [Description("参数定义JSON Schema")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ParametersSchema {get;set;}
        
          /// <summary>
        ///  工具返回值JSON Schema(用于结果验证)
        /// </summary>
        [Description("工具返回值JSON Schema(用于结果验证)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ResponseSchema {get;set;}
        
          /// <summary>
        ///  错误处理策略: Abort(终止), Retry(重试), Fallback(降级)
        /// </summary>
        [Description("错误处理策略: Abort(终止), Retry(重试), Fallback(降级)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string ErrorHandlingStrategy {get;set;}
        
          /// <summary>
        ///  备用工具ID(当主工具失败时)
        /// </summary>
        [Description("备用工具ID(当主工具失败时)")]
                  [Range(0, long.MaxValue)]
                  public long? FallbackToolID {get;set;}
        
          /// <summary>
        ///  工具执行超时时间(秒)
        /// </summary>
        [Description("工具执行超时时间(秒)")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? TimeoutSeconds {get;set;}
        
          /// <summary>
        ///  鉴权类型: 0-无, 1-APIKey, 2-OAuth
        /// </summary>
        [Description("鉴权类型: 0-无, 1-APIKey, 2-OAuth")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? AuthType {get;set;}
        
          /// <summary>
        ///  鉴权配置JSON
        /// </summary>
        [Description("鉴权配置JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string AuthConfig {get;set;}
        
          /// <summary>
        ///  是否需要人工确认(高危操作)
        /// </summary>
        [Description("是否需要人工确认(高危操作)")]
                  [Required]
                  public bool? RequiresConfirmation {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsEnabled {get;set;}
        
     

    }
}
