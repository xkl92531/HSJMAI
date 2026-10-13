using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353CurrentTestSourceTests
{
    private readonly ITestOutputHelper output;
    public Original353CurrentTestSourceTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(4617381855010885632L)]
    [InlineData(4617382048292802560L)]
    public async Task CurrentTestProjectCanGenerateFromUploadedArchive(long projectId)
    {
        var connection = Environment.GetEnvironmentVariable("HSJM_XMLV2_TEST_CONNECTION");
        var archive = Environment.GetEnvironmentVariable(projectId == 4617381855010885632L
            ? "HSJM_XMLV2_PLC1_ARCHIVE" : "HSJM_XMLV2_PLC2_ARCHIVE");
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(archive)) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["IndependentPlcPipeline:ConnectionString"] = connection
        }).Build();

        var stored = await new PostgresPipelineStore(config).LoadSourceAsync(projectId, CancellationToken.None);
        var source = Original353ArchiveSource.Read(archive, projectId, stored.Tags);
        Assert.True(source.Units.Count > 500);
        Assert.Contains(source.Units, unit => unit.InterfaceXml.Contains("<Member", StringComparison.Ordinal));

        var generated = OriginalConstraintWorkflow.Generate(source);
        output.WriteLine($"project={projectId} units={source.Units.Count} rules={generated.RuleCount} conditional={generated.ConditionalFeedbackCount} issues={generated.Units.Sum(u => u.Issues.Count)}");
        Assert.True(generated.RuleCount > 100);
        Assert.Equal(projectId == 4617381855010885632L ? 222 : 173,
            generated.RuleCount + generated.ConditionalFeedbackCount);
        Assert.True(generated.RepairCount >= 3,
            $"safety repairs={generated.RepairCount}; issues={string.Join(';', generated.Units.SelectMany(u => u.Issues).Where(i => i.StartsWith("Repair", StringComparison.Ordinal)).Distinct().Take(12))}");
    }
}
