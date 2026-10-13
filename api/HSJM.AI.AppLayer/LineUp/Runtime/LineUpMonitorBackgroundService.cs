using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed class LineUpMonitorBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILineUpMonitorBroadcastPump broadcastPump,
    LineUpMonitorRuntimeGate runtimeGate,
    IConfiguration configuration,
    ILogger<LineUpMonitorBackgroundService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunMonitorAsync(stoppingToken), broadcastPump.RunAsync(stoppingToken));

    private async Task RunMonitorAsync(CancellationToken cancellationToken)
    {
        var intervalMs = LineUpMonitorPollInterval.Resolve(
            configuration["WebConfig:LineUpMonitor:PollIntervalMs"]);
        if (!runtimeGate.IsEnabled)
        {
            logger.LogInformation("LineUp runtime monitor worker is disabled by server configuration.");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<LineUpMonitorCoordinator>();
                await coordinator.PollEnabledAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "LineUp runtime monitor cycle failed.");
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
