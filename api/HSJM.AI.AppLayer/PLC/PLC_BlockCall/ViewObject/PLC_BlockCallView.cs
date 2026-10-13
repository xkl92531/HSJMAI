
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:13
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// 块调用关系表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_BlockCallView:ViewBaseField
    {
            /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
          [ImporterHeader(Name = "主键ID（雪花算法生成）", Format = ""), ExporterHeader("主键ID（雪花算法生成）")]
        public long Id {get;set;}
        
          /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
          [ImporterHeader(Name = "关联的项目ID（外键）", Format = ""), ExporterHeader("关联的项目ID（外键）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  发起调用的程序块ID（调用者）
        /// </summary>
          [ImporterHeader(Name = "发起调用的程序块ID（调用者）", Format = ""), ExporterHeader("发起调用的程序块ID（调用者）")]
        public long? CallerBlockId {get;set;}
        
          /// <summary>
        ///  调用者块名称（冗余字段）
        /// </summary>
          [ImporterHeader(Name = "调用者块名称（冗余字段）", Format = ""), ExporterHeader("调用者块名称（冗余字段）")]
        public string CallerBlockName {get;set;}
        
          /// <summary>
        ///  被调用的程序块名称（被调用者）
        /// </summary>
          [ImporterHeader(Name = "被调用的程序块名称（被调用者）", Format = ""), ExporterHeader("被调用的程序块名称（被调用者）")]
        public string CalleeBlockName {get;set;}
        
          /// <summary>
        ///  被调用块的类型（如：FB, FC）
        /// </summary>
          [ImporterHeader(Name = "被调用块的类型（如：FB, FC）", Format = ""), ExporterHeader("被调用块的类型（如：FB, FC）")]
        public string CalleeBlockType {get;set;}
        
          /// <summary>
        ///  发生调用的逻辑单元ID
        /// </summary>
          [ImporterHeader(Name = "发生调用的逻辑单元ID", Format = ""), ExporterHeader("发生调用的逻辑单元ID")]
        public long? LogicUnitId {get;set;}
        
          /// <summary>
        ///  发生调用的Network编号
        /// </summary>
          [ImporterHeader(Name = "发生调用的Network编号", Format = ""), ExporterHeader("发生调用的Network编号")]
        public int? NetworkId {get;set;}
        
  
     

    }
}
