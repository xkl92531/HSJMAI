using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    public class StationShadowStateCacheModel
    {
        public string Status { get; set; }

        public string StationMode { get; set; }

        public DateTime? LastSampleTime { get; set; }

        /// <summary>状态创建后的首次采样时间（2026-09-18 新增）：从未有过 change 的工位
        /// 以此起算静默时长。此前误用 LastSampleTime（每轮刷新）导致静默秒数永远不超过
        /// 一个巡检周期、在阈值上下抖动，isSilent 边沿被随机消费/推迟。</summary>
        public DateTime? FirstSampleTime { get; set; }

        public DateTime? LastActionTime { get; set; }

        public DateTime? LastBundleTime { get; set; }

        public bool IsSilent { get; set; }

        public int SilentSeconds { get; set; }

        public string LastEventId { get; set; }

        public string LastSnapshotId { get; set; }

        public int CoreSignalCount { get; set; }

        public int ContextSignalCount { get; set; }

        public string RuntimeVersion { get; set; }

        public string SamplerNode { get; set; }
    }

    public class StationShadowSignalCacheModel
    {
        public string SignalKey { get; set; }

        public string VariableName { get; set; }

        public object CurrentValue { get; set; }

        public object PreviousValue { get; set; }

        public DateTime? LastChangeTime { get; set; }

        public string Quality { get; set; }

        public string GroupName { get; set; }

        public string SignalType { get; set; }

        public string AnchorType { get; set; }

        public bool IsCore { get; set; }

        public long? LogicUnitId { get; set; }

        public string SemanticLabel { get; set; }

        public string GraphStepRole { get; set; }

        public decimal? GraphStepRoleConfidence { get; set; }

        public string GraphStepRoleReason { get; set; }
    }

    public class StationShadowChangeCacheModel
    {
        public string ChangeId { get; set; }

        public string SignalKey { get; set; }

        public string VariableName { get; set; }

        public object OldValue { get; set; }

        public object NewValue { get; set; }

        public DateTime? ChangedAt { get; set; }

        public string StationMode { get; set; }

        public string GroupName { get; set; }

        public string SignalType { get; set; }

        public string Quality { get; set; }

        public long? LogicUnitId { get; set; }
    }

    public class StationShadowBundleCacheModel
    {
        public string BundleId { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public string StationMode { get; set; }

        public List<string> SignalKeys { get; set; } = new List<string>();

        public List<string> AnchorKeys { get; set; } = new List<string>();

        public int ConcurrencyCount { get; set; }

        public string BundleSignature { get; set; }

        public string BundleLabel { get; set; }

        public string PrecedingBundleId { get; set; }
    }

    public class StationShadowSnapshotCacheModel
    {
        public string SnapshotId { get; set; }

        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string TriggerType { get; set; }

        public DateTime? SnapshotTime { get; set; }

        public string StationMode { get; set; }

        public string LastBundleId { get; set; }

        public List<string> RequestedSymbols { get; set; } = new List<string>();

        public Dictionary<string, object> CurrentValues { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> PreviousValues { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DateTime?> LastChangeTimes { get; set; } = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> SignalQuality { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<StationShadowBundleCacheModel> RecentBundles { get; set; } = new List<StationShadowBundleCacheModel>();

        public object ExpectedNextBundle { get; set; }

        public object MissingBundle { get; set; }
    }

    public class StationShadowRuntimeLeaseCacheModel
    {
        public string NodeName { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? LastHeartbeat { get; set; }

        public string JobSource { get; set; }

        public string Status { get; set; }
    }
}
