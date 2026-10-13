

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 11:10:31
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace MAF.DEMO.AppLayer.LOG.DTOS
{
    /// <summary>
    /// 异常日志
    /// </summary>
    public class ExceptionInsertDTO
    {
            /// <summary>
        ///  名称
        /// </summary>
        [Description("名称")]
                  [MinLength(0)]
                   [MaxLength(2000)]
                  public string Name {get;set;}
        
          /// <summary>
        ///  类名称
        /// </summary>
        [Description("类名称")]
                  [MinLength(0)]
                   [MaxLength(2000)]
                  public string ClassName {get;set;}
        
          /// <summary>
        ///  方法名称
        /// </summary>
        [Description("方法名称")]
                  [MinLength(0)]
                   [MaxLength(2000)]
                  public string MethodName {get;set;}
        
          /// <summary>
        ///  异常名称
        /// </summary>
        [Description("异常名称")]
                  [MinLength(0)]
                   [MaxLength(2000)]
                  public string ExceptionName {get;set;}
        
          /// <summary>
        ///  异常信息
        /// </summary>
        [Description("异常信息")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ExceptionMsg {get;set;}
        
          /// <summary>
        ///  异常源
        /// </summary>
        [Description("异常源")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ExceptionSource {get;set;}
        
          /// <summary>
        ///  堆栈信息
        /// </summary>
        [Description("堆栈信息")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string StackTrace {get;set;}
        
          /// <summary>
        ///  参数对象
        /// </summary>
        [Description("参数对象")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ParamsObj {get;set;}
        
          /// <summary>
        ///  异常时间
        /// </summary>
        [Description("异常时间")]
                 public DateTime? ExceptionTime {get;set;}
        
          /// <summary>
        ///  操作人
        /// </summary>
        [Description("操作人")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Account {get;set;}
        
     

    }
}
