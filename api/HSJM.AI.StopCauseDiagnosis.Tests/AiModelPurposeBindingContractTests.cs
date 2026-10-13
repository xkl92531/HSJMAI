using System.Text.Json;
using HSJM.AI.AppLayer.Agent.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public sealed class AiModelPurposeBindingContractTests
{
    [Theory]
    [InlineData(AiModelPurposes.DefaultChat, "Chat", true)]
    [InlineData(AiModelPurposes.ConstraintDescription, "VL", true)]
    [InlineData(AiModelPurposes.IndependentPlc, "Embedding", false)]
    [InlineData(AiModelPurposes.MemoryChat, "VL", true)]
    [InlineData(AiModelPurposes.MemoryEmbedding, "Embedding", true)]
    [InlineData(AiModelPurposes.MemoryEmbedding, "Chat", false)]
    [InlineData("unknown", "Chat", false)]
    public void Purpose_rejects_incompatible_model_types(string purpose, string modelType, bool expected)
    {
        Assert.Equal(expected, AiModelPurposes.IsAllowedModelType(purpose, modelType));
    }

    [Fact]
    public void Safe_list_contract_never_contains_credentials_or_numeric_snowflake_ids()
    {
        const string snowflake = "4625023512158015488";
        var response = new AiModelPurposeListDto(
            [new(AiModelPurposes.DefaultChat, "平台默认聊天与工具", snowflake,
                "Qwen", "qwen-model", "Chat", 2, "Ready")],
            [new(snowflake, "Qwen", "qwen-model", "Chat", true)]);

        var json = JsonSerializer.Serialize(response);

        Assert.Contains($"\"{snowflake}\"", json);
        Assert.DoesNotContain("ApiKey", json);
        Assert.DoesNotContain("BaseUrl", json);
    }
}
