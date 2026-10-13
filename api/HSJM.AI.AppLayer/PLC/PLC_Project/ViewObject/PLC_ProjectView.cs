
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:43
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.ViewObject
{
    /// <summary>
    /// PLC项目主表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class PLC_ProjectView : ViewBaseField
    {
        /// <summary>
        ///  主键ID（雪花算法生成）
        /// </summary>
        [ImporterHeader(Name = "主键ID（雪花算法生成）", Format = ""), ExporterHeader("主键ID（雪花算法生成）")]
        public long Id { get; set; }

        /// <summary>
        ///  项目名称（如：OP70工位控制系统）
        /// </summary>
        [ImporterHeader(Name = "项目名称（如：OP70工位控制系统）", Format = ""), ExporterHeader("项目名称（如：OP70工位控制系统）")]
        public string ProjectName { get; set; }

        /// <summary>
        ///  PLC品牌（如：Siemens, Beckhoff, Inovance）
        /// </summary>
        [ImporterHeader(Name = "PLC品牌（如：Siemens, Beckhoff, Inovance）", Format = ""), ExporterHeader("PLC品牌（如：Siemens, Beckhoff, Inovance）")]
        public string Brand { get; set; }

        /// <summary>
        ///  开发软件产品（如：TIA Portal, TwinCAT）
        /// </summary>
        [ImporterHeader(Name = "开发软件产品（如：TIA Portal, TwinCAT）", Format = ""), ExporterHeader("开发软件产品（如：TIA Portal, TwinCAT）")]
        public string Product { get; set; }

        /// <summary>
        ///  软件版本号（如：V18, V3.1）
        /// </summary>
        [ImporterHeader(Name = "软件版本号（如：V18, V3.1）", Format = ""), ExporterHeader("软件版本号（如：V18, V3.1）")]
        public string ProductVersion { get; set; }

        /// <summary>
        ///  对应工位ID（如：OP70，用于与IoT数据关联）
        /// </summary>
        [ImporterHeader(Name = "对应工位ID（如：OP70，用于与IoT数据关联）", Format = ""), ExporterHeader("对应工位ID（如：OP70，用于与IoT数据关联）")]
        public string StationId { get; set; }

        /// <summary>
        ///  项目描述文本
        /// </summary>
        [ImporterHeader(Name = "项目描述文本", Format = ""), ExporterHeader("项目描述文本")]
        public string Description { get; set; }

        /// <summary>
        ///  解析配置JSON（存储特定的解析规则或参数）
        /// </summary>
        [ImporterHeader(Name = "解析配置JSON（存储特定的解析规则或参数）", Format = ""), ExporterHeader("解析配置JSON（存储特定的解析规则或参数）")]
        public string ParseConfig { get; set; }

        /// <summary>
        ///  数据采集接口IP地址
        /// </summary>
        [ImporterHeader(Name = "数据采集接口IP地址", Format = ""), ExporterHeader("数据采集接口IP地址")]
        public string IotUrl { get; set; }

    }
}
