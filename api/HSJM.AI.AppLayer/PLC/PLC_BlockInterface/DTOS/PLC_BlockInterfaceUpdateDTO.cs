

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:22
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 程序块接口变量表
    /// </summary>
    public class PLC_BlockInterfaceUpdateDTO
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
        ///  变量名称（如：General）
        /// </summary>
        [Description("变量名称（如：General）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string VariableName {get;set;}
        
          /// <summary>
        ///  变量完整路径（如：Static.General.StepNum）
        /// </summary>
        [Description("变量完整路径（如：Static.General.StepNum）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(300)]
                  public string VariablePath {get;set;}
        
          /// <summary>
        ///  数据类型（如：Int, Bool, UDT_General）
        /// </summary>
        [Description("数据类型（如：Int, Bool, UDT_General）")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string DataType {get;set;}
        
          /// <summary>
        ///  变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）
        /// </summary>
        [Description("变量作用域（如：IN, OUT, INOUT, STAT, TEMP, CONSTANT）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string Scope {get;set;}
        
          /// <summary>
        ///  变量注释
        /// </summary>
        [Description("变量注释")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Comment {get;set;}
        
          /// <summary>
        ///  变量初始值
        /// </summary>
        [Description("变量初始值")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string InitialValue {get;set;}
        
          /// <summary>
        ///  保持性设置（如：Retain, NonRetain）
        /// </summary>
        [Description("保持性设置（如：Retain, NonRetain）")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string Remanence {get;set;}
        
          /// <summary>
        ///  父级变量ID（用于表示结构体/UDT的层级关系）
        /// </summary>
        [Description("父级变量ID（用于表示结构体/UDT的层级关系）")]
                  [Range(0, long.MaxValue)]
                  public long? ParentId {get;set;}
        
          /// <summary>
        ///  嵌套层级深度（0表示顶层）
        /// </summary>
        [Description("嵌套层级深度（0表示顶层）")]
                  [Range(0, int.MaxValue)]
                  public int? NestingLevel {get;set;}
        
          /// <summary>
        ///  数组维度信息（如：[1..10, 0..5]）
        /// </summary>
        [Description("数组维度信息（如：[1..10, 0..5]）")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string ArrayInfo {get;set;}
        
   
     

    }
}
