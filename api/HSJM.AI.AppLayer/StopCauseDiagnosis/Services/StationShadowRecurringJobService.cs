using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class StationShadowRecurringJobSyncResult
    {
        public int ActivePatternJobs { get; set; }

        public int RemovedPatternJobs { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class StationShadowRecurringJobService : ISelfScopedAutoInject
    {
        private const int MinimumSweepIntervalMs = 60 * 1000;
        private const int DefaultRetentionDays = 3;

        private readonly StopCauseDiagnosisApp _stopCauseDiagnosisApp;
        private readonly IPLC_StationShadowConfigService _stationShadowConfigService;
        private readonly IPLC_StationShadowEventService _stationShadowEventService;
        private readonly IPLC_StationSignalHistoryService _stationSignalHistoryService;
        private readonly StationShadowStateCacheService _stationShadowStateCacheService;
        private readonly ILogger<StationShadowRecurringJobService> _logger;

        public StationShadowRecurringJobService(
            StopCauseDiagnosisApp stopCauseDiagnosisApp,
            IPLC_StationShadowConfigService stationShadowConfigService,
            IPLC_StationShadowEventService stationShadowEventService,
            IPLC_StationSignalHistoryService stationSignalHistoryService,
            StationShadowStateCacheService stationShadowStateCacheService,
            ILogger<StationShadowRecurringJobService> logger)
        {
            _stopCauseDiagnosisApp = stopCauseDiagnosisApp;
            _stationShadowConfigService = stationShadowConfigService;
            _stationShadowEventService = stationShadowEventService;
            _stationSignalHistoryService = stationSignalHistoryService;
            _stationShadowStateCacheService = stationShadowStateCacheService;
            _logger = logger;
        }

        [Hangfire.JobDisplayName("工位影子监控扫描（每分钟）")]
        public async Task ExecuteMonitoringSweepAsync()
        {
            var configs = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.Enabled == true && it.ProjectId != null && it.StationCode != null)
                .OrderBy(it => it.ProjectId, OrderByType.Asc)
                .OrderBy(it => it.StationCode, OrderByType.Asc)
                .ToListAsync();

            foreach (var config in configs)
            {
                try
                {
                    if (!await ShouldRunCycleAsync(config))
                    {
                        continue;
                    }

                    var response = await _stopCauseDiagnosisApp.RunMonitoringCycleAsync(new RunStationShadowCycleRequestDto
                    {
                        ProjectId = config.ProjectId.GetValueOrDefault(),
                        StationCode = config.StationCode?.Trim(),
                        PersistHistory = true,
                        PersistSnapshot = true,
                        PersistEvents = true,
                        JobSource = "HangfireSweep",
                        SnapshotRecentBundleCount = 10,
                        DiagnosePendingAfterCycle = true,
                        DiagnosisEventLimit = 5,
                    });

                    if (!response.IsSuccess)
                    {
                        _logger.LogWarning("Station shadow monitoring sweep failed for project {ProjectId}, station {StationCode}: {Message}", config.ProjectId, config.StationCode, response.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Station shadow monitoring sweep threw for project {ProjectId}, station {StationCode}", config.ProjectId, config.StationCode);
                }
            }
        }

        [Hangfire.JobDisplayName("工位影子待执行诊断 · 项目{0} 工位{1}")]
        public async Task ExecutePendingDiagnosisAsync(long projectId, string stationCode)
        {
            if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                return;
            }

            var response = await _stopCauseDiagnosisApp.DiagnosePendingMonitoringEventsAsync(new StationShadowDiagnosisRunRequestDto
            {
                ProjectId = projectId,
                StationCode = stationCode.Trim(),
                Limit = 10,
            });

            if (!response.IsSuccess)
            {
                _logger.LogWarning("Pending diagnosis job failed for project {ProjectId}, station {StationCode}: {Message}", projectId, stationCode, response.ErrorMessage);
            }
        }

        public async Task ExecutePatternRebuildAsync(long projectId, string stationCode)
        {
            if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                return;
            }

            var response = await _stopCauseDiagnosisApp.RebuildMonitoringPatternTemplatesAsync(new StationShadowPatternTemplateRebuildRequestDto
            {
                ProjectId = projectId,
                StationCode = stationCode.Trim(),
                PatternType = "ExpectedNextBundle",
                ReplaceExisting = true,
                Limit = 120,
                MinSupportCount = 1,
            });

            if (!response.IsSuccess)
            {
                _logger.LogWarning("Pattern rebuild job failed for project {ProjectId}, station {StationCode}: {Message}", projectId, stationCode, response.ErrorMessage);
                return;
            }

            await _stationShadowConfigService.UpdateAsync(
                it => new PLC_StationShadowConfigEntity
                {
                    LastPatternRebuildTime = DateTime.Now,
                    ModifyTime = DateTime.Now,
                },
                it => it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == stationCode);
        }

        /// <summary>
        /// 单个 Hangfire 再建模扫描任务：遍历所有需要再建模的工位并依次执行。
        /// 用一个作业替换以前 N 个按工位注册的作业，消除大量分布式锁竞争。
        /// </summary>
        [Hangfire.JobDisplayName("工位影子模式再建模（每小时）")]
        public async Task ExecutePatternRebuildAllAsync()
        {
            var configs = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.Enabled == true
                             && it.ProjectId != null && it.StationCode != null
                             && !string.IsNullOrEmpty(it.PatternRebuildCron))
                .OrderBy(it => it.ProjectId, OrderByType.Asc)
                .OrderBy(it => it.StationCode, OrderByType.Asc)
                .ToListAsync();

            foreach (var config in configs)
            {
                var projectId = config.ProjectId.GetValueOrDefault();
                var stationCode = config.StationCode?.Trim();
                if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
                {
                    continue;
                }

                // 用 NCrontab 解析工位自定义 cron，判断自上次再建模以来是否已到期
                if (!IsPatternRebuildDue(config))
                {
                    continue;
                }

                try
                {
                    await ExecutePatternRebuildAsync(projectId, stationCode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pattern rebuild failed for project {ProjectId}, station {StationCode}", projectId, stationCode);
                }
            }
        }

        /// <summary>
        /// 判断工位距上次 PatternRebuild 是否已满 24 小时（简化策略，统一每日执行）。
        /// </summary>
        private static bool IsPatternRebuildDue(PLC_StationShadowConfigEntity config)
        {
            if (string.IsNullOrWhiteSpace(config.PatternRebuildCron))
            {
                return false;
            }

            var lastRebuild = config.LastPatternRebuildTime;
            if (!lastRebuild.HasValue)
            {
                return true; // 从未执行过，立即执行
            }

            // 缺省策略：距上次再建模超过 24 小时才重新执行
            return (DateTime.Now - lastRebuild.Value).TotalHours >= 24;
        }

        [Hangfire.JobDisplayName("工位影子定时任务同步（每5分钟）")]
        public async Task<StationShadowRecurringJobSyncResult> SyncRecurringJobsAsync()
        {
            var result = new StationShadowRecurringJobSyncResult();
            var configs = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId != null && it.StationCode != null)
                .ToListAsync();

            // 清理所有旧的按工位注册的 pattern-rebuild 作业（消除历史锁竞争）
            foreach (var config in configs)
            {
                var projectId = config.ProjectId.GetValueOrDefault();
                var stationCode = config.StationCode?.Trim();
                if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
                {
                    continue;
                }

                var oldJobId = BuildPatternRebuildJobId(projectId, stationCode);
                RecurringJob.RemoveIfExists(oldJobId);
                result.RemovedPatternJobs += 1;
            }

            // 注册单个整体扫描作业（每小时跑一次，内部按 cron 判断是否到期）
            var hasAnyEnabled = configs.Any(c => c.Enabled == true && !string.IsNullOrWhiteSpace(c.PatternRebuildCron));
            if (hasAnyEnabled)
            {
                RecurringJob.AddOrUpdate<StationShadowRecurringJobService>(
                    "station-shadow-pattern-rebuild-all",
                    service => service.ExecutePatternRebuildAllAsync(),
                    Cron.Hourly());
                result.ActivePatternJobs = 1;
            }
            else
            {
                RecurringJob.RemoveIfExists("station-shadow-pattern-rebuild-all");
            }

            return result;
        }

        private async Task<bool> ShouldRunCycleAsync(PLC_StationShadowConfigEntity config)
        {
            var projectId = config.ProjectId.GetValueOrDefault();
            var stationCode = config.StationCode?.Trim();
            if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                return false;
            }

            var runtimeLease = await _stationShadowStateCacheService.GetRuntimeLeaseAsync(projectId, stationCode);
            var runtimeStatus = runtimeLease?.Status?.Trim();
            if (!string.IsNullOrWhiteSpace(runtimeStatus)
                && (string.Equals(runtimeStatus, "Sampling", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(runtimeStatus, "Running", StringComparison.OrdinalIgnoreCase))
                && runtimeLease.LastHeartbeat.HasValue
                && (DateTime.Now - runtimeLease.LastHeartbeat.Value).TotalSeconds <= 120)
            {
                return false;
            }

            var state = await _stationShadowStateCacheService.GetStationStateAsync(projectId, stationCode);
            var intervalMs = config.CoreIntervalMs.GetValueOrDefault();
            if (intervalMs <= 0)
            {
                intervalMs = config.ContextIntervalMs.GetValueOrDefault();
            }

            intervalMs = Math.Max(intervalMs <= 0 ? MinimumSweepIntervalMs : intervalMs, MinimumSweepIntervalMs);
            if (!state?.LastSampleTime.HasValue ?? true)
            {
                return true;
            }

            return (DateTime.Now - state.LastSampleTime.Value).TotalMilliseconds >= intervalMs;
        }

        /// <summary>
        /// 数据保留清理任务：按各工位配置的 HistoryRetentionDays（默认 3 天）
        /// 物理删除 ai_plc_station_shadow_event 和 ai_plc_station_signal_history 中的过期记录。
        /// 每日由 Hangfire 自动执行，也可通过 Hangfire Dashboard 手动触发。
        /// </summary>
        [Hangfire.JobDisplayName("工位影子数据保留清理（每日02:00，物理删除超3天数据）")]
        public async Task ExecuteDataRetentionCleanupAsync()
        {
            var configs = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId != null && it.StationCode != null)
                .ToListAsync();

            var totalEventRows = 0;
            var totalHistoryRows = 0;

            foreach (var config in configs)
            {
                var projectId = config.ProjectId.GetValueOrDefault();
                var stationCode = config.StationCode?.Trim();
                if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
                {
                    continue;
                }

                var retentionDays = (config.HistoryRetentionDays.GetValueOrDefault() > 0)
                    ? config.HistoryRetentionDays.GetValueOrDefault()
                    : DefaultRetentionDays;

                var cutoff = DateTime.Now.AddDays(-retentionDays);

                try
                {
                    // 物理删除过期事件记录（ai_plc_station_shadow_event）
                    var eventDeleted = await _stationShadowEventService.DeleteAsync(
                        it => it.ProjectId == projectId
                           && it.StationCode == stationCode
                           && it.TriggeredAt != null
                           && it.TriggeredAt < cutoff);

                    // 物理删除过期信号历史记录（ai_plc_station_signal_history）
                    var historyDeleted = await _stationSignalHistoryService.DeleteAsync(
                        it => it.ProjectId == projectId
                           && it.StationCode == stationCode
                           && it.ChangedAt != null
                           && it.ChangedAt < cutoff);

                    if (eventDeleted)
                    {
                        totalEventRows++;
                    }

                    if (historyDeleted)
                    {
                        totalHistoryRows++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Data retention cleanup failed for project {ProjectId}, station {StationCode} (cutoff={Cutoff})",
                        projectId, stationCode, cutoff);
                }
            }

            _logger.LogInformation(
                "Data retention cleanup done. Stations with event deletes: {EventStations}, signal-history deletes: {HistoryStations}",
                totalEventRows, totalHistoryRows);
        }

        public static string BuildPatternRebuildJobId(long projectId, string stationCode)
        {
            var normalizedStation = string.IsNullOrWhiteSpace(stationCode) ? "unknown" : stationCode.Trim().Replace(':', '_').Replace('/', '_');
            return $"station-shadow-pattern-rebuild:{projectId}:{normalizedStation}";
        }
    }
}