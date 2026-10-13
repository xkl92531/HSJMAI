#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class UnderstandingModelSetupTests
{
    // 与 AiModelPurposeBindingService.ResolveAsync、离线评测相同：to_jsonb(m)::text 按属性名不区分大小写反序列化
    const string Row = """{"id":7,"modelcode":"qwen36","modelname":"Qwen","provider":"OpenAI","baseurl":"http://192.168.6.146:26077/v1","apikey":"k","contextwindow":32768,"maxtokens":4096,"customrequestparams":"{\"top_p\":0.9,\"temperature\":0.7,\"chat_template_kwargs\":{\"foo\":1}}"}""";

    static LLM_ModelEntity FromRow() =>
        JsonSerializer.Deserialize<LLM_ModelEntity>(Row, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void PrepareFixesTemperatureThinkingAndMaxTokensAndKeepsOtherParameters()
    {
        var (model, fingerprint) = UnderstandingModelSetup.Prepare(FromRow());
        var p = JsonNode.Parse(model.CustomRequestParams!)!.AsObject();
        Assert.Equal(0.1, p["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, p["top_p"]!.GetValue<double>());
        Assert.True(p["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(1, p["chat_template_kwargs"]!["foo"]!.GetValue<int>());
        Assert.Equal(16000, model.MaxTokens);
        Assert.Equal(PipelineModelInference.ModelFingerprint(model), fingerprint);
    }

    [Fact]
    public void TheSameDatabaseRowGivesTheSameFingerprintEveryTimeAndPrepareIsIdempotent()
    {
        var first = UnderstandingModelSetup.Prepare(FromRow());
        var second = UnderstandingModelSetup.Prepare(FromRow());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint, UnderstandingModelSetup.Prepare(first.Model).Fingerprint);
    }

    [Fact]
    public void RequestBodyCarriesThePreparedParameters()
    {
        var (model, _) = UnderstandingModelSetup.Prepare(FromRow());
        var body = PipelineModelCatalog.BuildRequest(model, "s", "u");
        Assert.Equal(0.1, body["temperature"]!.GetValue<double>());
        Assert.Equal(16000, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("qwen36", body["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("不是 JSON")]
    [InlineData("[1,2]")]
    public void UnusableParametersAreTreatedAsEmpty(string? raw)
    {
        var model = FromRow();
        model.CustomRequestParams = raw;
        var (prepared, _) = UnderstandingModelSetup.Prepare(model);
        var p = JsonNode.Parse(prepared.CustomRequestParams!)!.AsObject();
        Assert.Equal(0.1, p["temperature"]!.GetValue<double>());
    }
}
