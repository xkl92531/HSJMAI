using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353TestConfigurationTests
{
    [Fact]
    public void IndependentReaderMayReuseExplicitlyEnabledTestBusinessConnection()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["WebConfig:DBList:0:ConnectionString"] = "Host=localhost;Database=hsjm_ai_new_test;Username=test;Password=test"
        }).Build();

        Assert.Contains("hsjm_ai_new_test", new PostgresPipelineStore(config).ConnectionString);
    }

    [Fact]
    public void IndependentReaderRejectsProductionDatabaseEvenWhenEnabled()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["WebConfig:DBList:0:ConnectionString"] = "Host=localhost;Database=hsjm_ai_new;Username=test;Password=test"
        }).Build();

        Assert.Throws<PipelineException>(() => new PostgresPipelineStore(config).ConnectionString);
    }
}
