

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:13
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 块调用关系表
    /// </summary>
    public class PLC_BlockCallInsertDTO
    {
            /// <summary>
        ///  关联的项目ID（外键）
        /// </summary>
        [Description("关联的项目ID（外键）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? ProjectId {get;set;}
        
          /// <summary>
        ///  发起调用的程序块ID（调用者）
        /// </summary>
        [Description("发起调用的程序块ID（调用者）")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? CallerBlockId {get;set;}
        
          /// <summary>
        ///  调用者块名称（冗余字段）
        /// </summary>
        [Description("调用者块名称（冗余字段）")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string CallerBlockName {get;set;}
        
          /// <summary>
        ///  被调用的程序块名称（被调用者）
        /// </summary>
        [Description("被调用的程序块名称（被调用者）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string CalleeBlockName {get;set;}
        
          /// <summary>
        ///  被调用块的类型（如：FB, FC）
        /// </summary>
        [Description("被调用块的类型（如：FB, FC）")]
                  [MinLength(0)]
                   [MaxLength(10)]
                  public string CalleeBlockType {get;set;}
        
          /// <summary>
        ///  发生调用的逻辑单元ID
        /// </summary>
        [Description("发生调用的逻辑单元ID")]
                  [Range(0, long.MaxValue)]
                  public long? LogicUnitId {get;set;}
        
          /// <summary>
        ///  发生调用的Network编号
        /// </summary>
        [Description("发生调用的Network编号")]
                  [Range(0, int.MaxValue)]
                  public int? NetworkId {get;set;}
        
    
     

    }
}
