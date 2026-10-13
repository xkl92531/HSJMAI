

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 11:10:31
using System;
using System.Collections.Generic;
namespace MAF.DEMO.AppLayer.LOG.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class ExceptionConditionDTO
    {
            /// <summary>
        ///  名称
        /// </summary>
        public string Name {get;set;}
        
           /// <summary>
        ///  类名称
        /// </summary>
        public string ClassName {get;set;}
        
           /// <summary>
        ///  方法名称
        /// </summary>
        public string MethodName {get;set;}
        
           /// <summary>
        ///  异常名称
        /// </summary>
        public string ExceptionName {get;set;}
        
           /// <summary>
        ///  异常信息
        /// </summary>
        public string ExceptionMsg {get;set;}
        
           /// <summary>
        ///  异常源
        /// </summary>
        public string ExceptionSource {get;set;}
        
           /// <summary>
        ///  堆栈信息
        /// </summary>
        public string StackTrace {get;set;}
        
      

    }
}
