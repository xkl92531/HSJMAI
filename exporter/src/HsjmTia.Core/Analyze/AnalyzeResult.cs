using System.Collections.Generic;

namespace HsjmTia.Core.Analyze
{
    public class AnalyzeFailure
    {
        public string File { get; set; }
        public string Error { get; set; }
    }

    public class AnalyzeReport
    {
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public IReadOnlyList<AnalyzeFailure> Failures { get; set; } = new List<AnalyzeFailure>();
    }
}
