using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_device_health_semantic_finding", tableDescription: "设备健康语义发现表")]
    public class PLC_DeviceHealthSemanticFindingEntity
    {
        public PLC_DeviceHealthSemanticFindingEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "project_id", IsNullable = true)]
        public long? ProjectId { get; set; }

        [SugarColumn(ColumnName = "station_code", IsNullable = true)]
        public string StationCode { get; set; }

        [SugarColumn(ColumnName = "block_id", IsNullable = true)]
        public long? BlockId { get; set; }

        [SugarColumn(ColumnName = "block_name", IsNullable = true)]
        public string BlockName { get; set; }

        [SugarColumn(ColumnName = "logic_unit_id", IsNullable = true)]
        public long? LogicUnitId { get; set; }

        [SugarColumn(ColumnName = "network_id", IsNullable = true)]
        public int? NetworkId { get; set; }

        [SugarColumn(ColumnName = "variable_name", IsNullable = true)]
        public string VariableName { get; set; }

        [SugarColumn(ColumnName = "anchor_type", IsNullable = true)]
        public string AnchorType { get; set; }

        [SugarColumn(ColumnName = "semantic_category", IsNullable = true)]
        public string SemanticCategory { get; set; }

        [SugarColumn(ColumnName = "semantic_label", IsNullable = true)]
        public string SemanticLabel { get; set; }

        [SugarColumn(ColumnName = "monitoring_role", IsNullable = true)]
        public string MonitoringRole { get; set; }

        [SugarColumn(ColumnName = "activation_rule", IsNullable = true)]
        public string ActivationRule { get; set; }

        [SugarColumn(ColumnName = "abnormal_when", IsNullable = true)]
        public string AbnormalWhen { get; set; }

        [SugarColumn(ColumnName = "root_priority", IsNullable = true)]
        public int? RootPriority { get; set; }

        [SugarColumn(ColumnName = "confidence", IsNullable = true)]
        public decimal? Confidence { get; set; }

        [SugarColumn(ColumnName = "reason", IsNullable = true)]
        public string Reason { get; set; }

        [SugarColumn(ColumnName = "context_variables_json", IsNullable = true)]
        public string ContextVariablesJson { get; set; }

        [SugarColumn(ColumnName = "input_variables_json", IsNullable = true)]
        public string InputVariablesJson { get; set; }

        [SugarColumn(ColumnName = "output_variables_json", IsNullable = true)]
        public string OutputVariablesJson { get; set; }

        [SugarColumn(ColumnName = "variable_scope", IsNullable = true)]
        public string VariableScope { get; set; }

        [SugarColumn(ColumnName = "monitorability", IsNullable = true)]
        public string Monitorability { get; set; }

        [SugarColumn(ColumnName = "recommended_for_monitoring", IsNullable = true)]
        public bool? RecommendedForMonitoring { get; set; }

        [SugarColumn(ColumnName = "classifier_version", IsNullable = true)]
        public string ClassifierVersion { get; set; }

        [SugarColumn(ColumnName = "source_hash", IsNullable = true)]
        public string SourceHash { get; set; }

        [SugarColumn(ColumnName = "raw_response", IsNullable = true)]
        public string RawResponse { get; set; }

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
