using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 变量传播关系表。
    /// 用于记录 IOTag / 中间变量 / 虚拟Q 点之间的数据流传播关系。
    /// </summary>
    [SugarTable("AI_PLC_VariablePropagation", tableDescription: "变量传播关系表")]
    public class PLC_VariablePropagationEntity : BaseField
    {
        public PLC_VariablePropagationEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "所属项目ID")]
        public long? ProjectId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "源变量")]
        public string FromVariable { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "目标变量")]
        public string ToVariable { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "逻辑单元ID")]
        public long? LogicUnitId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "块ID")]
        public long? BlockId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "传播类型，如 DirectWrite/NeighborFlow/BlockInterface")]
        public string PropagationType { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "传播置信度")]
        public decimal? Confidence { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "传播证据JSON")]
        public string EvidenceJson { get; set; }
    }
}