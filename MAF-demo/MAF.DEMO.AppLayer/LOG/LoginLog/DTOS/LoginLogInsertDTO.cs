

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:05:46
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.LOG.DTOS
{
    /// <summary>
    /// 用户登录日志
    /// </summary>
    public class LoginLogInsertDTO
    {
            /// <summary>
        ///  用户ID
        /// </summary>
        [Description("用户ID")]
                  [Range(0, long.MaxValue)]
                  public long? UserID {get;set;}
        
          /// <summary>
        ///  用户名
        /// </summary>
        [Description("用户名")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string Account {get;set;}
        
          /// <summary>
        ///  IP地址
        /// </summary>
        [Description("IP地址")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string LoginIP {get;set;}
        
          /// <summary>
        ///  登录类型（浏览器，手机端）
        /// </summary>
        [Description("登录类型（浏览器，手机端）")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string LoginType {get;set;}
        
          /// <summary>
        ///  操作系统
        /// </summary>
        [Description("操作系统")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string OSVersion {get;set;}
        
          /// <summary>
        ///  登录时间
        /// </summary>
        [Description("登录时间")]
                 public DateTime? LoginDate {get;set;}
        
          /// <summary>
        ///  地点（省份，城市）
        /// </summary>
        [Description("地点（省份，城市）")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Location {get;set;}
        
          /// <summary>
        ///  地理信息
        /// </summary>
        [Description("地理信息")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Address {get;set;}
        
     

    }
}
