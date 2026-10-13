using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 报警变量台账表（LLM 识别 + 手工维护，监控与小锦推送的数据源）。
    /// 一变量一行，(project_id, variable_name) 唯一。
    /// 方案文档: docs/报警变量自动识别与故障推送方案.md
    /// </summary>
    [SugarTable("ai_plc_alarm_variable", tableDescription: "报警变量台账表")]
    public class PLC_AlarmVariableEntity
    {
        public PLC_AlarmVariableEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        /// <summary>
        /// 工位编码（可空，手工维护）
        /// </summary>
        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "variable_name", IsNullable = true)]
        public string VariableName { get; set; }

        /// <summary>
        /// 地址（关联 ai_plc_iotag.address，可空）
        /// </summary>
        [SugarColumn(ColumnName = "address", IsNullable = true)]
        public string Address { get; set; }

        [SugarColumn(ColumnName = "data_type", IsNullable = true)]
        public string DataType { get; set; }

        /// <summary>
        /// 报警类别: 设备故障/安全/质量/其它（LLM 分类，可空）
        /// </summary>
        [SugarColumn(ColumnName = "alarm_category", IsNullable = true)]
        public string AlarmCategory { get; set; }

        /// <summary>
        /// 报警等级: Info/Warn/Critical，可空默认 Warn
        /// </summary>
        [SugarColumn(ColumnName = "alarm_level", IsNullable = true)]
        public string AlarmLevel { get; set; }

        /// <summary>
        /// 报警描述（LLM 摘要 + 手工编辑，varchar 500）
        /// </summary>
        [SugarColumn(ColumnName = "description", IsNullable = true)]
        public string Description { get; set; }

        /// <summary>
        /// 来源: LLM / Structural / Manual
        /// </summary>
        [SugarColumn(ColumnName = "source", IsNullable = true)]
        public string Source { get; set; }

        /// <summary>
        /// 人工确认锁: true = LLM 重跑永不覆盖本行
        /// </summary>
        [SugarColumn(ColumnName = "manually_confirmed", IsNullable = true)]
        public bool? ManuallyConfirmed { get; set; }

        /// <summary>
        /// 是否纳入故障监控（collectable=false 时强制 false）
        /// </summary>
        [SugarColumn(ColumnName = "monitor_enabled", IsNullable = true)]
        public bool? MonitorEnabled { get; set; }

        /// <summary>
        /// 故障发生时是否推送小锦
        /// </summary>
        [SugarColumn(ColumnName = "push_enabled", IsNullable = true)]
        public bool? PushEnabled { get; set; }

        /// <summary>
        /// 是否可采集（在 ai_plc_iotag 表且采集链路可读）；DB 内部报警位为 false
        /// </summary>
        [SugarColumn(ColumnName = "collectable", IsNullable = true)]
        public bool? Collectable { get; set; }

        /// <summary>
        /// 溯源证据 JSON: 命中的 LU/块/DB路径/LLM 理由
        /// </summary>
        [SugarColumn(ColumnName = "evidence_json", IsNullable = true)]
        public string EvidenceJson { get; set; }

        /// <summary>
        /// LLM 置信度 0~1（结构证据通道固定 1.0）；低于 0.7 页面提示人工复核
        /// </summary>
        [SugarColumn(ColumnName = "llm_confidence", IsNullable = true)]
        public decimal? LlmConfidence { get; set; }

        /// <summary>
        /// 最近一次故障开始时间（监控 Worker 回写）
        /// </summary>
        [SugarColumn(ColumnName = "last_alarm_at", IsNullable = true)]
        public DateTime? LastAlarmAt { get; set; }

        /// <summary>
        /// 累计故障次数（监控 Worker 回写）
        /// </summary>
        [SugarColumn(ColumnName = "alarm_count", IsNullable = true)]
        public int? AlarmCount { get; set; }

        [SugarColumn(ColumnName = "sort_order", IsNullable = true)]
        public int? SortOrder { get; set; }

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

        [SugarColumn(ColumnName = "modifiername", IsNullable = true)]
        public string ModifierName { get; set; }

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }
    }
}
