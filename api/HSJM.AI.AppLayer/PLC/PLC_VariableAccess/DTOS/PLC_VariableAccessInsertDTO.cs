

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:56
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 变量访问记录表
    /// </summary>
    public class PLC_VariableAccessInsertDTO
    {
            /// <summary>
        ///  关联的逻辑单元ID（外键）
        /// </summary>
        [Description("关联的逻辑单元ID（外键）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? LogicUnitId {get;set;}
        
          /// <summary>
        ///  关联的程序块ID（冗余字段，加速查询）
        /// </summary>
        [Description("关联的程序块ID（冗余字段，加速查询）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? BlockId {get;set;}
        
          /// <summary>
        ///  关联的项目ID（冗余字段，加速查询）
        /// </summary>
        [Description("关联的项目ID（冗余字段，加速查询）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ProjectId {get;set;}
        
          /// <summary>
        ///  变量全路径名（如：DB745.Control.FwdAuto）
        /// </summary>
        [Description("变量全路径名（如：DB745.Control.FwdAuto）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(300)]
                  public string VariableName {get;set;}
        
          /// <summary>
        ///  访问类型（READ:读取, WRITE:写入, READWRITE:读写）
        /// </summary>
        [Description("访问类型（READ:读取, WRITE:写入, READWRITE:读写）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(10)]
                  public string AccessType {get;set;}
        
          /// <summary>
        ///  变量在原始XML中的UID标识
        /// </summary>
        [Description("变量在原始XML中的UID标识")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string Uid {get;set;}
        
          /// <summary>
        ///  变量在访问处的特定注释
        /// </summary>
        [Description("变量在访问处的特定注释")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Comment {get;set;}
        
          /// <summary>
        ///  变量作用域来源（Global, Local, Constant）
        /// </summary>
        [Description("变量作用域来源（Global, Local, Constant）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string VariableScope {get;set;}
        
          /// <summary>
        ///  是否为物理I/O点（True/False）
        /// </summary>
        [Description("是否为物理I/O点（True/False）")]
                 public bool? IsIOTag {get;set;}
        
          /// <summary>
        ///  是否为关键步序变量（用于流程诊断）
        /// </summary>
        [Description("是否为关键步序变量（用于流程诊断）")]
                 public bool? IsStepVariable {get;set;}
        
          /// <summary>
        ///  是否为报警变量（用于故障识别）
        /// </summary>
        [Description("是否为报警变量（用于故障识别）")]
                 public bool? IsAlarmVariable {get;set;}
        
  

    }
}
