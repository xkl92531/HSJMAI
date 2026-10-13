
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:49
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// PLC源文件表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_SourceFileView:ViewBaseField
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
        ///  源文件名（包含扩展名，如：MainProgram.xml）
        /// </summary>
          [ImporterHeader(Name = "源文件名（包含扩展名，如：MainProgram.xml）", Format = ""), ExporterHeader("源文件名（包含扩展名，如：MainProgram.xml）")]
        public string FileName {get;set;}
        
          /// <summary>
        ///  文件存储的完整路径或URL
        /// </summary>
          [ImporterHeader(Name = "文件存储的完整路径或URL", Format = ""), ExporterHeader("文件存储的完整路径或URL")]
        public string FilePath {get;set;}
        
          /// <summary>
        ///  文件类型标识（如：XML, JSON, CSV, AWL）
        /// </summary>
          [ImporterHeader(Name = "文件类型标识（如：XML, JSON, CSV, AWL）", Format = ""), ExporterHeader("文件类型标识（如：XML, JSON, CSV, AWL）")]
        public string FileType {get;set;}
        
          /// <summary>
        ///  文件大小（字节为单位）
        /// </summary>
          [ImporterHeader(Name = "文件大小（字节为单位）", Format = ""), ExporterHeader("文件大小（字节为单位）")]
        public long? FileSize {get;set;}
        
          /// <summary>
        ///  文件编码格式（如：UTF-8, GB2312）
        /// </summary>
          [ImporterHeader(Name = "文件编码格式（如：UTF-8, GB2312）", Format = ""), ExporterHeader("文件编码格式（如：UTF-8, GB2312）")]
        public string Encoding {get;set;}
        
          /// <summary>
        ///  文件内容的SHA256哈希值（用于检测文件是否变更）
        /// </summary>
          [ImporterHeader(Name = "文件内容的SHA256哈希值（用于检测文件是否变更）", Format = ""), ExporterHeader("文件内容的SHA256哈希值（用于检测文件是否变更）")]
        public string FileHash {get;set;}
        
          /// <summary>
        ///  解析状态：0-待解析，1-解析成功，2-解析失败
        /// </summary>
          [ImporterHeader(Name = "解析状态：0-待解析，1-解析成功，2-解析失败", Format = ""), ExporterHeader("解析状态：0-待解析，1-解析成功，2-解析失败")]
        public int? ParseStatus {get;set;}
        
          /// <summary>
        ///  解析完成时间
        /// </summary>
          [ImporterHeader(Name = "解析完成时间", Format = ""), ExporterHeader("解析完成时间")]
        public DateTime? ParseTime {get;set;}
        
          /// <summary>
        ///  解析失败时的错误日志（JSON格式）
        /// </summary>
          [ImporterHeader(Name = "解析失败时的错误日志（JSON格式）", Format = ""), ExporterHeader("解析失败时的错误日志（JSON格式）")]
        public string ParseErrors {get;set;}
        
 
     

    }
}
