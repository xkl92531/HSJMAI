using SqlSugar;
using System;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    [SugarTable("cylinder_signal_event", tableDescription: "Cylinder related PLC signal change raw event table")]
    public class CylinderSignalEventEntity
    {
        public CylinderSignalEventEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "plc_id", IsNullable = false, Length = 64)]
        public string PlcId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = false, Length = 32)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "cylinder_id", IsNullable = false, Length = 64)]
        public string CylinderId { get; set; }

        [SugarColumn(ColumnName = "signal_name", IsNullable = false, Length = 128)]
        public string SignalName { get; set; }

        [SugarColumn(ColumnName = "signal_role", IsNullable = false, Length = 64)]
        public string SignalRole { get; set; }

        [SugarColumn(ColumnName = "old_value", IsNullable = true, Length = 16)]
        public string OldValue { get; set; }

        [SugarColumn(ColumnName = "new_value", IsNullable = true, Length = 16)]
        public string NewValue { get; set; }

        [SugarColumn(ColumnName = "changed_at", IsNullable = false)]
        public DateTime ChangedAt { get; set; }

        [SugarColumn(ColumnName = "station_mode", IsNullable = true, Length = 32)]
        public string StationMode { get; set; }

        [SugarColumn(ColumnName = "isdeleted", IsNullable = false)]
        public bool IsDeleted { get; set; }

        [SugarColumn(ColumnName = "createrid", IsNullable = true)]
        public long? CreaterId { get; set; } = 0;

        [SugarColumn(ColumnName = "creatername", IsNullable = true, Length = 50)]
        public string CreaterName { get; set; }

        [SugarColumn(ColumnName = "createtime", IsNullable = false)]
        public DateTime? CreateTime { get; set; } = DateTime.Now;

        [SugarColumn(ColumnName = "modifierid", IsNullable = true)]
        public long? ModifierId { get; set; } = 0;

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }

        [SugarColumn(ColumnName = "modifiername", IsNullable = true, Length = 50)]
        public string ModifierName { get; set; }
    }
}
