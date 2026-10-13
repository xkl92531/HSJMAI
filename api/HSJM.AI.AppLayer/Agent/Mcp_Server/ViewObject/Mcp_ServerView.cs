
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:18
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// MCP服务配置表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Mcp_ServerView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  服务名称
        /// </summary>
          [ImporterHeader(Name = "服务名称", Format = ""), ExporterHeader("服务名称")]
        public string ServerName {get;set;}
        
          /// <summary>
        ///  服务类型(stdio/sse)
        /// </summary>
          [ImporterHeader(Name = "服务类型(stdio/sse)", Format = ""), ExporterHeader("服务类型(stdio/sse)")]
        public string ServerType {get;set;}
        
          /// <summary>
        ///  连接配置JSON(cmd+args 或 url)
        /// </summary>
          [ImporterHeader(Name = "连接配置JSON(cmd+args 或 url)", Format = ""), ExporterHeader("连接配置JSON(cmd+args 或 url)")]
        public string ConnectionConfig {get;set;}
        
          /// <summary>
        ///  描述
        /// </summary>
          [ImporterHeader(Name = "描述", Format = ""), ExporterHeader("描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
     

    }
}
