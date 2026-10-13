

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:38
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 逻辑单元表
    /// </summary>
    public class PLC_LogicUnitUpdateDTO
    {
            /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
        [Description("主键ID（雪花算法生成）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long Id {get;set;}
        
          /// <summary>
        ///  关联的程序块ID（外键）
        /// </summary>
        [Description("关联的程序块ID（外键）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? BlockId {get;set;}
        
          /// <summary>
        ///  逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）
        /// </summary>
        [Description("逻辑单元类型（如：NETWORK, TRANSITION, STEP_ACTION）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string UnitType {get;set;}
        
          /// <summary>
        ///  单元编号（对应Network号或Transition号）
        /// </summary>
        [Description("单元编号（对应Network号或Transition号）")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? NetworkId {get;set;}
        
          /// <summary>
        ///  单元标题/摘要
        /// </summary>
        [Description("单元标题/摘要")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Title {get;set;}
        
          /// <summary>
        ///  单元注释
        /// </summary>
        [Description("单元注释")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Comment {get;set;}
        
          /// <summary>
        ///  该单元使用的编程语言（如：LAD, FBD）
        /// </summary>
        [Description("该单元使用的编程语言（如：LAD, FBD）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string ProgrammingLanguage {get;set;}
        
          /// <summary>
        ///  原始逻辑代码/XML片段（AI诊断的直接输入）
        /// </summary>
        [Description("原始逻辑代码/XML片段（AI诊断的直接输入）")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string RawContent {get;set;}
        
          /// <summary>
        ///  UID到变量名的映射字典（如：{"21": "OP070_CYL01_W"}）
        /// </summary>
        [Description("UID到变量名的映射字典（如：）")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string UidSymbolMapJson {get;set;}
        
          /// <summary>
        ///  该单元读取的不同变量数量
        /// </summary>
        [Description("该单元读取的不同变量数量")]
                  [Range(0, int.MaxValue)]
                  public int? ReadVarsCount {get;set;}
        
          /// <summary>
        ///  该单元写入的不同变量数量
        /// </summary>
        [Description("该单元写入的不同变量数量")]
                  [Range(0, int.MaxValue)]
                  public int? WriteVarsCount {get;set;}
        
          /// <summary>
        ///  该单元包含的函数调用数量
        /// </summary>
        [Description("该单元包含的函数调用数量")]
                  [Range(0, int.MaxValue)]
                  public int? CallsCount {get;set;}
        
 

    }
}
