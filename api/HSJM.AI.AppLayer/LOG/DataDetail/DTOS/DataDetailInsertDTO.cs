

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/31 9:31:17
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.LOG.DTOS
{
    /// <summary>
    /// 操作数据明细
    /// </summary>
    public class DataDetailInsertDTO
    {
            /// <summary>
        ///  操作日志ID
        /// </summary>
        [Description("操作日志ID")]
                  [Range(0, long.MaxValue)]
                  public long? DataLogID {get;set;}
        
          /// <summary>
        ///  列描述
        /// </summary>
        [Description("列描述")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ColumnDesc {get;set;}
        
          /// <summary>
        ///  列字段
        /// </summary>
        [Description("列字段")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ColumnField {get;set;}
        
          /// <summary>
        ///  原值
        /// </summary>
        [Description("原值")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OldValue {get;set;}
        
          /// <summary>
        ///  新值
        /// </summary>
        [Description("新值")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string NewValue {get;set;}
        
     

    }
}
