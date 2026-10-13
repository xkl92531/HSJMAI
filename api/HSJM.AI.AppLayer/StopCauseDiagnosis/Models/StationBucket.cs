using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位桶：表示一个站点聚合后的锚点、逻辑单元、块统计。
    /// </summary>
    public class StationBucket
    {
        public string StationCode { get; set; }

        public decimal Confidence { get; set; }

        public int AnchorCount { get; set; }

        public int LogicUnitCount { get; set; }

        public int BlockCount { get; set; }

        public List<string> AnchorKeys { get; set; } = new List<string>();

        public List<string> VariableNames { get; set; } = new List<string>();

        public List<string> Evidence { get; set; } = new List<string>();
    }
}
