using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public sealed class DefaultChatPurposeSelectionTests
{
    [Fact]
    public async Task LegacyRecommendationUsesOnlyBoundDefaultChatModel()
    {
        var model = new LLM_ModelEntity
        {
            Id = 123,
            ModelCode = "bound-vision",
            ModelType = "VL",
            ContextWindow = 131072,
            CostPerInputToken = 10m
        };
        var resolver = new StubPurposeResolver(new ResolvedAiModel(model, 7));
        var service = new LLMModelService(null!, NullLogger<LLMModelService>.Instance, null!, resolver);

        var selected = await service.RecommendModelAsync(requiredContextSize: 12000);

        Assert.Same(model, selected);
        Assert.Equal(AiModelPurposes.DefaultChat, resolver.RequestedPurpose);
    }

    [Fact]
    public async Task LegacyRecommendationFailsIfBoundModelCannotMeetContextRequirement()
    {
        var model = new LLM_ModelEntity { Id = 456, ModelType = "Chat", ContextWindow = 2048 };
        var service = new LLMModelService(null!, NullLogger<LLMModelService>.Instance, null!,
            new StubPurposeResolver(new ResolvedAiModel(model, 1)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecommendModelAsync(requiredContextSize: 4096));
    }

    private sealed class StubPurposeResolver(ResolvedAiModel binding) : IAiModelPurposeResolver
    {
        public string? RequestedPurpose { get; private set; }

        public Task<ResolvedAiModel> ResolveAsync(string purpose, CancellationToken token = default)
        {
            RequestedPurpose = purpose;
            return Task.FromResult(binding);
        }
    }
}
