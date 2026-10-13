using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS
{
    public class CylinderSignalCollectRequestDto
    {
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
        public string StationMode { get; set; }
        public bool ResetCacheBeforeCollect { get; set; }
        public bool UseSimulation { get; set; }
        public Dictionary<string, object> SimulatedValues { get; set; }
    }

    public class CylinderSignalSimulationGenerateRequestDto
    {
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
    }

    public class CylinderSignalCollectAllRequestDto
    {
        public string ProjectIdsText { get; set; }
        public string StationCode { get; set; }
        public List<string> StationCodes { get; set; } = new List<string>();
        public string StationMode { get; set; }
        public bool ResetCacheBeforeCollect { get; set; }
        public bool UseSimulation { get; set; }
        public bool AutoGenerateSimulationValues { get; set; }
        public string TriggerSource { get; set; }
    }

    public class CylinderSignalCollectResultDto
    {
        public long ProjectId { get; set; }
        public string PlcId { get; set; }
        public string StationCode { get; set; }
        public int PointCount { get; set; }
        public int ReadCount { get; set; }
        public int CacheHitCount { get; set; }
        public int NoCacheCount { get; set; }
        public long ReadPlcDurationMilliseconds { get; set; }
        public int ChangedCount { get; set; }
        public int InsertedCount { get; set; }
        public List<string> MissingSignals { get; set; } = new List<string>();
        public List<CylinderSignalEventItemDto> InsertedEvents { get; set; } = new List<CylinderSignalEventItemDto>();
        public List<CylinderSignalCompareItemDto> ComparedSignals { get; set; } = new List<CylinderSignalCompareItemDto>();
    }

    public class CylinderSignalCompareItemDto
    {
        public string StationCode { get; set; }
        public string CylinderId { get; set; }
        public string SignalName { get; set; }
        public string SignalRole { get; set; }
        public string OldValue { get; set; }
        public string CurrentValue { get; set; }
        public bool IsChanged { get; set; }
        public bool IsMissing { get; set; }
        public string CompareStatus { get; set; }
    }

    public class CylinderSignalSimulationGenerateResultDto
    {
        public long ProjectId { get; set; }
        public string PlcId { get; set; }
        public string StationCode { get; set; }
        public int PointCount { get; set; }
        public int GeneratedCount { get; set; }
        public Dictionary<string, object> SimulatedValues { get; set; } = new Dictionary<string, object>();
    }

    public class CylinderSignalCollectAllResultDto
    {
        public DateTime TriggeredAt { get; set; }
        public string TriggerSource { get; set; }
        public int ProjectCount { get; set; }
        public int SuccessProjectCount { get; set; }
        public int FailedProjectCount { get; set; }
        public int TotalPointCount { get; set; }
        public int TotalReadCount { get; set; }
        public int TotalCacheHitCount { get; set; }
        public int TotalNoCacheCount { get; set; }
        public long TotalReadPlcDurationMilliseconds { get; set; }
        public int TotalChangedCount { get; set; }
        public int TotalInsertedCount { get; set; }
        public List<CylinderSignalCollectProjectResultDto> ProjectResults { get; set; } = new List<CylinderSignalCollectProjectResultDto>();
    }

    public class CylinderSignalCollectProjectResultDto
    {
        public long ProjectId { get; set; }
        public string ProjectName { get; set; }
        public string PlcId { get; set; }
        public string StationCode { get; set; }
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; }
        public int PointCount { get; set; }
        public int ReadCount { get; set; }
        public int CacheHitCount { get; set; }
        public int NoCacheCount { get; set; }
        public long ReadPlcDurationMilliseconds { get; set; }
        public int ChangedCount { get; set; }
        public int InsertedCount { get; set; }
    }

    public class CylinderSignalCollectorJobConfigDto
    {
        public bool Enabled { get; set; }
        public string ProjectIdsText { get; set; } = "4541738602710831104";
        public string StationGroupCodesJson { get; set; } = "[]";
        public int IntervalMilliseconds { get; set; } = 30000;
        public int? IntervalSeconds { get; set; }
        public string StationMode { get; set; } = "AUTO";
        public bool ResetCacheBeforeCollect { get; set; }
        public DateTime? LastRunAt { get; set; }
        public string LastRunStatus { get; set; }
        public string LastMessage { get; set; }
        public int LastProjectCount { get; set; }
        public int LastReadCount { get; set; }
        public int LastCacheHitCount { get; set; }
        public int LastNoCacheCount { get; set; }
        public long? LastReadPlcDurationMilliseconds { get; set; }
        public int LastChangedCount { get; set; }
        public int LastInsertedCount { get; set; }
    }

    public class CylinderSignalCacheClearResultDto
    {
        public int ProjectCount { get; set; }
        public int StationCount { get; set; }
        public long ClearedKeyCount { get; set; }
    }

    public class CylinderSignalEventQueryDto
    {
        public long? ProjectId { get; set; }
        public string PlcId { get; set; }
        public string StationCode { get; set; }
        public string CylinderId { get; set; }
        public string SignalName { get; set; }
        public string SignalRole { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }

    public class CylinderSignalEventItemDto
    {
        public long Id { get; set; }
        public string PlcId { get; set; }
        public string StationCode { get; set; }
        public string CylinderId { get; set; }
        public string SignalName { get; set; }
        public string SignalRole { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public DateTime ChangedAt { get; set; }
        public string StationMode { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CylinderSignalPointDto
    {
        public long RuleId { get; set; }
        public long? TagId { get; set; }
        public string PlcId { get; set; }
        public string StationCode { get; set; }
        public string CylinderId { get; set; }
        public string SignalName { get; set; }
        public string ReadTagName { get; set; }
        public string TableName { get; set; }
        public string SignalRole { get; set; }
        public string ConstraintRole { get; set; }
        public int? SortOrder { get; set; }
        public string Address { get; set; }
        public string IoMapping { get; set; }
    }

    public class CylinderSignalCacheValue
    {
        public string Value { get; set; }
        public DateTime ChangedAt { get; set; }
        public string CylinderId { get; set; }
        public string SignalRole { get; set; }
    }
}
