using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_station_pattern_template", tableDescription: "工位规律模板表")]
    public class PLC_StationPatternTemplateEntity
    {
        public PLC_StationPatternTemplateEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "station_mode", IsNullable = true)]
        public string StationMode { get; set; }

        [SugarColumn(ColumnName = "pattern_type", IsNullable = true)]
        public string PatternType { get; set; }

        [SugarColumn(ColumnName = "template_name", IsNullable = true)]
        public string TemplateName { get; set; }

        [SugarColumn(ColumnName = "template_version", IsNullable = true)]
        public int? TemplateVersion { get; set; }

        [SugarColumn(ColumnName = "support_count", IsNullable = true)]
        public int? SupportCount { get; set; }

        [SugarColumn(ColumnName = "confidence", IsNullable = true, ColumnDataType = "decimal(5,4)")]
        public decimal? Confidence { get; set; }

        [SugarColumn(ColumnName = "bundle_sequence_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string BundleSequenceJson { get; set; }

        [SugarColumn(ColumnName = "next_expected_map_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string NextExpectedMapJson { get; set; }

        [SugarColumn(ColumnName = "time_window_stats_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string TimeWindowStatsJson { get; set; }

        [SugarColumn(ColumnName = "last_bundle_signature", IsNullable = true)]
        public string LastBundleSignature { get; set; }

        [SugarColumn(ColumnName = "active")]
        public bool Active { get; set; } = true;

        [SugarColumn(ColumnName = "last_rebuild_time", IsNullable = true)]
        public DateTime? LastRebuildTime { get; set; }

        [SugarColumn(ColumnName = "source_sample_count", IsNullable = true)]
        public int? SourceSampleCount { get; set; }

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