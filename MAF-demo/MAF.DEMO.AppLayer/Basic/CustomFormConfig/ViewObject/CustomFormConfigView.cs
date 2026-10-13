
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/3/7 11:29:06
using System;
using MAF.DEMO.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace MAF.DEMO.AppLayer.Basic.ViewObject
{
    /// <summary>
    /// 用户表单列配置
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class CustomFormConfigView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  表单Code
        /// </summary>
          [ImporterHeader(Name = "表单Code", Format = ""), ExporterHeader("表单Code")]
        public string FormCode {get;set;}
        
          /// <summary>
        ///  用户ID
        /// </summary>
          [ImporterHeader(Name = "用户ID", Format = ""), ExporterHeader("用户ID")]
        public long? UserId {get;set;}
        
          /// <summary>
        ///  表单Code
        /// </summary>
          [ImporterHeader(Name = "表单Code", Format = ""), ExporterHeader("表单Code")]
        public string FormColumnJson {get;set;}
        
          /// <summary>
        ///  预留1
        /// </summary>
          [ImporterHeader(Name = "预留1", Format = ""), ExporterHeader("预留1")]
        public string Attr1 {get;set;}
        
          /// <summary>
        ///  预留2
        /// </summary>
          [ImporterHeader(Name = "预留2", Format = ""), ExporterHeader("预留2")]
        public string Attr2 {get;set;}
        
          /// <summary>
        ///  预留3
        /// </summary>
          [ImporterHeader(Name = "预留3", Format = ""), ExporterHeader("预留3")]
        public string Attr3 {get;set;}
        
          /// <summary>
        ///  预留4
        /// </summary>
          [ImporterHeader(Name = "预留4", Format = ""), ExporterHeader("预留4")]
        public string Attr4 {get;set;}
        
     

    }
}
