using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 交叉引用分析结果
    /// 包含变量的所有访问点（读取/写入）和根本原因分析
    /// </summary>
    public class CrossReferenceResult
    {
        /// <summary>
        /// 被分析的变量名称
        /// </summary>
        [JsonPropertyName("variable")]
        public string Variable { get; set; }

        /// <summary>
        /// 所有访问记录（统一的访问模型，包含读取和写入）
        /// 按优先级排序，最关键的访问在前
        /// </summary>
        [JsonPropertyName("accesses")]
        public List<VariableAccess> Accesses { get; set; } = new List<VariableAccess>();

        /// <summary>
        /// 写入位置列表（从 Accesses 中筛选，向后兼容旧接口）
        /// 变量在哪些地方被写入/赋值
        /// </summary>
        [JsonPropertyName("writtenAt")]
        public List<ReferenceLocation> WrittenAt
        {
            get
            {
                return Accesses?
                    .Where(a => a.AccessType == VariableAccessType.Write || 
                                a.AccessType == VariableAccessType.ReadWrite)
                    .Select(a => a.Location)
                    .ToList() ?? new List<ReferenceLocation>();
            }
        }

        /// <summary>
        /// 读取位置列表（从 Accesses 中筛选，向后兼容旧接口）
        /// 变量在哪些地方被读取/使用
        /// </summary>
        [JsonPropertyName("readAt")]
        public List<ReferenceLocation> ReadAt
        {
            get
            {
                return Accesses?
                    .Where(a => a.AccessType == VariableAccessType.Read || 
                                a.AccessType == VariableAccessType.ReadWrite)
                    .Select(a => a.Location)
                    .ToList() ?? new List<ReferenceLocation>();
            }
        }

        /// <summary>
        /// 根本原因（追溯到最底层的输入信号）
        /// </summary>
        [JsonPropertyName("rootCause")]
        public RootCauseInfo RootCause { get; set; }

        /// <summary>
        /// 交叉引用的依赖链（变量之间的依赖关系）
        /// 例如：["InputSensor", "IntermediateVar", "OutputCoil"] 表示依赖链
        /// </summary>
        [JsonPropertyName("dependencyChain")]
        public List<string> DependencyChain { get; set; } = new List<string>();

        /// <summary>
        /// 是否找到完整的依赖链（从输入到输出）
        /// </summary>
        [JsonPropertyName("isComplete")]
        public bool IsComplete { get; set; }

        /// <summary>
        /// 分析置信度（0-100）
        /// AI 对此分析结果的置信度评分
        /// </summary>
        [JsonPropertyName("confidence")]
        public int Confidence { get; set; } = 80;

        /// <summary>
        /// 诊断摘要（简洁格式，100-200字）
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; }
    }
}
