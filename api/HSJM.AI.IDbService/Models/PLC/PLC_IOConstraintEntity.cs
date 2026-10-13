using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IO信号约束规则主表。
    /// 一条规则描述一组信号之间的约束关系，信号明细存储在 AI_PLC_IOConstraintSignal 表中。
    /// </summary>
    [SugarTable("AI_PLC_IOConstraintRule", tableDescription: "IO信号约束规则主表")]
    public class PLC_IOConstraintRuleEntity : BaseField
    {
        public PLC_IOConstraintRuleEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 关联的项目ID。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的项目ID")]
        public long? ProjectId { get; set; }

        /// <summary>
        /// 规则名称。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "text", ColumnDescription = "规则名称")]
        public string? RuleName { get; set; }

        /// <summary>
        /// 约束规则类型，如 Mutex、ExactlyOne、AllRequired、Pairing、Safety、Sequence。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 30, ColumnDescription = "约束规则类型")]
        public string RuleType { get; set; } = string.Empty;

        /// <summary>
        /// 约束运算符，如 AT_MOST_ONE、EXACTLY_ONE、ALL_TRUE、IMPLIES、REQUIRES_0、CHAIN_ORDER、CUSTOM。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 30, ColumnDescription = "约束运算符")]
        public string Operator { get; set; } = string.Empty;

        /// <summary>
        /// 关联的设备或机构ID。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 100, ColumnDescription = "关联的设备或机构ID")]
        public string? EquipmentId { get; set; }

        /// <summary>
        /// 规则置信度。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "规则置信度")]
        public decimal? Confidence { get; set; }

        /// <summary>
        /// 规则来源，如 AI、Manual。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 30, ColumnDescription = "规则来源")]
        public string? Source { get; set; }

        /// <summary>
        /// 规则描述。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "text", ColumnDescription = "规则描述")]
        public string? Description { get; set; }

        /// <summary>
        /// 规则优先级，如 Critical、Warning、Info。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20, ColumnDescription = "规则优先级")]
        public string? Priority { get; set; }

        /// <summary>
        /// 自定义有效状态组合，仅在 Operator=CUSTOM 时使用。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(max)", ColumnDescription = "自定义有效状态组合JSON")]
        public string? ValidStates { get; set; }

        /// <summary>
        /// 生成本条约束的 LogicUnit ID（外键 → AI_PLC_LogicUnit.Id，可空）。
        /// 用途：PLC 程序片段更新时，按 LU 增量软删/重生约束（详见 CLAUDE.md §四 增量约束更新）。
        /// 旧约束 / 非 LU 来源约束（如人工录入）允许为 NULL。
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "生成本条约束的 LogicUnit ID（外键，可空）")]
        public long? SourceLuId { get; set; }
    }
}
