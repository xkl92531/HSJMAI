using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_station_shadow_signal", tableDescription: "工位关注点与监控点配置表")]
    public class PLC_StationShadowSignalEntity
    {
        public PLC_StationShadowSignalEntity()
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

        [SugarColumn(ColumnName = "signal_type", IsNullable = true)]
        public string SignalType { get; set; }

        [SugarColumn(ColumnName = "anchor_type", IsNullable = true)]
        public string AnchorType { get; set; }

        [SugarColumn(ColumnName = "group_name", IsNullable = true)]
        public string GroupName { get; set; }

        [SugarColumn(ColumnName = "priority", IsNullable = true)]
        public int? Priority { get; set; }

        [SugarColumn(ColumnName = "is_core", IsNullable = true)]
        public bool? IsCore { get; set; }

        [SugarColumn(ColumnName = "is_context", IsNullable = true)]
        public bool? IsContext { get; set; }

        [SugarColumn(ColumnName = "is_watch_only", IsNullable = true)]
        public bool? IsWatchOnly { get; set; }

        [SugarColumn(ColumnName = "participate_in_pattern", IsNullable = true)]
        public bool? ParticipateInPattern { get; set; }

        [SugarColumn(ColumnName = "logic_unit_id", IsNullable = true)]
        public long? LogicUnitId { get; set; }

        [SugarColumn(ColumnName = "semantic_label", IsNullable = true)]
        public string SemanticLabel { get; set; }

        [SugarColumn(ColumnName = "semantic_category", IsNullable = true)]
        public string SemanticCategory { get; set; }

        [SugarColumn(ColumnName = "enabled", IsNullable = true)]
        public bool? Enabled { get; set; }

        /// <summary>
        /// 诊断时是否屏蔽该信号。常用于人工标记"只要运行就常亮"的 GraphAction 步骤节点。
        /// 硬开关，优先级高于自动稳定度过滤。
        /// </summary>
        [SugarColumn(ColumnName = "diagnosis_excluded", IsNullable = true)]
        public bool? DiagnosisExcluded { get; set; }

        /// <summary>
        /// GraphAction 信号激活条件规则，由离线 LLM 分析推导。
        /// 取值：BOOL_TRUE / INT_EQ:N / INT_GT:0 / WORD_GT:0 / BITMASK:N / UNKNOWN
        /// 为空时默认按 BOOL_TRUE 判定。
        /// </summary>
        [SugarColumn(ColumnName = "activation_rule", IsNullable = true)]
        public string ActivationRule { get; set; }

        /// <summary>
        /// Semantic role inferred for a GRAPH step action.
        /// Values: InitStep / IdleStep / LongWaitStep / WorkStep / WaitUpstream / WaitDownstream / AlarmHandling / Unknown.
        /// This is offline evidence for diagnosis ranking, not a hard runtime exclusion switch.
        /// </summary>
        [SugarColumn(ColumnName = "graph_step_role", IsNullable = true)]
        public string GraphStepRole { get; set; }

        [SugarColumn(ColumnName = "graph_step_role_confidence", IsNullable = true)]
        public decimal? GraphStepRoleConfidence { get; set; }

        [SugarColumn(ColumnName = "graph_step_role_reason", IsNullable = true)]
        public string GraphStepRoleReason { get; set; }

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

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }

        [SugarColumn(ColumnName = "modifiername", IsNullable = true)]
        public string ModifierName { get; set; }
    }
}
