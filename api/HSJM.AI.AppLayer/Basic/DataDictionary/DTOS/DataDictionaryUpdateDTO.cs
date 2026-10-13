

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:03:23
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Basic.DTOS
{
    /// <summary>
    /// 数据字典
    /// </summary>
    public class DataDictionaryUpdateDTO
    {
            /// <summary>
        ///  ID
        /// </summary>
        [Description("ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  数据代码
        /// </summary>
        [Description("数据代码")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Code {get;set;}
        
          /// <summary>
        ///  数据名称
        /// </summary>
        [Description("数据名称")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Name {get;set;}
        
          /// <summary>
        ///  父级代码
        /// </summary>
        [Description("父级代码")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ParentCode {get;set;}
        
          /// <summary>
        ///  父级名称
        /// </summary>
        [Description("父级名称")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string ParentName {get;set;}
        
          /// <summary>
        ///  简称
        /// </summary>
        [Description("简称")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string ShorterForm {get;set;}
        
          /// <summary>
        ///  排序
        /// </summary>
        [Description("排序")]
                  [Range(0, int.MaxValue)]
                  public int? Sort {get;set;}
        
          /// <summary>
        ///  备注
        /// </summary>
        [Description("备注")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Remarks {get;set;}
        
          /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [Description("状态（正常_1、停用_2）")]
                  [Range(0, int.MaxValue)]
                  public int? Status {get;set;}
        
     

    }
}
