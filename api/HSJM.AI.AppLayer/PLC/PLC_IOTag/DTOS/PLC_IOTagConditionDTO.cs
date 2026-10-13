

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:32
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// IO标签查询条件
    /// </summary>
    public class PLC_IOTagConditionDTO
    {
        /// <summary>
        /// 项目ID
        /// </summary>
        public long? ProjectId { get; set; }
        
        /// <summary>
        /// 标签名（模糊查询）
        /// </summary>
        public string TagName { get; set; }
        
        /// <summary>
        /// 地址（模糊查询）
        /// </summary>
        public string Address { get; set; }
        
        /// <summary>
        /// 数据类型
        /// </summary>
        public string DataType { get; set; }
        
        /// <summary>
        /// 表名
        /// </summary>
        public string TableName { get; set; }
        
        /// <summary>
        /// 注释（模糊查询）
        /// </summary>
        public string Comment { get; set; }

        public string IoMapping { set; get; }
    }
}

