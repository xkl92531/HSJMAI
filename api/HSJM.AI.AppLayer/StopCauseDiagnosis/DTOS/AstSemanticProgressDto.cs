using System;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class AstSemanticProgressDto
    {
        public string Key { get; set; }

        public string Status { get; set; }

        public int TotalCandidates { get; set; }

        public int CompletedCandidates { get; set; }

        public int TotalBatches { get; set; }

        public int CompletedBatches { get; set; }

        public int FoundCount { get; set; }

        public string Message { get; set; }

        public string ErrorMessage { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public decimal ProgressPercent => TotalCandidates <= 0
            ? (Status == "Completed" ? 100m : 0m)
            : Math.Min(100m, Math.Round(CompletedCandidates * 100m / TotalCandidates, 1));
    }
}