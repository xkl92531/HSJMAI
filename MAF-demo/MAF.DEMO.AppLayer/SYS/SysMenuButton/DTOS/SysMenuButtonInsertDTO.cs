

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:28
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 菜单按钮
    /// </summary>
    public class SysMenuButtonInsertDTO
    {
            /// <summary>
        ///  菜单ID
        /// </summary>
        [Description("菜单ID")]
                  [Range(0, long.MaxValue)]
                  public long? MenuID {get;set;}
        
          /// <summary>
        ///  按钮名称
        /// </summary>
        [Description("按钮名称")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Name {get;set;}
        
          /// <summary>
        ///  按钮代码
        /// </summary>
        [Description("按钮代码")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string Code {get;set;}
        
          /// <summary>
        ///  排序
        /// </summary>
        [Description("排序")]
                  [Range(0, int.MaxValue)]
                  public int? Sort {get;set;}
        
     

    }
}
