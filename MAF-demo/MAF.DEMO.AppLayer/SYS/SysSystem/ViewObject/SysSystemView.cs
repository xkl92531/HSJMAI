
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:57:03
using System;
using MAF.DEMO.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace MAF.DEMO.AppLayer.SYS.ViewObject
{
    /// <summary>
    /// 
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class SysSystemView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  系统名称
        /// </summary>
          [ImporterHeader(Name = "系统名称", Format = ""), ExporterHeader("系统名称")]
        public string Name {get;set;}
        
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
        
          /// <summary>
        ///  排序
        /// </summary>
          [ImporterHeader(Name = "排序", Format = ""), ExporterHeader("排序")]
        public int? Sort {get;set;}
        
     

    }
}
