using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Text.Json.Nodes;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353RuleTextLiveTests
{
    [Fact]
    public async Task TestDescriptionServiceCachesRealXmlWordingInTestDatabase()
    {
        if (Environment.GetEnvironmentVariable("HSJM_XMLV2_AI_SERVICE_PROBE") != "1") return;
        var connection = Environment.GetEnvironmentVariable("HSJM_XMLV2_TEST_CONNECTION")!;
        var archive = Environment.GetEnvironmentVariable("HSJM_XMLV2_PLC1_ARCHIVE")!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["IndependentPlcPipeline:ConnectionString"] = connection,
            ["WebConfig:DBList:0:ConnectionString"] = connection
        }).Build();
        var store = new PostgresPipelineStore(config);
        const long project = 4617381855010885632L;
        var tags = (await store.LoadSourceAsync(project, CancellationToken.None)).Tags;
        var source = Original353ArchiveSource.Read(archive, project, tags);
        var generated = OriginalConstraintWorkflow.Generate(source);
        using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var catalog = new PipelineModelCatalog(store, services.GetRequiredService<IHttpClientFactory>());
        var service = new Original353RuleTextService(config, store, catalog,
            new AiModelPurposeBindingService(config),
            NullLogger<Original353RuleTextService>.Instance);
        var wording = await service.BuildAsync(source, generated, CancellationToken.None);
        Assert.NotEmpty(wording);
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM plc_independent.rule_text_cache WHERE project_id=@project", db);
        command.Parameters.AddWithValue("project", project);
        Assert.True((long)(await command.ExecuteScalarAsync())! > 0);
        await using var plain = new NpgsqlCommand("SELECT count(*) FROM plc_independent.plain_language WHERE project_id=@project AND length(content)>0", db);
        plain.Parameters.AddWithValue("project", project);
        Assert.True((long)(await plain.ExecuteScalarAsync())! > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestModelCanExplainOneRealXmlRuleWithoutChangingItsContract(bool conditionalFeedback)
    {
        if (Environment.GetEnvironmentVariable("HSJM_XMLV2_AI_PROBE") != "1") return;
        var connection = Environment.GetEnvironmentVariable("HSJM_XMLV2_TEST_CONNECTION")!;
        var archive = Environment.GetEnvironmentVariable("HSJM_XMLV2_PLC1_ARCHIVE")!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["IndependentPlcPipeline:ConnectionString"] = connection,
            ["WebConfig:DBList:0:ConnectionString"] = connection
        }).Build();
        var store = new PostgresPipelineStore(config);
        const long project = 4617381855010885632L;
        var tags = (await store.LoadSourceAsync(project, CancellationToken.None)).Tags;
        var source = Original353ArchiveSource.Read(archive, project, tags);
        var generated = OriginalConstraintWorkflow.Generate(source);
        var selectedRules = conditionalFeedback
            ? generated.Units.SelectMany(unit => unit.ConditionalFeedbackRules
                .Select(item => (unit, rule: item.Rule, evidence: item.Evidence)))
            : generated.Units.SelectMany(unit => unit.SafetyRules
                .Select(item => (unit, rule: item.Rule, evidence: item.Evidence)));
        var selected = selectedRules
            .Select(pair => (pair.unit, pair.rule, pair.evidence,
                sourceUnit: source.Units.Single(unit => unit.Id == pair.unit.LogicUnitId)))
            .Select(row => (row.unit, row.rule, row.evidence,
                pld: SiemensFlgNetConstraintExtractor.BuildParsedLogicDescription(
                    row.sourceUnit.RawContent,
                    tags.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key,
                            group => (group.First().Address, (string?)null), StringComparer.OrdinalIgnoreCase),
                    row.sourceUnit.NetworkOrder, blockName: row.sourceUnit.BlockName)))
            .First(row => row.pld.Length is > 0 and < 12000);

        using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var model = (await new AiModelPurposeBindingService(config)
            .ResolveAsync(AiModelPurposes.ConstraintDescription, CancellationToken.None)).Model;
        var parameters = string.IsNullOrWhiteSpace(model.CustomRequestParams)
            ? new JsonObject() : JsonNode.Parse(model.CustomRequestParams)!.AsObject();
        parameters["temperature"] = 0.1;
        var template = parameters["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        template["enable_thinking"] = false;
        parameters["chat_template_kwargs"] = template;
        parameters.Remove("response_format");
        model.CustomRequestParams = parameters.ToJsonString();
        model.MaxTokens = 3000;
        var key = selected.unit.LogicUnitId + ":" + selected.rule.Key;
        var prompt = Original353RuleTextAi.BuildPrompt(key, selected.rule,
            selected.pld, selected.evidence,
            Original353RuleText.Render(selected.rule,
                Original353ActivationPolicy.Classify(selected.rule, tags)).Description);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await new PipelineModelCatalog(store, services.GetRequiredService<IHttpClientFactory>())
            .CompleteAsync(model, Original353RuleTextAi.SystemPrompt, prompt, timeout.Token);
        var accepted = Original353RuleTextAi.TryParse(response,
            new Dictionary<string, PipelineRule> { [key] = selected.rule });
        Assert.True(accepted.Count == 1, "Model wording rejected: " + response[..Math.Min(response.Length, 2400)]);
    }
}
