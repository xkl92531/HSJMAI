using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_logicunit_semantic_finding", tableDescription: "逻辑单元语义发现表")]
    public class PLC_LogicUnitSemanticFindingEntity
    {
        public PLC_LogicUnitSemanticFindingEntity()
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

        [SugarColumn(ColumnName = "variable_name", IsNullable = true)]
        public string VariableName { get; set; }

        [SugarColumn(ColumnName = "category", IsNullable = true)]
        public string Category { get; set; }

        [SugarColumn(ColumnName = "semantic_label", IsNullable = true)]
        public string SemanticLabel { get; set; }

        [SugarColumn(ColumnName = "reason", IsNullable = true)]
        public string Reason { get; set; }

        [SugarColumn(ColumnName = "confidence", IsNullable = true)]
        public decimal? Confidence { get; set; }

        [SugarColumn(ColumnName = "related_io_tags_json", IsNullable = true)]
        public string RelatedIoTagsJson { get; set; }

        [SugarColumn(ColumnName = "io_mapping", IsNullable = true)]
        public string IoMapping { get; set; }

        [SugarColumn(ColumnName = "ast_version", IsNullable = true)]
        public string AstVersion { get; set; }

        [SugarColumn(ColumnName = "source_hash", IsNullable = true)]
        public string SourceHash { get; set; }

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
