
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:24:25
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 智能体关系表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Agent_RelationView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  父智能体ID (Manager/Supervisor)
        /// </summary>
          [ImporterHeader(Name = "父智能体ID (Manager/Supervisor)", Format = ""), ExporterHeader("父智能体ID (Manager/Supervisor)")]
        public long? ParentRoleID {get;set;}
        
          /// <summary>
        ///  子智能体ID (Worker/Subordinate)
        /// </summary>
          [ImporterHeader(Name = "子智能体ID (Worker/Subordinate)", Format = ""), ExporterHeader("子智能体ID (Worker/Subordinate)")]
        public long? ChildRoleID {get;set;}
        
          /// <summary>
        ///  关系类型: Supervisor, Member, Delegate
        /// </summary>
          [ImporterHeader(Name = "关系类型: Supervisor, Member, Delegate", Format = ""), ExporterHeader("关系类型: Supervisor, Member, Delegate")]
        public string RelationType {get;set;}
        
          /// <summary>
        ///  关系描述
        /// </summary>
          [ImporterHeader(Name = "关系描述", Format = ""), ExporterHeader("关系描述")]
        public string Description {get;set;}
        
     

    }
}
