using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    [SugarTable("ai_plc_logicunit_ast", tableDescription: "逻辑单元AST持久化表")]
    public class PLC_LogicUnitAstEntity
    {
        public PLC_LogicUnitAstEntity()
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

        [SugarColumn(ColumnName = "unit_type", IsNullable = true)]
        public string UnitType { get; set; }

        [SugarColumn(ColumnName = "network_id", IsNullable = true)]
        public int? NetworkId { get; set; }

        [SugarColumn(ColumnName = "title", IsNullable = true)]
        public string Title { get; set; }

        [SugarColumn(ColumnName = "comment", IsNullable = true)]
        public string Comment { get; set; }

        [SugarColumn(ColumnName = "programming_language", IsNullable = true)]
        public string ProgrammingLanguage { get; set; }

        [SugarColumn(ColumnName = "read_vars_count", IsNullable = true)]
        public int? ReadVarsCount { get; set; }

        [SugarColumn(ColumnName = "write_vars_count", IsNullable = true)]
        public int? WriteVarsCount { get; set; }

        [SugarColumn(ColumnName = "calls_count", IsNullable = true)]
        public int? CallsCount { get; set; }

        [SugarColumn(ColumnName = "compiler_name", IsNullable = true)]
        public string CompilerName { get; set; }

        [SugarColumn(ColumnName = "compile_status", IsNullable = true)]
        public string CompileStatus { get; set; }

        [SugarColumn(ColumnName = "ast_version", IsNullable = true)]
        public string AstVersion { get; set; }

        [SugarColumn(ColumnName = "source_hash", IsNullable = true)]
        public string SourceHash { get; set; }

        [SugarColumn(ColumnName = "root_node_json", IsNullable = true)]
        public string RootNodeJson { get; set; }

        [SugarColumn(ColumnName = "coverage_reports_json", IsNullable = true)]
        public string CoverageReportsJson { get; set; }

        [SugarColumn(ColumnName = "messages_json", IsNullable = true)]
        public string MessagesJson { get; set; }

        [SugarColumn(ColumnName = "input_variables_json", IsNullable = true)]
        public string InputVariablesJson { get; set; }

        [SugarColumn(ColumnName = "output_variables_json", IsNullable = true)]
        public string OutputVariablesJson { get; set; }

        [SugarColumn(ColumnName = "raw_content_excerpt", IsNullable = true)]
        public string RawContentExcerpt { get; set; }

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
