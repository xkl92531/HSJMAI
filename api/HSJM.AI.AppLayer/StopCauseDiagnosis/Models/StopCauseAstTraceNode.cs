using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// AST 运行时求值轨迹节点。
    /// </summary>
    public class StopCauseAstTraceNode
    {
        public string NodeId { get; set; }

        public string NodeType { get; set; }

        public string VariableName { get; set; }

        public string DisplayName { get; set; }

        public string Operator { get; set; }

        public string ExpectedValue { get; set; }

        public object ActualValue { get; set; }

        public bool? Result { get; set; }

        public string Status { get; set; }

        public string Message { get; set; }

        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public List<StopCauseAstTraceNode> Children { get; set; } = new List<StopCauseAstTraceNode>();
    }
}