

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:03:23
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Basic.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class DataDictionaryConditionDTO
    {
            /// <summary>
        ///  数据代码
        /// </summary>
        public string Code {get;set;}
        
           /// <summary>
        ///  数据名称
        /// </summary>
        public string Name {get;set;}
        
           /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        public int? Status {get;set;}

        /// <summary>
        /// 父级代码
        /// </summary>
        public string ParentCode { get;set;}

        /// <summary>
        /// 关键字搜索
        /// </summary>
        public string Keyword { get; set; }
        
         /// <summary>
        /// 关键字搜索
        /// </summary>
        public string TreeKeyword { get; set; }

    }
}
