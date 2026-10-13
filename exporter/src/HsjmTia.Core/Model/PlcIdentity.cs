namespace HsjmTia.Core.Model
{
    /// <summary>
    /// PLC 身份。名字可能是工程师没改过的 TIA 默认值（PLC_1/PLC_2），
    /// 因此**不得**用作判断依据；真正的身份是 Ip。
    /// </summary>
    public class PlcIdentity
    {
        public string Name { get; set; }
        public string Ip { get; set; }
        public string OrderNumber { get; set; }

        /// <summary>该 CPU 在工程内的枚举序号（1 基），仅在 Ip 缺失时用于目录消歧。</summary>
        public int DeviceIndex { get; set; }
    }
}
