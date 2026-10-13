using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class GraphActiveCandidateRetentionPolicyTests
{
    [Fact]
    public void Apply_WhenEveryCurrentCandidateWouldBeSuppressed_RetainsCurrentCandidates()
    {
        var currentCandidates = new[] { "Graph.Step40", "Graph.Step41" };

        var result = GraphActiveCandidateRetentionPolicy.Apply(
            currentCandidates,
            Array.Empty<string>(),
            suppressedCount: 2);

        Assert.Equal(currentCandidates, result.Candidates);
        Assert.False(result.FilterApplied);
        Assert.True(result.FilterWouldClearAll);
    }

    [Fact]
    public void Apply_WhenOnlySomeCandidatesAreSuppressed_AppliesFilter()
    {
        var result = GraphActiveCandidateRetentionPolicy.Apply(
            new[] { "Graph.Step40", "Graph.Step41" },
            new[] { "Graph.Step41" },
            suppressedCount: 1);

        Assert.Equal(new[] { "Graph.Step41" }, result.Candidates);
        Assert.True(result.FilterApplied);
        Assert.False(result.FilterWouldClearAll);
    }
}
