

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2025/3/28 16:07:46
using System;
using System.Collections.Generic;
namespace MAF.DEMO.AppLayer.Basic.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class SystemParameterConditionDTO
    {
            /// <summary>
        ///  参数代码
        /// </summary>
        public string ParamCode {get;set;}
        
           /// <summary>
        ///  参数名称
        /// </summary>
        public string ParamName {get;set;}
        
           /// <summary>
        ///  参数值
        /// </summary>
        public string ParamValue {get;set;}
        
           /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        public int? Status {get;set;}
        
      

    }
}
