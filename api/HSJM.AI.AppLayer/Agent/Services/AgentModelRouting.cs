using System;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Agent.Services;

internal readonly record struct AgentModelChoice(long ModelId, bool FromDefaultBinding);

/// <summary>
/// The diagnosis role follows the website's default_chat binding. Other roles keep their
/// explicitly configured model; roles without one also use the website default.
/// </summary>
internal static class AgentModelRouting
{
    internal static async Task<AgentModelChoice> SelectAsync(
        long roleId, long? roleModelId, long diagnosticRoleId,
        Func<Task<long?>> loadDefaultModelId)
    {
        ArgumentNullException.ThrowIfNull(loadDefaultModelId);
        if (roleId != diagnosticRoleId && roleModelId is > 0)
            return new AgentModelChoice(roleModelId.Value, false);

        var defaultModelId = await loadDefaultModelId();
        if (defaultModelId is not > 0)
            throw new InvalidOperationException("网站默认聊天用途未绑定可用模型");
        return new AgentModelChoice(defaultModelId.Value, true);
    }
}
