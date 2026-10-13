using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CSRedis;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS;
using HSJM.AI.AppLayer.PLC.JobCenter.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.CylinderSignalEvent
{
    public class CylinderSignalEventCollectorService : ISelfScopedAutoInject
    {
        private const int CacheSeconds = 60 * 60 * 24 * 30;
        private const int ExistingSignalMapCacheSeconds = 5;
        private static readonly SemaphoreSlim CollectLock = new SemaphoreSlim(1, 1);
        private static readonly string[] CylinderHintKeywords =
        {
            "cyl", "cylinder", "valve", "solenoid", "yv", "fwd", "bwd", "bkd", "extend", "retract", "home", "work"
        };
        private static readonly ConcurrentDictionary<string, InMemoryCacheItem<Dictionary<string, CylinderSignalEventEntity>>> ExistingSignalMapCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex StationCodeRegex = new Regex(@"[A-Za-z]{1,4}\d{2,4}", RegexOptions.Compiled);

        private readonly ICylinderSignalEventService _eventService;
        private readonly IPLC_ProjectService _projectService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_IOConstraintRuleService _constraintRuleService;
        private readonly IPLC_IOConstraintSignalService _constraintSignalService;
        private readonly IPLC_StationShadowSignalService _stationShadowSignalService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly ILogger<CylinderSignalEventCollectorService> _logger;

        public CylinderSignalEventCollectorService(
            ICylinderSignalEventService eventService,
            IPLC_ProjectService projectService,
            IPLC_BlockService blockService,
            IPLC_IOTagService ioTagService,
            IPLC_IOConstraintRuleService constraintRuleService,
            IPLC_IOConstraintSignalService constraintSignalService,
            IPLC_StationShadowSignalService stationShadowSignalService,
            IPLC_VariableAccessService variableAccessService,
            ILogger<CylinderSignalEventCollectorService> logger)
        {
            _eventService = eventService;
            _projectService = projectService;
            _blockService = blockService;
            _ioTagService = ioTagService;
            _constraintRuleService = constraintRuleService;
            _constraintSignalService = constraintSignalService;
            _stationShadowSignalService = stationShadowSignalService;
            _variableAccessService = variableAccessService;
            _logger = logger;
        }

        public async Task<HttpResponseResultModel<CylinderSignalCollectResultDto>> CollectOnceAsync(CylinderSignalCollectRequestDto request)
        {
            var response = new HttpResponseResultModel<CylinderSignalCollectResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.ErrorMessage = "ProjectId is required.";
                return response;
            }

            if (!await CollectLock.WaitAsync(0))
            {
                response.ErrorMessage = "当前已有气缸信号采集在执行，请稍后重试。";
                return response;
            }

            try
            {

                if (CommonRedisHelper.client == null)
                {
                    response.ErrorMessage = "Redis is not initialized. Cylinder signal collection requires Redis cache.";
                    return response;
                }

                var project = await _projectService.GetByIdAsync(request.ProjectId);
                if (project == null)
                {
                    response.ErrorMessage = $"Project {request.ProjectId} was not found.";
                    return response;
                }

            var points = await DiscoverPointsAsync(project, request.StationCode);
            var stationCode = NormalizeStationCode(request.StationCode, points, project);
            var plcId = project.Id.ToString();
            _logger.LogInformation(
                "Cylinder collect start. ProjectId={ProjectId}, RequestedStation={RequestedStation}, ResolvedStation={ResolvedStation}, PointCount={PointCount}, DistinctStations={DistinctStations}",
                project.Id,
                request.StationCode,
                stationCode,
                points.Count,
                string.Join(",", points.Select(it => it.StationCode).Where(it => string.IsNullOrWhiteSpace(it) == false).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(it => it)));
            var result = new CylinderSignalCollectResultDto
            {
                ProjectId = project.Id,
                PlcId = plcId,
                StationCode = stationCode,
                PointCount = points.Count
            };

            if (points.Count == 0)
            {
                response.IsSuccess = true;
                response.BackResult = result;
                return response;
            }

            if (request.ResetCacheBeforeCollect)
            {
                var cacheKeys = points
                    .Select(it => BuildCacheKey(plcId, it.StationCode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (cacheKeys.Length > 0)
                {
                    CommonRedisHelper.DeleteString(cacheKeys);
                }
            }

            List<PLC_ReadTagResult> readResults;
            if (request.UseSimulation)
            {
                readResults = BuildSimulationReadResults(points, request.SimulatedValues);
            }
            else
            {
                var readWatch = Stopwatch.StartNew();
                readResults = await ReadRealtimeValuesAsync(project.Id, points);
                readWatch.Stop();
                result.ReadPlcDurationMilliseconds = readWatch.ElapsedMilliseconds;
            }

            result.ReadCount = readResults.Count(it => it.IsGood);
            var readMap = BuildReadResultMap(readResults);
            var cacheSnapshotMap = ReadCacheSnapshots(plcId, points);
            result.CacheHitCount = CountCacheSignals(cacheSnapshotMap, plcId, points);
            result.NoCacheCount = Math.Max(points.Count - result.CacheHitCount, 0);
            var existingSignalMap = await LoadExistingSignalMapAsync(plcId, points);
            var currentCycleValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pendingCacheWrites = new Dictionary<string, Dictionary<string, CylinderSignalCacheValue>>(StringComparer.OrdinalIgnoreCase);

            var now = DateTime.Now;
            var toInsert = new List<CylinderSignalEventEntity>();
            foreach (var point in points)
            {
                var signalName = NormalizeSignalName(point.SignalName);
                var pointKey = BuildSignalIdentityKey(plcId, point.CylinderId, point.SignalName);
                var readKeys = BuildReadMatchKeys(point);
                var read = readKeys
                    .Select(key => readMap.TryGetValue(key, out var item) ? item : null)
                    .FirstOrDefault(item => item != null);
                if (read == null || !read.IsGood)
                {
                    result.MissingSignals.Add(point.SignalName);
                    result.ComparedSignals.Add(new CylinderSignalCompareItemDto
                    {
                        StationCode = point.StationCode,
                        CylinderId = point.CylinderId,
                        SignalName = point.SignalName,
                        SignalRole = point.SignalRole,
                        OldValue = ReadCache(cacheSnapshotMap, plcId, point.StationCode, signalName)?.Value,
                        CurrentValue = null,
                        IsChanged = false,
                        IsMissing = true,
                        CompareStatus = "missing"
                    });
                    continue;
                }

                var newValue = NormalizeValue(read.Value);
                var cacheKey = BuildCacheKey(plcId, point.StationCode);
                var cache = ReadCache(cacheSnapshotMap, plcId, point.StationCode, signalName);
                existingSignalMap.TryGetValue(pointKey, out var existingEntity);
                var existsInDatabase = existingEntity != null;
                currentCycleValueMap.TryGetValue(pointKey, out var currentCycleValue);
                var hasCurrentCycleValue = string.IsNullOrWhiteSpace(currentCycleValue) == false;
                var effectivePreviousValue = hasCurrentCycleValue ? currentCycleValue : cache?.Value;
                var previousValue = effectivePreviousValue ?? existingEntity?.NewValue;
                var hasCacheValue = hasCurrentCycleValue || (cache != null && string.IsNullOrWhiteSpace(cache.Value) == false);
                var compareBaselineValue = hasCurrentCycleValue ? currentCycleValue : cache?.Value;
                var isChanged = !existsInDatabase || !hasCacheValue || !ValuesEqual(compareBaselineValue, newValue);
                result.ComparedSignals.Add(new CylinderSignalCompareItemDto
                {
                    StationCode = point.StationCode,
                    CylinderId = point.CylinderId,
                    SignalName = point.SignalName,
                    SignalRole = point.SignalRole,
                    OldValue = previousValue,
                    CurrentValue = newValue,
                    IsChanged = isChanged,
                    IsMissing = false,
                    CompareStatus = !existsInDatabase
                        ? "db_missing"
                        : !hasCacheValue
                        ? "first_persist"
                        : isChanged ? "changed" : "unchanged"
                });

                WriteCache(pendingCacheWrites, cacheKey, signalName, new CylinderSignalCacheValue
                {
                    Value = newValue,
                    ChangedAt = now,
                    CylinderId = point.CylinderId,
                    SignalRole = point.SignalRole
                });
                currentCycleValueMap[pointKey] = newValue;

                if (!isChanged)
                {
                    continue;
                }

                if (isChanged)
                {
                    var entity = new CylinderSignalEventEntity
                    {
                        PlcId = plcId,
                        StationCode = point.StationCode,
                        CylinderId = point.CylinderId,
                        SignalName = point.SignalName,
                        SignalRole = point.SignalRole,
                        OldValue = previousValue,
                        NewValue = newValue,
                        ChangedAt = now,
                        StationMode = request.StationMode,
                        CreateTime = now,
                        IsDeleted = false
                    };
                    toInsert.Add(entity);
                    existingSignalMap[pointKey] = entity;
                    continue;
                }
            }

                result.ChangedCount = toInsert.Count;
                if (toInsert.Count > 0)
                {
                    FlushCacheWrites(pendingCacheWrites);
                    try
                    {
                        await _eventService.InsertRangeAsync(toInsert);
                    }
                    catch
                    {
                        RemoveCacheWrites(pendingCacheWrites);
                        throw;
                    }

                    result.InsertedCount = toInsert.Count;
                    result.InsertedEvents = toInsert.Select(MapEvent).ToList();
                }

                UpdateExistingSignalMapCache(plcId, points, existingSignalMap, toInsert);

                response.IsSuccess = true;
                response.BackResult = result;
                return response;
            }
            finally
            {
                CollectLock.Release();
            }
        }

        public async Task<HttpResponseResultModel<CylinderSignalSimulationGenerateResultDto>> GenerateSimulationValuesAsync(CylinderSignalSimulationGenerateRequestDto request)
        {
            var response = new HttpResponseResultModel<CylinderSignalSimulationGenerateResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.ErrorMessage = "ProjectId is required.";
                return response;
            }

            if (CommonRedisHelper.client == null)
            {
                response.ErrorMessage = "Redis is not initialized. Cylinder signal collection requires Redis cache.";
                return response;
            }

            var project = await _projectService.GetByIdAsync(request.ProjectId);
            if (project == null)
            {
                response.ErrorMessage = $"Project {request.ProjectId} was not found.";
                return response;
            }

            var points = await DiscoverPointsAsync(project, request.StationCode);
            var stationCode = NormalizeStationCode(request.StationCode, points, project);
            var plcId = project.Id.ToString();
            var result = new CylinderSignalSimulationGenerateResultDto
            {
                ProjectId = project.Id,
                PlcId = plcId,
                StationCode = stationCode,
                PointCount = points.Count
            };

            if (points.Count == 0)
            {
                response.IsSuccess = true;
                response.BackResult = result;
                return response;
            }

            var readResults = await ReadRealtimeValuesAsync(project.Id, points);
            var readMap = BuildReadResultMap(readResults);
            var cacheSnapshotMap = ReadCacheSnapshots(plcId, points);

            foreach (var point in points)
            {
                var signalName = NormalizeSignalName(point.SignalName);
                if (string.IsNullOrWhiteSpace(signalName))
                {
                    continue;
                }

                var cache = ReadCache(cacheSnapshotMap, plcId, point.StationCode, signalName);
                var read = BuildReadMatchKeys(point)
                    .Select(key => readMap.TryGetValue(key, out var item) ? item : null)
                    .FirstOrDefault(item => item != null && item.IsGood);

                var baseValue = cache?.Value ?? NormalizeValue(read?.Value);
                result.SimulatedValues[point.SignalName] = BuildRandomSimulationValue(baseValue);
            }

            result.GeneratedCount = result.SimulatedValues.Count;
            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        public async Task<List<PlcJobCenterStationGroupDto>> GetStationGroupsAsync(List<long> projectIds)
        {
            var normalizedProjectIds = (projectIds ?? new List<long>())
                .Where(it => it > 0)
                .Distinct()
                .ToList();

            var query = _stationShadowSignalService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false)
                .Where(it => it.SignalType == "PhysicalAction")
                .Where(it => SqlFunc.HasValue(it.StationCode));

            if (normalizedProjectIds.Count > 0)
            {
                query = query.Where(it => it.ProjectId != null && normalizedProjectIds.Contains(it.ProjectId.Value));
            }

            var shadowSignals = await query.ToListAsync();

            return shadowSignals
                .GroupBy(
                    it => it.StationCode.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new PlcJobCenterStationGroupDto
                {
                    StationCode = group.Key,
                    StationName = group.Key,
                    ProjectCount = group
                        .Where(x => x.ProjectId.HasValue)
                        .Select(x => x.ProjectId.Value)
                        .Distinct()
                        .Count(),
                    PointCount = group.Count(),
                    TableNames = group
                        .Select(x => x.GroupName)
                        .Where(x => string.IsNullOrWhiteSpace(x) == false)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToList()
                })
                .OrderBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<CylinderSignalCacheClearResultDto> ClearSignalCacheAsync(
            List<long> projectIds,
            List<string> stationCodes,
            bool clearAllStations)
        {
            if (CommonRedisHelper.client == null)
            {
                throw new InvalidOperationException("Redis is not initialized. Cylinder signal collection requires Redis cache.");
            }

            var normalizedProjectIds = (projectIds ?? new List<long>())
                .Where(it => it > 0)
                .Distinct()
                .ToList();

            var projectQuery = _projectService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false);
            if (normalizedProjectIds.Count > 0)
            {
                projectQuery = projectQuery.Where(it => normalizedProjectIds.Contains(it.Id));
            }

            var projects = await projectQuery.ToListAsync();
            var result = new CylinderSignalCacheClearResultDto
            {
                ProjectCount = projects.Count
            };

            var normalizedStations = (stationCodes ?? new List<string>())
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!clearAllStations && normalizedStations.Count > 0)
            {
                var keys = projects
                    .SelectMany(project => normalizedStations.Select(stationCode => BuildCacheKey(project.Id.ToString(), stationCode)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                result.StationCount = normalizedStations.Count;
                result.ClearedKeyCount = keys.Length == 0 ? 0 : CommonRedisHelper.DeleteString(keys);
                return result;
            }

            var totalCleared = 0L;
            foreach (var project in projects)
            {
                var keys = await ScanCacheKeysAsync($"cylinder_signal:last:{project.Id}:*");
                if (keys.Count == 0)
                {
                    continue;
                }

                totalCleared += CommonRedisHelper.DeleteString(keys.ToArray());
            }

            result.StationCount = normalizedStations.Count;
            result.ClearedKeyCount = totalCleared;
            return result;
        }

        private async Task<List<CylinderSignalPointDto>> DiscoverPointsAsync(PLC_ProjectEntity project, string stationCode)
        {
            var normalizedStation = stationCode?.Trim();
            var plcId = project.Id.ToString();
            var blocks = await _blockService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == project.Id)
                .ToListAsync();

            var stationCandidates = blocks
                .Select(it => it.StationCode)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(normalizedStation) && !stationCandidates.Contains(normalizedStation, StringComparer.OrdinalIgnoreCase))
            {
                stationCandidates.Add(normalizedStation);
            }
            if (stationCandidates.Count == 0 && !string.IsNullOrWhiteSpace(project.StationId))
            {
                stationCandidates.Add(project.StationId.Trim());
            }

            var rules = await _constraintRuleService.GetDb().AsQueryable()
                .Where(it => it.ProjectId == project.Id)
                .ToListAsync();
            var ruleIds = rules.Select(it => it.Id).Distinct().ToList();
            if (ruleIds.Count == 0)
            {
                return new List<CylinderSignalPointDto>();
            }

            var constraintSignals = await _constraintSignalService.GetDb().AsQueryable()
                .Where(it => ruleIds.Contains(it.RuleId))
                .OrderBy(it => it.RuleId, OrderByType.Asc)
                .OrderBy(it => it.SignalRole, OrderByType.Asc)
                .OrderBy(it => it.SortOrder, OrderByType.Asc)
                .ToListAsync();

            var signalNames = constraintSignals
                .Select(it => NormalizeSignalName(it.SignalName))
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var tagQuery = _ioTagService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == project.Id)
                .Where(it => signalNames.Contains(it.TagName));
            if (!string.IsNullOrWhiteSpace(normalizedStation))
            {
                tagQuery = tagQuery.Where(it => SqlFunc.HasValue(it.TagName) && it.TagName.Contains(normalizedStation));
            }

            var tags = await tagQuery.ToListAsync();
            var tagMap = tags
                .Where(it => !string.IsNullOrWhiteSpace(it.TagName))
                .GroupBy(it => NormalizeSignalName(it.TagName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(it => it.Key, it => it.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<CylinderSignalPointDto>();
            foreach (var group in constraintSignals.GroupBy(it => it.RuleId))
            {
                var ordered = group
                    .OrderBy(it => it.SortOrder ?? int.MaxValue)
                    .ThenBy(it => it.Id)
                    .ToList();
                if (!IsCylinderRuleCandidate(ordered, tagMap))
                {
                    continue;
                }

                foreach (var signal in ordered)
                {
                    var signalName = NormalizeSignalName(signal.SignalName);
                    if (string.IsNullOrWhiteSpace(signalName))
                    {
                        continue;
                    }

                    tagMap.TryGetValue(signalName, out var tag);
                    var resolvedStation = ResolveStationCode(signalName, tag, normalizedStation, stationCandidates, project);
                    if (!IsStationMatched(normalizedStation, resolvedStation, tag))
                    {
                        continue;
                    }

                    var signalRole = ResolveCylinderSignalRole(signal, ordered, tag);
                    result.Add(new CylinderSignalPointDto
                    {
                        RuleId = signal.RuleId,
                        TagId = tag?.Id,
                        PlcId = plcId,
                        StationCode = resolvedStation,
                        CylinderId = BuildCylinderId(tag),
                        SignalName = signalName,
                        ReadTagName = BuildReadTagName(tag, signalName),
                        TableName = NormalizeSignalName(tag?.TableName),
                        SignalRole = signalRole,
                        ConstraintRole = signal.SignalRole,
                        SortOrder = signal.SortOrder,
                        Address = tag?.Address,
                        IoMapping = tag?.IoMapping
                    });
                }
            }

            var discoveredPoints = result
                .GroupBy(it => BuildPointIdentityKey(it), StringComparer.OrdinalIgnoreCase)
                .Select(it => it
                    .OrderBy(x => x.SortOrder ?? int.MaxValue)
                    .ThenBy(x => x.RuleId)
                    .First())
                .ToList();
            _logger.LogInformation(
                "Cylinder discover finished. ProjectId={ProjectId}, RequestedStation={RequestedStation}, RawPointCount={RawPointCount}, FinalPointCount={FinalPointCount}, DistinctStations={DistinctStations}, SampleSignals={SampleSignals}",
                project.Id,
                normalizedStation,
                result.Count,
                discoveredPoints.Count,
                string.Join(",", discoveredPoints.Select(it => it.StationCode).Where(it => string.IsNullOrWhiteSpace(it) == false).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(it => it)),
                string.Join(",", discoveredPoints.Take(12).Select(it => $"{it.StationCode}:{it.SignalName}")));
            return discoveredPoints;
        }

        private async Task<Dictionary<string, CylinderSignalEventEntity>> LoadExistingSignalMapAsync(string plcId, List<CylinderSignalPointDto> points)
        {
            var cylinderIds = points
                .Select(it => it.CylinderId)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cylinderIds.Count == 0)
            {
                return new Dictionary<string, CylinderSignalEventEntity>(StringComparer.OrdinalIgnoreCase);
            }

            var cacheKey = BuildExistingSignalMapCacheKey(plcId, cylinderIds);
            if (TryGetMemoryCache(ExistingSignalMapCache, cacheKey, out Dictionary<string, CylinderSignalEventEntity> cachedMap))
            {
                return CloneExistingSignalMap(cachedMap);
            }

            var rows = await _eventService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.PlcId == plcId)
                .Where(it => cylinderIds.Contains(it.CylinderId))
                .ToListAsync();

            var map = rows
                .GroupBy(it => BuildSignalIdentityKey(it.PlcId, it.CylinderId, it.SignalName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    it => it.Key,
                    it => it
                        .OrderByDescending(x => x.ChangedAt)
                        .ThenByDescending(x => x.ModifyTime ?? DateTime.MinValue)
                        .ThenByDescending(x => x.CreateTime ?? DateTime.MinValue)
                        .ThenByDescending(x => x.Id)
                        .First(),
                    StringComparer.OrdinalIgnoreCase);
            SetMemoryCache(ExistingSignalMapCache, cacheKey, CloneExistingSignalMap(map), ExistingSignalMapCacheSeconds);
            return map;
        }

        private async Task<List<PLC_ReadTagResult>> ReadRealtimeValuesAsync(long projectId, List<CylinderSignalPointDto> points)
        {
            var tagNames = points?
                .SelectMany(BuildReadCandidateNames)
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            _logger.LogInformation(
                "Cylinder read realtime. ProjectId={ProjectId}, TagCount={TagCount}, DistinctStations={DistinctStations}, SampleTags={SampleTags}",
                projectId,
                tagNames.Count,
                string.Join(",", (points ?? new List<CylinderSignalPointDto>()).Select(it => it.StationCode).Where(it => string.IsNullOrWhiteSpace(it) == false).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(it => it)),
                string.Join(",", tagNames.Take(20)));

            return await _variableAccessService.ReadTagsByFixedDeviceAsync(projectId, tagNames) ?? new List<PLC_ReadTagResult>();
        }

        private static IEnumerable<string> BuildReadCandidateNames(CylinderSignalPointDto point)
        {
            if (point == null)
            {
                yield break;
            }

            var preferredReadTagName = NormalizeSignalName(point.ReadTagName);
            if (!string.IsNullOrWhiteSpace(preferredReadTagName))
            {
                yield return preferredReadTagName;
                yield break;
            }

            var signalName = NormalizeSignalName(point.SignalName);
            if (!string.IsNullOrWhiteSpace(signalName))
            {
                yield return signalName;
            }
        }

        private static List<PLC_ReadTagResult> BuildSimulationReadResults(List<CylinderSignalPointDto> points, Dictionary<string, object> values)
        {
            values ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return points.Select(point =>
            {
                values.TryGetValue(point.SignalName, out var value);
                return new PLC_ReadTagResult
                {
                    TagName = point.SignalName,
                    Value = value,
                    IsGood = values.ContainsKey(point.SignalName)
                };
            }).ToList();
        }

        private static Dictionary<string, PLC_ReadTagResult> BuildReadResultMap(List<PLC_ReadTagResult> readResults)
        {
            var map = new Dictionary<string, PLC_ReadTagResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in readResults ?? new List<PLC_ReadTagResult>())
            {
                foreach (var key in BuildReadResultKeys(item))
                {
                    if (!map.ContainsKey(key) || item.IsGood)
                    {
                        map[key] = item;
                    }
                }
            }

            return map;
        }

        private static IEnumerable<string> BuildReadResultKeys(PLC_ReadTagResult item)
        {
            if (item == null)
            {
                yield break;
            }

            foreach (var raw in new[] { item.TagName, item.OldTagName })
            {
                var normalized = NormalizeSignalName(raw);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                yield return normalized;
                var tail = normalized.Contains('.')
                    ? normalized.Split('.').LastOrDefault()
                    : null;
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    yield return tail.Trim();
                }
            }
        }

        private static List<string> BuildReadMatchKeys(CylinderSignalPointDto point)
        {
            var keys = new List<string>();
            AddKey(keys, point.SignalName);
            AddKey(keys, point.ReadTagName);
            if (!string.IsNullOrWhiteSpace(point.ReadTagName) && point.ReadTagName.Contains('.'))
            {
                AddKey(keys, point.ReadTagName.Split('.').LastOrDefault());
            }

            return keys;
        }

        private static void AddKey(List<string> keys, string value)
        {
            var key = NormalizeSignalName(value);
            if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(key);
            }
        }

        private static string ResolveCylinderSignalRole(PLC_IOConstraintSignalEntity signal, List<PLC_IOConstraintSignalEntity> group, PLC_IOTagEntity tag)
        {
            var role = signal.SignalRole?.Trim();
            if (string.Equals(role, "Source", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsOutputTag(tag) && !LooksLikeValveSignal(signal.SignalName, tag))
                {
                    return "unknown";
                }

                var sources = group
                    .Where(it => string.Equals(it.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(it => it.SortOrder ?? int.MaxValue)
                    .ThenBy(it => it.Id)
                    .ToList();
                if (sources.Count == 1)
                {
                    return "common_valve";
                }

                var index = sources.FindIndex(it => it.Id == signal.Id);
                return index switch
                {
                    0 => "extend_valve",
                    1 => "retract_valve",
                    _ => "common_valve"
                };
            }

            if (string.Equals(role, "Target", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "Member", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsInputTag(tag) && !LooksLikeSensorSignal(signal.SignalName, tag))
                {
                    return "unknown";
                }

                var feedbacks = group
                    .Where(it => string.Equals(it.SignalRole, "Target", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(it.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(it => it.SortOrder ?? int.MaxValue)
                    .ThenBy(it => it.Id)
                    .ToList();
                var index = feedbacks.FindIndex(it => it.Id == signal.Id);
                return index switch
                {
                    0 => "work_sensor",
                    1 => "home_sensor",
                    _ => "unknown"
                };
            }

            return "unknown";
        }

        private static bool IsCylinderRuleCandidate(
            List<PLC_IOConstraintSignalEntity> group,
            IReadOnlyDictionary<string, PLC_IOTagEntity> tagMap)
        {
            if (group == null || group.Count < 2)
            {
                return false;
            }

            var sourceSignals = group
                .Where(it => string.Equals(it.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var feedbackSignals = group
                .Where(it => string.Equals(it.SignalRole, "Target", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(it.SignalRole, "Member", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sourceSignals.Count == 0 || feedbackSignals.Count == 0)
            {
                return false;
            }

            var hasValveSource = sourceSignals.Any(signal =>
            {
                tagMap.TryGetValue(NormalizeSignalName(signal.SignalName), out var tag);
                return IsOutputTag(tag) || LooksLikeValveSignal(signal.SignalName, tag);
            });

            var hasSensorFeedback = feedbackSignals.Any(signal =>
            {
                tagMap.TryGetValue(NormalizeSignalName(signal.SignalName), out var tag);
                return IsInputTag(tag) || LooksLikeSensorSignal(signal.SignalName, tag);
            });

            if (!hasValveSource || !hasSensorFeedback)
            {
                return false;
            }

            return sourceSignals.Concat(feedbackSignals).Any(signal =>
            {
                tagMap.TryGetValue(NormalizeSignalName(signal.SignalName), out var tag);
                return LooksLikeCylinderSignal(signal.SignalName, tag);
            });
        }

        private static bool IsInputTag(PLC_IOTagEntity tag)
        {
            var text = $"{tag?.Address} {tag?.IoMapping} {tag?.TagType} {tag?.TagCategory}".Trim();
            return text.StartsWith("I", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("%I", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Input", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOutputTag(PLC_IOTagEntity tag)
        {
            var text = $"{tag?.Address} {tag?.IoMapping} {tag?.TagType} {tag?.TagCategory}".Trim();
            return text.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("%Q", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Output", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeCylinderSignal(string signalName, PLC_IOTagEntity tag)
        {
            var text = BuildSignalHintText(signalName, tag);
            return CylinderHintKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeValveSignal(string signalName, PLC_IOTagEntity tag)
        {
            var text = BuildSignalHintText(signalName, tag);
            return text.Contains("valve", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("solenoid", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("yv", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("fwd", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("bwd", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("bkd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeSensorSignal(string signalName, PLC_IOTagEntity tag)
        {
            var text = BuildSignalHintText(signalName, tag);
            return text.Contains("sensor", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("sq", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("home", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("work", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("fwdok", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("bwdok", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("bkdok", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSignalHintText(string signalName, PLC_IOTagEntity tag)
        {
            return $"{signalName} {tag?.TagName} {tag?.Comment} {tag?.TagCategory} {tag?.TagEffect} {tag?.EquipmentId}".Trim();
        }

        private static string ResolveStationCode(
            string signalName,
            PLC_IOTagEntity tag,
            string requestedStation,
            List<string> stationCandidates,
            PLC_ProjectEntity project)
        {
            var text = $"{signalName} {tag?.Comment} {tag?.Path} {tag?.TableName}";
            var matched = stationCandidates.FirstOrDefault(st => text.Contains(st, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matched))
            {
                return matched.Trim();
            }

            if (!string.IsNullOrWhiteSpace(requestedStation))
            {
                return requestedStation.Trim();
            }

            return stationCandidates.FirstOrDefault()?.Trim()
                   ?? project.StationId?.Trim()
                   ?? "UNKNOWN";
        }

        private static bool IsStationMatched(string requestedStation, string resolvedStation, PLC_IOTagEntity tag)
        {
            if (string.IsNullOrWhiteSpace(requestedStation))
            {
                return true;
            }

            var keyword = requestedStation.Trim();
            if (string.Equals(resolvedStation?.Trim(), keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(resolvedStation) &&
                resolvedStation.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var tableName = NormalizeSignalName(tag?.TableName);
            if (!string.IsNullOrWhiteSpace(tableName) &&
                tableName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string NormalizeStationCode(string requestedStation, List<CylinderSignalPointDto> points, PLC_ProjectEntity project)
        {
            if (points != null && points.Select(it => it.StationCode).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            {
                return !string.IsNullOrWhiteSpace(requestedStation)
                    ? requestedStation.Trim()
                    : "MULTI";
            }

            return !string.IsNullOrWhiteSpace(requestedStation)
                ? requestedStation.Trim()
                : points.FirstOrDefault()?.StationCode ?? project.StationId ?? "UNKNOWN";
        }

        private static string ResolveStationGroupCode(CylinderSignalPointDto point)
        {
            var candidates = new[]
            {
                NormalizeSignalName(point?.StationCode),
                ExtractStationCode(point?.TableName),
                ExtractStationCode(point?.ReadTagName),
                ExtractStationCode(point?.SignalName)
            };

            return candidates.FirstOrDefault(it => string.IsNullOrWhiteSpace(it) == false) ?? "UNKNOWN";
        }

        private static string ExtractStationCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var normalized = NormalizeSignalName(text);
            var match = StationCodeRegex.Match(normalized);
            return match.Success ? match.Value.ToUpperInvariant() : null;
        }

        private static string BuildCylinderId(PLC_IOTagEntity tag)
        {
            return tag?.Id > 0 ? tag.Id.ToString() : "0";
        }

        private static string BuildReadTagName(PLC_IOTagEntity tag, string signalName)
        {
            var tableName = NormalizeSignalName(tag?.TableName);
            var tagName = NormalizeSignalName(tag?.TagName);
            if (!string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(tagName))
            {
                return $"{tableName}.{tagName}";
            }

            if (!string.IsNullOrWhiteSpace(tagName))
            {
                return tagName;
            }

            return NormalizeSignalName(signalName);
        }

        private static string BuildCacheKey(string plcId, string stationCode)
        {
            return $"cylinder_signal:last:{plcId}:{stationCode}";
        }

        private static string BuildSignalIdentityKey(string plcId, string cylinderId, string signalName)
        {
            return $"{plcId}|{cylinderId}|{NormalizeSignalName(signalName)}";
        }

        private static string BuildPointIdentityKey(CylinderSignalPointDto point)
        {
            if (point == null)
            {
                return string.Empty;
            }

            var tagIdPart = point.TagId?.ToString() ?? string.Empty;
            var cylinderPart = string.IsNullOrWhiteSpace(point.CylinderId) ? string.Empty : point.CylinderId.Trim();
            var stationPart = string.IsNullOrWhiteSpace(point.StationCode) ? string.Empty : point.StationCode.Trim();
            var signalPart = NormalizeSignalName(point.SignalName);
            return $"{stationPart}|{tagIdPart}|{cylinderPart}|{signalPart}";
        }

        private static Dictionary<string, Dictionary<string, CylinderSignalCacheValue>> ReadCacheSnapshots(string plcId, List<CylinderSignalPointDto> points)
        {
            var cacheKeys = points?
                .Select(it => BuildCacheKey(plcId, it.StationCode))
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            var result = new Dictionary<string, Dictionary<string, CylinderSignalCacheValue>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cacheKey in cacheKeys)
            {
                var payload = CommonRedisHelper.GetHashAll<string>(cacheKey) ?? new Dictionary<string, string>();
                var fieldMap = new Dictionary<string, CylinderSignalCacheValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in payload)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    try
                    {
                        var item = JsonConvert.DeserializeObject<CylinderSignalCacheValue>(pair.Value);
                        if (item != null)
                        {
                            fieldMap[NormalizeSignalName(pair.Key)] = item;
                        }
                    }
                    catch
                    {
                    }
                }

                result[cacheKey] = fieldMap;
            }

            return result;
        }

        private static int CountCacheSignals(
            IReadOnlyDictionary<string, Dictionary<string, CylinderSignalCacheValue>> cacheSnapshotMap,
            string plcId,
            List<CylinderSignalPointDto> points)
        {
            if (cacheSnapshotMap == null || points == null || points.Count == 0)
            {
                return 0;
            }

            var count = 0;
            foreach (var point in points)
            {
                var signalName = NormalizeSignalName(point?.SignalName);
                if (string.IsNullOrWhiteSpace(signalName))
                {
                    continue;
                }

                if (ReadCache(cacheSnapshotMap, plcId, point.StationCode, signalName) != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static CylinderSignalCacheValue ReadCache(
            IReadOnlyDictionary<string, Dictionary<string, CylinderSignalCacheValue>> cacheSnapshotMap,
            string plcId,
            string stationCode,
            string signalName)
        {
            var cacheKey = BuildCacheKey(plcId, stationCode);
            if (cacheSnapshotMap != null &&
                cacheSnapshotMap.TryGetValue(cacheKey, out var signalMap) &&
                signalMap != null &&
                signalMap.TryGetValue(NormalizeSignalName(signalName), out var cacheValue))
            {
                return cacheValue;
            }

            return null;
        }

        private static void WriteCache(
            Dictionary<string, Dictionary<string, CylinderSignalCacheValue>> pendingCacheWrites,
            string cacheKey,
            string signalName,
            CylinderSignalCacheValue value)
        {
            if (!pendingCacheWrites.TryGetValue(cacheKey, out var fieldMap))
            {
                fieldMap = new Dictionary<string, CylinderSignalCacheValue>(StringComparer.OrdinalIgnoreCase);
                pendingCacheWrites[cacheKey] = fieldMap;
            }

            fieldMap[NormalizeSignalName(signalName)] = value;
        }

        private static void FlushCacheWrites(Dictionary<string, Dictionary<string, CylinderSignalCacheValue>> pendingCacheWrites)
        {
            foreach (var cacheEntry in pendingCacheWrites)
            {
                if (cacheEntry.Value == null || cacheEntry.Value.Count == 0)
                {
                    continue;
                }

                var payload = new List<object>(cacheEntry.Value.Count * 2);
                foreach (var field in cacheEntry.Value)
                {
                    payload.Add(field.Key);
                    payload.Add(JsonConvert.SerializeObject(field.Value));
                }

                CommonRedisHelper.HashSet(cacheEntry.Key, payload.ToArray(), CacheSeconds);
            }
        }

        private static void RemoveCacheWrites(Dictionary<string, Dictionary<string, CylinderSignalCacheValue>> pendingCacheWrites)
        {
            foreach (var cacheEntry in pendingCacheWrites)
            {
                if (cacheEntry.Value == null || cacheEntry.Value.Count == 0)
                {
                    continue;
                }

                var fields = cacheEntry.Value.Keys
                    .Where(it => string.IsNullOrWhiteSpace(it) == false)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (fields.Length == 0)
                {
                    continue;
                }

                CommonRedisHelper.DeleteHash(cacheEntry.Key, fields);
            }
        }

        private static async Task<List<string>> ScanCacheKeysAsync(string pattern)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long cursor = 0;
            do
            {
                var scanResult = await RedisHelper.ScanAsync(cursor, pattern, 200);
                cursor = scanResult.Cursor;
                if (scanResult.Items == null)
                {
                    continue;
                }

                foreach (var key in scanResult.Items)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    keys.Add(key);
                }
            }
            while (cursor > 0);

            return keys.ToList();
        }

        private static string BuildExistingSignalMapCacheKey(string plcId, List<string> cylinderIds)
        {
            var normalizedIds = cylinderIds
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it, StringComparer.OrdinalIgnoreCase);
            return $"{plcId}|{string.Join(",", normalizedIds)}";
        }

        private static bool TryGetMemoryCache<T>(
            ConcurrentDictionary<string, InMemoryCacheItem<T>> cache,
            string key,
            out T value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(key) || cache.TryGetValue(key, out var item) == false)
            {
                return false;
            }

            if (item.ExpireAt <= DateTime.Now)
            {
                cache.TryRemove(key, out _);
                return false;
            }

            value = item.Value;
            return value != null;
        }

        private static void SetMemoryCache<T>(
            ConcurrentDictionary<string, InMemoryCacheItem<T>> cache,
            string key,
            T value,
            int ttlSeconds)
        {
            cache[key] = new InMemoryCacheItem<T>
            {
                Value = value,
                ExpireAt = DateTime.Now.AddSeconds(Math.Max(ttlSeconds, 1))
            };
        }

        private static List<CylinderSignalPointDto> ClonePoints(IEnumerable<CylinderSignalPointDto> points)
        {
            return points?.Select(it => new CylinderSignalPointDto
            {
                RuleId = it.RuleId,
                TagId = it.TagId,
                PlcId = it.PlcId,
                StationCode = it.StationCode,
                CylinderId = it.CylinderId,
                SignalName = it.SignalName,
                ReadTagName = it.ReadTagName,
                TableName = it.TableName,
                SignalRole = it.SignalRole,
                ConstraintRole = it.ConstraintRole,
                SortOrder = it.SortOrder,
                Address = it.Address,
                IoMapping = it.IoMapping
            }).ToList() ?? new List<CylinderSignalPointDto>();
        }

        private static Dictionary<string, CylinderSignalEventEntity> CloneExistingSignalMap(
            IReadOnlyDictionary<string, CylinderSignalEventEntity> source)
        {
            var result = new Dictionary<string, CylinderSignalEventEntity>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (var pair in source)
            {
                var entity = pair.Value;
                if (entity == null)
                {
                    continue;
                }

                result[pair.Key] = new CylinderSignalEventEntity
                {
                    Id = entity.Id,
                    PlcId = entity.PlcId,
                    StationCode = entity.StationCode,
                    CylinderId = entity.CylinderId,
                    SignalName = entity.SignalName,
                    SignalRole = entity.SignalRole,
                    OldValue = entity.OldValue,
                    NewValue = entity.NewValue,
                    ChangedAt = entity.ChangedAt,
                    StationMode = entity.StationMode,
                    IsDeleted = entity.IsDeleted,
                    CreaterId = entity.CreaterId,
                    CreaterName = entity.CreaterName,
                    CreateTime = entity.CreateTime,
                    ModifierId = entity.ModifierId,
                    ModifyTime = entity.ModifyTime,
                    ModifierName = entity.ModifierName
                };
            }

            return result;
        }

        private static void UpdateExistingSignalMapCache(
            string plcId,
            List<CylinderSignalPointDto> points,
            Dictionary<string, CylinderSignalEventEntity> existingSignalMap,
            List<CylinderSignalEventEntity> inserted)
        {
            var cylinderIds = points?
                .Select(it => it.CylinderId)
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cylinderIds == null || cylinderIds.Count == 0)
            {
                return;
            }

            foreach (var entity in inserted)
            {
                existingSignalMap[BuildSignalIdentityKey(entity.PlcId, entity.CylinderId, entity.SignalName)] = entity;
            }

            var cacheKey = BuildExistingSignalMapCacheKey(plcId, cylinderIds);
            SetMemoryCache(ExistingSignalMapCache, cacheKey, CloneExistingSignalMap(existingSignalMap), ExistingSignalMapCacheSeconds);
        }

        private static string NormalizeSignalName(string signalName)
        {
            return (signalName ?? string.Empty).Trim().Trim('"').Replace("\"", string.Empty);
        }

        private static string NormalizeValue(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is bool b)
            {
                return b ? "TRUE" : "FALSE";
            }

            var text = Convert.ToString(value) ?? string.Empty;
            if (bool.TryParse(text, out var boolValue))
            {
                return boolValue ? "TRUE" : "FALSE";
            }

            return text.Trim();
        }

        private static object BuildRandomSimulationValue(string baseValue)
        {
            if (string.IsNullOrWhiteSpace(baseValue))
            {
                return true;
            }

            var normalized = baseValue.Trim().ToUpperInvariant();
            if (normalized == "TRUE")
            {
                return false;
            }

            if (normalized == "FALSE")
            {
                return true;
            }

            if (normalized == "1")
            {
                return 0;
            }

            if (normalized == "0")
            {
                return 1;
            }

            if (decimal.TryParse(baseValue, out var number))
            {
                return number == 0 ? 1 : 0;
            }

            return $"{baseValue}_SIM";
        }

        private static bool ValuesEqual(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static CylinderSignalEventItemDto MapEvent(CylinderSignalEventEntity entity)
        {
            return new CylinderSignalEventItemDto
            {
                Id = entity.Id,
                PlcId = entity.PlcId,
                StationCode = entity.StationCode,
                CylinderId = entity.CylinderId,
                SignalName = entity.SignalName,
                SignalRole = entity.SignalRole,
                OldValue = entity.OldValue,
                NewValue = entity.NewValue,
                ChangedAt = entity.ChangedAt,
                StationMode = entity.StationMode,
                CreatedAt = entity.CreateTime ?? entity.ChangedAt
            };
        }

        private class InMemoryCacheItem<T>
        {
            public T Value { get; set; }
            public DateTime ExpireAt { get; set; }
        }

        private class StationGroupAccumulator
        {
            public string StationCode { get; set; }
            public string StationName { get; set; }
            public int PointCount { get; set; }
            public HashSet<long> ProjectIds { get; } = new HashSet<long>();
            public HashSet<string> TableNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
