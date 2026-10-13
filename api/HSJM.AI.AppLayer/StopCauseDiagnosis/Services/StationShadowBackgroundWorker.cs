using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 高频影子监控 BackgroundService。
    /// 每个工位独立并行采样，各自按 IntervalSeconds 频率轮询，互不阻塞。
    /// 通过 appsettings "StationShadowWorker" 节控制：
    ///   Enabled        = true/false  → 总开关
    ///   IntervalSeconds = 2          → 轮询间隔（秒）
    /// </summary>
    public class StationShadowBackgroundWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StationShadowBackgroundWorker> _logger;

        public StationShadowBackgroundWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<StationShadowBackgroundWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var config = StationShadowWorkerConfig.Current;
            if (!config.Enabled)
            {
                _logger.LogInformation("StationShadowBackgroundWorker is disabled via configuration.");
                return;
            }

            var intervalSeconds = Math.Max(config.IntervalSeconds, 1);
            _logger.LogInformation("StationShadowBackgroundWorker started. Interval={IntervalSeconds}s (parallel per-station)", intervalSeconds);

            // 每 30 秒重新发现工位列表，为每个工位维护独立的并行采样任务
            var activeStationTasks = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
            var stationCts = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (StationShadowWorkerConfig.Paused)
                {
                    _logger.LogDebug("StationShadowBackgroundWorker is paused, skipping discovery.");
                    await SafeDelay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                try
                {
                    var stationKeys = await DiscoverEnabledStationsAsync();

                    // 启动新发现的工位
                    foreach (var key in stationKeys)
                    {
                        if (activeStationTasks.ContainsKey(key)) continue;

                        var parts = key.Split('|');
                        var projectId = long.Parse(parts[0]);
                        var stationCode = parts[1];
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        stationCts[key] = cts;
                        activeStationTasks[key] = RunStationLoopAsync(projectId, stationCode, intervalSeconds, cts.Token);
                        _logger.LogInformation("Started parallel sampler for {StationKey}, interval={Interval}s", key, intervalSeconds);
                    }

                    // 停止已移除/禁用的工位
                    var keysToRemove = activeStationTasks.Keys.Where(k => !stationKeys.Contains(k)).ToList();
                    foreach (var key in keysToRemove)
                    {
                        if (stationCts.TryGetValue(key, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                            stationCts.Remove(key);
                        }
                        activeStationTasks.Remove(key);
                        _logger.LogInformation("Stopped parallel sampler for {StationKey}", key);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "StationShadowBackgroundWorker discovery threw.");
                }

                await SafeDelay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            // 清理
            foreach (var cts in stationCts.Values) { cts.Cancel(); cts.Dispose(); }
            await Task.WhenAll(activeStationTasks.Values.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            _logger.LogInformation("StationShadowBackgroundWorker stopped.");
        }

        /// <summary>
        /// 单工位独立采样循环，按固定间隔轮询，不受其他工位阻塞。
        /// </summary>
        private async Task RunStationLoopAsync(long projectId, string stationCode, int intervalSeconds, CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(intervalSeconds);
            // 诊断计数器：每 N 个采样周期执行一次 pending 事件诊断（避免每次都做）
            var cycleCount = 0;
            const int diagnosisEveryN = 15; // 约 30 秒做一次诊断（intervalSeconds=2 时）

            while (!ct.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var diagnosisApp = scope.ServiceProvider.GetRequiredService<StopCauseDiagnosisApp>();

                    cycleCount++;
                    var doDiagnosis = cycleCount % diagnosisEveryN == 0;

                    await diagnosisApp.RunMonitoringCycleAsync(new RunStationShadowCycleRequestDto
                    {
                        ProjectId = projectId,
                        StationCode = stationCode,
                        PersistHistory = true,
                        PersistSnapshot = true,
                        PersistEvents = true,
                        JobSource = "BackgroundWorker",
                        SnapshotRecentBundleCount = 10,
                        DiagnosePendingAfterCycle = doDiagnosis,
                        DiagnosisEventLimit = doDiagnosis ? 5 : 0,
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Station sampler threw for project {ProjectId}, station {StationCode}", projectId, stationCode);
                }

                // 减去已花费的采样时间，确保尽量接近配置的频率。
                // 最低保留 200 ms 空隙，防止 PLC 离线时出现空转紧密循环。
                sw.Stop();
                var remaining = interval - sw.Elapsed;
                var minGap = TimeSpan.FromMilliseconds(200);
                if (remaining < minGap) remaining = minGap;
                await SafeDelay(remaining, ct);
            }
        }

        private async Task<HashSet<string>> DiscoverEnabledStationsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IPLC_StationShadowConfigService>();
            var configs = await configService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.Enabled == true && it.ProjectId != null && it.StationCode != null)
                .Select(it => new { it.ProjectId, it.StationCode })
                .ToListAsync();

            return configs
                .Where(c => c.ProjectId.HasValue && !string.IsNullOrWhiteSpace(c.StationCode))
                .Select(c => $"{c.ProjectId.Value}|{c.StationCode.Trim()}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// 高频 Worker 配置
    /// </summary>
    public class StationShadowWorkerConfig
    {
        private static StationShadowWorkerConfig _current = new StationShadowWorkerConfig();

        /// <summary>
        /// 运行时暂停标志（不持久化，重启恢复）。可通过 API 动态控制。
        /// </summary>
        private static volatile bool _paused = false;

        public static bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        /// <summary>
        /// 全局单例，在 Program.cs 绑定 appsettings
        /// </summary>
        public static StationShadowWorkerConfig Current
        {
            get => _current;
            set => _current = value ?? new StationShadowWorkerConfig();
        }

        /// <summary>
        /// 是否启用高频 Worker（默认 false，不开启则退回 Hangfire 每分钟模式）
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 轮询间隔（秒），最低 5 秒
        /// </summary>
        public int IntervalSeconds { get; set; } = 10;
    }
}
