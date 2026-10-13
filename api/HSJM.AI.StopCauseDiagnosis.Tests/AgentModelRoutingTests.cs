using HSJM.AI.AppLayer.Agent.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public sealed class AgentModelRoutingTests
{
    [Fact]
    public async Task Diagnostic_role_uses_website_default_even_with_a_role_model()
    {
        var choice = await AgentModelRouting.SelectAsync(
            roleId: 2013, roleModelId: 10005, diagnosticRoleId: 2013,
            loadDefaultModelId: () => Task.FromResult<long?>(4625023512158015489));

        Assert.Equal(4625023512158015489, choice.ModelId);
        Assert.True(choice.FromDefaultBinding);
    }

    [Fact]
    public async Task Other_role_keeps_its_explicit_model()
    {
        var defaultWasRead = false;
        var choice = await AgentModelRouting.SelectAsync(
            roleId: 2020, roleModelId: 10005, diagnosticRoleId: 2013,
            loadDefaultModelId: () =>
            {
                defaultWasRead = true;
                return Task.FromResult<long?>(4625023512158015489);
            });

        Assert.Equal(10005, choice.ModelId);
        Assert.False(choice.FromDefaultBinding);
        Assert.False(defaultWasRead);
    }

    [Fact]
    public async Task Diagnostic_role_does_not_silently_fall_back_to_old_role_model()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentModelRouting.SelectAsync(
            roleId: 2013, roleModelId: 10005, diagnosticRoleId: 2013,
            loadDefaultModelId: () => Task.FromResult<long?>(null)));
    }
}
