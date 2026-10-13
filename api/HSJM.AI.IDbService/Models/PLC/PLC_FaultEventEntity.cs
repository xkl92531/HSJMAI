using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 故障事件表（报警监测 Worker 产出）。
    /// 一行 = 一次完整故障：true 沿(去抖)插入，false 沿(去抖)回填 ended_at/duration_seconds。
    /// FaultEventHub 的 FaultStarted/FaultEnded 推送与 Pull 补拉均以本表为数据源。
    /// </summary>
    [SugarTable("ai_plc_fault_event", tableDescription: "故障事件表")]
    public class PLC_FaultEventEntity
    {
        public PLC_FaultEventEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = false)]
        public long ProjectId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "variable_name", IsNullable = true)]
        public string VariableName { get; set; }

        /// <summary>
        /// 关联 ai_plc_alarm_variable.id
        /// </summary>
        [SugarColumn(ColumnName = "alarm_variable_id", IsNullable = true)]
        public long? AlarmVariableId { get; set; }

        [SugarColumn(ColumnName = "alarm_level", IsNullable = true)]
        public string AlarmLevel { get; set; }

        [SugarColumn(ColumnName = "description", IsNullable = true)]
        public string Description { get; set; }

        /// <summary>
        /// 故障开始时间（首个 true 采样时刻）
        /// </summary>
        [SugarColumn(ColumnName = "started_at", IsNullable = false)]
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// 故障结束时间；NULL = 故障进行中
        /// </summary>
        [SugarColumn(ColumnName = "ended_at", IsNullable = true)]
        public DateTime? EndedAt { get; set; }

        /// <summary>
        /// 持续时长（秒），结束时回填
        /// </summary>
        [SugarColumn(ColumnName = "duration_seconds", IsNullable = true)]
        public int? DurationSeconds { get; set; }

        /// <summary>
        /// 触发去抖参数（审计用）
        /// </summary>
        [SugarColumn(ColumnName = "start_debounce_ms", IsNullable = true)]
        public int? StartDebounceMs { get; set; }

        /// <summary>
        /// 触发时刻关键信号快照 JSON（可选）
        /// </summary>
        [SugarColumn(ColumnName = "value_snapshot_json", IsNullable = true)]
        public string ValueSnapshotJson { get; set; }

        /// <summary>
        /// 是否已成功推送给订阅方（小锦）；补拉去重参考
        /// </summary>
        [SugarColumn(ColumnName = "pushed_to_subscriber", IsNullable = true)]
        public bool? PushedToSubscriber { get; set; }

        /// <summary>
        /// 最近一次推送失败原因
        /// </summary>
        [SugarColumn(ColumnName = "push_error", IsNullable = true)]
        public string PushError { get; set; }

        [SugarColumn(ColumnName = "isdeleted")]
        public bool IsDeleted { get; set; }

        [SugarColumn(ColumnName = "createtime", IsNullable = true)]
        public DateTime? CreateTime { get; set; }

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }
    }
}
