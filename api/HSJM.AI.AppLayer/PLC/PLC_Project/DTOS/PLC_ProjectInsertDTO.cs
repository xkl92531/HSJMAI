

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:43
using SqlSugar;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// PLC项目主表
    /// </summary>
    public class PLC_ProjectInsertDTO
    {
            /// <summary>
        ///  项目名称（如：OP70工位控制系统）
        /// </summary>
        [Description("项目名称（如：OP70工位控制系统）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(200)]
                  public string ProjectName {get;set;}
        
          /// <summary>
        ///  PLC品牌（如：Siemens, Beckhoff, Inovance）
        /// </summary>
        [Description("PLC品牌（如：Siemens, Beckhoff, Inovance）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string Brand {get;set;}
        
          /// <summary>
        ///  开发软件产品（如：TIA Portal, TwinCAT）
        /// </summary>
        [Description("开发软件产品（如：TIA Portal, TwinCAT）")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string Product {get;set;}
        
          /// <summary>
        ///  软件版本号（如：V18, V3.1）
        /// </summary>
        [Description("软件版本号（如：V18, V3.1）")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ProductVersion {get;set;}
        
          /// <summary>
        ///  对应工位ID（如：OP70，用于与IoT数据关联）
        /// </summary>
        [Description("对应工位ID（如：OP70，用于与IoT数据关联）")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string StationId {get;set;}
        
          /// <summary>
        ///  项目描述文本
        /// </summary>
        [Description("项目描述文本")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  解析配置JSON（存储特定的解析规则或参数）
        /// </summary>
        [Description("解析配置JSON（存储特定的解析规则或参数）")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ParseConfig {get;set;}

        /// <summary>
        ///  数据采集接口IP地址
        /// </summary>
        [Description("数据采集接口IP地址")]
        [MinLength(0)]
        [MaxLength(500)]
        public string IotUrl { get; set; }

        /// <summary>
        ///  PLC 直连 IP（IGW 批量注册用：生成项目通道下设备 endpoint / 逻辑设备名；绑定 PLC 设备时服务端按设备 IpAddress 自动回填）
        /// </summary>
        [Description("PLC直连IP（新IGW注册用）")]
        [MinLength(0)]
        [MaxLength(50)]
        public string PlcIp { get; set; }

        /// <summary>
        ///  PLC 直连端口（新 IGW 注册用；null/0 = 网关按协议默认，S7=102）
        /// </summary>
        [Description("PLC直连端口（新IGW注册用，0=协议默认）")]
        [Range(0, 65535)]
        public int? PlcPort { get; set; }

        /// <summary>
        ///  绑定产线ID（基础资料 Basic_ProductionLine.Id，下拉选择）
        /// </summary>
        [Description("绑定产线ID（ai_plc_productionline.id）")]
        public long? ProductionLineId { get; set; }

        /// <summary>
        ///  绑定PLC设备ID（基础资料 Basic_Device.Id，下拉选择；选择后服务端自动回填 PlcIp/PlcCode）
        /// </summary>
        [Description("绑定PLC设备ID（ai_plc_device.id）")]
        public long? PlcDeviceId { get; set; }

    }
}
