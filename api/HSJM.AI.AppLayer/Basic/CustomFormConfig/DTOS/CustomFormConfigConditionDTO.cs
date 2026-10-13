

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/3/7 11:29:06
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Basic.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class CustomFormConfigConditionDTO
    {
            /// <summary>
        ///  表单Code
        /// </summary>
        public string FormCode {get;set;}
        
           /// <summary>
        ///  预留1
        /// </summary>
        public string Attr1 {get;set;}
        
           /// <summary>
        ///  预留2
        /// </summary>
        public string Attr2 {get;set;}
        
           /// <summary>
        ///  用户ID
        /// </summary>
        public long? UserId {get;set;}
        
           /// <summary>
        ///  预留3
        /// </summary>
        public string Attr3 {get;set;}
        
      

    }
}
