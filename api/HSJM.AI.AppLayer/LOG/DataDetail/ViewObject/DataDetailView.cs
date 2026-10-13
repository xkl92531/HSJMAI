
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/31 9:31:17
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.LOG.ViewObject
{
    /// <summary>
    /// 操作数据明细
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class DataDetailView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long Id {get;set;}
        
          /// <summary>
        ///  操作日志ID
        /// </summary>
          [ImporterHeader(Name = "操作日志ID", Format = ""), ExporterHeader("操作日志ID")]
        public long? DataLogID {get;set;}
        
          /// <summary>
        ///  列描述
        /// </summary>
          [ImporterHeader(Name = "列描述", Format = ""), ExporterHeader("列描述")]
        public string ColumnDesc {get;set;}
        
          /// <summary>
        ///  列字段
        /// </summary>
          [ImporterHeader(Name = "列字段", Format = ""), ExporterHeader("列字段")]
        public string ColumnField {get;set;}
        
          /// <summary>
        ///  原值
        /// </summary>
          [ImporterHeader(Name = "原值", Format = ""), ExporterHeader("原值")]
        public string OldValue {get;set;}
        
          /// <summary>
        ///  新值
        /// </summary>
          [ImporterHeader(Name = "新值", Format = ""), ExporterHeader("新值")]
        public string NewValue {get;set;}
        
     

    }
}
