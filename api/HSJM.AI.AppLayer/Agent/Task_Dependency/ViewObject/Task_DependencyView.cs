
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:36:03
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 任务依赖关系表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Task_DependencyView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  所属工作流ID(冗余字段，方便查询)
        /// </summary>
          [ImporterHeader(Name = "所属工作流ID(冗余字段，方便查询)", Format = ""), ExporterHeader("所属工作流ID(冗余字段，方便查询)")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  前置任务ID
        /// </summary>
          [ImporterHeader(Name = "前置任务ID", Format = ""), ExporterHeader("前置任务ID")]
        public long? PredecessorTaskID {get;set;}
        
          /// <summary>
        ///  后置任务ID
        /// </summary>
          [ImporterHeader(Name = "后置任务ID", Format = ""), ExporterHeader("后置任务ID")]
        public long? SuccessorTaskID {get;set;}
        
          /// <summary>
        ///  依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)
        /// </summary>
          [ImporterHeader(Name = "依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)", Format = ""), ExporterHeader("依赖类型: 0-FinishToStart(完成即开始), 1-Conditional(条件触发)")]
        public int? DependencyType {get;set;}
        
     

    }
}
