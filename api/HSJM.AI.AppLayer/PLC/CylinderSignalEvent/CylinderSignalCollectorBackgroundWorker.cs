using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.CylinderSignalEvent
{
    public class CylinderSignalCollectorBackgroundWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CylinderSignalCollectorBackgroundWorker> _logger;

        public CylinderSignalCollectorBackgroundWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<CylinderSignalCollectorBackgroundWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var config = CylinderSignalCollectorConfig.Current;
            if (!config.Enabled)
            {
                _logger.LogInformation("CylinderSignalCollectorBackgroundWorker is disabled.");
                return;
            }

            var interval = TimeSpan.FromSeconds(Math.Max(config.IntervalSeconds, 1));
            while (!stoppingToken.IsCancellationRequested)
            {
                var startedAt = DateTime.Now;
                try
                {
                    await RunOnceAsync(config, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cylinder signal collector cycle failed.");
                }

                var delay = interval - (DateTime.Now - startedAt);
                if (delay < TimeSpan.FromMilliseconds(200))
                {
                    delay = TimeSpan.FromMilliseconds(200);
                }

                await Task.Delay(delay, stoppingToken);
            }
        }

        private async Task RunOnceAsync(CylinderSignalCollectorConfig config, CancellationToken ct)
        {
            var projectIds = config.ProjectIds?.Where(it => it > 0).Distinct().ToList() ?? new List<long>();
            if (projectIds.Count == 0)
            {
                return;
            }

            var stationCodes = config.StationCodes?
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            using var scope = _scopeFactory.CreateScope();
            var app = scope.ServiceProvider.GetRequiredService<CylinderSignalEventApp>();
            foreach (var projectId in projectIds)
            {
                ct.ThrowIfCancellationRequested();
                if (stationCodes.Count == 0)
                {
                    await app.CollectOnceAsync(new CylinderSignalCollectRequestDto
                    {
                        ProjectId = projectId,
                        StationMode = config.StationMode
                    });
                    continue;
                }

                foreach (var stationCode in stationCodes)
                {
                    ct.ThrowIfCancellationRequested();
                    await app.CollectOnceAsync(new CylinderSignalCollectRequestDto
                    {
                        ProjectId = projectId,
                        StationCode = stationCode,
                        StationMode = config.StationMode
                    });
                }
            }
        }
    }

    public class CylinderSignalCollectorConfig
    {
        private static CylinderSignalCollectorConfig _current = new CylinderSignalCollectorConfig();

        public static CylinderSignalCollectorConfig Current
        {
            get => _current;
            set => _current = value ?? new CylinderSignalCollectorConfig();
        }

        public bool Enabled { get; set; } = false;
        public int IntervalSeconds { get; set; } = 2;
        public List<long> ProjectIds { get; set; } = new List<long>();
        public List<string> StationCodes { get; set; } = new List<string>();
        public string StationMode { get; set; }
    }
}
