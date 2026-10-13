

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/03/05 13:43:57
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 组织架构
    /// </summary>
    public class OUInsertDTO
    {
            /// <summary>
        ///  顶级ID
        /// </summary>
        [Description("顶级ID")]
                  [Range(0, long.MaxValue)]
                  public long? CompanyID { get;set;}
        
          /// <summary>
        ///  父级ID
        /// </summary>
        [Description("父级ID")]
                  [Range(0, long.MaxValue)]
                  public long? ParentID {get;set;}
        
          /// <summary>
        ///  组织名称
        /// </summary>
        [Description("组织名称")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Name {get;set;}
        
          /// <summary>
        ///  组织类型（1,公司，2.部门）
        /// </summary>
        [Description("组织类型（1,公司，2.部门）")]
                  [Range(0, int.MaxValue)]
                  public int? Type {get;set;}
        
          /// <summary>
        ///  层级数
        /// </summary>
        [Description("层级数")]
                  [Range(0, int.MaxValue)]
                  public int? LevelCode {get;set;}
        
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
