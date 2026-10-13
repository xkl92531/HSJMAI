using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.JobCenter.DTOS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.JobCenter
{
    public class PlcJobCenterBackgroundWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlcJobCenterBackgroundWorker> _logger;
        private readonly ConcurrentDictionary<string, byte> _runningTasks = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public PlcJobCenterBackgroundWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<PlcJobCenterBackgroundWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PlcJobCenterBackgroundWorker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var storeService = scope.ServiceProvider.GetRequiredService<PlcJobCenterStoreService>();
                    var tasks = await storeService.GetAllTasksAsync();
                    var now = DateTime.Now;

                    foreach (var task in tasks.Where(it => it.Enabled))
                    {
                        if (!ShouldRun(task, now))
                        {
                            continue;
                        }

                        if (!_runningTasks.TryAdd(task.TaskKey, 0))
                        {
                            continue;
                        }

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var runScope = _scopeFactory.CreateScope();
                                var runApp = runScope.ServiceProvider.GetRequiredService<PlcJobCenterApp>();
                                await runApp.ExecuteScheduledTaskAsync(task.TaskKey, "BackgroundWorker");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Plc job task failed. TaskKey={TaskKey}", task.TaskKey);
                            }
                            finally
                            {
                                _runningTasks.TryRemove(task.TaskKey, out _);
                            }
                        }, stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "PlcJobCenterBackgroundWorker loop failed.");
                }

                await SafeDelay(TimeSpan.FromMilliseconds(100), stoppingToken);
            }

            _logger.LogInformation("PlcJobCenterBackgroundWorker stopped.");
        }

        private static bool ShouldRun(PlcJobCenterTaskDefinitionDto task, DateTime now)
        {
            if (string.Equals(task.ScheduleType, "daily", StringComparison.OrdinalIgnoreCase))
            {
                var timeText = string.IsNullOrWhiteSpace(task.DailyTime) ? "08:00:00" : task.DailyTime.Trim();
                if (!TimeSpan.TryParse(timeText, out var dailyTime))
                {
                    dailyTime = TimeSpan.FromHours(8);
                }

                if (task.LastRunAt.HasValue && task.LastRunAt.Value.Date == now.Date)
                {
                    return false;
                }

                return now.TimeOfDay >= dailyTime;
            }

            var interval = Math.Clamp(task.IntervalMilliseconds <= 0 ? 30000 : task.IntervalMilliseconds, 1, 86400000);
            if (!task.LastRunAt.HasValue)
            {
                return true;
            }

            var duration = Math.Max(task.LastDurationMilliseconds ?? 0, 0);
            var nextRunAt = task.LastRunAt.Value
                .AddMilliseconds(duration)
                .AddMilliseconds(interval);
            return now >= nextRunAt;
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
