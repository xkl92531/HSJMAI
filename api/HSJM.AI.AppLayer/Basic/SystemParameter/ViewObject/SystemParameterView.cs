
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/28 16:07:46
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Basic.ViewObject
{
    /// <summary>
    /// 系统参数
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class SystemParameterView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  参数代码
        /// </summary>
          [ImporterHeader(Name = "参数代码", Format = ""), ExporterHeader("参数代码")]
        public string ParamCode {get;set;}
        
          /// <summary>
        ///  参数名称
        /// </summary>
          [ImporterHeader(Name = "参数名称", Format = ""), ExporterHeader("参数名称")]
        public string ParamName {get;set;}
        
          /// <summary>
        ///  参数值
        /// </summary>
          [ImporterHeader(Name = "参数值", Format = ""), ExporterHeader("参数值")]
        public string ParamValue {get;set;}
        
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
