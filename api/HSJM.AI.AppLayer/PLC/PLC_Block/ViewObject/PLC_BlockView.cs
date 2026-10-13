
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:03
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// 程序块表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_BlockView:ViewBaseField
    {
            /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
          [ImporterHeader(Name = "主键ID（雪花算法生成）", Format = ""), ExporterHeader("主键ID（雪花算法生成）")]
        public long Id {get;set;}
        
          /// <summary>
        ///  关联的源文件ID（外键）
        /// </summary>
          [ImporterHeader(Name = "关联的源文件ID（外键）", Format = ""), ExporterHeader("关联的源文件ID（外键）")]
        public long? SourceFileId {get;set;}
        
          /// <summary>
        ///  关联的项目ID（冗余字段，便于直接查询）
        /// </summary>
          [ImporterHeader(Name = "关联的项目ID（冗余字段，便于直接查询）", Format = ""), ExporterHeader("关联的项目ID（冗余字段，便于直接查询）")]
        public long? ProjectId {get;set;}
        
          /// <summary>
        ///  程序块名称（如：FB720_OP70Manual）
        /// </summary>
          [ImporterHeader(Name = "程序块名称（如：FB720_OP70Manual）", Format = ""), ExporterHeader("程序块名称（如：FB720_OP70Manual）")]
        public string BlockName {get;set;}
        
          /// <summary>
        ///  程序块类型（如：FB, FC, OB, DB, UDT）
        /// </summary>
          [ImporterHeader(Name = "程序块类型（如：FB, FC, OB, DB, UDT）", Format = ""), ExporterHeader("程序块类型（如：FB, FC, OB, DB, UDT）")]
        public string BlockType {get;set;}
        
          /// <summary>
        ///  程序块编号（如：720，部分品牌可能无编号）
        /// </summary>
          [ImporterHeader(Name = "程序块编号（如：720，部分品牌可能无编号）", Format = ""), ExporterHeader("程序块编号（如：720，部分品牌可能无编号）")]
        public int? BlockNumber {get;set;}
        
          /// <summary>
        ///  编程语言（如：LAD, FBD, STL, SCL, GRAPH）
        /// </summary>
          [ImporterHeader(Name = "编程语言（如：LAD, FBD, STL, SCL, GRAPH）", Format = ""), ExporterHeader("编程语言（如：LAD, FBD, STL, SCL, GRAPH）")]
        public string ProgrammingLanguage {get;set;}
        
          /// <summary>
        ///  程序块的功能描述或注释
        /// </summary>
          [ImporterHeader(Name = "程序块的功能描述或注释", Format = ""), ExporterHeader("程序块的功能描述或注释")]
        public string Description {get;set;}
        
          /// <summary>
        ///  是否为潜在入口块（True表示没有被其他块调用，如OB1）
        /// </summary>
          [ImporterHeader(Name = "是否为潜在入口块（True表示没有被其他块调用，如OB1）", Format = ""), ExporterHeader("是否为潜在入口块（True表示没有被其他块调用，如OB1）")]
        public bool? IsPotentialEntry {get;set;}
        
          /// <summary>
        ///  该块调用其他块的数量（出度）
        /// </summary>
          [ImporterHeader(Name = "该块调用其他块的数量（出度）", Format = ""), ExporterHeader("该块调用其他块的数量（出度）")]
        public int? CallsOutCount {get;set;}
        
          /// <summary>
        ///  该块被其他块调用的数量（入度）
        /// </summary>
          [ImporterHeader(Name = "该块被其他块调用的数量（入度）", Format = ""), ExporterHeader("该块被其他块调用的数量（入度）")]
        public int? CalledByCount {get;set;}
        
          /// <summary>
        ///  该块包含的Network/段落数量
        /// </summary>
          [ImporterHeader(Name = "该块包含的Network/段落数量", Format = ""), ExporterHeader("该块包含的Network/段落数量")]
        public int? NetworksCount {get;set;}
        
          /// <summary>
        ///  扩展元数据（包含作者、版本、时间戳等信息的JSON对象）
        /// </summary>
          [ImporterHeader(Name = "扩展元数据（包含作者、版本、时间戳等信息的JSON对象）", Format = ""), ExporterHeader("扩展元数据（包含作者、版本、时间戳等信息的JSON对象）")]
        public string MetadataJson {get;set;}
        
 
     

    }
}
