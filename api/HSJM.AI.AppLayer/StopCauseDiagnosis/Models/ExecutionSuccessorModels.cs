using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    public class ExecutionSuccessorSource
    {
        public string VariableName { get; set; }

        public string AnchorType { get; set; }

        public string DisplayLabel { get; set; }
    }

    public class ExecutionSuccessorCandidate
    {
        public string VariableName { get; set; }

        public string DisplayLabel { get; set; }

        public string AnchorType { get; set; }

        public decimal Confidence { get; set; }

        public int Depth { get; set; }

        public List<string> SourceVariables { get; set; } = new List<string>();

        public List<string> Evidence { get; set; } = new List<string>();
    }
}