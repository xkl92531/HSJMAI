
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 读取PLC参数返回结果实体
    /// </summary>
    public class PLC_ReadTagResult
    {
        /// <summary>
        /// 是否正常
        /// </summary>
        public bool IsGood { get; set; }
        /// <summary>
        /// 值
        /// </summary>
        public Object Value { get; set; }
        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; }
        /// <summary>
        /// 标签名称
        /// </summary>
        public string TagName { get; set; }
        /// <summary>
        /// 标签名称
        /// </summary>
        public string OldTagName { get; set; }
    }
    /// <summary>
    /// PLC读取标签请求参数
    /// </summary>
    public class PLC_ReadTagParame
    {
        /// <summary>
        /// 设备名称
        /// </summary>
        public string DeviceName { get; set; } 
        /// <summary>
        /// 标签列表
        /// </summary>
        public List<string> TagList { get; set; }
    }
}
