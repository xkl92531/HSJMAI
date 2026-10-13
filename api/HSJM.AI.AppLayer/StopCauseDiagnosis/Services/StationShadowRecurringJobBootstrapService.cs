using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class StationShadowRecurringJobBootstrapService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StationShadowRecurringJobBootstrapService> _logger;

        public StationShadowRecurringJobBootstrapService(
            IServiceScopeFactory scopeFactory,
            ILogger<StationShadowRecurringJobBootstrapService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            RecurringJob.AddOrUpdate<StationShadowRecurringJobService>(
                "station-shadow-monitoring-sweep",
                service => service.ExecuteMonitoringSweepAsync(),
                Cron.Minutely());

            RecurringJob.AddOrUpdate<StationShadowRecurringJobService>(
                "station-shadow-job-sync",
                service => service.SyncRecurringJobsAsync(),
                "*/5 * * * *");

            // 每日凌晨 2 点执行数据保留清理，删除超出 HistoryRetentionDays（默认 3 天）的过期记录
            RecurringJob.AddOrUpdate<StationShadowRecurringJobService>(
                "station-shadow-data-retention-cleanup",
                service => service.ExecuteDataRetentionCleanupAsync(),
                Cron.Daily(2));

            using var scope = _scopeFactory.CreateScope();
            var recurringJobService = scope.ServiceProvider.GetRequiredService<StationShadowRecurringJobService>();
            var syncResult = await recurringJobService.SyncRecurringJobsAsync();

            _logger.LogInformation(
                "Station shadow recurring jobs initialized. Active pattern jobs: {ActiveJobs}, removed jobs: {RemovedJobs}, warnings: {WarningCount}",
                syncResult.ActivePatternJobs,
                syncResult.RemovedPatternJobs,
                syncResult.Warnings.Count);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}