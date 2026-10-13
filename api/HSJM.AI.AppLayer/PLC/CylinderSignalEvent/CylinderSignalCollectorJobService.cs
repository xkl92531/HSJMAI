using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS;
using HSJM.AI.AppLayer.PLC.JobCenter.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.CylinderSignalEvent
{
    public class CylinderSignalCollectorJobService : ISelfScopedAutoInject
    {
        private const string ConfigParamCode = "PLC_CYLINDER_SIGNAL_COLLECTOR_JOB";
        private readonly ISystemParameterService _systemParameterService;
        private readonly IPLC_ProjectService _projectService;
        private readonly CylinderSignalEventCollectorService _collectorService;
        private readonly ILogger<CylinderSignalCollectorJobService> _logger;

        public CylinderSignalCollectorJobService(
            ISystemParameterService systemParameterService,
            IPLC_ProjectService projectService,
            CylinderSignalEventCollectorService collectorService,
            ILogger<CylinderSignalCollectorJobService> logger)
        {
            _systemParameterService = systemParameterService;
            _projectService = projectService;
            _collectorService = collectorService;
            _logger = logger;
        }

        public async Task<CylinderSignalCollectorJobConfigDto> GetConfigAsync()
        {
            var entity = await GetConfigEntityAsync();
            return DeserializeConfig(entity?.ParamValue);
        }

        public async Task<HttpResponseResultModel<CylinderSignalCollectorJobConfigDto>> SaveConfigAsync(CylinderSignalCollectorJobConfigDto request)
        {
            var response = new HttpResponseResultModel<CylinderSignalCollectorJobConfigDto>();
            var current = await GetConfigAsync();
            var next = MergeConfig(current, request);
            await UpsertConfigAsync(next);
            response.IsSuccess = true;
            response.BackResult = next;
            return response;
        }

        public async Task<List<PlcJobCenterStationGroupDto>> GetStationGroupsAsync(string projectIdsText)
        {
            return await _collectorService.GetStationGroupsAsync(ParseProjectIds(projectIdsText));
        }

        public async Task<HttpResponseResultModel<CylinderSignalCacheClearResultDto>> ClearCacheAsync(
            string projectIdsText,
            List<string> stationCodes,
            bool clearAllStations)
        {
            var response = new HttpResponseResultModel<CylinderSignalCacheClearResultDto>();
            var result = await _collectorService.ClearSignalCacheAsync(ParseProjectIds(projectIdsText), stationCodes, clearAllStations);
            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        public async Task<HttpResponseResultModel<CylinderSignalCollectAllResultDto>> CollectAllProjectsOnceAsync(CylinderSignalCollectAllRequestDto request)
        {
            request ??= new CylinderSignalCollectAllRequestDto();
            var response = new HttpResponseResultModel<CylinderSignalCollectAllResultDto>();

            try
            {
                var config = await GetConfigAsync();
                var result = new CylinderSignalCollectAllResultDto
                {
                    TriggeredAt = DateTime.Now,
                    TriggerSource = string.IsNullOrWhiteSpace(request.TriggerSource) ? "Manual" : request.TriggerSource.Trim()
                };
                var projectIds = ParseProjectIds(request.ProjectIdsText ?? config.ProjectIdsText);
                var stationCodes = NormalizeStationCodes(request.StationCodes, request.StationCode);
                _logger.LogInformation(
                    "Cylinder collect-all start. TriggerSource={TriggerSource}, ProjectIds={ProjectIds}, StationCodes={StationCodes}, StationMode={StationMode}, UseSimulation={UseSimulation}, AutoGenerateSimulationValues={AutoGenerateSimulationValues}",
                    result.TriggerSource,
                    projectIds.Count > 0 ? string.Join(",", projectIds) : "ALL",
                    stationCodes.Count > 0 ? string.Join(",", stationCodes) : "ALL",
                    string.IsNullOrWhiteSpace(request.StationMode) ? config.StationMode : request.StationMode,
                    request.UseSimulation,
                    request.AutoGenerateSimulationValues);

                var projectQuery = _projectService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false)
                    .OrderBy(it => it.Id, OrderByType.Asc);
                if (projectIds.Count > 0)
                {
                    projectQuery = projectQuery.Where(it => projectIds.Contains(it.Id));
                }

                var projects = await projectQuery.ToListAsync();

                result.ProjectCount = projects.Count;

                foreach (var project in projects)
                {
                    var item = new CylinderSignalCollectProjectResultDto
                    {
                        ProjectId = project.Id,
                        ProjectName = project.ProjectName,
                        PlcId = project.Id.ToString()
                    };

                    try
                    {
                        var stationResults = await CollectProjectStationsAsync(project, request, config, stationCodes);
                        var successStations = stationResults.Where(it => it.IsSuccess).ToList();
                        var failedStations = stationResults.Where(it => !it.IsSuccess).ToList();
                        if (successStations.Count == 0)
                        {
                            item.IsSuccess = false;
                            item.ErrorMessage = string.Join(" | ", failedStations.Select(it => it.ErrorMessage).Where(it => string.IsNullOrWhiteSpace(it) == false));
                            result.FailedProjectCount++;
                            result.ProjectResults.Add(item);
                            continue;
                        }

                        item.IsSuccess = failedStations.Count == 0;
                        item.ErrorMessage = failedStations.Count == 0
                            ? null
                            : string.Join(" | ", failedStations.Select(it => $"{it.StationCode}:{it.ErrorMessage}"));
                        item.StationCode = successStations.Select(it => it.BackResult?.StationCode).Where(it => string.IsNullOrWhiteSpace(it) == false).Distinct(StringComparer.OrdinalIgnoreCase).Any()
                            ? string.Join(",", successStations.Select(it => it.BackResult?.StationCode).Where(it => string.IsNullOrWhiteSpace(it) == false).Distinct(StringComparer.OrdinalIgnoreCase))
                            : "ALL";
                        item.PointCount = successStations.Sum(it => it.BackResult?.PointCount ?? 0);
                        item.ReadCount = successStations.Sum(it => it.BackResult?.ReadCount ?? 0);
                        item.CacheHitCount = successStations.Sum(it => it.BackResult?.CacheHitCount ?? 0);
                        item.NoCacheCount = successStations.Sum(it => it.BackResult?.NoCacheCount ?? 0);
                        item.ReadPlcDurationMilliseconds = successStations.Sum(it => it.BackResult?.ReadPlcDurationMilliseconds ?? 0);
                        item.ChangedCount = successStations.Sum(it => it.BackResult?.ChangedCount ?? 0);
                        item.InsertedCount = successStations.Sum(it => it.BackResult?.InsertedCount ?? 0);

                        result.SuccessProjectCount++;
                        result.TotalPointCount += item.PointCount;
                        result.TotalReadCount += item.ReadCount;
                        result.TotalCacheHitCount += item.CacheHitCount;
                        result.TotalNoCacheCount += item.NoCacheCount;
                        result.TotalReadPlcDurationMilliseconds += item.ReadPlcDurationMilliseconds;
                        result.TotalChangedCount += item.ChangedCount;
                        result.TotalInsertedCount += item.InsertedCount;
                        result.ProjectResults.Add(item);
                    }
                    catch (Exception ex)
                    {
                        item.IsSuccess = false;
                        item.ErrorMessage = ex.Message;
                        result.FailedProjectCount++;
                        result.ProjectResults.Add(item);
                        _logger.LogError(ex, "Collect all cylinder signal events failed for project {ProjectId}", project.Id);
                    }
                }

                var updatedConfig = MergeConfig(config, config);
                updatedConfig.StationGroupCodesJson = stationCodes.Count > 0
                    ? JsonConvert.SerializeObject(stationCodes)
                    : config.StationGroupCodesJson;
                updatedConfig.LastRunAt = result.TriggeredAt;
                updatedConfig.LastRunStatus = result.FailedProjectCount > 0
                    ? (result.SuccessProjectCount > 0 ? "partial_success" : "failed")
                    : "success";
                updatedConfig.ProjectIdsText = projectIds.Count > 0
                    ? string.Join(",", projectIds)
                    : HSJM.AI.AppLayer.PLC.JobCenter.PlcJobCenterStoreService.NormalizeProjectIdsText(config.ProjectIdsText);
                updatedConfig.LastProjectCount = result.ProjectCount;
                if (ShouldApplySummaryMetrics(result, updatedConfig))
                {
                    updatedConfig.LastReadCount = result.TotalReadCount;
                    updatedConfig.LastCacheHitCount = result.TotalCacheHitCount;
                    updatedConfig.LastNoCacheCount = result.TotalNoCacheCount;
                    updatedConfig.LastReadPlcDurationMilliseconds = result.TotalReadPlcDurationMilliseconds;
                    updatedConfig.LastChangedCount = result.TotalChangedCount;
                    updatedConfig.LastInsertedCount = result.TotalInsertedCount;
                }
                updatedConfig.LastMessage = $"本次处理 {result.ProjectCount} 个项目，成功 {result.SuccessProjectCount}，失败 {result.FailedProjectCount}，落库 {result.TotalInsertedCount} 条。";
                await UpsertConfigAsync(updatedConfig);

                response.IsSuccess = true;
                response.BackResult = result;
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CollectAllProjectsOnceAsync failed.");
                var config = await GetConfigAsync();
                config.LastRunAt = DateTime.Now;
                config.LastRunStatus = "failed";
                config.LastMessage = ex.Message;
                await UpsertConfigAsync(config);
                response.ErrorMessage = ex.Message;
                return response;
            }
        }

        public async Task<HttpResponseResultModel<CylinderSignalCollectAllResultDto>> RunScheduledCollectAllAsync()
        {
            var config = await GetConfigAsync();
            return await CollectAllProjectsOnceAsync(new CylinderSignalCollectAllRequestDto
            {
                ProjectIdsText = config.ProjectIdsText,
                StationCodes = ParseStationGroupCodesJson(config.StationGroupCodesJson),
                StationMode = config.StationMode,
                ResetCacheBeforeCollect = config.ResetCacheBeforeCollect,
                TriggerSource = "BackgroundWorker"
            });
        }

        private async Task<List<StationCollectExecutionResult>> CollectProjectStationsAsync(
            PLC_ProjectEntity project,
            CylinderSignalCollectAllRequestDto request,
            CylinderSignalCollectorJobConfigDto config,
            List<string> stationCodes)
        {
            var normalizedStations = stationCodes?.Count > 0 ? stationCodes : new List<string> { null };
            var results = new List<StationCollectExecutionResult>();
            foreach (var stationCode in normalizedStations)
            {
                var stationResult = await CollectSingleStationAsync(project, stationCode, request, config);
                results.Add(stationResult);
            }

            return results;
        }

        private async Task<StationCollectExecutionResult> CollectSingleStationAsync(
            PLC_ProjectEntity project,
            string stationCode,
            CylinderSignalCollectAllRequestDto request,
            CylinderSignalCollectorJobConfigDto config)
        {
            _logger.LogInformation(
                "Cylinder collect single station. ProjectId={ProjectId}, StationCode={StationCode}, TriggerSource={TriggerSource}",
                project.Id,
                string.IsNullOrWhiteSpace(stationCode) ? "ALL" : stationCode,
                request.TriggerSource);

            var collectRequest = new CylinderSignalCollectRequestDto
            {
                ProjectId = project.Id,
                StationCode = stationCode,
                StationMode = string.IsNullOrWhiteSpace(request.StationMode) ? config.StationMode : request.StationMode.Trim(),
                ResetCacheBeforeCollect = request.ResetCacheBeforeCollect,
                UseSimulation = request.UseSimulation || request.AutoGenerateSimulationValues
            };

            if (request.AutoGenerateSimulationValues)
            {
                var simulationResult = await _collectorService.GenerateSimulationValuesAsync(new CylinderSignalSimulationGenerateRequestDto
                {
                    ProjectId = project.Id,
                    StationCode = stationCode
                });

                if (simulationResult.IsSuccess == false)
                {
                    return new StationCollectExecutionResult
                    {
                        StationCode = stationCode ?? "ALL",
                        ErrorMessage = simulationResult.ErrorMessage
                    };
                }

                collectRequest.SimulatedValues = simulationResult.BackResult?.SimulatedValues;
            }

            var collectResult = await _collectorService.CollectOnceAsync(collectRequest);
            return new StationCollectExecutionResult
            {
                StationCode = stationCode ?? "ALL",
                IsSuccess = collectResult.IsSuccess,
                ErrorMessage = collectResult.ErrorMessage,
                BackResult = collectResult.BackResult
            };
        }

        private async Task<SystemParameterEntity> GetConfigEntityAsync()
        {
            return await _systemParameterService.GetDb().AsQueryable()
                .FirstAsync(it => it.IsDeleted == false && it.ParamCode == ConfigParamCode);
        }

        private async Task UpsertConfigAsync(CylinderSignalCollectorJobConfigDto config)
        {
            var entity = await GetConfigEntityAsync();
            var json = JsonConvert.SerializeObject(config);

            if (entity == null)
            {
                entity = new SystemParameterEntity
                {
                    ParamCode = ConfigParamCode,
                    ParamName = "气缸信号采集任务配置",
                    ParamValue = json,
                    Remarks = "气缸信号事件全量采集任务配置",
                    Status = 1,
                    IsDeleted = false,
                    CreateTime = DateTime.Now
                };
                await _systemParameterService.InsertAsync(entity);
                return;
            }

            entity.ParamValue = json;
            entity.ParamName = "气缸信号采集任务配置";
            entity.Remarks = "气缸信号事件全量采集任务配置";
            entity.Status = 1;
            entity.ModifyTime = DateTime.Now;
            await _systemParameterService.UpdateAsync(entity);
        }

        private static CylinderSignalCollectorJobConfigDto DeserializeConfig(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return CreateDefaultConfig();
            }

            try
            {
                var config = JsonConvert.DeserializeObject<CylinderSignalCollectorJobConfigDto>(json);
                return MergeConfig(CreateDefaultConfig(), config);
            }
            catch
            {
                return CreateDefaultConfig();
            }
        }

        private static CylinderSignalCollectorJobConfigDto CreateDefaultConfig()
        {
            return new CylinderSignalCollectorJobConfigDto
            {
                Enabled = false,
                ProjectIdsText = "4541738602710831104",
                StationGroupCodesJson = "[]",
                IntervalMilliseconds = 30000,
                IntervalSeconds = 30,
                StationMode = "AUTO",
                ResetCacheBeforeCollect = false
            };
        }

        private static CylinderSignalCollectorJobConfigDto MergeConfig(
            CylinderSignalCollectorJobConfigDto current,
            CylinderSignalCollectorJobConfigDto incoming)
        {
            current ??= CreateDefaultConfig();
            incoming ??= current;

            return new CylinderSignalCollectorJobConfigDto
            {
                Enabled = incoming.Enabled,
                ProjectIdsText = HSJM.AI.AppLayer.PLC.JobCenter.PlcJobCenterStoreService.NormalizeProjectIdsText(string.IsNullOrWhiteSpace(incoming.ProjectIdsText) ? current.ProjectIdsText : incoming.ProjectIdsText),
                StationGroupCodesJson = HSJM.AI.AppLayer.PLC.JobCenter.PlcJobCenterStoreService.NormalizeStringArrayJson(string.IsNullOrWhiteSpace(incoming.StationGroupCodesJson) ? current.StationGroupCodesJson : incoming.StationGroupCodesJson),
                IntervalMilliseconds = NormalizeIntervalMilliseconds(incoming, current),
                IntervalSeconds = null,
                StationMode = string.IsNullOrWhiteSpace(incoming.StationMode) ? (string.IsNullOrWhiteSpace(current.StationMode) ? "AUTO" : current.StationMode) : incoming.StationMode.Trim(),
                ResetCacheBeforeCollect = incoming.ResetCacheBeforeCollect,
                LastRunAt = incoming.LastRunAt ?? current.LastRunAt,
                LastRunStatus = string.IsNullOrWhiteSpace(incoming.LastRunStatus) ? current.LastRunStatus : incoming.LastRunStatus,
                LastMessage = string.IsNullOrWhiteSpace(incoming.LastMessage) ? current.LastMessage : incoming.LastMessage,
                LastProjectCount = incoming.LastProjectCount > 0 ? incoming.LastProjectCount : current.LastProjectCount,
                LastReadCount = incoming.LastReadCount > 0 ? incoming.LastReadCount : current.LastReadCount,
                LastCacheHitCount = incoming.LastCacheHitCount > 0 ? incoming.LastCacheHitCount : current.LastCacheHitCount,
                LastNoCacheCount = incoming.LastNoCacheCount > 0 ? incoming.LastNoCacheCount : current.LastNoCacheCount,
                LastReadPlcDurationMilliseconds = incoming.LastReadPlcDurationMilliseconds > 0 ? incoming.LastReadPlcDurationMilliseconds : current.LastReadPlcDurationMilliseconds,
                LastChangedCount = incoming.LastChangedCount > 0 ? incoming.LastChangedCount : current.LastChangedCount,
                LastInsertedCount = incoming.LastInsertedCount > 0 ? incoming.LastInsertedCount : current.LastInsertedCount
            };
        }

        private static List<long> ParseProjectIds(string projectIdsText)
        {
            return (projectIdsText ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(it => long.TryParse(it.Trim(), out var projectId) ? projectId : 0)
                .Where(it => it > 0)
                .Distinct()
                .ToList();
        }

        private static int NormalizeIntervalMilliseconds(
            CylinderSignalCollectorJobConfigDto incoming,
            CylinderSignalCollectorJobConfigDto current)
        {
            var fallback = current?.IntervalMilliseconds > 0
                ? current.IntervalMilliseconds
                : (current?.IntervalSeconds > 0 ? current.IntervalSeconds.Value * 1000 : 30000);

            var candidate = incoming?.IntervalMilliseconds > 0
                ? incoming.IntervalMilliseconds
                : (incoming?.IntervalSeconds > 0 ? incoming.IntervalSeconds.Value * 1000 : fallback);

            return Math.Clamp(candidate, 1, 86400000);
        }

        private static List<string> NormalizeStationCodes(IEnumerable<string> stationCodes, string singleStationCode)
        {
            var values = (stationCodes ?? new List<string>())
                .Concat(string.IsNullOrWhiteSpace(singleStationCode) ? Array.Empty<string>() : new[] { singleStationCode })
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return values;
        }

        private static List<string> ParseStationGroupCodesJson(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<List<string>>(HSJM.AI.AppLayer.PLC.JobCenter.PlcJobCenterStoreService.NormalizeStringArrayJson(json))
                       ?.Where(it => string.IsNullOrWhiteSpace(it) == false)
                       .Select(it => it.Trim())
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList()
                    ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static bool ShouldApplySummaryMetrics(
            CylinderSignalCollectAllResultDto result,
            CylinderSignalCollectorJobConfigDto currentConfig)
        {
            if (result == null)
            {
                return false;
            }

            if (result.TotalReadCount > 0 ||
                result.TotalCacheHitCount > 0 ||
                result.TotalNoCacheCount > 0 ||
                result.TotalInsertedCount > 0 ||
                result.TotalChangedCount > 0 ||
                (result.TotalReadPlcDurationMilliseconds > 0 && result.TotalReadPlcDurationMilliseconds > 50))
            {
                return true;
            }

            return (currentConfig?.LastReadCount ?? 0) <= 0 &&
                   (currentConfig?.LastCacheHitCount ?? 0) <= 0 &&
                   (currentConfig?.LastNoCacheCount ?? 0) <= 0 &&
                   (currentConfig?.LastInsertedCount ?? 0) <= 0 &&
                   (currentConfig?.LastChangedCount ?? 0) <= 0;
        }

        private class StationCollectExecutionResult
        {
            public string StationCode { get; set; }
            public bool IsSuccess { get; set; }
            public string ErrorMessage { get; set; }
            public CylinderSignalCollectResultDto BackResult { get; set; }
        }
    }
}
