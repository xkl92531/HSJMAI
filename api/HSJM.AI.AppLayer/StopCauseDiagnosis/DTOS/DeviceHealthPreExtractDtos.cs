using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class DeviceHealthPreExtractRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public int MaxCandidates { get; set; } = 300;

        public int MinScore { get; set; } = 60;

        public bool IncludeLocalVariables { get; set; }

        public bool IncludeFeedbackSignals { get; set; } = true;

        public bool IncludeAnalogContextSignals { get; set; }

        public bool IncludeMessageSignals { get; set; }

        public bool UseLlmSemanticClassification { get; set; } = true;

        public bool UseSemanticCache { get; set; } = true;

        public bool RefreshSemanticCache { get; set; }

        public bool PersistSemanticCache { get; set; } = true;

        public int MaxLlmCandidates { get; set; } = 80;

        public bool IncludeLlmRejectedCandidates { get; set; }
    }

    public class DeviceHealthPreExtractResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public int ScannedAstRowCount { get; set; }

        public int ScannedOutputCount { get; set; }

        public int CandidateCount { get; set; }

        public int ExistingMonitoringCount { get; set; }

        public int ExistingAnchorBindingCount { get; set; }

        public long ElapsedMs { get; set; }

        public bool LlmSemanticClassificationUsed { get; set; }

        public string SemanticClassifierVersion { get; set; }

        public int LlmCacheHitCount { get; set; }

        public int LlmSentCount { get; set; }

        public int LlmReturnedCount { get; set; }

        public int LlmPersistedCount { get; set; }

        public int LlmLimitSkippedCount { get; set; }

        public long LlmElapsedMs { get; set; }

        public string LlmErrorMessage { get; set; }

        public List<DeviceHealthPreExtractCandidateDto> Candidates { get; set; } = new List<DeviceHealthPreExtractCandidateDto>();

        public Dictionary<string, int> CategoryCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> StationCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class DeviceHealthPreExtractCandidateDto
    {
        public string VariableName { get; set; }

        public string StationCode { get; set; }

        public string AnchorType { get; set; }

        public string SemanticCategory { get; set; }

        public string SemanticLabel { get; set; }

        public string ActivationRule { get; set; }

        public string AbnormalWhen { get; set; }

        public string MonitoringRole { get; set; }

        public int? RootPriority { get; set; }

        public string SemanticSource { get; set; }

        public string SourceHash { get; set; }

        public int Score { get; set; }

        public decimal Confidence { get; set; }

        public bool RecommendedForMonitoring { get; set; }

        public string Monitorability { get; set; }

        public bool AlreadyInMonitoring { get; set; }

        public bool AlreadyInAnchorBinding { get; set; }

        public long? LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public int? NetworkId { get; set; }

        public string NetworkTitle { get; set; }

        public string UnitType { get; set; }

        public string VariableScope { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();
    }

    public class DeviceFocusSignalQueryDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string Keyword { get; set; }

        public string AnchorType { get; set; }

        public int Limit { get; set; } = 2000;
    }

    public class DeviceFocusSignalsResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int TotalCount { get; set; }

        public int StationCount { get; set; }

        public Dictionary<string, int> CategoryCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public List<DeviceFocusStationGroupDto> Stations { get; set; } = new List<DeviceFocusStationGroupDto>();

        public List<DeviceFocusSignalDto> Signals { get; set; } = new List<DeviceFocusSignalDto>();
    }

    public class DeviceFocusStationGroupDto
    {
        public string StationCode { get; set; }

        public int TotalCount { get; set; }

        public Dictionary<string, int> CategoryCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public List<DeviceFocusSignalDto> Signals { get; set; } = new List<DeviceFocusSignalDto>();
    }

    public class DeviceFocusSignalDto
    {
        public long Id { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public string SignalKey { get; set; }

        public string VariableName { get; set; }

        public string AnchorType { get; set; }

        public string SemanticCategory { get; set; }

        public string SemanticLabel { get; set; }

        public string ActivationRule { get; set; }

        public string GroupName { get; set; }

        public int? Priority { get; set; }

        public bool IsCore { get; set; }

        public bool IsContext { get; set; }

        public bool IsWatchOnly { get; set; }

        public bool ParticipateInPattern { get; set; }

        public bool Enabled { get; set; }

        public long? LogicUnitId { get; set; }

        public int? SortOrder { get; set; }

        public string Remark { get; set; }

        public DateTime? CreateTime { get; set; }

        public DateTime? ModifyTime { get; set; }
    }

    public class DeviceFocusSignalSeedRequestDto : DeviceHealthPreExtractRequestDto
    {
        public DeviceFocusSignalSeedRequestDto()
        {
            MaxCandidates = 0;
            MinScore = 0;
            IncludeLocalVariables = true;
            IncludeFeedbackSignals = true;
            IncludeAnalogContextSignals = true;
            UseLlmSemanticClassification = true;
            UseSemanticCache = true;
            PersistSemanticCache = true;
            MaxLlmCandidates = 0;
        }

        public bool ReplaceExistingDeviceSignals { get; set; } = true;

        public bool SeedRecommendedOnly { get; set; }
    }

    public class DeviceFocusSignalSeedResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int AnalyzedCandidateCount { get; set; }

        public int SeedCandidateCount { get; set; }

        public int DuplicateCandidateCount { get; set; }

        public int InsertedCount { get; set; }

        public int UpdatedCount { get; set; }

        public int DeletedCount { get; set; }

        public Dictionary<string, int> StationCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings { get; set; } = new List<string>();
    }
}
