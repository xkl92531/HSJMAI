using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.CylinderSignalEvent
{
    public class CylinderSignalCollectorJobBackgroundWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CylinderSignalCollectorJobBackgroundWorker> _logger;

        public CylinderSignalCollectorJobBackgroundWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<CylinderSignalCollectorJobBackgroundWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("CylinderSignalCollectorJobBackgroundWorker started.");

            DateTime? lastRunAt = null;
            var lastIntervalMilliseconds = 0;
            var lastEnabled = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var jobService = scope.ServiceProvider.GetRequiredService<CylinderSignalCollectorJobService>();
                    var config = await jobService.GetConfigAsync();
                    var enabled = config.Enabled;
                    var intervalMilliseconds = Math.Clamp(config.IntervalMilliseconds <= 0 ? 30000 : config.IntervalMilliseconds, 1, 86400000);

                    if (!enabled)
                    {
                        lastRunAt = null;
                        lastIntervalMilliseconds = intervalMilliseconds;
                        lastEnabled = false;
                        await SafeDelay(TimeSpan.FromMilliseconds(200), stoppingToken);
                        continue;
                    }

                    if (!lastEnabled || lastIntervalMilliseconds != intervalMilliseconds)
                    {
                        lastRunAt = null;
                    }

                    if (lastRunAt == null || DateTime.Now - lastRunAt.Value >= TimeSpan.FromMilliseconds(intervalMilliseconds))
                    {
                        lastRunAt = DateTime.Now;
                        var result = await jobService.RunScheduledCollectAllAsync();
                        if (!result.IsSuccess)
                        {
                            _logger.LogWarning("Cylinder signal collector scheduled run finished with error: {Message}", result.ErrorMessage);
                        }
                    }

                    lastEnabled = true;
                    lastIntervalMilliseconds = intervalMilliseconds;
                    await SafeDelay(TimeSpan.FromMilliseconds(100), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "CylinderSignalCollectorJobBackgroundWorker threw.");
                    await SafeDelay(TimeSpan.FromMilliseconds(500), stoppingToken);
                }
            }

            _logger.LogInformation("CylinderSignalCollectorJobBackgroundWorker stopped.");
        }

        private static async Task SafeDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
