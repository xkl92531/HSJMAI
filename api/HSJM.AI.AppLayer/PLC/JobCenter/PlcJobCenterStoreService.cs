using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.JobCenter.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.JobCenter
{
    internal static class PlcJobCenterPersistenceGate
    {
        internal static readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);
    }

    public class PlcJobCenterStoreService : ISelfScopedAutoInject
    {
        public const string TaskParamCodePrefix = "PLC_JOB_CENTER_TASK:";

        private readonly ISystemParameterService _systemParameterService;
        private readonly ILogger<PlcJobCenterStoreService> _logger;

        public PlcJobCenterStoreService(
            ISystemParameterService systemParameterService,
            ILogger<PlcJobCenterStoreService> logger)
        {
            _systemParameterService = systemParameterService;
            _logger = logger;
        }

        public async Task<List<PlcJobCenterTaskDefinitionDto>> GetAllTasksAsync(bool ensureDefaults = true)
        {
            await PlcJobCenterPersistenceGate.Lock.WaitAsync();
            try
            {
                var rows = await _systemParameterService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false && it.ParamCode.StartsWith(TaskParamCodePrefix))
                    .OrderBy(it => it.CreateTime, OrderByType.Asc)
                    .ToListAsync();

                return rows
                    .Select(MapTaskFromEntity)
                    .Where(it => it != null)
                    .ToList();
            }
            finally
            {
                PlcJobCenterPersistenceGate.Lock.Release();
            }
        }

        public async Task<PlcJobCenterTaskDefinitionDto> GetTaskAsync(string taskKey)
        {
            if (string.IsNullOrWhiteSpace(taskKey))
            {
                return null;
            }

            await PlcJobCenterPersistenceGate.Lock.WaitAsync();
            try
            {
                var entity = await _systemParameterService.GetDb().AsQueryable()
                    .FirstAsync(it => it.IsDeleted == false && it.ParamCode == BuildTaskParamCode(taskKey));
                return MapTaskFromEntity(entity);
            }
            finally
            {
                PlcJobCenterPersistenceGate.Lock.Release();
            }
        }

        public async Task SaveTaskAsync(PlcJobCenterTaskDefinitionDto definition)
        {
            NormalizeTask(definition);
            _logger.LogInformation(
                "PLC job center save task. TaskKey={TaskKey}, Enabled={Enabled}, ProjectIds={ProjectIds}, StationGroupCodesJson={StationGroupCodesJson}, ScheduleType={ScheduleType}, IntervalMilliseconds={IntervalMilliseconds}",
                definition?.TaskKey,
                definition?.Enabled,
                definition?.ProjectIdsText,
                definition?.StationGroupCodesJson,
                definition?.ScheduleType,
                definition?.IntervalMilliseconds);
            await PlcJobCenterPersistenceGate.Lock.WaitAsync();
            try
            {
            var entity = await _systemParameterService.GetDb().AsQueryable()
                .FirstAsync(it => it.IsDeleted == false && it.ParamCode == BuildTaskParamCode(definition.TaskKey));

            var json = SerializeTask(definition);
            if (entity == null)
            {
                entity = new SystemParameterEntity
                {
                    ParamCode = BuildTaskParamCode(definition.TaskKey),
                    ParamName = definition.TaskName,
                    ParamValue = json,
                    Remarks = $"PLC任务调度中心:{definition.TaskType}",
                    Status = 1,
                    IsDeleted = false,
                    CreateTime = DateTime.Now
                };
                await _systemParameterService.InsertAsync(entity);
                return;
            }

            entity.ParamName = definition.TaskName;
            entity.ParamValue = json;
            entity.Remarks = $"PLC任务调度中心:{definition.TaskType}";
            entity.Status = definition.Enabled ? 1 : 2;
            entity.ModifyTime = DateTime.Now;
            await _systemParameterService.UpdateAsync(entity);
            }
            finally
            {
                PlcJobCenterPersistenceGate.Lock.Release();
            }
        }

        public async Task UpdateTaskRuntimeStateAsync(
            string taskKey,
            Action<JObject> updateAction,
            string fallbackTaskName = null,
            string fallbackTaskType = null,
            bool? enabled = null)
        {
            if (string.IsNullOrWhiteSpace(taskKey) || updateAction == null)
            {
                return;
            }

            await PlcJobCenterPersistenceGate.Lock.WaitAsync();
            try
            {
            var entity = await _systemParameterService.GetDb().AsQueryable()
                .FirstAsync(it => it.IsDeleted == false && it.ParamCode == BuildTaskParamCode(taskKey));
            if (entity == null)
            {
                return;
            }

            JObject root;
            try
            {
                root = string.IsNullOrWhiteSpace(entity.ParamValue)
                    ? new JObject()
                    : JObject.Parse(entity.ParamValue);
            }
            catch
            {
                root = new JObject();
            }

            root["TaskKey"] = taskKey;
            if (string.IsNullOrWhiteSpace((string)root["TaskName"]) && string.IsNullOrWhiteSpace(fallbackTaskName) == false)
            {
                root["TaskName"] = fallbackTaskName;
            }

            if (string.IsNullOrWhiteSpace((string)root["TaskType"]) && string.IsNullOrWhiteSpace(fallbackTaskType) == false)
            {
                root["TaskType"] = fallbackTaskType;
            }

            if (enabled.HasValue)
            {
                root["Enabled"] = enabled.Value;
            }

            updateAction(root);

            entity.ParamName = string.IsNullOrWhiteSpace(fallbackTaskName) ? entity.ParamName : fallbackTaskName;
            entity.ParamValue = root.ToString(Formatting.None);
            entity.ModifyTime = DateTime.Now;
            await _systemParameterService.UpdateAsync(entity);
            }
            finally
            {
                PlcJobCenterPersistenceGate.Lock.Release();
            }
        }

        public async Task UpdateTaskConfigFieldsAsync(PlcJobCenterTaskDefinitionDto definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.TaskKey))
            {
                return;
            }

            await UpdateTaskRuntimeStateAsync(
                definition.TaskKey,
                root =>
                {
                    root["TaskName"] = definition.TaskName ?? string.Empty;
                    root["TaskType"] = definition.TaskType ?? string.Empty;
                    root["TaskTypeName"] = definition.TaskTypeName ?? string.Empty;
                    root["ExecutorType"] = definition.ExecutorType ?? string.Empty;
                    root["Category"] = definition.Category ?? string.Empty;
                    root["Description"] = definition.Description ?? string.Empty;
                    root["Enabled"] = definition.Enabled;
                    root["ScheduleType"] = definition.ScheduleType ?? "interval";
                    root["IntervalMilliseconds"] = definition.IntervalMilliseconds;
                    root["DailyTime"] = definition.DailyTime ?? "08:00:00";
                    root["TimeoutMilliseconds"] = definition.TimeoutMilliseconds;
                    root["RetryCount"] = definition.RetryCount;
                    root["ProjectIdsText"] = definition.ProjectIdsText ?? "4541738602710831104";
                    root["StationGroupCodesJson"] = NormalizeStringArrayJson(definition.StationGroupCodesJson);
                    root["StationMode"] = definition.StationMode ?? "AUTO";
                    root["ResetCacheBeforeCollect"] = definition.ResetCacheBeforeCollect;
                    root["RequestUrl"] = definition.RequestUrl ?? string.Empty;
                    root["HttpMethod"] = definition.HttpMethod ?? "POST";
                    root["HeadersJson"] = definition.HeadersJson ?? "{}";
                    root["QueryJson"] = definition.QueryJson ?? "{}";
                    root["BodyJson"] = definition.BodyJson ?? "{}";
                    root["ContentType"] = definition.ContentType ?? "application/json";
                },
                definition.TaskName,
                definition.TaskType,
                definition.Enabled);
        }

        public async Task DeleteTaskAsync(string taskKey)
        {
            if (string.IsNullOrWhiteSpace(taskKey))
            {
                return;
            }

            await PlcJobCenterPersistenceGate.Lock.WaitAsync();
            try
            {
            await _systemParameterService.GetDb().AsUpdateable()
                .SetColumns(it => new SystemParameterEntity
                {
                    IsDeleted = true,
                    ModifyTime = DateTime.Now
                })
                .Where(it => it.IsDeleted == false && it.ParamCode == BuildTaskParamCode(taskKey))
                .ExecuteCommandAsync();
            }
            finally
            {
                PlcJobCenterPersistenceGate.Lock.Release();
            }
        }

        public static string BuildTaskParamCode(string taskKey)
        {
            return $"{TaskParamCodePrefix}{taskKey}";
        }

        public static void NormalizeTask(PlcJobCenterTaskDefinitionDto model)
        {
            if (model == null)
            {
                return;
            }

            model.TaskKey = (model.TaskKey ?? string.Empty).Trim();
            model.TaskName = string.IsNullOrWhiteSpace(model.TaskName) ? model.TaskKey : model.TaskName.Trim();
            model.TaskType = (model.TaskType ?? string.Empty).Trim();
            model.TaskTypeName = string.IsNullOrWhiteSpace(model.TaskTypeName) ? model.TaskType : model.TaskTypeName.Trim();
            model.ExecutorType = NormalizeExecutorType(model.ExecutorType);
            model.Category = (model.Category ?? string.Empty).Trim();
            model.Description = (model.Description ?? string.Empty).Trim();
            model.ScheduleType = NormalizeScheduleType(model.ScheduleType);
            model.IntervalMilliseconds = Math.Clamp(model.IntervalMilliseconds <= 0 ? 30000 : model.IntervalMilliseconds, 1, 86400000);
            model.DailyTime = NormalizeDailyTime(model.DailyTime);
            model.TimeoutMilliseconds = Math.Clamp(model.TimeoutMilliseconds <= 0 ? 30000 : model.TimeoutMilliseconds, 1000, 600000);
            model.RetryCount = Math.Clamp(model.RetryCount, 0, 5);
            model.ProjectIdsText = NormalizeProjectIdsText(model.ProjectIdsText);
            model.StationGroupCodesJson = NormalizeStringArrayJson(model.StationGroupCodesJson);
            model.StationMode = string.IsNullOrWhiteSpace(model.StationMode) ? "AUTO" : model.StationMode.Trim();
            model.HttpMethod = NormalizeHttpMethod(model.HttpMethod);
            model.RequestUrl = (model.RequestUrl ?? string.Empty).Trim();
            model.HeadersJson = NormalizeJson(model.HeadersJson, "{}");
            model.QueryJson = NormalizeJson(model.QueryJson, "{}");
            model.BodyJson = NormalizeJson(model.BodyJson, "{}");
            model.ContentType = string.IsNullOrWhiteSpace(model.ContentType) ? "application/json" : model.ContentType.Trim();
        }

        public static string NormalizeScheduleType(string scheduleType)
        {
            return string.Equals(scheduleType, "daily", StringComparison.OrdinalIgnoreCase)
                ? "daily"
                : "interval";
        }

        public static string NormalizeExecutorType(string executorType)
        {
            return string.Equals(executorType, PlcJobCenterApp.ExecutorTypeHttp, StringComparison.OrdinalIgnoreCase)
                ? PlcJobCenterApp.ExecutorTypeHttp
                : PlcJobCenterApp.ExecutorTypeInternal;
        }

        public static string NormalizeDailyTime(string dailyTime)
        {
            if (TimeSpan.TryParse(dailyTime, out var time))
            {
                return time.ToString(@"hh\:mm\:ss");
            }

            return "08:00:00";
        }

        public static string NormalizeHttpMethod(string httpMethod)
        {
            var method = (httpMethod ?? "POST").Trim().ToUpperInvariant();
            return method switch
            {
                "GET" => "GET",
                "POST" => "POST",
                "PUT" => "PUT",
                "DELETE" => "DELETE",
                _ => "POST"
            };
        }

        public static string NormalizeProjectIdsText(string projectIdsText)
        {
            if (string.IsNullOrWhiteSpace(projectIdsText))
            {
                return "4541738602710831104";
            }

            var ids = projectIdsText
                .Split(new[] { ',', '，', ';', '；', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(it => it.Trim())
                .Where(it => long.TryParse(it, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return ids.Count == 0 ? "4541738602710831104" : string.Join(",", ids);
        }

        private static string NormalizeJson(string json, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return fallback;
            }

            try
            {
                JsonConvert.DeserializeObject(json);
                return json.Trim();
            }
            catch
            {
                return fallback;
            }
        }

        public static string NormalizeStringArrayJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "[]";
            }

            try
            {
                var token = JToken.Parse(json);
                if (token.Type != JTokenType.Array)
                {
                    return "[]";
                }

                var values = token.Values<string>()
                    .Where(it => string.IsNullOrWhiteSpace(it) == false)
                    .Select(it => it.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return JsonConvert.SerializeObject(values);
            }
            catch
            {
                return "[]";
            }
        }

        private static PlcJobCenterTaskDefinitionDto MapTaskFromEntity(SystemParameterEntity entity)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.ParamValue))
            {
                return null;
            }

            try
            {
                var model = JsonConvert.DeserializeObject<PlcJobCenterTaskDefinitionDto>(entity.ParamValue);
                if (model == null)
                {
                    return null;
                }

                TryFillSummaryFieldsFromRawJson(model, entity.ParamValue);

                model.TaskKey = string.IsNullOrWhiteSpace(model.TaskKey)
                    ? entity.ParamCode.Replace(TaskParamCodePrefix, string.Empty)
                    : model.TaskKey.Trim();
                model.TaskName = string.IsNullOrWhiteSpace(model.TaskName) ? entity.ParamName : model.TaskName.Trim();
                NormalizeTask(model);
                return model;
            }
            catch
            {
                return null;
            }
        }

        private static string SerializeTask(PlcJobCenterTaskDefinitionDto definition)
        {
            var root = JObject.FromObject(definition ?? new PlcJobCenterTaskDefinitionDto());
            root["TaskKey"] = definition?.TaskKey ?? string.Empty;
            root["TaskName"] = definition?.TaskName ?? string.Empty;
            root["TaskType"] = definition?.TaskType ?? string.Empty;
            root["TaskTypeName"] = definition?.TaskTypeName ?? string.Empty;
            root["ExecutorType"] = definition?.ExecutorType ?? string.Empty;
            root["Category"] = definition?.Category ?? string.Empty;
            root["Description"] = definition?.Description ?? string.Empty;
            root["Enabled"] = definition?.Enabled ?? false;
            root["ScheduleType"] = definition?.ScheduleType ?? "interval";
            root["IntervalMilliseconds"] = definition?.IntervalMilliseconds ?? 30000;
            root["DailyTime"] = definition?.DailyTime ?? "08:00:00";
            root["TimeoutMilliseconds"] = definition?.TimeoutMilliseconds ?? 30000;
            root["RetryCount"] = definition?.RetryCount ?? 0;
            root["ProjectIdsText"] = definition?.ProjectIdsText ?? "4541738602710831104";
            root["StationGroupCodesJson"] = definition?.StationGroupCodesJson ?? "[]";
            root["StationMode"] = definition?.StationMode ?? "AUTO";
            root["ResetCacheBeforeCollect"] = definition?.ResetCacheBeforeCollect ?? false;
            root["RequestUrl"] = definition?.RequestUrl ?? string.Empty;
            root["HttpMethod"] = definition?.HttpMethod ?? "POST";
            root["HeadersJson"] = definition?.HeadersJson ?? "{}";
            root["QueryJson"] = definition?.QueryJson ?? "{}";
            root["BodyJson"] = definition?.BodyJson ?? "{}";
            root["ContentType"] = definition?.ContentType ?? "application/json";
            return root.ToString(Formatting.None);
        }

        private static void TryFillSummaryFieldsFromRawJson(PlcJobCenterTaskDefinitionDto model, string json)
        {
            if (model == null || string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                var jObject = JObject.Parse(json);
                model.Enabled = ReadBool(jObject, "Enabled", model.Enabled);
                model.StationGroupCodesJson = ReadString(jObject, "StationGroupCodesJson", model.StationGroupCodesJson);
                model.LastReadCount = ReadInt(jObject, "LastReadCount", model.LastReadCount);
                model.LastCacheHitCount = ReadInt(jObject, "LastCacheHitCount", model.LastCacheHitCount);
                model.LastNoCacheCount = ReadInt(jObject, "LastNoCacheCount", model.LastNoCacheCount);
                model.LastInsertedCount = ReadInt(jObject, "LastInsertedCount", model.LastInsertedCount);
                model.LastChangedCount = ReadInt(jObject, "LastChangedCount", model.LastChangedCount);
                model.LastProjectCount = ReadInt(jObject, "LastProjectCount", model.LastProjectCount);
                model.LastReadPlcDurationMilliseconds = ReadLongNullable(jObject, "LastReadPlcDurationMilliseconds", model.LastReadPlcDurationMilliseconds);
                model.LastDurationMilliseconds = ReadLongNullable(jObject, "LastDurationMilliseconds", model.LastDurationMilliseconds);
            }
            catch
            {
            }
        }

        private static int ReadInt(JObject jObject, string propertyName, int fallback)
        {
            var token = jObject[propertyName];
            if (token == null)
            {
                return fallback;
            }

            return token.Type switch
            {
                JTokenType.Integer => token.Value<int>(),
                JTokenType.String when int.TryParse(token.Value<string>(), out var parsed) => parsed,
                _ => fallback
            };
        }

        private static long? ReadLongNullable(JObject jObject, string propertyName, long? fallback)
        {
            var token = jObject[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            return token.Type switch
            {
                JTokenType.Integer => token.Value<long>(),
                JTokenType.String when long.TryParse(token.Value<string>(), out var parsed) => parsed,
                _ => fallback
            };
        }

        private static bool ReadBool(JObject jObject, string propertyName, bool fallback)
        {
            var token = jObject[propertyName];
            if (token == null)
            {
                return fallback;
            }

            return token.Type switch
            {
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.String when bool.TryParse(token.Value<string>(), out var parsed) => parsed,
                _ => fallback
            };
        }

        private static string ReadString(JObject jObject, string propertyName, string fallback)
        {
            var token = jObject[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            return token.Type == JTokenType.String
                ? token.Value<string>()
                : fallback;
        }
    }
}
