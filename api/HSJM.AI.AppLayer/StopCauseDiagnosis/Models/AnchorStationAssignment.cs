using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 锚点在工位地图层的最终归属结果。
    /// </summary>
    public class AnchorStationAssignment
    {
        public string AnchorKey { get; set; }

        public string VariableName { get; set; }

        public string AnchorType { get; set; }

        public string StationCode { get; set; }

        public decimal Confidence { get; set; }

        public bool HasConflict { get; set; }

        public List<string> ConflictingStations { get; set; } = new List<string>();

        public List<string> Evidence { get; set; } = new List<string>();
    }
}
