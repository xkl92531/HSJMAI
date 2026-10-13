using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 变量引用位置信息
    /// 描述变量在哪里被写入或读取
    /// </summary>
    public class ReferenceLocation
    {
        /// <summary>
        /// 逻辑单元 ID
        /// </summary>
        [JsonPropertyName("logicUnitId")]
        public long? LogicUnitId { get; set; }

        /// <summary>
        /// 所属块名称
        /// </summary>
        [JsonPropertyName("blockName")]
        public string BlockName { get; set; }

        /// <summary>
        /// 所属步骤名称
        /// </summary>
        [JsonPropertyName("stepName")]
        public string StepName { get; set; }

        /// <summary>
        /// 位置描述
        /// 格式: "步骤名/块名/逻辑单元名"
        /// 例如: "初始化步骤/伺服驱动块/位置反馈处理"
        /// </summary>
        [JsonPropertyName("location")]
        public string Location { get; set; }

        /// <summary>
        /// 详细描述（AI 生成）
        /// 例如: "由传感器 I_PositionSensor[15] 的信号赋值"
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// 逻辑代码片段（可选）
        /// 例如: "WorkOK[3] := I_PositionSensor[15] AND NOT I_ErrorFlag[3]"
        /// </summary>
        [JsonPropertyName("logicSnippet")]
        public string LogicSnippet { get; set; }

        /// <summary>
        /// 影响程度（仅用于读取位置）
        /// 例如: "CRITICAL - 直接导致启动失败"
        /// </summary>
        [JsonPropertyName("impact")]
        public string Impact { get; set; }

        /// <summary>
        /// 上下文信息（补充说明）
        /// 例如: "常开触点串联于归零完成条件的首端"
        /// </summary>
        [JsonPropertyName("context")]
        public string Context { get; set; }
    }
}
