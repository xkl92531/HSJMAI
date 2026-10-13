

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:57
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class SysRoleConditionDTO
    {
            /// <summary>
        ///  名称
        /// </summary>
        public string Name {get;set;}

        /// <summary>
        ///  名称
        /// </summary>
        public long? RoleId { get; set; }

        /// <summary>
        ///  用户组（预留）
        /// </summary>
        public long? GroupID {get;set;}
        
      

    }
}
