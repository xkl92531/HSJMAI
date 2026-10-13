using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 根本原因信息
    /// AI 追溯到的最底层输入信号或物理传感器
    /// </summary>
    public class RootCauseInfo
    {
        /// <summary>
        /// 根本原因变量名称
        /// 例如: "I_PositionSensor[15]"
        /// </summary>
        [JsonPropertyName("variable")]
        public string Variable { get; set; }

        /// <summary>
        /// 当前实时值
        /// </summary>
        [JsonPropertyName("currentValue")]
        public string CurrentValue { get; set; }

        /// <summary>
        /// 通俗易懂的描述
        /// 例如: "3号伺服电机位置传感器未触发，电机可能未到位"
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// 建议措施
        /// 例如: "检查3号电机位置传感器的物理连接"
        /// </summary>
        [JsonPropertyName("suggestedAction")]
        public string SuggestedAction { get; set; }
    }
}
