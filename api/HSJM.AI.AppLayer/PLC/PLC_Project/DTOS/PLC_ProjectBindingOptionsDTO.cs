using System.ComponentModel;

namespace HSJM.AI.AppLayer.PLC.DTOS
{
    /// <summary>
    /// 产线下拉选项（数据源：基础资料表 Basic_ProductionLine）
    /// </summary>
    public class PLC_ProductionLineOptionDTO
    {
        [Description("产线ID（ai_plc_productionline.id）")]
        public long Id { get; set; }

        [Description("产线编码")]
        public string Code { get; set; }

        [Description("产线名称")]
        public string Name { get; set; }
    }

    /// <summary>
    /// PLC 设备下拉选项（数据源：基础资料表 Basic_Device）
    /// </summary>
    public class PLC_PlcDeviceOptionDTO
    {
        [Description("设备ID（ai_plc_device.id）")]
        public long Id { get; set; }

        [Description("设备名称（如 PLC1/PLC2）")]
        public string DeviceName { get; set; }

        [Description("设备IP地址（选中后回填项目 PlcIp）")]
        public string IpAddress { get; set; }

        [Description("设备类型")]
        public string DeviceType { get; set; }

        [Description("所属产线ID（ai_plc_device.lineid，按产线过滤下拉用）")]
        public long? LineId { get; set; }

        [Description("设备对应工程名")]
        public string ProjectName { get; set; }
    }
}
