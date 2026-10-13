

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:13
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class PLC_BlockCallConditionDTO
    {
       public long? CallerBlockId { get; set; }
       public string CalleeBlockName { get; set; }
    }
}
