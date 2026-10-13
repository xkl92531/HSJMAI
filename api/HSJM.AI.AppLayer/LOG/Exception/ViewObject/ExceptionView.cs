
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 11:10:31
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.LOG.ViewObject
{
    /// <summary>
    /// 异常日志
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class ExceptionView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  名称
        /// </summary>
          [ImporterHeader(Name = "名称", Format = ""), ExporterHeader("名称")]
        public string Name {get;set;}
        
          /// <summary>
        ///  类名称
        /// </summary>
          [ImporterHeader(Name = "类名称", Format = ""), ExporterHeader("类名称")]
        public string ClassName {get;set;}
        
          /// <summary>
        ///  方法名称
        /// </summary>
          [ImporterHeader(Name = "方法名称", Format = ""), ExporterHeader("方法名称")]
        public string MethodName {get;set;}
        
          /// <summary>
        ///  异常名称
        /// </summary>
          [ImporterHeader(Name = "异常名称", Format = ""), ExporterHeader("异常名称")]
        public string ExceptionName {get;set;}
        
          /// <summary>
        ///  异常信息
        /// </summary>
          [ImporterHeader(Name = "异常信息", Format = ""), ExporterHeader("异常信息")]
        public string ExceptionMsg {get;set;}
        
          /// <summary>
        ///  异常源
        /// </summary>
          [ImporterHeader(Name = "异常源", Format = ""), ExporterHeader("异常源")]
        public string ExceptionSource {get;set;}
        
          /// <summary>
        ///  堆栈信息
        /// </summary>
          [ImporterHeader(Name = "堆栈信息", Format = ""), ExporterHeader("堆栈信息")]
        public string StackTrace {get;set;}
        
          /// <summary>
        ///  参数对象
        /// </summary>
          [ImporterHeader(Name = "参数对象", Format = ""), ExporterHeader("参数对象")]
        public string ParamsObj {get;set;}
        
          /// <summary>
        ///  异常时间
        /// </summary>
          [ImporterHeader(Name = "异常时间", Format = ""), ExporterHeader("异常时间")]
        public DateTime? ExceptionTime {get;set;}
        
          /// <summary>
        ///  操作人
        /// </summary>
          [ImporterHeader(Name = "操作人", Format = ""), ExporterHeader("操作人")]
        public string Account {get;set;}
        
     

    }
}
