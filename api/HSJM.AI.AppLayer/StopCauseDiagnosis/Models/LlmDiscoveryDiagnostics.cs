using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// LLM 候选发现诊断信息，用于解释哪些候选进入了 LLM，哪些被预筛掉。
    /// </summary>
    public class LlmDiscoveryDiagnostics
    {
        public int TotalContextCount { get; set; }

        public int StationFilteredCount { get; set; }

        public int SentToLlmCount { get; set; }

        public int SuppressedCount { get; set; }

        public int LlmReturnedCount { get; set; }

        public int ConfirmedByLlmCount { get; set; }

        public int HandshakeByLlmCount { get; set; }

        public int ReviewByLlmCount { get; set; }

        public int UnknownDroppedCount { get; set; }

        public int StateFlagDroppedCount { get; set; }

        public List<LlmCandidateDiagnosticItem> SuppressedSamples { get; set; } = new List<LlmCandidateDiagnosticItem>();

        public List<LlmCandidateDiagnosticItem> ClassifiedSamples { get; set; } = new List<LlmCandidateDiagnosticItem>();
    }

    public class LlmCandidateDiagnosticItem
    {
        public string VariableName { get; set; }

        public string Decision { get; set; }

        public string Reason { get; set; }

        public string Category { get; set; }

        public string StationCode { get; set; }

        public string LogicSourceType { get; set; }
    }
}