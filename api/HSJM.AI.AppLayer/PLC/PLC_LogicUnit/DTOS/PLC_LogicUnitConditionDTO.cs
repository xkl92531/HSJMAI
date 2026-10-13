

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:38
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 逻辑单元查询条件
    /// </summary>
    public class PLC_LogicUnitConditionDTO
    {
        /// <summary>
        /// 程序块ID
        /// </summary>
        public long? BlockId { get; set; }
        
        /// <summary>
        /// 网络ID
        /// </summary>
        public int? NetworkId { get; set; }
        
        /// <summary>
        /// 标题（模糊查询）
        /// </summary>
        public string Title { get; set; }
    }
}
