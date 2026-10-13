
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:38
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 角色-工具关联表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Role_Tool_RelationView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  角色ID
        /// </summary>
          [ImporterHeader(Name = "角色ID", Format = ""), ExporterHeader("角色ID")]
        public long? RoleID {get;set;}
        
          /// <summary>
        ///  工具ID
        /// </summary>
          [ImporterHeader(Name = "工具ID", Format = ""), ExporterHeader("工具ID")]
        public long? ToolID {get;set;}
        
          /// <summary>
        ///  针对该角色的特定配置覆盖(JSON)
        /// </summary>
          [ImporterHeader(Name = "针对该角色的特定配置覆盖(JSON)", Format = ""), ExporterHeader("针对该角色的特定配置覆盖(JSON)")]
        public string OverrideConfig {get;set;}
        
     

    }
}
