

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:03
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 程序块查询条件
    /// </summary>
    public class PLC_BlockConditionDTO
    {
        /// <summary>
        /// 项目ID
        /// </summary>
        public long? ProjectId { get; set; }
        
        /// <summary>
        /// 块名称（模糊查询）
        /// </summary>
        public string BlockName { get; set; }
        
        /// <summary>
        /// 块类型
        /// </summary>
        public string BlockType { get; set; }
        
        /// <summary>
        /// 编程语言
        /// </summary>
        public string ProgrammingLanguage { get; set; }
    }
}
