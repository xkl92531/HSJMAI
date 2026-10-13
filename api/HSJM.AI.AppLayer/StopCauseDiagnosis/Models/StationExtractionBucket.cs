using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位提取阶段的工位聚合桶。
    /// </summary>
    public class StationExtractionBucket
    {
        public string StationCode { get; set; }

        public decimal Confidence { get; set; }

        public int TotalSourceCount { get; set; }

        public int PhysicalTagCount { get; set; }

        public int VariableGroupCount { get; set; }

        public int LogicUnitCount { get; set; }

        public int BlockCount { get; set; }

        public List<string> SourceKeys { get; set; } = new List<string>();

        public List<string> Evidence { get; set; } = new List<string>();
    }
}