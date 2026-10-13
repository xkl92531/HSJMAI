
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:43
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// PLC项目主表
    /// </summary>
    [SugarTable("AI_PLC_Project_new",tableDescription:"PLC项目主表")]
    public class PLC_ProjectEntity: BaseField
    {

       public  PLC_ProjectEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  项目名称（如：OP70工位控制系统）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="项目名称（如：OP70工位控制系统）")]
        public string ProjectName {get;set;}
        
          /// <summary>
        ///  PLC品牌（如：Siemens, Beckhoff, Inovance）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="PLC品牌（如：Siemens, Beckhoff, Inovance）")]
        public string Brand {get;set;}
        
          /// <summary>
        ///  开发软件产品（如：TIA Portal, TwinCAT）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="开发软件产品（如：TIA Portal, TwinCAT）")]
        public string Product {get;set;}
        
          /// <summary>
        ///  软件版本号（如：V18, V3.1）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="软件版本号（如：V18, V3.1）")]
        public string ProductVersion {get;set;}
        
          /// <summary>
        ///  对应工位ID（如：OP70，用于与IoT数据关联）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="对应工位ID（如：OP70，用于与IoT数据关联）")]
        public string StationId {get;set;}
        
          /// <summary>
        ///  项目描述文本
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="项目描述文本")]
        public string Description {get;set;}
        
          /// <summary>
        ///  解析配置JSON（存储特定的解析规则或参数）
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="解析配置JSON（存储特定的解析规则或参数）")]
        public string ParseConfig {get;set;}


        /// <summary>
        ///  数据采集接口IP地址
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "数据采集接口IP地址")]
        public string IotUrl { get; set; }


        /// <summary>
        /// PLC 直连 IP（IGW 批量注册用：项目通道下 S7/S7Plus/GRAPH 设备的 endpoint 与逻辑设备名 S1500_{ip} 等由它生成；老网关不需要。为空时 IGW 注册判定为配置缺失）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "PLC直连IP（新IGW注册用）")]
        public string PlcIp { get; set; }

        /// <summary>
        /// 所属 PLC 编码（对齐工位基础信息 Basic_SubStation.PlcName，如 PLC1/PLC2）。
        /// 工站归属推断按「本 PLC 的工位白名单」过滤，防止跨 PLC 通信变量把块染到别的 PLC 的工位（2026-08-26）。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "所属PLC编码（PLC1/PLC2，对齐工位基础信息）")]
        public string PlcCode { get; set; }

        /// <summary>
        /// PLC 直连端口（新 IGW 注册用；null/0 = 网关按协议默认，S7=102）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "PLC直连端口（新IGW注册用，0=协议默认）")]
        public int? PlcPort { get; set; }

        /// <summary>
        /// 绑定产线ID（基础资料 Basic_ProductionLine.Id；项目新建/编辑时下拉选择）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定产线ID（ai_plc_productionline.id）")]
        public long? ProductionLineId { get; set; }

        /// <summary>
        /// 绑定产线名称（冗余自 Basic_ProductionLine.Name/Code，列表展示用）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定产线名称（冗余自基础表）")]
        public string ProductionLineName { get; set; }

        /// <summary>
        /// 绑定PLC设备ID（基础资料 Basic_Device.Id；选择后 PlcIp/PlcCode 自动取设备 IpAddress/DeviceName）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定PLC设备ID（ai_plc_device.id）")]
        public long? PlcDeviceId { get; set; }

        /// <summary>
        /// 绑定PLC设备名称（冗余自 Basic_Device.DeviceName，列表展示用）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "绑定PLC设备名称（冗余自基础表）")]
        public string PlcDeviceName { get; set; }

        // IGW 注册通道名（IgwTagNaming.ChannelName 首次注册时算出后写回，此后以列为准，项目/产线改名不再导致
        // 通道名漂移）物理列 ai_plc_project_new.igwchannelname 由迁移脚本/启动初始化器补出，但**故意不在本实体
        // 里映射**（task-5 fix round 1 controller 决策）：本实体的 Queryable/Insertable/Updateable 在多处直接用
        // `PLC_ProjectEntity` 整条更新（如编辑项目基本信息），一旦这里挂了该属性，任何一次遗漏赋值的整体 Update
        // 都会把 igwchannelname 覆盖成 NULL。读写改走 IPLC_IgwRegistrationService.GetChannelNameAsync /
        // SetChannelNameAsync（裸表名 ai_plc_project_new + 裸列名 igwchannelname 的参数化原生 SQL，与本实体解耦）。

    }


}
