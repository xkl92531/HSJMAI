using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// AST 构建种子
    /// </summary>
    public class StopCauseAstSeed
    {
        public AnchorCandidate Anchor { get; set; }

        public string CompileStatus { get; set; } = AstCompileStatus.Pending;

        public List<WriterLogicUnitSummary> WriterLogicUnits { get; set; } = new List<WriterLogicUnitSummary>();

        public List<string> RelatedVariables { get; set; } = new List<string>();

        public List<AstCoverageReport> CoverageReports { get; set; } = new List<AstCoverageReport>();
    }

    /// <summary>
    /// 写入锚点的逻辑单元摘要
    /// </summary>
    public class WriterLogicUnitSummary
    {
        public long LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string ProgrammingLanguage { get; set; }

        public string UnitType { get; set; }

        public int? NetworkId { get; set; }

        public string Title { get; set; }

        public string RawContent { get; set; }

        public string RawContentExcerpt { get; set; }

        /// <summary>
        /// FB/FC调用引脚绑定（JSON），由解析器提取，用于跨边界AST展平
        /// </summary>
        public string CallPinBindingsJson { get; set; }

        /// <summary>
        /// GRAPH Step 出口 Transition 编号列表（JSON int[]），用于在 AST 中嵌入转移条件子节点
        /// </summary>
        public string GraphOutgoingTransitionsJson { get; set; }

        /// <summary>
        /// GRAPH Step 出口 Transition 条件摘要（JSON），由一键分析预处理阶段生成
        /// </summary>
        public string GraphTransitionConditionsJson { get; set; }
    }
}
