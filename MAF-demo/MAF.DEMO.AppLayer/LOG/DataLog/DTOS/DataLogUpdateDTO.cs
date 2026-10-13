

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/31 9:31:07
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.LOG.DTOS
{
    /// <summary>
    /// 操作日志
    /// </summary>
    public class DataLogUpdateDTO
    {
            /// <summary>
        ///  ID
        /// </summary>
        [Description("ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long Id {get;set;}
        
          /// <summary>
        ///  操作类型
        /// </summary>
        [Description("操作类型")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string DiffType {get;set;}
        
          /// <summary>
        ///  表名称
        /// </summary>
        [Description("表名称")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string TableName {get;set;}
        
          /// <summary>
        ///  表描述
        /// </summary>
        [Description("表描述")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string TableDesc {get;set;}
        
          /// <summary>
        ///  操作内容
        /// </summary>
        [Description("操作内容")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string LogContent {get;set;}
        
     

    }
}
