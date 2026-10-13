using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 无法归入任何稳定工位桶的锚点。
    /// </summary>
    public class OrphanAnchor
    {
        public string AnchorKey { get; set; }

        public string VariableName { get; set; }

        public string AnchorType { get; set; }

        public string Reason { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();
    }
}
