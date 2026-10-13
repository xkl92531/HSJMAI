

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/03/05 13:43:57
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class OUConditionDTO
    {
            /// <summary>
        ///  顶级ID
        /// </summary>
        public long? FirstID {get;set;}
        
           /// <summary>
        ///  父级ID
        /// </summary>
        public long? ParentID {get;set;}
        
           /// <summary>
        ///  组织名称
        /// </summary>
        public string Name {get;set;}
        
           /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        public int? Status {get;set;}
        
      

    }
}
