

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/3/7 11:29:06
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.Basic.DTOS
{
    /// <summary>
    /// 用户表单列配置
    /// </summary>
    public class CustomFormConfigInsertDTO
    {
            /// <summary>
        ///  表单Code
        /// </summary>
        [Description("表单Code")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string FormCode {get;set;}
        
          /// <summary>
        ///  用户ID
        /// </summary>
        [Description("用户ID")]
                  [Range(0, long.MaxValue)]
                  public long? UserId {get;set;}
        
          /// <summary>
        ///  表单Code
        /// </summary>
        [Description("表单Code")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string FormColumnJson {get;set;}
        
          /// <summary>
        ///  预留1
        /// </summary>
        [Description("预留1")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Attr1 {get;set;}
        
          /// <summary>
        ///  预留2
        /// </summary>
        [Description("预留2")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Attr2 {get;set;}
        
          /// <summary>
        ///  预留3
        /// </summary>
        [Description("预留3")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Attr3 {get;set;}
        
          /// <summary>
        ///  预留4
        /// </summary>
        [Description("预留4")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string Attr4 {get;set;}
        
     

    }
}
