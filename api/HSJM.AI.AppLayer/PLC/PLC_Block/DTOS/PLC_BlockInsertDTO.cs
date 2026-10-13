

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:03
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 程序块表
    /// </summary>
    public class PLC_BlockInsertDTO
    {
            /// <summary>
        ///  关联的源文件ID（外键）
        /// </summary>
        [Description("关联的源文件ID（外键）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? SourceFileId {get;set;}
        
          /// <summary>
        ///  关联的项目ID（冗余字段，便于直接查询）
        /// </summary>
        [Description("关联的项目ID（冗余字段，便于直接查询）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ProjectId {get;set;}
        
          /// <summary>
        ///  程序块名称（如：FB720_OP70Manual）
        /// </summary>
        [Description("程序块名称（如：FB720_OP70Manual）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string BlockName {get;set;}
        
          /// <summary>
        ///  程序块类型（如：FB, FC, OB, DB, UDT）
        /// </summary>
        [Description("程序块类型（如：FB, FC, OB, DB, UDT）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(10)]
                  public string BlockType {get;set;}
        
          /// <summary>
        ///  程序块编号（如：720，部分品牌可能无编号）
        /// </summary>
        [Description("程序块编号（如：720，部分品牌可能无编号）")]
                  [Range(0, int.MaxValue)]
                  public int? BlockNumber {get;set;}
        
          /// <summary>
        ///  编程语言（如：LAD, FBD, STL, SCL, GRAPH）
        /// </summary>
        [Description("编程语言（如：LAD, FBD, STL, SCL, GRAPH）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string ProgrammingLanguage {get;set;}
        
          /// <summary>
        ///  程序块的功能描述或注释
        /// </summary>
        [Description("程序块的功能描述或注释")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  是否为潜在入口块（True表示没有被其他块调用，如OB1）
        /// </summary>
        [Description("是否为潜在入口块（True表示没有被其他块调用，如OB1）")]
                 public bool? IsPotentialEntry {get;set;}
        
          /// <summary>
        ///  该块调用其他块的数量（出度）
        /// </summary>
        [Description("该块调用其他块的数量（出度）")]
                  [Range(0, int.MaxValue)]
                  public int? CallsOutCount {get;set;}
        
          /// <summary>
        ///  该块被其他块调用的数量（入度）
        /// </summary>
        [Description("该块被其他块调用的数量（入度）")]
                  [Range(0, int.MaxValue)]
                  public int? CalledByCount {get;set;}
        
          /// <summary>
        ///  该块包含的Network/段落数量
        /// </summary>
        [Description("该块包含的Network/段落数量")]
                  [Range(0, int.MaxValue)]
                  public int? NetworksCount {get;set;}
        
          /// <summary>
        ///  扩展元数据（包含作者、版本、时间戳等信息的JSON对象）
        /// </summary>
        [Description("扩展元数据（包含作者、版本、时间戳等信息的JSON对象）")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string MetadataJson {get;set;}
        
     
        
     

    }
}
