using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class StationShadowQueryDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }
    }

    public class StationShadowSignalQueryDto : StationShadowQueryDto
    {
        public bool? OnlyCore { get; set; }

        public string GroupName { get; set; }

        public string SignalType { get; set; }

        public string AnchorType { get; set; }

        public string Keyword { get; set; }

        public int Limit { get; set; } = 500;
    }

    public class StationShadowTimelineQueryDto : StationShadowQueryDto
    {
        public int Limit { get; set; } = 50;
    }

    public class StationShadowHistoryQueryDto : StationShadowQueryDto
    {
        public string BundleId { get; set; }

        public string Keyword { get; set; }

        public int Page { get; set; } = 1;

        public int PageSize { get; set; } = 50;
    }

    public class StationShadowSampleLibraryQueryDto : StationShadowQueryDto
    {
        public string Keyword { get; set; }

        public int Limit { get; set; } = 30;
    }

    public class StationShadowPatternTemplateQueryDto : StationShadowQueryDto
    {
        public string StationMode { get; set; }

        public string PatternType { get; set; }

        public string LastBundleSignature { get; set; }

        public string Keyword { get; set; }

        public bool OnlyActive { get; set; } = true;

        public int Limit { get; set; } = 30;
    }

    public class StationShadowPatternTemplateRebuildRequestDto : StationShadowSampleLibraryQueryDto
    {
        public string StationMode { get; set; }

        public string PatternType { get; set; } = "ExpectedNextBundle";

        public bool ReplaceExisting { get; set; } = true;

        public int MinSupportCount { get; set; } = 1;

        public List<string> SampleKeys { get; set; } = new List<string>();
    }

    public class StationShadowPatternTemplateRebuildResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string StationMode { get; set; }

        public string PatternType { get; set; }

        public int SourceSampleCount { get; set; }

        public int AcceptedSampleCount { get; set; }

        public int ReplacedTemplateCount { get; set; }

        public int GeneratedTemplateCount { get; set; }

        public List<long> GeneratedTemplateIds { get; set; } = new List<long>();

        public List<string> Warnings { get; set; } = new List<string>();

        public List<StationShadowPatternTemplateDto> Templates { get; set; } = new List<StationShadowPatternTemplateDto>();
    }

    public class StationShadowDiagnosisRunRequestDto : StationShadowQueryDto
    {
        public long? EventId { get; set; }

        public bool ForceReprocess { get; set; }

        public int Limit { get; set; } = 5;
    }

    public class StationShadowDiagnosisRunItemDto
    {
        public long EventId { get; set; }

        public string StationCode { get; set; }

        public string DiagnosisStatus { get; set; }

        public bool Success { get; set; }

        public long? MatchedTemplateId { get; set; }

        public decimal? TemplateConfidence { get; set; }

        public string LastBundleSignature { get; set; }

        public string ExpectedBundleSignature { get; set; }

        public List<string> MissingSignalKeys { get; set; } = new List<string>();

        public string TargetAnchorKey { get; set; }

        public string TargetVariableName { get; set; }

        public long? TargetLogicUnitId { get; set; }

        public string ResolutionSource { get; set; }

        public bool AstEvaluated { get; set; }

        public bool? AstEvaluatedValue { get; set; }

        public string AstEvaluationStatus { get; set; }

        public List<string> Messages { get; set; } = new List<string>();

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class StationShadowDiagnosisRunResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int RequestedCount { get; set; }

        public int ProcessedCount { get; set; }

        public int SuccessCount { get; set; }

        public int FailedCount { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();

        public List<StationShadowDiagnosisRunItemDto> Items { get; set; } = new List<StationShadowDiagnosisRunItemDto>();
    }

    public class StationShadowExpectedNextBundleDto
    {
        public string BundleSignature { get; set; }

        public int Count { get; set; }

        public decimal Probability { get; set; }

        public double AvgGapMs { get; set; }

        public double MinGapMs { get; set; }

        public double MaxGapMs { get; set; }
    }

    public class StationShadowMissingBundleDto
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = "monitoring-missing-bundle/v1";

        [JsonPropertyName("bundleSignature")]
        public string BundleSignature { get; set; }

        [JsonPropertyName("signalKeys")]
        public List<string> SignalKeys { get; set; } = new List<string>();

        [JsonPropertyName("inferredFromTemplateId")]
        public long? InferredFromTemplateId { get; set; }

        [JsonPropertyName("inferredAt")]
        public DateTime? InferredAt { get; set; }
    }

    public class StationShadowDiagnosisTemplateDto
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("templateName")]
        public string TemplateName { get; set; }

        [JsonPropertyName("templateVersion")]
        public int? TemplateVersion { get; set; }

        [JsonPropertyName("confidence")]
        public decimal? Confidence { get; set; }

        [JsonPropertyName("expectedBundleSignature")]
        public string ExpectedBundleSignature { get; set; }

        [JsonPropertyName("probability")]
        public decimal? Probability { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }
    }

    public class StationShadowDiagnosisTargetDto
    {
        [JsonPropertyName("signalKey")]
        public string SignalKey { get; set; }

        [JsonPropertyName("anchorKey")]
        public string AnchorKey { get; set; }

        [JsonPropertyName("variableName")]
        public string VariableName { get; set; }

        [JsonPropertyName("logicUnitId")]
        public long? LogicUnitId { get; set; }

        [JsonPropertyName("resolutionSource")]
        public string ResolutionSource { get; set; }
    }

    public class StationShadowDiagnosisAstDto
    {
        [JsonPropertyName("logicUnitId")]
        public long? LogicUnitId { get; set; }

        [JsonPropertyName("blockId")]
        public long? BlockId { get; set; }

        [JsonPropertyName("blockName")]
        public string BlockName { get; set; }

        [JsonPropertyName("variableName")]
        public string VariableName { get; set; }

        [JsonPropertyName("evaluatedValue")]
        public bool? EvaluatedValue { get; set; }

        [JsonPropertyName("evaluationStatus")]
        public string EvaluationStatus { get; set; }

        [JsonPropertyName("missingSymbols")]
        public List<string> MissingSymbols { get; set; } = new List<string>();

        [JsonPropertyName("messages")]
        public List<string> Messages { get; set; } = new List<string>();

        [JsonPropertyName("failurePath")]
        public object FailurePath { get; set; }
    }

    public class StationShadowDiagnosisEventContextDto
    {
        [JsonPropertyName("eventId")]
        public long? EventId { get; set; }

        [JsonPropertyName("eventType")]
        public string EventType { get; set; }

        [JsonPropertyName("triggeredAt")]
        public DateTime? TriggeredAt { get; set; }

        [JsonPropertyName("lastBundleSignature")]
        public string LastBundleSignature { get; set; }

        [JsonPropertyName("lastChangedSignal")]
        public string LastChangedSignal { get; set; }

        [JsonPropertyName("snapshotId")]
        public string SnapshotId { get; set; }
    }

    public class StationShadowDiagnosisResultDto
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = "monitoring-diagnosis/v1";

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("eventType")]
        public string EventType { get; set; }

        [JsonPropertyName("eventId")]
        public long? EventId { get; set; }

        [JsonPropertyName("lastBundleSignature")]
        public string LastBundleSignature { get; set; }

        [JsonPropertyName("expectedSignalKeys")]
        public List<string> ExpectedSignalKeys { get; set; } = new List<string>();

        [JsonPropertyName("fallbackSignalKey")]
        public string FallbackSignalKey { get; set; }

        [JsonPropertyName("availableBindings")]
        public int? AvailableBindings { get; set; }

        [JsonPropertyName("template")]
        public StationShadowDiagnosisTemplateDto Template { get; set; }

        [JsonPropertyName("target")]
        public StationShadowDiagnosisTargetDto Target { get; set; }

        [JsonPropertyName("ast")]
        public StationShadowDiagnosisAstDto Ast { get; set; }

        [JsonPropertyName("eventContext")]
        public StationShadowDiagnosisEventContextDto EventContext { get; set; }

        [JsonPropertyName("diagnosedAt")]
        public DateTime? DiagnosedAt { get; set; }

        [JsonPropertyName("llmFallback")]
        public AstLlmFallbackResultDto LlmFallback { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }
    }

    public class StationShadowSnapshotQueryDto : StationShadowQueryDto
    {
        public string SnapshotId { get; set; }
    }

    public class StationChangeSummaryDto
    {
        public string StationCode { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; }
        public string StationMode { get; set; }
        public bool IsSilent { get; set; }
        public DateTime? LastSampleTime { get; set; }
        public int CoreSignalCount { get; set; }
        public int ContextSignalCount { get; set; }
        public List<StationShadowChangeCacheModel> RecentChanges { get; set; }
    }

    public class StationShadowStationListItemDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public bool Enabled { get; set; }

        public string Status { get; set; }

        public string StationMode { get; set; }

        public bool IsSilent { get; set; }

        public int SilentSeconds { get; set; }

        public DateTime? LastSampleTime { get; set; }

        public DateTime? LastBundleTime { get; set; }

        public string LastEventId { get; set; }

        public string LastSnapshotId { get; set; }

        public int CoreSignalCount { get; set; }

        public int ContextSignalCount { get; set; }

        public string RuntimeStatus { get; set; }

        public string RuntimeNodeName { get; set; }

        public DateTime? RuntimeHeartbeat { get; set; }

        public int? CoreIntervalMs { get; set; }

        public int? ContextIntervalMs { get; set; }

        public int? BundleWindowMs { get; set; }

        public int? SilenceThresholdSeconds { get; set; }
    }

    public class StationShadowOverviewDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public bool HasState { get; set; }

        public bool HasRuntimeLease { get; set; }

        public bool HasSnapshot { get; set; }

        public int SignalMirrorCount { get; set; }

        public DateTime GeneratedAt { get; set; }

        public StationShadowStateCacheModel State { get; set; }

        public StationShadowRuntimeLeaseCacheModel RuntimeLease { get; set; }

        public StationShadowEventDto LatestEvent { get; set; }

        public StationShadowSnapshotCacheModel Snapshot { get; set; }
    }

    public class StationShadowEventDto
    {
        public long Id { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public string EventType { get; set; }

        public string EventLevel { get; set; }

        public DateTime? TriggeredAt { get; set; }

        public string StationMode { get; set; }

        public string LastBundleId { get; set; }

        public string LastBundleSignature { get; set; }

        public string LastChangedSignal { get; set; }

        public string SnapshotId { get; set; }

        public string TargetAnchorKey { get; set; }

        public string DiagnosisStatus { get; set; }

        public int? SignalCount { get; set; }

        public int? BundleCount { get; set; }

        public string SourceRuntimeNode { get; set; }

        public bool IsArchived { get; set; }

        public string ExpectedNextBundleJson { get; set; }

        public StationShadowExpectedNextBundleDto ExpectedNextBundle { get; set; }

        public string MissingBundleJson { get; set; }

        public StationShadowMissingBundleDto MissingBundle { get; set; }

        public string DiagnosisResultJson { get; set; }

        public StationShadowDiagnosisResultDto DiagnosisResult { get; set; }
    }

    public class StationShadowHistoryItemDto
    {
        public long Id { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public string SignalKey { get; set; }

        public string VariableName { get; set; }

        public string OldValue { get; set; }

        public string NewValue { get; set; }

        public DateTime? ChangedAt { get; set; }

        public string StationMode { get; set; }

        public string Quality { get; set; }

        public string BundleId { get; set; }

        public string SnapshotId { get; set; }

        public long? EventId { get; set; }

        public string SignalType { get; set; }

        public string GroupName { get; set; }

        public long? LogicUnitId { get; set; }
    }

    public class StationShadowSampleLibraryItemDto
    {
        public string SampleKey { get; set; }

        public string SampleType { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public string BundleId { get; set; }

        public string SnapshotId { get; set; }

        public long? EventId { get; set; }

        public string StationMode { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public int ChangeCount { get; set; }

        public int SignalCount { get; set; }

        public List<string> SignalKeys { get; set; } = new List<string>();

        public List<string> GroupNames { get; set; } = new List<string>();

        public List<string> SignalTypes { get; set; } = new List<string>();

        public string SampleLabel { get; set; }

        public string SampleSummary { get; set; }

        public string EventType { get; set; }

        public string EventLevel { get; set; }

        public DateTime? LatestTriggeredAt { get; set; }

        public string DiagnosisStatus { get; set; }

        public string LastBundleSignature { get; set; }
    }

    public class StationShadowPatternTemplateDto
    {
        public long Id { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public string StationMode { get; set; }

        public string PatternType { get; set; }

        public string TemplateName { get; set; }

        public int? TemplateVersion { get; set; }

        public int? SupportCount { get; set; }

        public decimal? Confidence { get; set; }

        public string LastBundleSignature { get; set; }

        public string BundleSequenceJson { get; set; }

        public string NextExpectedMapJson { get; set; }

        public string TimeWindowStatsJson { get; set; }

        public bool Active { get; set; }

        public DateTime? LastRebuildTime { get; set; }

        public int? SourceSampleCount { get; set; }

        public string Remark { get; set; }
    }

    public class StationShadowRealtimeCycleMessageDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public DateTime SampleTime { get; set; }

        public string StationMode { get; set; }

        public string Status { get; set; }

        public int SignalCount { get; set; }

        public int ChangedSignalCount { get; set; }

        public bool IsSilent { get; set; }

        public int SilentSeconds { get; set; }

        public string BundleId { get; set; }

        public string BundleLabel { get; set; }

        public string SnapshotId { get; set; }

        public string EventId { get; set; }

        public string EventType { get; set; }

        public string EventLevel { get; set; }

        public string RuntimeNodeName { get; set; }

        public string JobSource { get; set; }

        public int MissingSymbolCount { get; set; }

        public List<string> MissingSymbols { get; set; } = new List<string>();

        public List<string> ChangeSignalKeys { get; set; } = new List<string>();
    }

    public class StationShadowRealtimeFailureMessageDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string RuntimeNodeName { get; set; }

        public string JobSource { get; set; }

        public string ErrorMessage { get; set; }

        public DateTime OccurredAt { get; set; } = DateTime.Now;
    }

    public class RunStationShadowCycleRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string StationMode { get; set; }

        public string RuntimeNodeName { get; set; }

        public string JobSource { get; set; } = "Manual";

        public bool PersistHistory { get; set; } = true;

        public bool PersistSnapshot { get; set; } = true;

        public bool PersistEvents { get; set; } = true;

        public int SnapshotRecentBundleCount { get; set; } = 10;

        public bool RebuildPatternsAfterCycle { get; set; }

        public int PatternSampleLimit { get; set; } = 60;

        public bool DiagnosePendingAfterCycle { get; set; }

        public int DiagnosisEventLimit { get; set; } = 3;
    }

    public class RunStationShadowCycleResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public DateTime SampleTime { get; set; }

        public string StationMode { get; set; }

        public string Status { get; set; }

        public bool ConfigFound { get; set; }

        public bool ConfigEnabled { get; set; }

        public int SignalCount { get; set; }

        public int CoreSignalCount { get; set; }

        public int ContextSignalCount { get; set; }

        public int ChangedSignalCount { get; set; }

        public bool BundleCreated { get; set; }

        public string BundleId { get; set; }

        public string SnapshotId { get; set; }

        public string EventId { get; set; }

        public bool IsSilent { get; set; }

        public int SilentSeconds { get; set; }

        public DateTime? LastActionTime { get; set; }

        public DateTime? LastBundleTime { get; set; }

        public int MissingSymbolCount { get; set; }

        public List<string> MissingSymbols { get; set; } = new List<string>();

        public List<StationShadowChangeCacheModel> Changes { get; set; } = new List<StationShadowChangeCacheModel>();

        public List<string> Warnings { get; set; } = new List<string>();
    }

    // ==================== 工位监控配置 CRUD DTO ====================

    public class StationShadowConfigQueryDto
    {
        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public bool? Enabled { get; set; }

        public string Keyword { get; set; }

        public int Limit { get; set; } = 100;
    }

    public class StationShadowConfigCreateDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public bool Enabled { get; set; } = true;

        public string StationModeFilterJson { get; set; }

        public int? CoreIntervalMs { get; set; }

        public int? ContextIntervalMs { get; set; }

        public int? BundleWindowMs { get; set; }

        public int? SilenceThresholdSeconds { get; set; }

        public int? SnapshotTtlSeconds { get; set; }

        public int? HistoryRetentionDays { get; set; }

        public string PatternRebuildCron { get; set; }

        public string Remark { get; set; }
    }

    public class StationShadowConfigUpdateDto : StationShadowConfigCreateDto
    {
        public long Id { get; set; }
    }

    public class StationShadowConfigItemDto
    {
        public long Id { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public bool Enabled { get; set; }

        public string StationModeFilterJson { get; set; }

        public int? CoreIntervalMs { get; set; }

        public int? ContextIntervalMs { get; set; }

        public int? BundleWindowMs { get; set; }

        public int? SilenceThresholdSeconds { get; set; }

        public int? SnapshotTtlSeconds { get; set; }

        public int? HistoryRetentionDays { get; set; }

        public string PatternRebuildCron { get; set; }

        public DateTime? LastPatternRebuildTime { get; set; }

        public string Remark { get; set; }

        public DateTime? CreateTime { get; set; }

        public DateTime? ModifyTime { get; set; }
    }

    public class StationShadowSeedSignalsRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        /// <summary>
        /// true = 先删除再重建；false = 仅新增缺失的信号
        /// </summary>
        public bool ReplaceExisting { get; set; }
    }

    public class StationShadowSeedSignalsResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int AnchorBindingCount { get; set; }

        public int ExistingSignalCount { get; set; }

        public int InsertedCount { get; set; }

        public int DeletedCount { get; set; }

        public bool ConfigCreated { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BatchSeedSignalsResultDto
    {
        public long ProjectId { get; set; }
        public int TotalStations { get; set; }
        public int SuccessStations { get; set; }
        public int TotalInserted { get; set; }
        public int TotalDeleted { get; set; }
        public List<BatchSeedStationDetail> StationDetails { get; set; } = new List<BatchSeedStationDetail>();
    }

    public class BatchSeedStationDetail
    {
        public string StationCode { get; set; }
        public bool Success { get; set; }
        public int InsertedCount { get; set; }
        public int DeletedCount { get; set; }
        public int AnchorBindingCount { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class BatchSeedRequestDto
    {
        public long ProjectId { get; set; }
        public bool ReplaceExisting { get; set; } = true;
    }

    public class StationShadowLiveReadRequestDto
    {
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
    }

    public class StationShadowLiveReadResultDto
    {
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
        public string DeviceName { get; set; }
        public string FallbackDeviceName { get; set; }
        public string IotUrl { get; set; }
        public DateTime ReadTime { get; set; }
        public int TotalCount { get; set; }
        public int GoodCount { get; set; }
        public int BadCount { get; set; }
        public List<StationShadowLiveReadItemDto> Signals { get; set; } = new List<StationShadowLiveReadItemDto>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class StationShadowLiveReadItemDto
    {
        public string SignalKey { get; set; }
        public string VariableName { get; set; }
        public string AnchorType { get; set; }
        public object Value { get; set; }
        public bool IsGood { get; set; }
        public string Quality { get; set; }
    }
}