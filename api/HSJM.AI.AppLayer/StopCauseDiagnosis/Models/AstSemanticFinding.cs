using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 基于 AST + LLM 的语义识别结果。
    /// 用于统一承载虚拟Q点、握手位和 IOTag IoMapping 建议。
    /// </summary>
    public class AstSemanticFinding
    {
        public long LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string StationCode { get; set; }

        public string VariableName { get; set; }

        /// <summary>VirtualQPoint / HandshakeSignal / Ignore</summary>
        public string Category { get; set; }

        public string SemanticLabel { get; set; }

        public string Reason { get; set; }

        public decimal Confidence { get; set; }

        public List<string> RelatedIOTags { get; set; } = new List<string>();

        public string IoMapping { get; set; }
    }
}