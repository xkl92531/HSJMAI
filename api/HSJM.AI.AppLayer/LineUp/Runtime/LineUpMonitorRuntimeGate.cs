using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed class LineUpMonitorRuntimeGate(IConfiguration configuration) : ISelfSingletonAutoInject
{
    public bool IsEnabled =>
        bool.TryParse(configuration["WebConfig:LineUpMonitor:Enabled"], out var enabled) && enabled;
}
