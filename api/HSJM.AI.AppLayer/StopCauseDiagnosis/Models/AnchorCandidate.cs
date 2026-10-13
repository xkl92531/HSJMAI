using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 动作锚点候选
    /// </summary>
    public class AnchorCandidate
    {
        public string AnchorKey { get; set; }

        public string AnchorType { get; set; }

        public string VariableName { get; set; }

        public string Address { get; set; }

        public string StationCode { get; set; }

        public decimal StationConfidence { get; set; }

        public List<string> StationEvidence { get; set; } = new List<string>();

        public string EquipmentId { get; set; }

        public string SemanticLabel { get; set; }

        public string Source { get; set; }

        public decimal Confidence { get; set; }

        public string DiscoveryStatus { get; set; } = AnchorDiscoveryStatus.Confirmed;

        public string PrimaryReason { get; set; }

        public string LogicSourceType { get; set; }

        /// <summary>
        /// GRAPH StepAction 来源的 LogicUnitId，播种时直接使用，避免反查误匹配
        /// </summary>
        public long? LogicUnitId { get; set; }

        public AnchorScoreBreakdown ScoreBreakdown { get; set; } = new AnchorScoreBreakdown();

        public long? IOTagId { get; set; }

        public bool IsCoreAnchor { get; set; }

        public decimal CoreScore { get; set; }

        public string CoreReason { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();
    }
}
