
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/31 9:31:07
using System;
using MAF.DEMO.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace MAF.DEMO.AppLayer.LOG.ViewObject
{
    /// <summary>
    /// 操作日志
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class DataLogView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long Id {get;set;}
        
          /// <summary>
        ///  操作类型
        /// </summary>
          [ImporterHeader(Name = "操作类型", Format = ""), ExporterHeader("操作类型")]
        public string DiffType {get;set;}
        
          /// <summary>
        ///  表名称
        /// </summary>
          [ImporterHeader(Name = "表名称", Format = ""), ExporterHeader("表名称")]
        public string TableName {get;set;}
        
          /// <summary>
        ///  表描述
        /// </summary>
          [ImporterHeader(Name = "表描述", Format = ""), ExporterHeader("表描述")]
        public string TableDesc {get;set;}
        
          /// <summary>
        ///  操作内容
        /// </summary>
          [ImporterHeader(Name = "操作内容", Format = ""), ExporterHeader("操作内容")]
        public string LogContent {get;set;}
        
     

    }
}
