using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public sealed class GraphActiveCandidateFilterDecision
    {
        public IReadOnlyList<string> Candidates { get; init; } = new List<string>();
        public bool FilterApplied { get; init; }
        public bool FilterWouldClearAll { get; init; }
    }

    public static class GraphActiveCandidateRetentionPolicy
    {
        public static GraphActiveCandidateFilterDecision Apply(
            IEnumerable<string> currentCandidates,
            IEnumerable<string> retainedCandidates,
            int suppressedCount)
        {
            var current = currentCandidates?.ToList() ?? new List<string>();
            var retained = retainedCandidates?.ToList() ?? new List<string>();

            if (suppressedCount <= 0)
            {
                return new GraphActiveCandidateFilterDecision
                {
                    Candidates = current,
                };
            }

            if (current.Count > 0 && retained.Count == 0)
            {
                return new GraphActiveCandidateFilterDecision
                {
                    Candidates = current,
                    FilterWouldClearAll = true,
                };
            }

            return new GraphActiveCandidateFilterDecision
            {
                Candidates = retained,
                FilterApplied = true,
            };
        }
    }
}
