using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_station_shadow_config", tableDescription: "工位影子监控主配置表")]
    public class PLC_StationShadowConfigEntity
    {
        public PLC_StationShadowConfigEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "enabled", IsNullable = true)]
        public bool? Enabled { get; set; }

        [SugarColumn(ColumnName = "station_mode_filter_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string StationModeFilterJson { get; set; }

        [SugarColumn(ColumnName = "core_interval_ms", IsNullable = true)]
        public int? CoreIntervalMs { get; set; }

        [SugarColumn(ColumnName = "context_interval_ms", IsNullable = true)]
        public int? ContextIntervalMs { get; set; }

        [SugarColumn(ColumnName = "bundle_window_ms", IsNullable = true)]
        public int? BundleWindowMs { get; set; }

        [SugarColumn(ColumnName = "silence_threshold_seconds", IsNullable = true)]
        public int? SilenceThresholdSeconds { get; set; }

        [SugarColumn(ColumnName = "snapshot_ttl_seconds", IsNullable = true)]
        public int? SnapshotTtlSeconds { get; set; }

        [SugarColumn(ColumnName = "history_retention_days", IsNullable = true)]
        public int? HistoryRetentionDays { get; set; }

        [SugarColumn(ColumnName = "pattern_rebuild_cron", IsNullable = true)]
        public string PatternRebuildCron { get; set; }

        [SugarColumn(ColumnName = "last_pattern_rebuild_time", IsNullable = true)]
        public DateTime? LastPatternRebuildTime { get; set; }

        [SugarColumn(ColumnName = "remark", IsNullable = true)]
        public string Remark { get; set; }

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