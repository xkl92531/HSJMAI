using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.AlarmMonitor
{
    /// <summary>
    /// 报警监测 Worker 配置（appsettings "AlarmMonitor" 节，Program.cs 绑定到 Current）。
    /// 默认 Enabled=false —— 与 StationShadowWorker 一致的保守默认，联调时显式开启。
    /// </summary>
    public class AlarmMonitorWorkerConfig
    {
        private static AlarmMonitorWorkerConfig _current = new AlarmMonitorWorkerConfig();

        public static AlarmMonitorWorkerConfig Current
        {
            get => _current;
            set => _current = value ?? new AlarmMonitorWorkerConfig();
        }

        /// <summary>是否启用报警监测 Worker</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>采样轮询间隔（秒），最低 1</summary>
        public int IntervalSeconds { get; set; } = 2;

        /// <summary>故障开始去抖：连续 N 个采样周期为 true 才确认开始</summary>
        public int StartDebounceCount { get; set; } = 2;

        /// <summary>故障结束去抖：连续 N 个采样周期为 false 才确认结束</summary>
        public int EndDebounceCount { get; set; } = 2;

        /// <summary>是否广播到 FaultEventHub（false 时只落库不推送）</summary>
        public bool BroadcastEnabled { get; set; } = true;
    }

    /// <summary>
    /// 报警监测 BackgroundService。
    ///
    /// 数据流：ai_plc_alarm_variable（monitor_enabled=true 且 collectable=true 的行）
    ///   → 每周期批量读实时值（复用 PLC_VariableAccessService.GetReadData 的采集链路；
    ///     注：GetReadData 按用户决定保持老网关链路原样，IGW 项目上采 0 信号——2026-09-18）
    ///   → 边沿检测（去抖状态机）：true 沿确认 → 插入故障事件（started_at=首个 true 采样时刻）并广播 FaultStarted；
    ///     false 沿确认 → 回填 ended_at/duration_seconds 并广播 FaultEnded
    ///   → ai_plc_fault_event + FaultEventHub（Web 前端 + 外部 agent 小锦订阅）。
    ///
    /// 设计要点：
    ///   - 与 StationShadow（停机诊断）完全独立，互不影响；
    ///   - 采集失败周期不做任何状态推进（既不判开始也不判结束，避免采集抖动产生假故障）；
    ///   - 重启恢复：首次采样时从 DB 加载未关闭故障（ended_at IS NULL）对账 —— 已恢复的走正常结束链路补记结束。
    /// </summary>
    public class AlarmMonitorWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AlarmMonitorWorker> _logger;

        public AlarmMonitorWorker(IServiceScopeFactory scopeFactory, ILogger<AlarmMonitorWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>单变量运行时状态（内存态，Worker 生命周期内有效）</summary>
        private class VariableRuntimeState
        {
            public bool ConfirmedTrue;          // 当前确认态是否处于故障中
            public long? OpenEventId;           // 进行中故障事件 id
            public int TrueStreak, FalseStreak; // 连续 true/false 采样计数（去抖）
            public DateTime? FirstTrueAt;       // 本轮首个 true 采样时刻（故障开始时间）
            public DateTime? FirstFalseAt;      // 本轮首个 false 采样时刻（故障结束时间）
            // 广播/落库需要的字段快照（避免 END 时回查）
            public string AlarmLevel;
            public string Description;
            public string StationCode;
            public string VariableName;
            public long? AlarmVariableId;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var config = AlarmMonitorWorkerConfig.Current;
            if (!config.Enabled)
            {
                _logger.LogInformation("AlarmMonitorWorker is disabled via configuration.");
                return;
            }

            var intervalSeconds = Math.Max(config.IntervalSeconds, 1);
            _logger.LogInformation(
                "AlarmMonitorWorker started. Interval={Interval}s, StartDebounce={S}, EndDebounce={E}, Broadcast={B}",
                intervalSeconds, config.StartDebounceCount, config.EndDebounceCount, config.BroadcastEnabled);

            // 项目发现：台账中存在启用监控行的项目，每 30s 刷新
            var activeTasks = new Dictionary<long, Task>();
            var taskCts = new Dictionary<long, CancellationTokenSource>();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var projectIds = await DiscoverMonitoredProjectsAsync();

                    foreach (var pid in projectIds)
                    {
                        if (activeTasks.ContainsKey(pid)) continue;
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        taskCts[pid] = cts;
                        activeTasks[pid] = RunProjectLoopAsync(pid, intervalSeconds, cts.Token);
                        _logger.LogInformation("AlarmMonitor: started loop for ProjectId={ProjectId}", pid);
                    }

                    foreach (var pid in activeTasks.Keys.Where(k => !projectIds.Contains(k)).ToList())
                    {
                        if (taskCts.TryGetValue(pid, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                            taskCts.Remove(pid);
                        }
                        activeTasks.Remove(pid);
                        _logger.LogInformation("AlarmMonitor: stopped loop for ProjectId={ProjectId}", pid);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "AlarmMonitor discovery threw.");
                }

                await SafeDelay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            foreach (var cts in taskCts.Values) { cts.Cancel(); cts.Dispose(); }
            _logger.LogInformation("AlarmMonitorWorker stopped.");
        }

        private async Task<HashSet<long>> DiscoverMonitoredProjectsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var alarmVariableService = scope.ServiceProvider.GetRequiredService<IPLC_AlarmVariableService>();
            var projects = await alarmVariableService.GetListAsync(
                it => it.IsDeleted == false
                    && it.MonitorEnabled == true
                    && it.Collectable == true
                    && it.ProjectId != null);
            return projects
                .Where(p => p.ProjectId.HasValue)
                .Select(p => p.ProjectId.Value)
                .ToHashSet();
        }

        private async Task RunProjectLoopAsync(long projectId, int intervalSeconds, CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(intervalSeconds);
            var config = AlarmMonitorWorkerConfig.Current;
            var startDebounce = Math.Max(config.StartDebounceCount, 1);
            var endDebounce = Math.Max(config.EndDebounceCount, 1);

            var states = new Dictionary<string, VariableRuntimeState>(StringComparer.OrdinalIgnoreCase);
            var rowsCache = new List<PLC_AlarmVariableEntity>();
            var lastRowsReload = DateTime.MinValue;
            var projectNameCache = string.Empty;
            var projectNameLoaded = false;
            var consecutiveReadFailures = 0;

            while (!ct.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var alarmVariableService = scope.ServiceProvider.GetRequiredService<IPLC_AlarmVariableService>();
                    var faultEventService = scope.ServiceProvider.GetRequiredService<IPLC_FaultEventService>();
                    var variableAccessService = scope.ServiceProvider.GetRequiredService<IPLC_VariableAccessService>();
                    var broadcastService = scope.ServiceProvider.GetRequiredService<FaultEventBroadcastService>();
                    var projectService = scope.ServiceProvider.GetRequiredService<IPLC_ProjectService>();

                    // 30 个周期（约 1 分钟）重载一次台账行缓存
                    if ((DateTime.Now - lastRowsReload).TotalSeconds > 60 || rowsCache.Count == 0)
                    {
                        rowsCache = await alarmVariableService.GetListAsync(
                            it => it.IsDeleted == false
                                && it.ProjectId == projectId
                                && it.MonitorEnabled == true
                                && it.Collectable == true);
                        lastRowsReload = DateTime.Now;
                    }

                    if (rowsCache.Count == 0)
                    {
                        await SafeDelay(interval, ct);
                        continue;
                    }

                    if (!projectNameLoaded)
                    {
                        var project = await projectService.GetByIdAsync(projectId);
                        projectNameCache = project?.ProjectName ?? string.Empty;
                        projectNameLoaded = true;
                    }

                    var tagNames = rowsCache.Select(r => r.VariableName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

                    // 批量读实时值（采集失败 → 本周期不做任何状态推进）
                    List<PLC_ReadTagResult> readResults;
                    try
                    {
                        readResults = await variableAccessService.GetReadData(projectId, tagNames);
                        consecutiveReadFailures = 0;
                    }
                    catch (Exception readEx)
                    {
                        consecutiveReadFailures++;
                        if (consecutiveReadFailures % 10 == 1)
                        {
                            _logger.LogWarning(readEx,
                                "AlarmMonitor: 读取实时值失败（连续 {Count} 次），ProjectId={ProjectId}",
                                consecutiveReadFailures, projectId);
                        }
                        await SafeDelay(interval, ct);
                        continue;
                    }

                    var valueByName = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var read in readResults ?? new List<PLC_ReadTagResult>())
                    {
                        var key = read.TagName ?? read.OldTagName;
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        valueByName[key.Trim()] = read.IsGood ? CoerceBool(read.Value) : null;
                    }

                    var now = DateTime.Now;

                    // 首次采样：从 DB 加载未关闭故障对账（重启恢复）
                    if (states.Count == 0)
                    {
                        var openEvents = await faultEventService.GetListAsync(
                            it => it.IsDeleted == false && it.ProjectId == projectId && it.EndedAt == null);
                        foreach (var open in openEvents)
                        {
                            var name = (open.VariableName ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            var st = GetOrCreateState(states, name);
                            st.ConfirmedTrue = true;
                            st.OpenEventId = open.Id;
                            st.AlarmLevel = open.AlarmLevel;
                            st.Description = open.Description;
                            st.StationCode = open.StationCode;
                            st.VariableName = name;
                            st.AlarmVariableId = open.AlarmVariableId;
                        }
                    }

                    foreach (var row in rowsCache)
                    {
                        var name = (row.VariableName ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!valueByName.TryGetValue(name, out var value) || value == null)
                        {
                            continue; // 本周期没读到该变量（采集缺失）→ 不推进状态
                        }

                        var state = GetOrCreateState(states, name);
                        state.AlarmVariableId ??= row.Id;
                        state.AlarmLevel ??= row.AlarmLevel;
                        state.Description ??= row.Description;
                        state.StationCode ??= row.StationCode;
                        state.VariableName = name;

                        if (value.Value)
                        {
                            state.TrueStreak++;
                            state.FalseStreak = 0;
                            state.FirstTrueAt ??= now;
                            state.FirstFalseAt = null;

                            if (!state.ConfirmedTrue && state.TrueStreak >= startDebounce)
                            {
                                await HandleFaultStartAsync(faultEventService, broadcastService, alarmVariableService,
                                    projectId, projectNameCache, row, state, intervalSeconds);
                            }
                        }
                        else
                        {
                            state.FalseStreak++;
                            state.TrueStreak = 0;
                            state.FirstFalseAt ??= now;

                            if (state.ConfirmedTrue && state.OpenEventId.HasValue && state.FalseStreak >= endDebounce)
                            {
                                await HandleFaultEndAsync(faultEventService, broadcastService,
                                    projectId, projectNameCache, row, state);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AlarmMonitor: project loop threw, ProjectId={ProjectId}", projectId);
                }

                sw.Stop();
                var remaining = interval - sw.Elapsed;
                var minGap = TimeSpan.FromMilliseconds(200);
                if (remaining < minGap) remaining = minGap;
                await SafeDelay(remaining, ct);
            }

            _logger.LogInformation("AlarmMonitor: project loop exited, ProjectId={ProjectId}", projectId);
        }

        private async Task HandleFaultStartAsync(
            IPLC_FaultEventService faultEventService,
            FaultEventBroadcastService broadcastService,
            IPLC_AlarmVariableService alarmVariableService,
            long projectId, string projectName,
            PLC_AlarmVariableEntity row, VariableRuntimeState state, int intervalSeconds)
        {
            var now = DateTime.Now;
            var startedAt = state.FirstTrueAt ?? now;
            var config = AlarmMonitorWorkerConfig.Current;

            var evt = new PLC_FaultEventEntity
            {
                ProjectId = projectId,
                StationCode = state.StationCode,
                VariableName = state.VariableName,
                AlarmVariableId = state.AlarmVariableId ?? row.Id,
                AlarmLevel = state.AlarmLevel,
                Description = state.Description,
                StartedAt = startedAt,
                StartDebounceMs = intervalSeconds * 1000 * Math.Max(config.StartDebounceCount, 1),
                PushedToSubscriber = false,
                CreateTime = now,
                ModifyTime = now,
                IsDeleted = false
            };

            var eventId = await faultEventService.InsertSnowflakeIdAsync(evt);
            evt.Id = eventId;

            state.ConfirmedTrue = true;
            state.OpenEventId = eventId;
            state.TrueStreak = 0;
            state.FalseStreak = 0;
            state.FirstFalseAt = null;

            // 台账统计回写
            try
            {
                await alarmVariableService.UpdateAsync(
                    it => new PLC_AlarmVariableEntity
                    {
                        AlarmCount = SqlFunc.IsNull(it.AlarmCount, 0) + 1,
                        LastAlarmAt = startedAt,
                        ModifyTime = now
                    },
                    it => it.Id == row.Id);
            }
            catch (Exception statEx)
            {
                _logger.LogWarning(statEx, "AlarmMonitor: 台账统计回写失败 ProjectId={ProjectId} Variable={Variable}", projectId, state.VariableName);
            }

            // 广播（按行 push_enabled 开关）
            if (config.BroadcastEnabled && row.PushEnabled == true)
            {
                var ok = await broadcastService.BroadcastFaultStartedAsync(evt, projectName, row.Address, row.AlarmCategory);
                await MarkPushResultAsync(faultEventService, eventId, ok);
            }

            _logger.LogInformation(
                "AlarmMonitor: 故障开始 ProjectId={ProjectId} Variable={Variable} EventId={EventId} StartedAt={StartedAt}",
                projectId, state.VariableName, eventId, startedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private async Task HandleFaultEndAsync(
            IPLC_FaultEventService faultEventService,
            FaultEventBroadcastService broadcastService,
            long projectId, string projectName,
            PLC_AlarmVariableEntity row, VariableRuntimeState state)
        {
            var now = DateTime.Now;
            var endedAt = state.FirstFalseAt ?? now;
            var config = AlarmMonitorWorkerConfig.Current;

            await faultEventService.UpdateAsync(
                it => new PLC_FaultEventEntity
                {
                    EndedAt = endedAt,
                    ModifyTime = now
                },
                it => it.Id == state.OpenEventId.Value);

            // 回填时长（单独一步算秒数，避免表达式翻译问题）
            var evt = await faultEventService.GetByIdAsync(state.OpenEventId.Value);
            if (evt != null)
            {
                var duration = (int)Math.Max(0, (endedAt - evt.StartedAt).TotalSeconds);
                evt.DurationSeconds = duration;
                evt.ModifyTime = now;
                await faultEventService.UpdateAsync(evt);

                if (config.BroadcastEnabled && row.PushEnabled == true)
                {
                    var ok = await broadcastService.BroadcastFaultEndedAsync(evt, projectName, row.Address, row.AlarmCategory);
                    await MarkPushResultAsync(faultEventService, evt.Id, ok);
                }

                _logger.LogInformation(
                    "AlarmMonitor: 故障结束 ProjectId={ProjectId} Variable={Variable} EventId={EventId} Duration={Duration}s",
                    projectId, state.VariableName, evt.Id, duration);
            }

            state.ConfirmedTrue = false;
            state.OpenEventId = null;
            state.TrueStreak = 0;
            state.FalseStreak = 0;
            state.FirstTrueAt = null;
            state.FirstFalseAt = null;
        }

        private static async Task MarkPushResultAsync(IPLC_FaultEventService faultEventService, long eventId, bool ok)
        {
            try
            {
                await faultEventService.UpdateAsync(
                    it => new PLC_FaultEventEntity
                    {
                        PushedToSubscriber = ok,
                        PushError = ok ? null : "广播失败（详见后端日志）"
                    },
                    it => it.Id == eventId);
            }
            catch
            {
                // 推送标记失败不影响主流程
            }
        }

        private static VariableRuntimeState GetOrCreateState(Dictionary<string, VariableRuntimeState> states, string name)
        {
            if (!states.TryGetValue(name, out var st))
            {
                st = new VariableRuntimeState { VariableName = name };
                states[name] = st;
            }
            return st;
        }

        /// <summary>
        /// 实时值 → bool（兼容 bool/int/long/double/string）；无法判定返回 null（本周期不推进状态）
        /// </summary>
        private static bool? CoerceBool(object value)
        {
            switch (value)
            {
                case null: return null;
                case bool b: return b;
                case int i: return i != 0;
                case long l: return l != 0;
                case double d: return d != 0;
                case string s:
                    if (bool.TryParse(s, out var bb)) return bb;
                    if (s == "1") return true;
                    if (s == "0") return false;
                    return null;
                default: return null;
            }
        }

        private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { }
        }
    }
}
