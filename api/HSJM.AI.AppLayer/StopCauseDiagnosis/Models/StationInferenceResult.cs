using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位推断结果。
    /// 通过块、网络、IOTag 邻域、变量注释等多证据为变量/逻辑片段赋予工位归属。
    /// </summary>
    public class StationInferenceResult
    {
        public string StationCode { get; set; }

        public decimal Confidence { get; set; }

        public string InferenceSource { get; set; }

        public string InferenceDetail { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();
    }
}