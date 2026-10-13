

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 16:36:10
using System;
using System.Collections.Generic;
namespace MAF.DEMO.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class SysMenuConditionDTO
    {
            /// <summary>
        ///  菜单名称
        /// </summary>
        public string MenuName {get;set;}
        
           /// <summary>
        ///  系统ID
        /// </summary>
        public long? SystemID {get;set;}
        
           /// <summary>
        ///  系统名称
        /// </summary>
        public string SystemName {get;set;}
        
           /// <summary>
        ///  父级菜单ID
        /// </summary>
        public long? ParentID {get;set;}
        
           /// <summary>
        ///  父级菜单名称
        /// </summary>
        public string ParentName {get;set;}
        
           /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        public int? Status {get;set;}
        
      

    }
}
