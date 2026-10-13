using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.JobCenter.DTOS
{
    public class PlcJobCenterTaskTemplateDto
    {
        public string TaskType { get; set; }
        public string TaskTypeName { get; set; }
        public string ExecutorType { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
    }

    public class PlcJobCenterTaskDefinitionDto
    {
        public string TaskKey { get; set; }
        public string TaskName { get; set; }
        public string TaskType { get; set; }
        public string TaskTypeName { get; set; }
        public string ExecutorType { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public string ScheduleType { get; set; } = "interval";
        public int IntervalMilliseconds { get; set; } = 30000;
        public string DailyTime { get; set; } = "08:00:00";
        public int TimeoutMilliseconds { get; set; } = 30000;
        public int RetryCount { get; set; }
        public string ProjectIdsText { get; set; } = "4541738602710831104";
        public string StationGroupCodesJson { get; set; } = "[]";
        public string StationMode { get; set; } = "AUTO";
        public bool ResetCacheBeforeCollect { get; set; }
        public string RequestUrl { get; set; }
        public string HttpMethod { get; set; } = "POST";
        public string HeadersJson { get; set; } = "{}";
        public string QueryJson { get; set; } = "{}";
        public string BodyJson { get; set; } = "{}";
        public string ContentType { get; set; } = "application/json";
        public DateTime? LastRunAt { get; set; }
        public DateTime? NextRunAt { get; set; }
        public string LastRunStatus { get; set; }
        public string LastMessage { get; set; }
        public long? LastDurationMilliseconds { get; set; }
        public int LastReadCount { get; set; }
        public int LastCacheHitCount { get; set; }
        public int LastNoCacheCount { get; set; }
        public long? LastReadPlcDurationMilliseconds { get; set; }
        public int LastProjectCount { get; set; }
        public int LastChangedCount { get; set; }
        public int LastInsertedCount { get; set; }
    }

    public class PlcJobCenterTaskItemDto
    {
        public string TaskKey { get; set; }
        public string TaskName { get; set; }
        public string TaskType { get; set; }
        public string TaskTypeName { get; set; }
        public string ExecutorType { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public string ScheduleType { get; set; }
        public int? IntervalMilliseconds { get; set; }
        public string DailyTime { get; set; }
        public int TimeoutMilliseconds { get; set; }
        public int RetryCount { get; set; }
        public string ProjectIdsText { get; set; }
        public string StationGroupCodesJson { get; set; }
        public string RequestUrl { get; set; }
        public string HttpMethod { get; set; }
        public DateTime? LastRunAt { get; set; }
        public DateTime? NextRunAt { get; set; }
        public string LastRunStatus { get; set; }
        public string LastMessage { get; set; }
        public long? LastDurationMilliseconds { get; set; }
        public int LastReadCount { get; set; }
        public int LastCacheHitCount { get; set; }
        public int LastNoCacheCount { get; set; }
        public long? LastReadPlcDurationMilliseconds { get; set; }
        public int LastProjectCount { get; set; }
        public int LastChangedCount { get; set; }
        public int LastInsertedCount { get; set; }
    }

    public class PlcJobCenterTaskDetailDto : PlcJobCenterTaskItemDto
    {
        public string StationMode { get; set; }
        public bool ResetCacheBeforeCollect { get; set; }
        public string HeadersJson { get; set; }
        public string QueryJson { get; set; }
        public string BodyJson { get; set; }
        public string ContentType { get; set; }
    }

    public class PlcJobCenterTaskCreateDto
    {
        public string TaskName { get; set; }
        public string TaskType { get; set; }
        public bool Enabled { get; set; }
        public string ScheduleType { get; set; } = "interval";
        public int? IntervalMilliseconds { get; set; }
        public string DailyTime { get; set; }
        public int? TimeoutMilliseconds { get; set; }
        public int? RetryCount { get; set; }
        public string ProjectIdsText { get; set; }
        public string StationGroupCodesJson { get; set; }
        public string StationMode { get; set; }
        public bool? ResetCacheBeforeCollect { get; set; }
        public string RequestUrl { get; set; }
        public string HttpMethod { get; set; }
        public string HeadersJson { get; set; }
        public string QueryJson { get; set; }
        public string BodyJson { get; set; }
        public string ContentType { get; set; }
    }

    public class PlcJobCenterTaskConfigSaveDto
    {
        public string TaskKey { get; set; }
        public string TaskName { get; set; }
        public bool Enabled { get; set; }
        public string ScheduleType { get; set; }
        public int? IntervalMilliseconds { get; set; }
        public string DailyTime { get; set; }
        public int? TimeoutMilliseconds { get; set; }
        public int? RetryCount { get; set; }
        public string ProjectIdsText { get; set; }
        public string StationGroupCodesJson { get; set; }
        public string StationMode { get; set; }
        public bool? ResetCacheBeforeCollect { get; set; }
        public string RequestUrl { get; set; }
        public string HttpMethod { get; set; }
        public string HeadersJson { get; set; }
        public string QueryJson { get; set; }
        public string BodyJson { get; set; }
        public string ContentType { get; set; }
    }

    public class PlcJobCenterRunTaskRequestDto
    {
        public string TaskKey { get; set; }
        public bool UseSimulation { get; set; }
        public bool AutoGenerateSimulationValues { get; set; }
    }

    public class PlcJobCenterTaskStateChangeDto
    {
        public string TaskKey { get; set; }
        public bool Enabled { get; set; }
    }

    public class PlcJobCenterTaskDeleteDto
    {
        public string TaskKey { get; set; }
    }

    public class PlcJobCenterRunTaskResultDto
    {
        public string TaskKey { get; set; }
        public string TaskName { get; set; }
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
    }

    public class PlcJobCenterClearCacheRequestDto
    {
        public string TaskKey { get; set; }
        public bool ClearAllProjectCaches { get; set; }
    }

    public class PlcJobCenterClearCacheResultDto
    {
        public string TaskKey { get; set; }
        public int ProjectCount { get; set; }
        public int StationCount { get; set; }
        public long ClearedKeyCount { get; set; }
        public string Scope { get; set; }
    }

    public class PlcJobCenterStationGroupDto
    {
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public int ProjectCount { get; set; }
        public int PointCount { get; set; }
        public List<string> TableNames { get; set; } = new List<string>();
    }
}
