

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:57:03
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class SysSystemConditionDTO
    {
            /// <summary>
        ///  系统名称
        /// </summary>
        public string Name {get;set;}
        
           /// <summary>
        ///  状态（正常_1、停用_2）集合
        /// </summary>
        public int? Status {get;set;}
        
      

    }
}
