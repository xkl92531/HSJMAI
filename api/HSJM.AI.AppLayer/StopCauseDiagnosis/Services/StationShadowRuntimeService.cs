using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class StationShadowRuntimeService : ISelfScopedAutoInject
    {
        private const int DefaultSilenceThresholdSeconds = 30;

        // ── 进程内静态缓存：减少每 2 秒一次的重复 DB 查询 ──
        private static readonly ConcurrentDictionary<string, (PLC_StationShadowConfigEntity Config, DateTime ExpiresAt)> _configCache = new();
        private static readonly ConcurrentDictionary<string, (List<PLC_StationShadowSignalEntity> Signals, DateTime ExpiresAt)> _signalsCache = new();
        private const int ConfigCacheTtlSeconds = 60;
        private const int SignalsCacheTtlSeconds = 60;

        /// <summary>主动失效某工位的信号配置缓存（增删信号后调用）。</summary>
        public static void InvalidateStationCache(long projectId, string stationCode)
        {
            var key = $"{projectId}|{stationCode}";
            _configCache.TryRemove(key, out _);
            _signalsCache.TryRemove(key, out _);
        }

        private readonly StopCauseSignalSnapshotService _signalSnapshotService;
        private readonly StationShadowStateCacheService _stateCacheService;
        private readonly IPLC_StationShadowConfigService _stationShadowConfigService;
        private readonly IPLC_StationShadowSignalService _stationShadowSignalService;
        private readonly IPLC_StationSignalHistoryService _stationSignalHistoryService;
        private readonly IPLC_StationShadowEventService _stationShadowEventService;
        private readonly IPLC_StationShadowEventContextService _stationShadowEventContextService;
        private readonly StationShadowMonitoringBroadcastService _monitoringBroadcastService;
        private readonly ILogger<StationShadowRuntimeService> _logger;

        public StationShadowRuntimeService(
            StopCauseSignalSnapshotService signalSnapshotService,
            StationShadowStateCacheService stateCacheService,
            IPLC_StationShadowConfigService stationShadowConfigService,
            IPLC_StationShadowSignalService stationShadowSignalService,
            IPLC_StationSignalHistoryService stationSignalHistoryService,
            IPLC_StationShadowEventService stationShadowEventService,
            IPLC_StationShadowEventContextService stationShadowEventContextService,
            StationShadowMonitoringBroadcastService monitoringBroadcastService,
            ILogger<StationShadowRuntimeService> logger)
        {
            _signalSnapshotService = signalSnapshotService;
            _stateCacheService = stateCacheService;
            _stationShadowConfigService = stationShadowConfigService;
            _stationShadowSignalService = stationShadowSignalService;
            _stationSignalHistoryService = stationSignalHistoryService;
            _stationShadowEventService = stationShadowEventService;
            _stationShadowEventContextService = stationShadowEventContextService;
            _monitoringBroadcastService = monitoringBroadcastService;
            _logger = logger;
        }

        public async Task<RunStationShadowCycleResultDto> RunCycleAsync(RunStationShadowCycleRequestDto request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var projectId = request.ProjectId;
            var stationCode = NormalizeRequired(request.StationCode, nameof(request.StationCode));
            var now = DateTime.Now;
            var runtimeNodeName = ResolveRuntimeNodeName(request.RuntimeNodeName);
            var result = new RunStationShadowCycleResultDto
            {
                ProjectId = projectId,
                StationCode = stationCode,
                SampleTime = now
            };

            await SaveLeaseAsync(projectId, stationCode, runtimeNodeName, request.JobSource, "Sampling", now, now);

            try
            {
                // 配置缓存（60 秒 TTL）
                var cacheKey = $"{projectId}|{stationCode}";
                PLC_StationShadowConfigEntity config;
                if (!_configCache.TryGetValue(cacheKey, out var configEntry) || DateTime.Now >= configEntry.ExpiresAt)
                {
                    config = await _stationShadowConfigService.GetDb().AsQueryable()
                        .Where(it => it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == stationCode)
                        .OrderBy(it => it.ModifyTime, OrderByType.Desc)
                        .OrderBy(it => it.CreateTime, OrderByType.Desc)
                        .FirstAsync();
                    if (config != null)
                        _configCache[cacheKey] = (config, DateTime.Now.AddSeconds(ConfigCacheTtlSeconds));
                }
                else
                {
                    config = configEntry.Config;
                }

                result.ConfigFound = config != null;
                result.ConfigEnabled = config?.Enabled == true;
                if (config == null)
                {
                    result.Status = "ConfigMissing";
                    result.Warnings.Add("未找到工位影子监控配置");
                    await SaveLeaseAsync(projectId, stationCode, runtimeNodeName, request.JobSource, "Idle", now, DateTime.Now);
                    return result;
                }

                if (config.Enabled != true)
                {
                    result.Status = "Disabled";
                    result.Warnings.Add("工位影子监控配置已禁用");
                    await SaveLeaseAsync(projectId, stationCode, runtimeNodeName, request.JobSource, "Idle", now, DateTime.Now);
                    return result;
                }

                // 信号列表缓存（60 秒 TTL）
                List<PLC_StationShadowSignalEntity> signalEntities;
                if (!_signalsCache.TryGetValue(cacheKey, out var signalsEntry) || DateTime.Now >= signalsEntry.ExpiresAt)
                {
                    var raw = await _stationShadowSignalService.GetDb().AsQueryable()
                        .Where(it => it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == stationCode && it.Enabled == true)
                        .OrderBy(it => it.SortOrder, OrderByType.Asc)
                        .OrderBy(it => it.Priority, OrderByType.Asc)
                        .OrderBy(it => it.Id, OrderByType.Asc)
                        .ToListAsync();
                    signalEntities = raw
                        .Where(it => !string.IsNullOrWhiteSpace(it.SignalKey) && !string.IsNullOrWhiteSpace(it.VariableName))
                        .GroupBy(it => it.SignalKey.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Select(it => it.First())
                        .ToList();
                    _signalsCache[cacheKey] = (signalEntities, DateTime.Now.AddSeconds(SignalsCacheTtlSeconds));
                }
                else
                {
                    signalEntities = signalsEntry.Signals;
                }

                result.SignalCount = signalEntities.Count;
                result.CoreSignalCount = signalEntities.Count(it => it.IsCore == true);
                result.ContextSignalCount = signalEntities.Count(it => it.IsContext == true);
                if (signalEntities.Count == 0)
                {
                    result.Status = "NoSignals";
                    result.Warnings.Add("当前工位没有启用的监控信号");
                    await SaveLeaseAsync(projectId, stationCode, runtimeNodeName, request.JobSource, "Idle", now, DateTime.Now);
                    return result;
                }

                var previousState = await _stateCacheService.GetStationStateAsync(projectId, stationCode);
                var previousSignalMirrors = await _stateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                // 从已加载的 signalEntities 预构建 AnchorTypeMap，传入快照请求，避免 SnapshotService 再次查库
                // 同一 VariableName 可能对应多条 GraphAction 信号（不同 LogicUnitId），
                // 此处只需 AnchorType 映射，按 VariableName 去重取第一条即可
                var anchorTypeMap = signalEntities
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName) && !string.IsNullOrWhiteSpace(it.AnchorType))
                    .GroupBy(it => NormalizeSymbolPath(it.VariableName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.First().AnchorType.Trim(),
                        StringComparer.OrdinalIgnoreCase);
                var snapshotRequest = BuildSnapshotRequest(request, signalEntities, previousSignalMirrors, previousState, anchorTypeMap);
                var snapshotResult = await _signalSnapshotService.AcquireAsync(snapshotRequest);
                var executionContext = snapshotResult.ExecutionContext ?? new StopCauseExecutionContext();
                var diagnosticSnapshot = snapshotResult.Snapshot ?? new DiagnosticSnapshot();

                RemoveStaleSignalMirrors(projectId, stationCode, previousSignalMirrors, signalEntities);

                var changes = new List<StationShadowChangeCacheModel>();
                var signalMirrors = new List<StationShadowSignalCacheModel>(signalEntities.Count);
                foreach (var signalEntity in signalEntities)
                {
                    var signalKey = NormalizeRequired(signalEntity.SignalKey, nameof(signalEntity.SignalKey));
                    var symbolPath = NormalizeSymbolPath(signalEntity.VariableName);
                    previousSignalMirrors.TryGetValue(signalKey, out var previousMirror);

                    executionContext.CurrentValues.TryGetValue(symbolPath, out var currentValue);
                    executionContext.PreviousValues.TryGetValue(symbolPath, out var previousValueFromSnapshot);
                    executionContext.SignalQuality.TryGetValue(symbolPath, out var quality);
                    diagnosticSnapshot.LastChangeTimes.TryGetValue(symbolPath, out var lastChangeTime);

                    var signalMirror = new StationShadowSignalCacheModel
                    {
                        SignalKey = signalKey,
                        VariableName = symbolPath,
                        CurrentValue = currentValue,
                        PreviousValue = previousMirror?.CurrentValue ?? previousValueFromSnapshot,
                        LastChangeTime = lastChangeTime ?? previousMirror?.LastChangeTime,
                        Quality = string.IsNullOrWhiteSpace(quality) ? previousMirror?.Quality : quality,
                        GroupName = signalEntity.GroupName,
                        SignalType = signalEntity.SignalType,
                        AnchorType = signalEntity.AnchorType,
                        IsCore = signalEntity.IsCore == true,
                        LogicUnitId = signalEntity.LogicUnitId,
                        SemanticLabel = signalEntity.SemanticLabel,
                        GraphStepRole = signalEntity.GraphStepRole,
                        GraphStepRoleConfidence = signalEntity.GraphStepRoleConfidence,
                        GraphStepRoleReason = signalEntity.GraphStepRoleReason
                    };

                    signalMirrors.Add(signalMirror);

                    if (!ValuesEqual(signalMirror.PreviousValue, signalMirror.CurrentValue))
                    {
                        // 跳过 STRING 类型信号变化：字符串值不参与变化跟踪、Bundle、诊断
                        if (IsStringValue(signalMirror.CurrentValue))
                            continue;

                        changes.Add(new StationShadowChangeCacheModel
                        {
                            ChangeId = GenerateChangeId(signalKey, now),
                            SignalKey = signalKey,
                            VariableName = signalEntity.VariableName,
                            OldValue = signalMirror.PreviousValue,
                            NewValue = signalMirror.CurrentValue,
                            ChangedAt = signalMirror.LastChangeTime ?? now,
                            StationMode = ResolveStationMode(request.StationMode, previousState?.StationMode),
                            GroupName = signalMirror.GroupName,
                            SignalType = signalMirror.SignalType,
                            Quality = signalMirror.Quality,
                            LogicUnitId = signalMirror.LogicUnitId
                        });
                    }
                }
                // 批量写入所有信号镜像（单次 Redis HSET，替代 N 次个别调用）
                await _stateCacheService.SaveSignalMirrorsAsync(projectId, stationCode, signalMirrors);

                result.ChangedSignalCount = changes.Count;
                result.Changes = changes;
                result.StationMode = ResolveStationMode(request.StationMode, previousState?.StationMode);
                result.MissingSymbols = (snapshotResult.MissingSymbols ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result.MissingSymbolCount = result.MissingSymbols.Count;

                var snapshotId = GenerateSnapshotId(now);
                var bundle = changes.Count > 0
                    ? BuildBundle(changes, result.StationMode, now)
                    : null;

                if (changes.Count > 0)
                {
                    // 批量写入变化记录（单次 ListTrim，替代 N 次）
                    await _stateCacheService.AppendChangesAsync(projectId, stationCode, changes);

                    if (bundle != null)
                    {
                        await _stateCacheService.AppendBundleAsync(projectId, stationCode, bundle);
                        result.BundleCreated = true;
                        result.BundleId = bundle.BundleId;
                    }
                }

                if (request.PersistHistory && changes.Count > 0)
                {
                    var historyRows = BuildHistoryRows(projectId, stationCode, signalEntities, changes, bundle?.BundleId, snapshotId);
                    if (historyRows.Count > 0)
                    {
                        await _stationSignalHistoryService.InsertRangeAsync(historyRows);
                    }
                }

                var lastActionTime = changes.Count > 0
                    ? now
                    : previousState?.LastActionTime;
                var lastBundleTime = bundle != null
                    ? (bundle.EndTime ?? now)
                    : previousState?.LastBundleTime;
                var silenceThreshold = config.SilenceThresholdSeconds.GetValueOrDefault(DefaultSilenceThresholdSeconds);
                // 冷启动修正：lastActionTime 为 null（首次采样或设备开机就停着）时，
                // 以「首次采样时间」（FirstSampleTime，状态创建后不动）起算静默时长。
                // 2026-09-18 修复：原实现误用 LastSampleTime（每轮采样都刷新）→ 静默秒数
                // 永远不超过一个巡检周期，在 30s 阈值上下抖动：isSilent=true 一旦被保存
                // 边沿即被消费，除非恰好再来一轮 <30s 间隔的周期（手动/UI 触发）存回
                // false 才重新武装——当日实测 OP030-OP070 静默 40+ 分钟零 Silent 事件。
                int silentSeconds;
                if (lastActionTime.HasValue)
                {
                    silentSeconds = Math.Max(0, (int)(now - lastActionTime.Value).TotalSeconds);
                }
                else
                {
                    var silenceOrigin = previousState?.FirstSampleTime ?? previousState?.LastSampleTime;
                    silentSeconds = silenceOrigin.HasValue ? Math.Max(0, (int)(now - silenceOrigin.Value).TotalSeconds) : 0;
                }
                var isSilent = changes.Count == 0 && silentSeconds >= silenceThreshold;
                var silentTransition = isSilent && !(previousState?.IsSilent ?? false);
                var resumeTransition = !isSilent && previousState?.IsSilent == true && changes.Count > 0;

                var snapshotModel = await BuildSnapshotModel(
                    projectId,
                    stationCode,
                    snapshotId,
                    result.StationMode,
                    signalEntities,
                    previousSignalMirrors,
                    executionContext,
                    diagnosticSnapshot,
                    bundle,
                    now,
                    request.SnapshotRecentBundleCount);

                if (request.PersistSnapshot)
                {
                    await _stateCacheService.SaveSnapshotAsync(projectId, stationCode, snapshotModel, config.SnapshotTtlSeconds.GetValueOrDefault(StopCauseMonitoringRedisKeyBuilder.SnapshotTtlSeconds));
                    result.SnapshotId = snapshotModel.SnapshotId;
                }

                StationShadowEventDto eventDto = null;
                if (request.PersistEvents && (silentTransition || resumeTransition))
                {
                    // 静默判定轮 changes 必为空 → 本轮 bundle 必为 null。Silent 事件必须携带
                    // "静默开始前最后一个变化束"（RecentBundles 头部=最近一条）作为诊断签名来源，
                    // 否则诊断链路第一步（按 LastBundleSignature 查模板）必然断在 NoSignature。
                    var eventBundle = bundle;
                    if (silentTransition && eventBundle == null)
                    {
                        var recentBundlesForEvent = await _stateCacheService.GetRecentBundlesAsync(projectId, stationCode, 1);
                        eventBundle = recentBundlesForEvent.FirstOrDefault();
                    }

                    var eventEntity = new PLC_StationShadowEventEntity
                    {
                        ProjectId = projectId,
                        StationCode = stationCode,
                        EventType = silentTransition ? "Silent" : "Resume",
                        EventLevel = silentTransition ? "Warning" : "Info",
                        TriggeredAt = now,
                        StationMode = result.StationMode,
                        LastBundleId = eventBundle?.BundleId,
                        LastBundleSignature = eventBundle?.BundleSignature,
                        // 启发式兜底：SignalKeys 为字母序非时序，此处取字母序最大仅作锚点提示，
                        // 精确最后变化信号待 Bundle 模型增加时序后修正
                        LastChangedSignal = changes.FirstOrDefault()?.SignalKey
                            ?? (eventBundle?.SignalKeys != null && eventBundle.SignalKeys.Count > 0
                                ? eventBundle.SignalKeys[eventBundle.SignalKeys.Count - 1]
                                : null),
                        SnapshotId = snapshotModel.SnapshotId,
                        DiagnosisStatus = silentTransition ? "Pending" : "Recovered",
                        SignalCount = signalEntities.Count,
                        BundleCount = eventBundle == null ? 0 : 1,
                        SourceRuntimeNode = runtimeNodeName,
                        IsArchived = false,
                        CreateTime = now,
                        ModifyTime = now,
                        IsDeleted = false
                    };
                    await _stationShadowEventService.InsertAsync(eventEntity);

                    // ── 2026-09-18 事件生命周期闭环：新 Silent/Resume 落库后归档同工位历史未归档事件 ──
                    // 此前旧事件永不关闭（无任何代码置 is_archived），事件无限积压——PLC2 曾自
                    // 09-15 起积压 130+ 条未闭 Silent/Resume，监控页长期把站点显示为异常。
                    // 每站仅保留最新一条未归档事件作为"当前状态"，历史事件归档供追溯。
                    try
                    {
                        await _stationShadowEventService.UpdateAsync(
                            e => new PLC_StationShadowEventEntity
                            {
                                IsArchived = true,
                                ModifyTime = now
                            },
                            e => e.ProjectId == projectId
                                && e.StationCode == stationCode
                                && e.Id != eventEntity.Id
                                && (e.IsArchived == false || e.IsArchived == null)
                                && e.IsDeleted == false);
                        _logger.LogInformation(
                            "StationShadow 事件 {EventType}#{EventId} 创建后归档历史未闭事件（工位 {ProjectId}/{StationCode}）",
                            eventEntity.EventType, eventEntity.Id, projectId, stationCode);
                    }
                    catch (Exception archiveEx)
                    {
                        _logger.LogWarning(archiveEx,
                            "StationShadow 归档工位 {ProjectId}/{StationCode} 历史事件失败（不阻断事件主流程）", projectId, stationCode);
                    }

                    // ── T3 事件后回填 event_id（阶段1 确定性现场保全）──
                    // 事件落库成功后，把本工位历史变化表中「尚未被任何事件认领（event_id IS NULL）」且
                    // changed_at 落在 [triggered_at - 静默回溯窗, triggered_at] 区间内的行打上本事件 id，
                    // 让 BI/诊断链路能直接按 event_id 拉到事件的现场信号明细。
                    // 回溯窗口径 = SilenceThresholdSeconds × 2（默认 30s×2 = 60s）：
                    //  - Silent 事件：本判定轮 changes 必为空，"静默开始前的最后变化束" 发生在约
                    //    silenceThreshold 秒之前（silentSeconds >= threshold 才触发），2× 阈值回看窗
                    //    可稳妥覆盖并容忍采样周期抖动；
                    //  - Resume 事件：本轮 changes 已在本轮先行写入历史表（event_id 为 NULL），
                    //    天然落在窗口内；若静默时长较短，窗口还可能顺带覆盖静默开始前的尾部变化——
                    //    它们尚未被之前的 Silent 事件认领（IS NULL 过滤），按"确定性现场保全"语义
                    //    归属本 Resume 事件，不额外排除。
                    // 失败不阻断主流程：event_id 回填是尽力而为的增强，仅记错误日志。
                    try
                    {
                        // 事件时间口径：eventEntity.TriggeredAt 即本轮采样时刻 now（构造时已赋值），
                        // 此处直接用 now 作为窗口上界，避免可空 DateTime 取值分支。
                        var backfillWindowStart = now.AddSeconds(-silenceThreshold * 2);
                        var backfilled = await _stationSignalHistoryService.UpdateAsync(
                            h => new PLC_StationSignalHistoryEntity
                            {
                                EventId = eventEntity.Id,
                                ModifyTime = now
                            },
                            h => h.ProjectId == projectId
                                && h.StationCode == stationCode
                                && h.EventId == null
                                && h.ChangedAt >= backfillWindowStart
                                && h.ChangedAt <= now);
                        _logger.LogInformation(
                            "StationShadow 事件 {EventType}#{EventId} 回填 event_id {Result}：工位 {ProjectId}/{StationCode}，窗口 [{Start:O} ~ {End:O}]",
                            eventEntity.EventType, eventEntity.Id, backfilled ? "命中" : "无未认领行", projectId, stationCode, backfillWindowStart, eventEntity.TriggeredAt);
                    }
                    catch (Exception backfillEx)
                    {
                        _logger.LogError(backfillEx,
                            "StationShadow 事件 {EventType}#{EventId} 回填 event_id 失败（工位 {ProjectId}/{StationCode}），不影响事件主流程",
                            eventEntity.EventType, eventEntity.Id, projectId, stationCode);
                    }

                    // ── T4 事件现场快照固化到 PG（阶段1 确定性现场保全）──
                    // Redis 快照受 SnapshotTtlSeconds（默认 3 天）TTL 限制，过期后事件的现场
                    // （全信号当前值/前值/最后变化时间/质量 + 最近变化束序列）即不可追溯。
                    // 事件创建成功后把 snapshotModel（本轮完整快照，含 RecentBundles）与
                    // eventBundle（Silent 时为静默前最后变化束，可能来自 Redis 回退；Resume 时
                    // 即本轮变化束）序列化固化到 ai_plc_station_shadow_event_context，作为事件
                    // 的永久现场底档。采用轮内 await 而非 fire-and-forget：事件是罕见的状态跃迁，
                    // 每 2 秒周期最多触发一次，一次 INSERT 开销可忽略；且后台 Task.Run 会脱离
                    // scoped 服务生命周期（DbContext 释放后写入必失败）。失败不阻断主流程。
                    // ⚠️ 已知边界：静默前束取自 Redis RecentBundles（TTL 24h），停机超 24h 的事件
                    // 可能仍无签名（eventBundle 为 null → bundle_json 落 NULL）——事件上下文固化表
                    // 保留的 snapshot（含全信号现值）可部分补偿该场景的现场缺失。
                    try
                    {
                        await _stationShadowEventContextService.EnsureSchemaAsync();
                        var contextEntity = new PLC_StationShadowEventContextEntity
                        {
                            EventId = eventEntity.Id,
                            ProjectId = projectId,
                            StationCode = stationCode,
                            EventType = eventEntity.EventType,
                            SnapshotId = snapshotModel.SnapshotId,
                            ContextJson = JsonSerializer.Serialize(snapshotModel),
                            BundleJson = eventBundle == null ? null : JsonSerializer.Serialize(eventBundle),
                            CreateTime = now,
                            ModifyTime = now,
                            IsDeleted = false
                        };
                        await _stationShadowEventContextService.InsertAsync(contextEntity);
                        _logger.LogInformation(
                            "StationShadow 事件 {EventType}#{EventId} 现场快照已固化（snapshot={SnapshotId}, bundle={BundleId}）",
                            eventEntity.EventType, eventEntity.Id, snapshotModel.SnapshotId, eventBundle?.BundleId);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogError(persistEx,
                            "StationShadow 事件 {EventType}#{EventId} 现场快照固化失败（工位 {ProjectId}/{StationCode}），不影响事件主流程",
                            eventEntity.EventType, eventEntity.Id, projectId, stationCode);
                    }

                    eventDto = new StationShadowEventDto
                    {
                        Id = eventEntity.Id,
                        ProjectId = eventEntity.ProjectId,
                        StationCode = eventEntity.StationCode,
                        EventType = eventEntity.EventType,
                        EventLevel = eventEntity.EventLevel,
                        TriggeredAt = eventEntity.TriggeredAt,
                        StationMode = eventEntity.StationMode,
                        LastBundleId = eventEntity.LastBundleId,
                        LastBundleSignature = eventEntity.LastBundleSignature,
                        LastChangedSignal = eventEntity.LastChangedSignal,
                        SnapshotId = eventEntity.SnapshotId,
                        DiagnosisStatus = eventEntity.DiagnosisStatus,
                        SignalCount = eventEntity.SignalCount,
                        BundleCount = eventEntity.BundleCount,
                        SourceRuntimeNode = eventEntity.SourceRuntimeNode,
                        IsArchived = eventEntity.IsArchived ?? false
                    };
                    result.EventId = eventEntity.Id.ToString();
                }

                var stateModel = new StationShadowStateCacheModel
                {
                    Status = isSilent ? "Silent" : (changes.Count > 0 ? "Running" : (previousState?.Status ?? "Running")),
                    StationMode = result.StationMode,
                    LastSampleTime = now,
                    // 首次采样时间保持粘性：状态创建（或过期重建）时锚定为当时，出现 change 后
                    // 静默起算切换为 LastActionTime，本字段不再参与计算但保留溯源。
                    FirstSampleTime = previousState?.FirstSampleTime ?? now,
                    LastActionTime = lastActionTime,
                    LastBundleTime = lastBundleTime,
                    IsSilent = isSilent,
                    SilentSeconds = silentSeconds,
                    LastEventId = eventDto?.Id.ToString() ?? previousState?.LastEventId,
                    LastSnapshotId = request.PersistSnapshot ? snapshotModel.SnapshotId : previousState?.LastSnapshotId,
                    CoreSignalCount = result.CoreSignalCount,
                    ContextSignalCount = result.ContextSignalCount,
                    RuntimeVersion = "v1-runtime-cycle",
                    SamplerNode = runtimeNodeName
                };

                await _stateCacheService.SaveStationStateAsync(projectId, stationCode, stateModel);
                await SaveLeaseAsync(projectId, stationCode, runtimeNodeName, request.JobSource, "Idle", now, DateTime.Now);

                result.Status = stateModel.Status;
                result.IsSilent = stateModel.IsSilent;
                result.SilentSeconds = stateModel.SilentSeconds;
                result.LastActionTime = stateModel.LastActionTime;
                result.LastBundleTime = stateModel.LastBundleTime;
                if (!request.PersistSnapshot)
                {
                    result.SnapshotId = snapshotModel.SnapshotId;
                }

                await _monitoringBroadcastService.BroadcastCycleCompletedAsync(new StationShadowRealtimeCycleMessageDto
                {
                    ProjectId = result.ProjectId,
                    StationCode = result.StationCode,
                    SampleTime = result.SampleTime,
                    StationMode = result.StationMode,
                    Status = result.Status,
                    SignalCount = result.SignalCount,
                    ChangedSignalCount = result.ChangedSignalCount,
                    IsSilent = result.IsSilent,
                    SilentSeconds = result.SilentSeconds,
                    BundleId = result.BundleId,
                    BundleLabel = bundle?.BundleLabel,
                    SnapshotId = result.SnapshotId,
                    EventId = result.EventId,
                    EventType = eventDto?.EventType,
                    EventLevel = eventDto?.EventLevel,
                    RuntimeNodeName = runtimeNodeName,
                    JobSource = request.JobSource,
                    MissingSymbolCount = result.MissingSymbolCount,
                    MissingSymbols = result.MissingSymbols ?? new List<string>(),
                    ChangeSignalKeys = changes.Select(it => it.SignalKey)
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(it => it)
                        .ToList()
                });

                return result;
            }
            catch (Exception ex)
            {
                await SaveLeaseAsync(projectId, stationCode, runtimeNodeName, request.JobSource, "Error", now, DateTime.Now);
                await _monitoringBroadcastService.BroadcastCycleFailedAsync(projectId, stationCode, runtimeNodeName, request.JobSource, ex.Message);
                throw;
            }
        }

        private static AcquireDiagnosticSnapshotRequestDto BuildSnapshotRequest(
            RunStationShadowCycleRequestDto request,
            List<PLC_StationShadowSignalEntity> signalEntities,
            Dictionary<string, StationShadowSignalCacheModel> previousSignalMirrors,
            StationShadowStateCacheModel previousState,
            Dictionary<string, string> anchorTypeMap = null)
        {
            var previousValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var previousSignalQuality = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lastChangeTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

            foreach (var signalEntity in signalEntities)
            {
                var signalKey = NormalizeRequired(signalEntity.SignalKey, nameof(signalEntity.SignalKey));
                var symbolPath = NormalizeSymbolPath(signalEntity.VariableName);
                if (!previousSignalMirrors.TryGetValue(signalKey, out var previousMirror))
                {
                    continue;
                }

                previousValues[symbolPath] = previousMirror.CurrentValue;
                previousSignalQuality[symbolPath] = previousMirror.Quality;
                lastChangeTimes[symbolPath] = previousMirror.LastChangeTime;
            }

            return new AcquireDiagnosticSnapshotRequestDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode?.Trim(),
                StationMode = ResolveStationMode(request.StationMode, previousState?.StationMode),
                IncludeStationAnchorVariables = false,
                SymbolPaths = signalEntities.Select(it => NormalizeSymbolPath(it.VariableName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                PreviousValues = previousValues,
                PreviousSignalQuality = previousSignalQuality,
                LastChangeTimes = lastChangeTimes,
                AnchorTypeMap = anchorTypeMap
            };
        }

        private static List<PLC_StationSignalHistoryEntity> BuildHistoryRows(
            long projectId,
            string stationCode,
            List<PLC_StationShadowSignalEntity> signalEntities,
            List<StationShadowChangeCacheModel> changes,
            string bundleId,
            string snapshotId)
        {
            var signalMap = signalEntities
                .Where(it => !string.IsNullOrWhiteSpace(it.SignalKey))
                .ToDictionary(it => it.SignalKey.Trim(), it => it, StringComparer.OrdinalIgnoreCase);

            return changes.Select(change =>
            {
                signalMap.TryGetValue(change.SignalKey, out var signalEntity);
                return new PLC_StationSignalHistoryEntity
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    SignalKey = change.SignalKey,
                    VariableName = signalEntity?.VariableName,
                    OldValue = ToDisplayValue(change.OldValue),
                    NewValue = ToDisplayValue(change.NewValue),
                    ChangedAt = change.ChangedAt,
                    StationMode = change.StationMode,
                    Quality = change.Quality,
                    BundleId = bundleId,
                    SnapshotId = snapshotId,
                    SignalType = signalEntity?.SignalType,
                    GroupName = signalEntity?.GroupName,
                    LogicUnitId = signalEntity?.LogicUnitId,
                    CreateTime = change.ChangedAt ?? DateTime.Now,
                    ModifyTime = change.ChangedAt ?? DateTime.Now,
                    IsDeleted = false
                };
            }).ToList();
        }

        private async Task SaveLeaseAsync(long projectId, string stationCode, string runtimeNodeName, string jobSource, string status, DateTime startedAt, DateTime heartbeat)
        {
            await _stateCacheService.SaveRuntimeLeaseAsync(projectId, stationCode, new StationShadowRuntimeLeaseCacheModel
            {
                NodeName = runtimeNodeName,
                StartedAt = startedAt,
                LastHeartbeat = heartbeat,
                JobSource = string.IsNullOrWhiteSpace(jobSource) ? "Manual" : jobSource.Trim(),
                Status = status
            });
        }

        private static StationShadowBundleCacheModel BuildBundle(List<StationShadowChangeCacheModel> changes, string stationMode, DateTime now)
        {
            var signalKeys = changes
                .Select(it => it.SignalKey)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it)
                .ToList();

            return new StationShadowBundleCacheModel
            {
                BundleId = GenerateBundleId(now),
                StartTime = changes.Min(it => it.ChangedAt) ?? now,
                EndTime = changes.Max(it => it.ChangedAt) ?? now,
                StationMode = stationMode,
                SignalKeys = signalKeys,
                AnchorKeys = changes
                    .Where(it => !string.IsNullOrWhiteSpace(it.SignalType))
                    .Select(it => $"{it.SignalKey}:{it.SignalType}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(it => it)
                    .ToList(),
                ConcurrencyCount = signalKeys.Count,
                BundleSignature = string.Join("|", signalKeys),
                BundleLabel = BuildBundleLabel(changes),
                PrecedingBundleId = null
            };
        }

        private async Task<StationShadowSnapshotCacheModel> BuildSnapshotModel(
            long projectId,
            string stationCode,
            string snapshotId,
            string stationMode,
            List<PLC_StationShadowSignalEntity> signalEntities,
            Dictionary<string, StationShadowSignalCacheModel> previousSignalMirrors,
            StopCauseExecutionContext executionContext,
            DiagnosticSnapshot diagnosticSnapshot,
            StationShadowBundleCacheModel currentBundle,
            DateTime now,
            int recentBundleCount)
        {
            var signalMap = signalEntities
                .Where(it => !string.IsNullOrWhiteSpace(it.SignalKey) && !string.IsNullOrWhiteSpace(it.VariableName))
                .ToDictionary(it => it.SignalKey.Trim(), it => NormalizeSymbolPath(it.VariableName), StringComparer.OrdinalIgnoreCase);

            var currentValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var previousValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var lastChangeTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            var signalQuality = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var signalEntity in signalEntities)
            {
                var signalKey = NormalizeRequired(signalEntity.SignalKey, nameof(signalEntity.SignalKey));
                var symbolPath = signalMap[signalKey];

                executionContext.CurrentValues.TryGetValue(symbolPath, out var currentValue);
                executionContext.PreviousValues.TryGetValue(symbolPath, out var previousValue);
                executionContext.SignalQuality.TryGetValue(symbolPath, out var quality);
                diagnosticSnapshot.LastChangeTimes.TryGetValue(symbolPath, out var changedAt);

                currentValues[signalKey] = currentValue;
                previousValues[signalKey] = previousSignalMirrors.TryGetValue(signalKey, out var previousMirror)
                    ? previousMirror.CurrentValue
                    : previousValue;
                lastChangeTimes[signalKey] = changedAt ?? previousMirror?.LastChangeTime;
                signalQuality[signalKey] = string.IsNullOrWhiteSpace(quality)
                    ? previousMirror?.Quality
                    : quality;
            }

            var recentBundles = await _stateCacheService.GetRecentBundlesAsync(projectId, stationCode, Math.Max(recentBundleCount, 1));
            if (currentBundle != null && !recentBundles.Any(it => string.Equals(it.BundleId, currentBundle.BundleId, StringComparison.OrdinalIgnoreCase)))
            {
                recentBundles.Insert(0, currentBundle);
            }

            return new StationShadowSnapshotCacheModel
            {
                SnapshotId = snapshotId,
                ProjectId = projectId,
                StationCode = stationCode,
                TriggerType = "ManualCycle",
                SnapshotTime = now,
                StationMode = stationMode,
                LastBundleId = currentBundle?.BundleId,
                RequestedSymbols = signalEntities.Select(it => NormalizeSymbolPath(it.VariableName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                CurrentValues = currentValues,
                PreviousValues = previousValues,
                LastChangeTimes = lastChangeTimes,
                SignalQuality = signalQuality,
                RecentBundles = recentBundles.Take(Math.Max(recentBundleCount, 1)).ToList(),
                ExpectedNextBundle = null,
                MissingBundle = null
            };
        }

        private static void RemoveStaleSignalMirrors(long projectId, string stationCode, Dictionary<string, StationShadowSignalCacheModel> previousSignalMirrors, List<PLC_StationShadowSignalEntity> signalEntities)
        {
            if (previousSignalMirrors == null || previousSignalMirrors.Count == 0)
            {
                return;
            }

            var validKeys = signalEntities
                .Where(it => !string.IsNullOrWhiteSpace(it.SignalKey))
                .Select(it => it.SignalKey.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var staleFields = previousSignalMirrors.Keys
                .Where(it => !validKeys.Contains(it))
                .ToArray();
            if (staleFields.Length == 0)
            {
                return;
            }

            CommonRedisHelper.DeleteHash(StopCauseMonitoringRedisKeyBuilder.BuildStationSignalsKey(projectId, stationCode), staleFields);
        }

        private static string ResolveRuntimeNodeName(string runtimeNodeName)
        {
            return string.IsNullOrWhiteSpace(runtimeNodeName)
                ? Environment.MachineName
                : runtimeNodeName.Trim();
        }

        private static string ResolveStationMode(string requestedStationMode, string cachedStationMode)
        {
            if (!string.IsNullOrWhiteSpace(requestedStationMode))
            {
                return requestedStationMode.Trim();
            }

            return string.IsNullOrWhiteSpace(cachedStationMode)
                ? string.Empty
                : cachedStationMode.Trim();
        }

        private static string BuildBundleLabel(List<StationShadowChangeCacheModel> changes)
        {
            var groupNames = changes
                .Select(it => it.GroupName)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (groupNames.Count == 1)
            {
                return groupNames[0];
            }

            return $"{changes.Count} signals changed";
        }

        private static string GenerateSnapshotId(DateTime timestamp)
        {
            return $"snap-{timestamp:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        }

        private static string GenerateBundleId(DateTime timestamp)
        {
            return $"bundle-{timestamp:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        }

        private static string GenerateChangeId(string signalKey, DateTime timestamp)
        {
            return $"chg-{timestamp:yyyyMMddHHmmssfff}-{Math.Abs((signalKey ?? string.Empty).GetHashCode())}";
        }

        private static string NormalizeRequired(string value, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value cannot be empty.", argumentName);
            }

            return value.Trim();
        }

        private static string NormalizeSymbolPath(string symbolPath)
        {
            return (symbolPath ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace("\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("IQ.", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValuesEqual(object left, object right)
        {
            if (left == null && right == null)
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(ToDisplayValue(left), ToDisplayValue(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string ToDisplayValue(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value) ?? string.Empty;
        }

        /// <summary>
        /// 判断值是否为 STRING 类型（非布尔、非数值则视为字符串）。
        /// </summary>
        private static bool IsStringValue(object value)
        {
            var str = value?.ToString();
            if (string.IsNullOrEmpty(str)) return false;
            if (str == "0" || str == "1"
                || str.Equals("true", StringComparison.OrdinalIgnoreCase)
                || str.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;
            if (double.TryParse(str, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;
            return true;
        }
    }
}
