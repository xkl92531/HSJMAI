
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:56
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// 变量访问记录表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_VariableAccessView:ViewBaseField
    {
            /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
          [ImporterHeader(Name = "主键ID（雪花算法生成）", Format = ""), ExporterHeader("主键ID（雪花算法生成）")]
        public long Id {get;set;}
        
          /// <summary>
        ///  关联的逻辑单元ID（外键）
        /// </summary>
          [ImporterHeader(Name = "关联的逻辑单元ID（外键）", Format = ""), ExporterHeader("关联的逻辑单元ID（外键）")]
        public long? LogicUnitId {get;set;}
        
          /// <summary>
        ///  关联的程序块ID（冗余字段，加速查询）
        /// </summary>
          [ImporterHeader(Name = "关联的程序块ID（冗余字段，加速查询）", Format = ""), ExporterHeader("关联的程序块ID（冗余字段，加速查询）")]
        public long? BlockId {get;set;}
        
          /// <summary>
        ///  关联的项目ID（冗余字段，加速查询）
        /// </summary>
          [ImporterHeader(Name = "关联的项目ID（冗余字段，加速查询）", Format = ""), ExporterHeader("关联的项目ID（冗余字段，加速查询）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  变量全路径名（如：DB745.Control.FwdAuto）
        /// </summary>
          [ImporterHeader(Name = "变量全路径名（如：DB745.Control.FwdAuto）", Format = ""), ExporterHeader("变量全路径名（如：DB745.Control.FwdAuto）")]
        public string VariableName {get;set;}
        
          /// <summary>
        ///  访问类型（READ:读取, WRITE:写入, READWRITE:读写）
        /// </summary>
          [ImporterHeader(Name = "访问类型（READ:读取, WRITE:写入, READWRITE:读写）", Format = ""), ExporterHeader("访问类型（READ:读取, WRITE:写入, READWRITE:读写）")]
        public string AccessType {get;set;}
        
          /// <summary>
        ///  变量在原始XML中的UID标识
        /// </summary>
          [ImporterHeader(Name = "变量在原始XML中的UID标识", Format = ""), ExporterHeader("变量在原始XML中的UID标识")]
        public string Uid {get;set;}
        
          /// <summary>
        ///  变量在访问处的特定注释
        /// </summary>
          [ImporterHeader(Name = "变量在访问处的特定注释", Format = ""), ExporterHeader("变量在访问处的特定注释")]
        public string Comment {get;set;}
        
          /// <summary>
        ///  变量作用域来源（Global, Local, Constant）
        /// </summary>
          [ImporterHeader(Name = "变量作用域来源（Global, Local, Constant）", Format = ""), ExporterHeader("变量作用域来源（Global, Local, Constant）")]
        public string VariableScope {get;set;}
        
          /// <summary>
        ///  是否为物理I/O点（True/False）
        /// </summary>
          [ImporterHeader(Name = "是否为物理I/O点（True/False）", Format = ""), ExporterHeader("是否为物理I/O点（True/False）")]
        public bool? IsIOTag {get;set;}
        
          /// <summary>
        ///  是否为关键步序变量（用于流程诊断）
        /// </summary>
          [ImporterHeader(Name = "是否为关键步序变量（用于流程诊断）", Format = ""), ExporterHeader("是否为关键步序变量（用于流程诊断）")]
        public bool? IsStepVariable {get;set;}
        
          /// <summary>
        ///  是否为报警变量（用于故障识别）
        /// </summary>
          [ImporterHeader(Name = "是否为报警变量（用于故障识别）", Format = ""), ExporterHeader("是否为报警变量（用于故障识别）")]
        public bool? IsAlarmVariable {get;set;}

     

    }
}
