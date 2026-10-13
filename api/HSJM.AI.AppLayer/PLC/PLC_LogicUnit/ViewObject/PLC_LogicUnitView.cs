
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:38
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// 逻辑单元表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_LogicUnitView:ViewBaseField
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
        ///  逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）
        /// </summary>
          [ImporterHeader(Name = "逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）", Format = ""), ExporterHeader("逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）")]
        public string UnitType {get;set;}
        
          /// <summary>
        ///  单元编号（对应Network号或Transition号）
        /// </summary>
          [ImporterHeader(Name = "单元编号（对应Network号或Transition号）", Format = ""), ExporterHeader("单元编号（对应Network号或Transition号）")]
        public int? NetworkId {get;set;}
        
          /// <summary>
        ///  单元标题/摘要
        /// </summary>
          [ImporterHeader(Name = "单元标题/摘要", Format = ""), ExporterHeader("单元标题/摘要")]
        public string Title {get;set;}
        
          /// <summary>
        ///  单元注释
        /// </summary>
          [ImporterHeader(Name = "单元注释", Format = ""), ExporterHeader("单元注释")]
        public string Comment {get;set;}
        
          /// <summary>
        ///  该单元使用的编程语言（如：LAD, FBD）
        /// </summary>
          [ImporterHeader(Name = "该单元使用的编程语言（如：LAD, FBD）", Format = ""), ExporterHeader("该单元使用的编程语言（如：LAD, FBD）")]
        public string ProgrammingLanguage {get;set;}
        
          /// <summary>
        ///  原始逻辑代码/XML片段（AI诊断的直接输入）
        /// </summary>
          [ImporterHeader(Name = "原始逻辑代码/XML片段（AI诊断的直接输入）", Format = ""), ExporterHeader("原始逻辑代码/XML片段（AI诊断的直接输入）")]
        public string RawContent {get;set;}
        
          /// <summary>
        ///  UID到变量名的映射字典（如：{"21": "OP070_CYL01_W"}）
        /// </summary>
          [ImporterHeader(Name = "UID到变量名的映射字典（）", Format = ""), ExporterHeader("UID到变量名的映射字典（如：）")]
        public string UidSymbolMapJson {get;set;}
        
          /// <summary>
        ///  该单元读取的不同变量数量
        /// </summary>
          [ImporterHeader(Name = "该单元读取的不同变量数量", Format = ""), ExporterHeader("该单元读取的不同变量数量")]
        public int? ReadVarsCount {get;set;}
        
          /// <summary>
        ///  该单元写入的不同变量数量
        /// </summary>
          [ImporterHeader(Name = "该单元写入的不同变量数量", Format = ""), ExporterHeader("该单元写入的不同变量数量")]
        public int? WriteVarsCount {get;set;}
        
          /// <summary>
        ///  该单元包含的函数调用数量
        /// </summary>
          [ImporterHeader(Name = "该单元包含的函数调用数量", Format = ""), ExporterHeader("该单元包含的函数调用数量")]
        public int? CallsCount {get;set;}

        public string Compact_logic_xml { set; get; }

    }
}
