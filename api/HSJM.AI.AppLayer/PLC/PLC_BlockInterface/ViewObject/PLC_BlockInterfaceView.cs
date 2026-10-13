
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:22
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// 程序块接口变量表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_BlockInterfaceView:ViewBaseField
    {
            /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
          [ImporterHeader(Name = "主键ID（雪花算法生成）", Format = ""), ExporterHeader("主键ID（雪花算法生成）")]
        public long Id {get;set;}
        
          /// <summary>
        ///  关联的程序块ID（外键）
        /// </summary>
          [ImporterHeader(Name = "关联的程序块ID（外键）", Format = ""), ExporterHeader("关联的程序块ID（外键）")]
        public long? BlockId {get;set;}
        
          /// <summary>
        ///  变量名称（如：General）
        /// </summary>
          [ImporterHeader(Name = "变量名称（如：General）", Format = ""), ExporterHeader("变量名称（如：General）")]
        public string VariableName {get;set;}
        
          /// <summary>
        ///  变量完整路径（如：Static.General.StepNum）
        /// </summary>
          [ImporterHeader(Name = "变量完整路径（如：Static.General.StepNum）", Format = ""), ExporterHeader("变量完整路径（如：Static.General.StepNum）")]
        public string VariablePath {get;set;}
        
          /// <summary>
        ///  数据类型（如：Int, Bool, UDT_General）
        /// </summary>
          [ImporterHeader(Name = "数据类型（如：Int, Bool, UDT_General）", Format = ""), ExporterHeader("数据类型（如：Int, Bool, UDT_General）")]
        public string DataType {get;set;}
        
          /// <summary>
        ///  变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）
        /// </summary>
          [ImporterHeader(Name = "变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）", Format = ""), ExporterHeader("变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）")]
        public string Scope {get;set;}
        
          /// <summary>
        ///  变量注释
        /// </summary>
          [ImporterHeader(Name = "变量注释", Format = ""), ExporterHeader("变量注释")]
        public string Comment {get;set;}
        
          /// <summary>
        ///  变量初始值
        /// </summary>
          [ImporterHeader(Name = "变量初始值", Format = ""), ExporterHeader("变量初始值")]
        public string InitialValue {get;set;}
        
          /// <summary>
        ///  保持性设置（如：Retain, NonRetain）
        /// </summary>
          [ImporterHeader(Name = "保持性设置（如：Retain, NonRetain）", Format = ""), ExporterHeader("保持性设置（如：Retain, NonRetain）")]
        public string Remanence {get;set;}
        
          /// <summary>
        ///  父级变量ID（用于表示结构体/UDT的层级关系）
        /// </summary>
          [ImporterHeader(Name = "父级变量ID（用于表示结构体/UDT的层级关系）", Format = ""), ExporterHeader("父级变量ID（用于表示结构体/UDT的层级关系）")]
        public long? ParentId {get;set;}
        
          /// <summary>
        ///  嵌套层级深度（0表示顶层）
        /// </summary>
          [ImporterHeader(Name = "嵌套层级深度（0表示顶层）", Format = ""), ExporterHeader("嵌套层级深度（0表示顶层）")]
        public int? NestingLevel {get;set;}
        
          /// <summary>
        ///  数组维度信息（如：[1..10, 0..5]）
        /// </summary>
          [ImporterHeader(Name = "数组维度信息（如：[1..10, 0..5]）", Format = ""), ExporterHeader("数组维度信息（如：[1..10, 0..5]）")]
        public string ArrayInfo {get;set;}
        

     

    }
}
