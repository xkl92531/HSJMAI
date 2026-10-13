using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_station_shadow_event", tableDescription: "工位监控事件表")]
    public class PLC_StationShadowEventEntity
    {
        public PLC_StationShadowEventEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "event_type", IsNullable = true)]
        public string EventType { get; set; }

        [SugarColumn(ColumnName = "event_level", IsNullable = true)]
        public string EventLevel { get; set; }

        [SugarColumn(ColumnName = "triggered_at", IsNullable = true)]
        public DateTime? TriggeredAt { get; set; }

        [SugarColumn(ColumnName = "station_mode", IsNullable = true)]
        public string StationMode { get; set; }

        [SugarColumn(ColumnName = "last_bundle_id", IsNullable = true)]
        public string LastBundleId { get; set; }

        [SugarColumn(ColumnName = "last_bundle_signature", IsNullable = true)]
        public string LastBundleSignature { get; set; }

        [SugarColumn(ColumnName = "last_changed_signal", IsNullable = true)]
        public string LastChangedSignal { get; set; }

        [SugarColumn(ColumnName = "snapshot_id", IsNullable = true)]
        public string SnapshotId { get; set; }

        [SugarColumn(ColumnName = "expected_next_bundle_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string ExpectedNextBundleJson { get; set; }

        [SugarColumn(ColumnName = "missing_bundle_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string MissingBundleJson { get; set; }

        [SugarColumn(ColumnName = "target_anchor_key", IsNullable = true)]
        public string TargetAnchorKey { get; set; }

        [SugarColumn(ColumnName = "diagnosis_status", IsNullable = true)]
        public string DiagnosisStatus { get; set; }

        [SugarColumn(ColumnName = "diagnosis_result_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string DiagnosisResultJson { get; set; }

        [SugarColumn(ColumnName = "signal_count", IsNullable = true)]
        public int? SignalCount { get; set; }

        [SugarColumn(ColumnName = "bundle_count", IsNullable = true)]
        public int? BundleCount { get; set; }

        [SugarColumn(ColumnName = "source_runtime_node", IsNullable = true)]
        public string SourceRuntimeNode { get; set; }

        [SugarColumn(ColumnName = "is_archived", IsNullable = true)]
        public bool? IsArchived { get; set; }

        [SugarColumn(ColumnName = "isdeleted")]
        public bool IsDeleted { get; set; }

        [SugarColumn(ColumnName = "createrid", IsNullable = true)]
        public long? CreaterId { get; set; } = 0;

        [SugarColumn(ColumnName = "creatername", IsNullable = true)]
        public string CreaterName { get; set; }

        [SugarColumn(ColumnName = "createtime", IsNullable = true)]
        public DateTime? CreateTime { get; set; }

        [SugarColumn(ColumnName = "modifierid", IsNullable = true)]
        public long? ModifierId { get; set; } = 0;

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }

        [SugarColumn(ColumnName = "modifiername", IsNullable = true)]
        public string ModifierName { get; set; }
    }
}