using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// AST 编译结果
    /// </summary>
    public class StopCauseAstCompileResult
    {
        public AnchorCandidate Anchor { get; set; }

        public string CompileStatus { get; set; } = AstCompileStatus.Pending;

        public string CompilerName { get; set; }

        public StopCauseAstNode RootNode { get; set; }

        public List<AstCoverageReport> CoverageReports { get; set; } = new List<AstCoverageReport>();

        public List<string> Messages { get; set; } = new List<string>();
    }
}