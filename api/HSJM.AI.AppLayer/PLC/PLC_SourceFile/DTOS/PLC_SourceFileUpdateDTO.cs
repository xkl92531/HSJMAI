

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:49
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// PLC源文件表
    /// </summary>
    public class PLC_SourceFileUpdateDTO
    {
            /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
        [Description("主键ID（雪花算法生成）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long Id {get;set;}
        
          /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
        [Description("关联的项目ID（外键）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ProjectId {get;set;}
        
          /// <summary>
        ///  源文件名（包含扩展名，如：MainProgram.xml）
        /// </summary>
        [Description("源文件名（包含扩展名，如：MainProgram.xml）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(255)]
                  public string FileName {get;set;}
        
          /// <summary>
        ///  文件存储的完整路径或URL
        /// </summary>
        [Description("文件存储的完整路径或URL")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string FilePath {get;set;}
        
          /// <summary>
        ///  文件类型标识（如：XML, JSON, CSV, AWL）
        /// </summary>
        [Description("文件类型标识（如：XML, JSON, CSV, AWL）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string FileType {get;set;}
        
          /// <summary>
        ///  文件大小（字节为单位）
        /// </summary>
        [Description("文件大小（字节为单位）")]
                  [Range(0, long.MaxValue)]
                  public long? FileSize {get;set;}
        
          /// <summary>
        ///  文件编码格式（如：UTF-8, GB2312）
        /// </summary>
        [Description("文件编码格式（如：UTF-8, GB2312）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string Encoding {get;set;}
        
          /// <summary>
        ///  文件内容的SHA256哈希值（用于检测文件是否变更）
        /// </summary>
        [Description("文件内容的SHA256哈希值（用于检测文件是否变更）")]
                  [MinLength(0)]
                   [MaxLength(64)]
                  public string FileHash {get;set;}
        
          /// <summary>
        ///  解析状态：0-待解析，1-解析成功，2-解析失败
        /// </summary>
        [Description("解析状态：0-待解析，1-解析成功，2-解析失败")]
                  [Range(0, int.MaxValue)]
                  public int? ParseStatus {get;set;}
        
          /// <summary>
        ///  解析完成时间
        /// </summary>
        [Description("解析完成时间")]
                 public DateTime? ParseTime {get;set;}
        
          /// <summary>
        ///  解析失败时的错误日志（JSON格式）
        /// </summary>
        [Description("解析失败时的错误日志（JSON格式）")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ParseErrors {get;set;}
        
 
    }
}
