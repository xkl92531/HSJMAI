using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// AST 编译状态
    /// </summary>
    public static class AstCompileStatus
    {
        public const string Pending = "Pending";
        public const string Compiled = "Compiled";
        public const string PartiallyCompiled = "PartiallyCompiled";
        public const string Unsupported = "Unsupported";
        public const string Failed = "Failed";
    }

    /// <summary>
    /// AST 覆盖报告
    /// </summary>
    public class AstCoverageReport
    {
        public string SourceType { get; set; }

        public string UnitType { get; set; }

        public string Title { get; set; }

        public string BlockName { get; set; }

        public long? LogicUnitId { get; set; }

        public int? NetworkId { get; set; }

        public string CompileStatus { get; set; } = AstCompileStatus.Pending;

        public string UnsupportedReason { get; set; }

        public List<string> UnknownPartTypes { get; set; } = new List<string>();

        public string RawContent { get; set; }

        public string RawContentExcerpt { get; set; }

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();

        public string SuggestedUpgradeCategory { get; set; }

        /// <summary>
        /// FB/FC调用引脚绑定（JSON），由解析器提取，用于跨边界AST展平
        /// </summary>
        public string CallPinBindingsJson { get; set; }

        /// <summary>
        /// GRAPH Step Action 出口 Transition NetworkId 列表（JSON int[]）。
        /// 运行时用于找到对应 Transition LogicUnit，将其转移条件嵌入 AST。
        /// </summary>
        public string GraphOutgoingTransitionsJson { get; set; }

        /// <summary>
        /// GRAPH Step Action 出口 Transition 条件摘要（JSON）。
        /// 由一键分析预处理生成，便于运行时/AI 在没有 Action 激活时仍能理解 Step 后继条件。
        /// </summary>
        public string GraphTransitionConditionsJson { get; set; }
    }

    /// <summary>
    /// GRAPH Step Action 对应出口 Transition 条件摘要。
    /// </summary>
    public class GraphTransitionConditionSummary
    {
        public int? TransitionNetworkId { get; set; }

        public long? LogicUnitId { get; set; }

        public string UnitType { get; set; }

        public string Title { get; set; }

        public string BlockName { get; set; }

        public string RawContentExcerpt { get; set; }

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();
    }
}
