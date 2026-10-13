

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:57
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 角色表
    /// </summary>
    public class SysRoleUpdateDTO
    {
            /// <summary>
        ///  ID
        /// </summary>
        [Description("ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  名称
        /// </summary>
        [Description("名称")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Name {get;set;}
        
          /// <summary>
        ///  用户组（预留）
        /// </summary>
        [Description("用户组（预留）")]
                  [Range(0, long.MaxValue)]
                  public long? GroupID {get;set;}
        
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
