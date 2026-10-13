

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:28
using System;
using System.Collections.Generic;
namespace MAF.DEMO.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class SysMenuButtonConditionDTO
    {
            /// <summary>
        ///  菜单ID
        /// </summary>
        public long? MenuID {get;set;}
        
           /// <summary>
        ///  按钮名称
        /// </summary>
        public string Name {get;set;}
        
           /// <summary>
        ///  按钮代码
        /// </summary>
        public string Code {get;set;}
        
      

    }
}
