using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS;
using HSJM.AI.AppLayer.PLC.JobCenter.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HSJM.AI.AppLayer.PLC.JobCenter
{
    public class PlcJobCenterApp : ISelfScopedAutoInject
    {
        public const string ExecutorTypeInternal = "internal";
        public const string ExecutorTypeHttp = "http";
        public const string CylinderSignalCollectorTaskType = "cylinder_signal_collector";
        public const string HttpRequestTaskType = "http_request";

        private readonly CylinderSignalCollectorJobService _cylinderSignalCollectorJobService;
        private readonly PlcJobCenterStoreService _storeService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<PlcJobCenterApp> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public PlcJobCenterApp(
            CylinderSignalCollectorJobService cylinderSignalCollectorJobService,
            PlcJobCenterStoreService storeService,
            IHttpClientFactory httpClientFactory,
            ILogger<PlcJobCenterApp> logger,
            IServiceScopeFactory scopeFactory)
        {
            _cylinderSignalCollectorJobService = cylinderSignalCollectorJobService;
            _storeService = storeService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        public Task<List<PlcJobCenterTaskTemplateDto>> GetTaskTemplatesAsync()
        {
            return Task.FromResult(new List<PlcJobCenterTaskTemplateDto>
            {
                new PlcJobCenterTaskTemplateDto
                {
                    TaskType = CylinderSignalCollectorTaskType,
                    TaskTypeName = "气缸信号全量采集",
                    ExecutorType = ExecutorTypeInternal,
                    Category = "PLC实时采集",
                    Description = "读取全部 PLC 项目的实时信号，结合 Redis 与数据库基线维护 cylinder_signal_event。"
                },
                new PlcJobCenterTaskTemplateDto
                {
                    TaskType = HttpRequestTaskType,
                    TaskTypeName = "通用HTTP接口任务",
                    ExecutorType = ExecutorTypeHttp,
                    Category = "通用接口调度",
                    Description = "自定义接口地址、请求方式、请求头、查询参数和请求体，适合调度任意 HTTP 接口。"
                }
            });
        }

        public async Task<List<PlcJobCenterTaskItemDto>> GetTasksAsync()
        {
            var tasks = await _storeService.GetAllTasksAsync();
            var collectorConfig = await _cylinderSignalCollectorJobService.GetConfigAsync();
            var now = DateTime.Now;
            return tasks.Select(it =>
            {
                ApplyCollectorSnapshot(it, collectorConfig);
                it.NextRunAt = CalculateNextRunAt(it, now);
                return MapTaskItem(it);
            }).ToList();
        }

        public async Task<PlcJobCenterTaskDetailDto> GetTaskDetailAsync(string taskKey)
        {
            var task = await _storeService.GetTaskAsync(taskKey);
            if (task == null)
            {
                return null;
            }

            ApplyCollectorSnapshot(task, await _cylinderSignalCollectorJobService.GetConfigAsync());
            task.NextRunAt = CalculateNextRunAt(task, DateTime.Now);
            return MapTaskDetail(task);
        }

        public async Task<List<PlcJobCenterStationGroupDto>> GetStationGroupsAsync(string projectIdsText)
        {
            return await _cylinderSignalCollectorJobService.GetStationGroupsAsync(projectIdsText);
        }

        public async Task<HttpResponseResultModel<PlcJobCenterTaskDetailDto>> CreateTaskAsync(PlcJobCenterTaskCreateDto request)
        {
            var response = new HttpResponseResultModel<PlcJobCenterTaskDetailDto>();
            var template = (await GetTaskTemplatesAsync()).FirstOrDefault(it =>
                string.Equals(it.TaskType, request?.TaskType, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                response.ErrorMessage = "不支持的任务类型。";
                return response;
            }

            var definition = new PlcJobCenterTaskDefinitionDto
            {
                TaskKey = $"{request.TaskType}_{GeneratePrimaryKeyIdHelper.GetPrimaryKeyId()}",
                TaskName = string.IsNullOrWhiteSpace(request.TaskName) ? template.TaskTypeName : request.TaskName.Trim(),
                TaskType = template.TaskType,
                TaskTypeName = template.TaskTypeName,
                ExecutorType = template.ExecutorType,
                Category = template.Category,
                Description = template.Description,
                Enabled = request.Enabled,
                ScheduleType = PlcJobCenterStoreService.NormalizeScheduleType(request.ScheduleType),
                IntervalMilliseconds = request.IntervalMilliseconds ?? 30000,
                DailyTime = request.DailyTime,
                TimeoutMilliseconds = request.TimeoutMilliseconds ?? 30000,
                RetryCount = request.RetryCount ?? 0,
                ProjectIdsText = request.ProjectIdsText,
                StationGroupCodesJson = request.StationGroupCodesJson,
                StationMode = request.StationMode,
                ResetCacheBeforeCollect = request.ResetCacheBeforeCollect ?? false,
                RequestUrl = request.RequestUrl,
                HttpMethod = request.HttpMethod,
                HeadersJson = request.HeadersJson,
                QueryJson = request.QueryJson,
                BodyJson = request.BodyJson,
                ContentType = request.ContentType
            };

            definition.NextRunAt = CalculateNextRunAt(definition, DateTime.Now);

            await SyncCollectorTaskConfigAsync(definition);
            await _storeService.SaveTaskAsync(definition);
            await _storeService.UpdateTaskConfigFieldsAsync(definition);
            ScheduleCollectorConfigReapply(definition);
            definition.NextRunAt = CalculateNextRunAt(definition, DateTime.Now);
            response.IsSuccess = true;
            response.BackResult = MapTaskDetail(definition);
            return response;
        }

        public async Task<HttpResponseResultModel<PlcJobCenterTaskDetailDto>> SaveTaskConfigAsync(PlcJobCenterTaskConfigSaveDto request)
        {
            var response = new HttpResponseResultModel<PlcJobCenterTaskDetailDto>();
            var task = await _storeService.GetTaskAsync(request?.TaskKey);
            if (task == null)
            {
                response.ErrorMessage = "任务不存在。";
                return response;
            }

            task.TaskName = string.IsNullOrWhiteSpace(request.TaskName) ? task.TaskName : request.TaskName.Trim();
            task.Enabled = request.Enabled;
            task.ScheduleType = PlcJobCenterStoreService.NormalizeScheduleType(request.ScheduleType);
            task.IntervalMilliseconds = request.IntervalMilliseconds ?? task.IntervalMilliseconds;
            task.DailyTime = request.DailyTime ?? task.DailyTime;
            task.TimeoutMilliseconds = request.TimeoutMilliseconds ?? task.TimeoutMilliseconds;
            task.RetryCount = request.RetryCount ?? task.RetryCount;
            task.ProjectIdsText = request.ProjectIdsText ?? task.ProjectIdsText;
            task.StationGroupCodesJson = request.StationGroupCodesJson ?? task.StationGroupCodesJson;
            task.StationMode = request.StationMode ?? task.StationMode;
            task.ResetCacheBeforeCollect = request.ResetCacheBeforeCollect ?? task.ResetCacheBeforeCollect;
            task.RequestUrl = request.RequestUrl ?? task.RequestUrl;
            task.HttpMethod = request.HttpMethod ?? task.HttpMethod;
            task.HeadersJson = request.HeadersJson ?? task.HeadersJson;
            task.QueryJson = request.QueryJson ?? task.QueryJson;
            task.BodyJson = request.BodyJson ?? task.BodyJson;
            task.ContentType = request.ContentType ?? task.ContentType;

            task.NextRunAt = CalculateNextRunAt(task, DateTime.Now);

            await SyncCollectorTaskConfigAsync(task);
            await _storeService.SaveTaskAsync(task);
            await _storeService.UpdateTaskConfigFieldsAsync(task);
            ScheduleCollectorConfigReapply(task);
            task.NextRunAt = CalculateNextRunAt(task, DateTime.Now);
            response.IsSuccess = true;
            response.BackResult = MapTaskDetail(task);
            return response;
        }

        public async Task<HttpResponseResultModel<bool>> ChangeTaskStateAsync(PlcJobCenterTaskStateChangeDto request)
        {
            var response = new HttpResponseResultModel<bool>();
            var task = await _storeService.GetTaskAsync(request?.TaskKey);
            if (task == null)
            {
                response.ErrorMessage = "任务不存在。";
                return response;
            }

            task.Enabled = request.Enabled;
            task.NextRunAt = CalculateNextRunAt(task, DateTime.Now);
            await SyncCollectorTaskConfigAsync(task);
            await _storeService.SaveTaskAsync(task);
            await _storeService.UpdateTaskConfigFieldsAsync(task);
            ScheduleCollectorConfigReapply(task);
            response.IsSuccess = true;
            response.BackResult = true;
            return response;
        }

        public async Task<HttpResponseResultModel<bool>> DeleteTaskAsync(PlcJobCenterTaskDeleteDto request)
        {
            var response = new HttpResponseResultModel<bool>();
            var task = await _storeService.GetTaskAsync(request?.TaskKey);
            if (task == null)
            {
                response.ErrorMessage = "任务不存在。";
                return response;
            }

            await _storeService.DeleteTaskAsync(task.TaskKey);
            response.IsSuccess = true;
            response.BackResult = true;
            return response;
        }

        public async Task<HttpResponseResultModel<PlcJobCenterRunTaskResultDto>> RunTaskNowAsync(PlcJobCenterRunTaskRequestDto request)
        {
            var response = new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>();
            var task = await _storeService.GetTaskAsync(request?.TaskKey);
            if (task == null)
            {
                response.ErrorMessage = "任务不存在。";
                return response;
            }

            await ApplyCollectorSnapshotAsync(task);
            var runResult = await ExecuteTaskAsync(task, request.UseSimulation, request.AutoGenerateSimulationValues, "Manual");
            response.IsSuccess = runResult.IsSuccess;
            response.ErrorMessage = runResult.ErrorMessage;
            response.BackResult = runResult.BackResult;
            return response;
        }

        public async Task<HttpResponseResultModel<PlcJobCenterClearCacheResultDto>> ClearTaskCacheAsync(PlcJobCenterClearCacheRequestDto request)
        {
            var response = new HttpResponseResultModel<PlcJobCenterClearCacheResultDto>();
            var task = await _storeService.GetTaskAsync(request?.TaskKey);
            if (task == null)
            {
                response.ErrorMessage = "任务不存在。";
                return response;
            }

            await ApplyCollectorSnapshotAsync(task);
            if (!string.Equals(task.TaskType, CylinderSignalCollectorTaskType, StringComparison.OrdinalIgnoreCase))
            {
                response.ErrorMessage = "当前任务不是气缸信号采集任务，无法清空缓存。";
                return response;
            }

            var stationCodes = request.ClearAllProjectCaches
                ? new List<string>()
                : ParseStationCodes(task.StationGroupCodesJson);
            var result = await _cylinderSignalCollectorJobService.ClearCacheAsync(
                task.ProjectIdsText,
                stationCodes,
                request.ClearAllProjectCaches);

            response.IsSuccess = result.IsSuccess;
            response.ErrorMessage = result.ErrorMessage;
            response.BackResult = new PlcJobCenterClearCacheResultDto
            {
                TaskKey = task.TaskKey,
                ProjectCount = result.BackResult?.ProjectCount ?? 0,
                StationCount = result.BackResult?.StationCount ?? 0,
                ClearedKeyCount = result.BackResult?.ClearedKeyCount ?? 0,
                Scope = request.ClearAllProjectCaches ? "project_all" : "task_current"
            };
            return response;
        }

        public async Task<HttpResponseResultModel<PlcJobCenterRunTaskResultDto>> ExecuteScheduledTaskAsync(string taskKey, string triggerSource = "BackgroundWorker")
        {
            var task = await _storeService.GetTaskAsync(taskKey);
            if (task == null)
            {
                return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
                {
                    ErrorMessage = "任务不存在。"
                };
            }

            await ApplyCollectorSnapshotAsync(task);
            if (!task.Enabled)
            {
                return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
                {
                    ErrorMessage = "任务已停用，本次不执行。",
                    BackResult = new PlcJobCenterRunTaskResultDto
                    {
                        TaskKey = task.TaskKey,
                        TaskName = task.TaskName,
                        IsSuccess = false,
                        Message = "任务已停用，本次不执行。"
                    }
                };
            }

            return await ExecuteTaskAsync(task, false, false, triggerSource);
        }

        private async Task<HttpResponseResultModel<PlcJobCenterRunTaskResultDto>> ExecuteTaskAsync(
            PlcJobCenterTaskDefinitionDto task,
            bool useSimulation,
            bool autoGenerateSimulationValues,
            string triggerSource)
        {
            var startedAt = DateTime.Now;
            var watch = Stopwatch.StartNew();
            await ResetTaskRunStateOnStartAsync(task, startedAt);
            using var heartbeatCts = new CancellationTokenSource();
            var heartbeatTask = RunTaskHeartbeatAsync(task, startedAt, heartbeatCts.Token);
            Exception lastException = null;
            string lastMessage = null;
            string lastStatus = "failed";
            object lastData = null;

            var maxAttempts = Math.Max(task.RetryCount, 0) + 1;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    HttpResponseResultModel<PlcJobCenterRunTaskResultDto> runResult;
                    if (string.Equals(task.ExecutorType, ExecutorTypeHttp, StringComparison.OrdinalIgnoreCase))
                    {
                        runResult = await ExecuteHttpTaskAsync(task);
                    }
                    else
                    {
                        runResult = await ExecuteInternalTaskAsync(task, useSimulation, autoGenerateSimulationValues, triggerSource);
                    }

                    if (runResult.IsSuccess)
                    {
                        watch.Stop();
                        heartbeatCts.Cancel();
                        await AwaitHeartbeatStopAsync(heartbeatTask);
                        var successStatus = "success";
                        if (runResult.BackResult?.Data is CylinderSignalCollectAllResultDto collectResult && collectResult.FailedProjectCount > 0)
                        {
                            successStatus = collectResult.SuccessProjectCount > 0 ? "partial_success" : "failed";
                        }

                        await UpdateTaskRunStateAsync(task, successStatus, runResult.BackResult?.Message, watch.ElapsedMilliseconds, runResult.BackResult?.Data, startedAt, triggerSource);
                        return runResult;
                    }

                    lastMessage = runResult.ErrorMessage ?? runResult.BackResult?.Message;
                    lastData = runResult.BackResult?.Data;
                    lastStatus = "failed";
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastMessage = ex.Message;
                    lastStatus = "failed";
                    _logger.LogError(ex, "PLC任务执行失败。TaskKey={TaskKey}, Attempt={Attempt}", task.TaskKey, attempt);
                }
            }

            watch.Stop();
            if (lastException != null)
            {
                lastMessage = lastException.Message;
            }

            heartbeatCts.Cancel();
            await AwaitHeartbeatStopAsync(heartbeatTask);
            await UpdateTaskRunStateAsync(task, lastStatus, lastMessage, watch.ElapsedMilliseconds, lastData, startedAt, triggerSource);
            return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
            {
                ErrorMessage = lastMessage,
                BackResult = new PlcJobCenterRunTaskResultDto
                {
                    TaskKey = task.TaskKey,
                    TaskName = task.TaskName,
                    IsSuccess = false,
                    Message = lastMessage,
                    Data = lastData
                }
            };
        }

        private async Task ResetTaskRunStateOnStartAsync(PlcJobCenterTaskDefinitionDto task, DateTime startedAt)
        {
            using var refreshScope = _scopeFactory.CreateScope();
            var freshStoreService = refreshScope.ServiceProvider.GetRequiredService<PlcJobCenterStoreService>();
            await freshStoreService.UpdateTaskRuntimeStateAsync(
                task.TaskKey,
                root =>
                {
                    root["LastRunAt"] = JToken.FromObject(startedAt);
                    root["LastRunStatus"] = "running";
                    root["LastMessage"] = "执行中...";
                    root["LastDurationMilliseconds"] = 0;
                },
                task.TaskName,
                task.TaskType,
                task.Enabled);
        }

        private async Task<HttpResponseResultModel<PlcJobCenterRunTaskResultDto>> ExecuteInternalTaskAsync(
            PlcJobCenterTaskDefinitionDto task,
            bool useSimulation,
            bool autoGenerateSimulationValues,
            string triggerSource)
        {
            if (!string.Equals(task.TaskType, CylinderSignalCollectorTaskType, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
                {
                    ErrorMessage = "当前内部任务类型尚未接入执行器。"
                };
            }

            var collectorConfig = await _cylinderSignalCollectorJobService.GetConfigAsync();
            var effectiveStationGroupCodesJson = !string.IsNullOrWhiteSpace(task.StationGroupCodesJson) && task.StationGroupCodesJson != "[]"
                ? task.StationGroupCodesJson
                : collectorConfig?.StationGroupCodesJson;
            var stationCodes = ParseStationCodes(effectiveStationGroupCodesJson);
            var effectiveProjectIdsText = string.IsNullOrWhiteSpace(task.ProjectIdsText)
                ? collectorConfig?.ProjectIdsText
                : task.ProjectIdsText;
            var effectiveStationMode = string.IsNullOrWhiteSpace(task.StationMode)
                ? collectorConfig?.StationMode
                : task.StationMode;
            var effectiveResetCache = task.ResetCacheBeforeCollect || (collectorConfig?.ResetCacheBeforeCollect ?? false);
            task.StationGroupCodesJson = string.IsNullOrWhiteSpace(effectiveStationGroupCodesJson) ? "[]" : effectiveStationGroupCodesJson;
            task.ProjectIdsText = effectiveProjectIdsText;
            task.StationMode = effectiveStationMode;
            task.ResetCacheBeforeCollect = effectiveResetCache;
            _logger.LogInformation(
                "PLC job center execute internal task. TaskKey={TaskKey}, TriggerSource={TriggerSource}, ProjectIds={ProjectIds}, StationCodes={StationCodes}, Enabled={Enabled}, ScheduleType={ScheduleType}",
                task.TaskKey,
                triggerSource,
                effectiveProjectIdsText,
                stationCodes.Count > 0 ? string.Join(",", stationCodes) : "ALL",
                task.Enabled,
                task.ScheduleType);

            var runResult = await _cylinderSignalCollectorJobService.CollectAllProjectsOnceAsync(new CylinderSignalCollectAllRequestDto
            {
                TriggerSource = triggerSource,
                StationCodes = stationCodes,
                StationMode = effectiveStationMode,
                ResetCacheBeforeCollect = effectiveResetCache,
                ProjectIdsText = effectiveProjectIdsText,
                UseSimulation = useSimulation,
                AutoGenerateSimulationValues = autoGenerateSimulationValues
            });

            if (runResult.BackResult != null)
            {
                var status = runResult.BackResult.FailedProjectCount > 0
                    ? (runResult.BackResult.SuccessProjectCount > 0 ? "partial_success" : "failed")
                    : "success";
                return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
                {
                    IsSuccess = runResult.IsSuccess,
                    ErrorMessage = runResult.ErrorMessage,
                    BackResult = new PlcJobCenterRunTaskResultDto
                    {
                        TaskKey = task.TaskKey,
                        TaskName = task.TaskName,
                        IsSuccess = runResult.IsSuccess,
                        Message = $"执行完成，处理 {runResult.BackResult.ProjectCount} 个项目，状态：{status}。",
                        Data = runResult.BackResult
                    }
                };
            }

            return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
            {
                IsSuccess = runResult.IsSuccess,
                ErrorMessage = runResult.ErrorMessage,
                BackResult = new PlcJobCenterRunTaskResultDto
                {
                    TaskKey = task.TaskKey,
                    TaskName = task.TaskName,
                    IsSuccess = false,
                    Message = runResult.ErrorMessage,
                    Data = null
                }
            };
        }

        private async Task<HttpResponseResultModel<PlcJobCenterRunTaskResultDto>> ExecuteHttpTaskAsync(PlcJobCenterTaskDefinitionDto task)
        {
            if (string.IsNullOrWhiteSpace(task.RequestUrl))
            {
                return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
                {
                    ErrorMessage = "HTTP任务未配置接口地址。"
                };
            }

            using var request = BuildHttpRequest(task);
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(task.TimeoutMilliseconds);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(task.TimeoutMilliseconds));
            var response = await client.SendAsync(request, cts.Token);
            var responseText = await response.Content.ReadAsStringAsync();

            var ok = response.IsSuccessStatusCode;
            var message = ok
                ? $"HTTP任务执行成功，状态码 {(int)response.StatusCode}。"
                : $"HTTP任务执行失败，状态码 {(int)response.StatusCode}。";

            return new HttpResponseResultModel<PlcJobCenterRunTaskResultDto>
            {
                IsSuccess = ok,
                ErrorMessage = ok ? null : responseText,
                BackResult = new PlcJobCenterRunTaskResultDto
                {
                    TaskKey = task.TaskKey,
                    TaskName = task.TaskName,
                    IsSuccess = ok,
                    Message = message,
                    Data = new
                    {
                        statusCode = (int)response.StatusCode,
                        responseText,
                        requestUrl = request.RequestUri?.ToString(),
                        method = request.Method.Method
                    }
                }
            };
        }

        private HttpRequestMessage BuildHttpRequest(PlcJobCenterTaskDefinitionDto task)
        {
            var url = AppendQueryString(task.RequestUrl, task.QueryJson);
            var request = new HttpRequestMessage(new HttpMethod(task.HttpMethod), url);

            foreach (var pair in ParseStringDictionary(task.HeadersJson))
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            if (!string.Equals(task.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(task.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new StringContent(NormalizeBody(task.BodyJson, task.ContentType), Encoding.UTF8, task.ContentType);
            }

            return request;
        }

        private static string AppendQueryString(string baseUrl, string queryJson)
        {
            var pairs = ParseStringDictionary(queryJson);
            if (pairs.Count == 0)
            {
                return baseUrl;
            }

            var joinChar = baseUrl.Contains("?") ? "&" : "?";
            var query = string.Join("&", pairs.Select(it =>
                $"{Uri.EscapeDataString(it.Key)}={Uri.EscapeDataString(it.Value ?? string.Empty)}"));
            return $"{baseUrl}{joinChar}{query}";
        }

        private static Dictionary<string, string> ParseStringDictionary(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var token = JToken.Parse(json);
                if (token.Type != JTokenType.Object)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                return token.Children<JProperty>()
                    .ToDictionary(it => it.Name, it => it.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeBody(string bodyJson, string contentType)
        {
            if (string.IsNullOrWhiteSpace(bodyJson))
            {
                return "{}";
            }

            if (!string.IsNullOrWhiteSpace(contentType)
                && contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    return JToken.Parse(bodyJson).ToString(Formatting.None);
                }
                catch
                {
                    return bodyJson;
                }
            }

            return bodyJson;
        }

        private async Task RunTaskHeartbeatAsync(
            PlcJobCenterTaskDefinitionDto task,
            DateTime startedAt,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken);
                    using var refreshScope = _scopeFactory.CreateScope();
                    var freshStoreService = refreshScope.ServiceProvider.GetRequiredService<PlcJobCenterStoreService>();
                    var elapsed = Math.Max((long)(DateTime.Now - startedAt).TotalMilliseconds, 0);
                    await freshStoreService.UpdateTaskRuntimeStateAsync(
                        task.TaskKey,
                        root =>
                        {
                            root["LastDurationMilliseconds"] = elapsed;
                        },
                        task.TaskName,
                        task.TaskType,
                        task.Enabled);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task AwaitHeartbeatStopAsync(Task heartbeatTask)
        {
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task UpdateTaskRunStateAsync(
            PlcJobCenterTaskDefinitionDto task,
            string status,
            string message,
            long durationMilliseconds,
            object data,
            DateTime startedAt,
            string triggerSource)
        {
            using var refreshScope = _scopeFactory.CreateScope();
            var freshStoreService = refreshScope.ServiceProvider.GetRequiredService<PlcJobCenterStoreService>();
            var target = await freshStoreService.GetTaskAsync(task.TaskKey) ?? task;
            _logger.LogInformation(
                "PLC job center update run state. TaskKey={TaskKey}, TriggerSource={TriggerSource}, IncomingStationGroupCodesJson={IncomingStationGroupCodesJson}, LoadedStationGroupCodesJson={LoadedStationGroupCodesJson}, Status={Status}, DurationMilliseconds={DurationMilliseconds}",
                task.TaskKey,
                triggerSource,
                task?.StationGroupCodesJson,
                target?.StationGroupCodesJson,
                status,
                durationMilliseconds);

            var nextRunAt = CalculateNextRunAt(new PlcJobCenterTaskDefinitionDto
            {
                Enabled = target.Enabled,
                ScheduleType = target.ScheduleType,
                IntervalMilliseconds = target.IntervalMilliseconds,
                DailyTime = target.DailyTime,
                LastRunAt = startedAt,
                LastDurationMilliseconds = durationMilliseconds
            }, DateTime.Now);

            await freshStoreService.UpdateTaskRuntimeStateAsync(
                task.TaskKey,
                root =>
                {
                root["LastRunAt"] = JToken.FromObject(startedAt);
                root["LastRunStatus"] = status;
                root["LastMessage"] = string.IsNullOrWhiteSpace(message) ? status : message;
                root["LastDurationMilliseconds"] = durationMilliseconds;
                root["NextRunAt"] = nextRunAt == null ? JValue.CreateNull() : JToken.FromObject(nextRunAt.Value);
                if (!string.IsNullOrWhiteSpace(task.StationGroupCodesJson))
                {
                    root["StationGroupCodesJson"] = task.StationGroupCodesJson;
                }

                if (TryGetCollectResultMetrics(data, out var collectResult))
                {
                    root["LastProjectCount"] = collectResult.ProjectCount;
                    root["LastReadCount"] = collectResult.TotalReadCount;
                    root["LastCacheHitCount"] = collectResult.TotalCacheHitCount;
                    root["LastNoCacheCount"] = collectResult.TotalNoCacheCount;
                    root["LastChangedCount"] = collectResult.TotalChangedCount;
                    root["LastInsertedCount"] = collectResult.TotalInsertedCount;
                    root["LastReadPlcDurationMilliseconds"] = collectResult.TotalReadPlcDurationMilliseconds;
                    root["LastMessage"] = $"处理 {collectResult.ProjectCount} 个项目，成功 {collectResult.SuccessProjectCount}，失败 {collectResult.FailedProjectCount}，落库 {collectResult.TotalInsertedCount} 条。";
                }
                },
                task.TaskName,
                task.TaskType,
                target.Enabled);
        }

        private static void ApplyCollectorSnapshot(
            PlcJobCenterTaskDefinitionDto task,
            CylinderSignalCollectorJobConfigDto collectorConfig)
        {
            if (task == null ||
                collectorConfig == null ||
                !string.Equals(task.TaskType, CylinderSignalCollectorTaskType, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            task.StationGroupCodesJson = string.IsNullOrWhiteSpace(task.StationGroupCodesJson) || task.StationGroupCodesJson == "[]"
                ? collectorConfig.StationGroupCodesJson
                : task.StationGroupCodesJson;
        }

        private static bool TryGetCollectResultMetrics(object data, out CylinderSignalCollectAllResultDto result)
        {
            result = null;
            if (data == null)
            {
                return false;
            }

            if (data is CylinderSignalCollectAllResultDto typedResult)
            {
                result = typedResult;
                return true;
            }

            try
            {
                result = (data as JObject ?? JObject.FromObject(data)).ToObject<CylinderSignalCollectAllResultDto>();
            }
            catch
            {
                result = null;
            }

            return result != null;
        }

        public static DateTime? CalculateNextRunAt(PlcJobCenterTaskDefinitionDto task, DateTime now)
        {
            if (task == null || !task.Enabled)
            {
                return null;
            }

            if (string.Equals(task.ScheduleType, "daily", StringComparison.OrdinalIgnoreCase))
            {
                var dailyTime = TimeSpan.TryParse(task.DailyTime, out var parsedTime)
                    ? parsedTime
                    : TimeSpan.FromHours(8);
                var next = now.Date.Add(dailyTime);
                if (task.LastRunAt.HasValue && task.LastRunAt.Value.Date == now.Date)
                {
                    next = now.Date.AddDays(1).Add(dailyTime);
                }
                else if (next <= now)
                {
                    next = now.Date.AddDays(1).Add(dailyTime);
                }

                return next;
            }

            var interval = Math.Clamp(task.IntervalMilliseconds <= 0 ? 30000 : task.IntervalMilliseconds, 1, 86400000);
            if (!task.LastRunAt.HasValue)
            {
                return now;
            }

            var duration = Math.Max(task.LastDurationMilliseconds ?? 0, 0);
            return task.LastRunAt.Value
                .AddMilliseconds(duration)
                .AddMilliseconds(interval);
        }

        private static PlcJobCenterTaskItemDto MapTaskItem(PlcJobCenterTaskDefinitionDto task)
        {
            return new PlcJobCenterTaskItemDto
            {
                TaskKey = task.TaskKey,
                TaskName = task.TaskName,
                TaskType = task.TaskType,
                TaskTypeName = task.TaskTypeName,
                ExecutorType = task.ExecutorType,
                Category = task.Category,
                Description = task.Description,
                Enabled = task.Enabled,
                ScheduleType = task.ScheduleType,
                IntervalMilliseconds = task.ScheduleType == "interval" ? task.IntervalMilliseconds : null,
                DailyTime = task.ScheduleType == "daily" ? task.DailyTime : null,
                TimeoutMilliseconds = task.TimeoutMilliseconds,
                RetryCount = task.RetryCount,
                ProjectIdsText = task.ProjectIdsText,
                StationGroupCodesJson = task.StationGroupCodesJson,
                RequestUrl = task.RequestUrl,
                HttpMethod = task.HttpMethod,
                LastRunAt = task.LastRunAt,
                NextRunAt = task.NextRunAt,
                LastRunStatus = task.LastRunStatus,
                LastMessage = task.LastMessage,
                LastDurationMilliseconds = task.LastDurationMilliseconds,
                LastReadCount = task.LastReadCount,
                LastCacheHitCount = task.LastCacheHitCount,
                LastNoCacheCount = task.LastNoCacheCount,
                LastReadPlcDurationMilliseconds = task.LastReadPlcDurationMilliseconds,
                LastProjectCount = task.LastProjectCount,
                LastChangedCount = task.LastChangedCount,
                LastInsertedCount = task.LastInsertedCount
            };
        }

        private static PlcJobCenterTaskDetailDto MapTaskDetail(PlcJobCenterTaskDefinitionDto task)
        {
            return new PlcJobCenterTaskDetailDto
            {
                TaskKey = task.TaskKey,
                TaskName = task.TaskName,
                TaskType = task.TaskType,
                TaskTypeName = task.TaskTypeName,
                ExecutorType = task.ExecutorType,
                Category = task.Category,
                Description = task.Description,
                Enabled = task.Enabled,
                ScheduleType = task.ScheduleType,
                IntervalMilliseconds = task.ScheduleType == "interval" ? task.IntervalMilliseconds : null,
                DailyTime = task.ScheduleType == "daily" ? task.DailyTime : null,
                TimeoutMilliseconds = task.TimeoutMilliseconds,
                RetryCount = task.RetryCount,
                ProjectIdsText = task.ProjectIdsText,
                StationGroupCodesJson = task.StationGroupCodesJson,
                RequestUrl = task.RequestUrl,
                HttpMethod = task.HttpMethod,
                HeadersJson = task.HeadersJson,
                QueryJson = task.QueryJson,
                BodyJson = task.BodyJson,
                ContentType = task.ContentType,
                StationMode = task.StationMode,
                ResetCacheBeforeCollect = task.ResetCacheBeforeCollect,
                LastRunAt = task.LastRunAt,
                NextRunAt = task.NextRunAt,
                LastRunStatus = task.LastRunStatus,
                LastMessage = task.LastMessage,
                LastDurationMilliseconds = task.LastDurationMilliseconds,
                LastReadCount = task.LastReadCount,
                LastCacheHitCount = task.LastCacheHitCount,
                LastNoCacheCount = task.LastNoCacheCount,
                LastReadPlcDurationMilliseconds = task.LastReadPlcDurationMilliseconds,
                LastProjectCount = task.LastProjectCount,
                LastChangedCount = task.LastChangedCount,
                LastInsertedCount = task.LastInsertedCount
            };
        }

        private static List<string> ParseStationCodes(string stationGroupCodesJson)
        {
            if (string.IsNullOrWhiteSpace(stationGroupCodesJson))
            {
                return new List<string>();
            }

            try
            {
                var values = JArray.Parse(stationGroupCodesJson)
                    .Values<string>()
                    .Where(it => string.IsNullOrWhiteSpace(it) == false)
                    .Select(it => it.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return values;
            }
            catch
            {
                return new List<string>();
            }
        }

        private async Task ApplyCollectorSnapshotAsync(PlcJobCenterTaskDefinitionDto task)
        {
            if (task == null ||
                !string.Equals(task.TaskType, CylinderSignalCollectorTaskType, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyCollectorSnapshot(task, await _cylinderSignalCollectorJobService.GetConfigAsync());
        }

        private void ScheduleCollectorConfigReapply(PlcJobCenterTaskDefinitionDto task)
        {
            if (task == null ||
                !task.Enabled ||
                !string.Equals(task.TaskType, CylinderSignalCollectorTaskType, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var snapshot = new PlcJobCenterTaskDefinitionDto
            {
                TaskKey = task.TaskKey,
                TaskName = task.TaskName,
                TaskType = task.TaskType,
                TaskTypeName = task.TaskTypeName,
                ExecutorType = task.ExecutorType,
                Category = task.Category,
                Description = task.Description,
                Enabled = task.Enabled,
                ScheduleType = task.ScheduleType,
                IntervalMilliseconds = task.IntervalMilliseconds,
                DailyTime = task.DailyTime,
                TimeoutMilliseconds = task.TimeoutMilliseconds,
                RetryCount = task.RetryCount,
                ProjectIdsText = task.ProjectIdsText,
                StationGroupCodesJson = task.StationGroupCodesJson,
                StationMode = task.StationMode,
                ResetCacheBeforeCollect = task.ResetCacheBeforeCollect,
                RequestUrl = task.RequestUrl,
                HttpMethod = task.HttpMethod,
                HeadersJson = task.HeadersJson,
                QueryJson = task.QueryJson,
                BodyJson = task.BodyJson,
                ContentType = task.ContentType,
                LastRunAt = task.LastRunAt,
                NextRunAt = task.NextRunAt,
                LastRunStatus = task.LastRunStatus,
                LastMessage = task.LastMessage,
                LastDurationMilliseconds = task.LastDurationMilliseconds,
                LastReadCount = task.LastReadCount,
                LastCacheHitCount = task.LastCacheHitCount,
                LastNoCacheCount = task.LastNoCacheCount,
                LastReadPlcDurationMilliseconds = task.LastReadPlcDurationMilliseconds,
                LastProjectCount = task.LastProjectCount,
                LastChangedCount = task.LastChangedCount,
                LastInsertedCount = task.LastInsertedCount
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);
                    using var scope = _scopeFactory.CreateScope();
                    var app = scope.ServiceProvider.GetRequiredService<PlcJobCenterApp>();
                    await app.SyncCollectorTaskConfigAsync(snapshot);
                    await app._storeService.UpdateTaskConfigFieldsAsync(snapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PLC job center delayed config reapply failed. TaskKey={TaskKey}", snapshot.TaskKey);
                }
            });
        }

        private async Task SyncCollectorTaskConfigAsync(PlcJobCenterTaskDefinitionDto task)
        {
            if (task == null ||
                !string.Equals(task.TaskType, CylinderSignalCollectorTaskType, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var current = await _cylinderSignalCollectorJobService.GetConfigAsync();
            await _cylinderSignalCollectorJobService.SaveConfigAsync(new CylinderSignalCollectorJobConfigDto
            {
                Enabled = task.Enabled,
                ProjectIdsText = task.ProjectIdsText,
                StationGroupCodesJson = task.StationGroupCodesJson,
                IntervalMilliseconds = task.IntervalMilliseconds,
                StationMode = task.StationMode,
                ResetCacheBeforeCollect = task.ResetCacheBeforeCollect,
                LastRunAt = current.LastRunAt,
                LastRunStatus = current.LastRunStatus,
                LastMessage = current.LastMessage,
                LastProjectCount = current.LastProjectCount,
                LastReadCount = current.LastReadCount,
                LastCacheHitCount = current.LastCacheHitCount,
                LastNoCacheCount = current.LastNoCacheCount,
                LastReadPlcDurationMilliseconds = current.LastReadPlcDurationMilliseconds,
                LastChangedCount = current.LastChangedCount,
                LastInsertedCount = current.LastInsertedCount
            });
        }
    }
}
