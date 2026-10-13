using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位冲突：同一变量或锚点存在多个强候选工位。
    /// </summary>
    public class StationConflict
    {
        public string VariableName { get; set; }

        public List<string> AnchorKeys { get; set; } = new List<string>();

        public string RecommendedStation { get; set; }

        public decimal RecommendedConfidence { get; set; }

        public List<string> CandidateStations { get; set; } = new List<string>();

        public string Reason { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();
    }
}
