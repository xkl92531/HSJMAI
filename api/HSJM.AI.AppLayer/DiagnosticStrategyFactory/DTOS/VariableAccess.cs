using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 变量访问信息
    /// 描述变量在某个逻辑单元中的访问方式（读取/写入）
    /// </summary>
    public class VariableAccess
    {
        /// <summary>
        /// 访问类型（Read/Write/ReadWrite）
        /// </summary>
        [JsonPropertyName("accessType")]
        public VariableAccessType AccessType { get; set; }

        /// <summary>
        /// 访问位置（所在的 LogicUnit、Block、Step）
        /// </summary>
        [JsonPropertyName("location")]
        public ReferenceLocation Location { get; set; }

        /// <summary>
        /// 访问条件（可选，描述在什么条件下访问此变量）
        /// 例如：前级触点 A 和 B 都为 true 时写入
        /// </summary>
        [JsonPropertyName("condition")]
        public string Condition { get; set; }

        /// <summary>
        /// 访问时的值（如果是写入操作，记录写入的值）
        /// 例如：写入 true、写入 Timer.Q 的值
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; }

        /// <summary>
        /// UID（可选，标识访问点在 XML 中的 UId）
        /// </summary>
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        /// <summary>
        /// 优先级（数字越小优先级越高，用于排序根本原因）
        /// </summary>
        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 99;

        /// <summary>
        /// 是否是根本原因的关键点
        /// </summary>
        [JsonPropertyName("isCritical")]
        public bool IsCritical { get; set; }

        /// <summary>
        /// 备注说明
        /// </summary>
        [JsonPropertyName("note")]
        public string Note { get; set; }
    }

    /// <summary>
    /// 变量访问类型枚举
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VariableAccessType
    {
        /// <summary>
        /// 读取（作为触点使用）
        /// </summary>
        Read = 0,

        /// <summary>
        /// 写入（作为线圈赋值）
        /// </summary>
        Write = 1,

        /// <summary>
        /// 读写（既作为输入又作为输出，如计数器、定时器）
        /// </summary>
        ReadWrite = 2
    }
}
