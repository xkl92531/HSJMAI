

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/28 16:07:46
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Basic.DTOS
{
    /// <summary>
    /// 系统参数
    /// </summary>
    public class SystemParameterInsertDTO
    {
            /// <summary>
        ///  参数代码
        /// </summary>
        [Description("参数代码")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string ParamCode {get;set;}
        
          /// <summary>
        ///  参数名称
        /// </summary>
        [Description("参数名称")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string ParamName {get;set;}
        
          /// <summary>
        ///  参数值
        /// </summary>
        [Description("参数值")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ParamValue {get;set;}
        
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
