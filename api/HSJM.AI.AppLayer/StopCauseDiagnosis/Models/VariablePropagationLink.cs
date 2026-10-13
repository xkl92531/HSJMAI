using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 变量传播关系预览模型。
    /// 表示在同一 LogicUnit 或相邻逻辑片段中，源变量对目标变量的传播影响。
    /// </summary>
    public class VariablePropagationLink
    {
        public string FromVariable { get; set; }

        public string ToVariable { get; set; }

        public long? LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string PropagationType { get; set; }

        public decimal Confidence { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();
    }
}