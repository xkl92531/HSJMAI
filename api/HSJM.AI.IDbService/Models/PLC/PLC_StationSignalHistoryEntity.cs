using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_station_signal_history", tableDescription: "工位监控信号变化历史表")]
    public class PLC_StationSignalHistoryEntity
    {
        public PLC_StationSignalHistoryEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "signal_key", IsNullable = true)]
        public string SignalKey { get; set; }

        [SugarColumn(ColumnName = "variable_name", IsNullable = true)]
        public string VariableName { get; set; }

        [SugarColumn(ColumnName = "old_value", IsNullable = true)]
        public string OldValue { get; set; }

        [SugarColumn(ColumnName = "new_value", IsNullable = true)]
        public string NewValue { get; set; }

        [SugarColumn(ColumnName = "changed_at", IsNullable = true)]
        public DateTime? ChangedAt { get; set; }

        [SugarColumn(ColumnName = "station_mode", IsNullable = true)]
        public string StationMode { get; set; }

        [SugarColumn(ColumnName = "quality", IsNullable = true)]
        public string Quality { get; set; }

        [SugarColumn(ColumnName = "bundle_id", IsNullable = true)]
        public string BundleId { get; set; }

        [SugarColumn(ColumnName = "snapshot_id", IsNullable = true)]
        public string SnapshotId { get; set; }

        [SugarColumn(ColumnName = "event_id", IsNullable = true)]
        public long? EventId { get; set; }

        [SugarColumn(ColumnName = "signal_type", IsNullable = true)]
        public string SignalType { get; set; }

        [SugarColumn(ColumnName = "group_name", IsNullable = true)]
        public string GroupName { get; set; }

        [SugarColumn(ColumnName = "logic_unit_id", IsNullable = true)]
        public long? LogicUnitId { get; set; }

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