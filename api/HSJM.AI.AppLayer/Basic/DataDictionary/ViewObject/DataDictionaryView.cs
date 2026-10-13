
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:03:23
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Basic.ViewObject
{
    /// <summary>
    /// 数据字典
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class DataDictionaryView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  数据代码
        /// </summary>
          [ImporterHeader(Name = "数据代码", Format = ""), ExporterHeader("数据代码")]
        public string Code {get;set;}
        
          /// <summary>
        ///  数据名称
        /// </summary>
          [ImporterHeader(Name = "数据名称", Format = ""), ExporterHeader("数据名称")]
        public string Name {get;set;}
        
          /// <summary>
        ///  父级代码
        /// </summary>
          [ImporterHeader(Name = "父级代码", Format = ""), ExporterHeader("父级代码")]
        public string ParentCode {get;set;}
        
          /// <summary>
        ///  父级名称
        /// </summary>
          [ImporterHeader(Name = "父级名称", Format = ""), ExporterHeader("父级名称")]
        public string ParentName {get;set;}
        
          /// <summary>
        ///  简称
        /// </summary>
          [ImporterHeader(Name = "简称", Format = ""), ExporterHeader("简称")]
        public string ShorterForm {get;set;}
        
          /// <summary>
        ///  排序
        /// </summary>
          [ImporterHeader(Name = "排序", Format = ""), ExporterHeader("排序")]
        public int? Sort {get;set;}
        
          /// <summary>
        ///  备注
        /// </summary>
          [ImporterHeader(Name = "备注", Format = ""), ExporterHeader("备注")]
        public string Remarks {get;set;}
        
          /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
          [ImporterHeader(Name = "状态（正常_1、停用_2）", Format = ""), ExporterHeader("状态（正常_1、停用_2）")]
        public int? Status {get;set;}
        
     

    }
}
