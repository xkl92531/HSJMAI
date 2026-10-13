

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:26
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Prompt.DTOS
{
    /// <summary>
    /// Prompt版本管理表
    /// </summary>
    public class Prompt_VersionUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  关联的角色ID
        /// </summary>
        [Description("关联的角色ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? RoleID {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
        [Description("版本号")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string VersionNumber {get;set;}
        
          /// <summary>
        ///  Prompt内容
        /// </summary>
        [Description("Prompt内容")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(-1)]
                  public string SystemPromptContent {get;set;}
        
          /// <summary>
        ///  变更说明
        /// </summary>
        [Description("变更说明")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ChangeLog {get;set;}
        
          /// <summary>
        ///  是否当前激活版本
        /// </summary>
        [Description("是否当前激活版本")]
                  [Required]
                  public bool? IsActive {get;set;}
        
          /// <summary>
        ///  是否参与A/B测试
        /// </summary>
        [Description("是否参与A/B测试")]
                  [Required]
                  public bool? IsABTest {get;set;}
        
          /// <summary>
        ///  流量分配比例
        /// </summary>
        [Description("流量分配比例")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? TrafficRatio {get;set;}
        
          /// <summary>
        ///  该版本的平均评分
        /// </summary>
        [Description("该版本的平均评分")]
                  [Required]
                   [Range(0, double.MaxValue)]
                  public decimal? AverageScore {get;set;}
        
          /// <summary>
        ///  使用次数
        /// </summary>
        [Description("使用次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? UsageCount {get;set;}
        
     

    }
}
