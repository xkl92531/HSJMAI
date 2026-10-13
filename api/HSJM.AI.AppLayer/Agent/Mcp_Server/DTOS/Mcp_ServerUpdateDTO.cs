

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:18
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// MCP服务配置表
    /// </summary>
    public class Mcp_ServerUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  服务名称
        /// </summary>
        [Description("服务名称")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string ServerName {get;set;}
        
          /// <summary>
        ///  服务类型(stdio/sse)
        /// </summary>
        [Description("服务类型(stdio/sse)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string ServerType {get;set;}
        
          /// <summary>
        ///  连接配置JSON(cmd+args 或 url)
        /// </summary>
        [Description("连接配置JSON(cmd+args 或 url)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ConnectionConfig {get;set;}
        
          /// <summary>
        ///  描述
        /// </summary>
        [Description("描述")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsEnabled {get;set;}
        
     

    }
}
