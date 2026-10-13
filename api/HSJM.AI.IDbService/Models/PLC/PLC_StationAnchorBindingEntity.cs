using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 工位与锚点绑定表。
    /// </summary>
    [SugarTable("AI_PLC_StationAnchorBinding", tableDescription: "工位与锚点绑定表")]
    public class PLC_StationAnchorBindingEntity : BaseField
    {
        public PLC_StationAnchorBindingEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "所属项目ID")]
        public long? ProjectId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位编码")]
        public string StationCode { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "锚点键")]
        public string AnchorKey { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "变量名")]
        public string VariableName { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "锚点类型")]
        public string AnchorType { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "归属置信度")]
        public decimal? Confidence { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "是否存在工位冲突")]
        public bool? HasConflict { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "冲突工位JSON")]
        public string ConflictingStationsJson { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "绑定证据JSON")]
        public string EvidenceJson { get; set; }
    }
}