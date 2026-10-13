using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 可执行语义树节点
    /// </summary>
    public class StopCauseAstNode
    {
        public string NodeId { get; set; }

        public string NodeType { get; set; }

        public string VariableName { get; set; }

        public string DisplayName { get; set; }

        public string Operator { get; set; }

        public string ExpectedValue { get; set; }

        public List<StopCauseAstNode> Children { get; set; } = new List<StopCauseAstNode>();

        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }
}
