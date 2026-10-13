using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 工位与逻辑单元绑定表。
    /// </summary>
    [SugarTable("AI_PLC_StationLogicUnitBinding", tableDescription: "工位与逻辑单元绑定表")]
    public class PLC_StationLogicUnitBindingEntity : BaseField
    {
        public PLC_StationLogicUnitBindingEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "所属项目ID")]
        public long? ProjectId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位编码")]
        public string StationCode { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "逻辑单元ID")]
        public long? LogicUnitId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "块ID")]
        public long? BlockId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "归属置信度")]
        public decimal? Confidence { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "绑定原因")]
        public string Reason { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "冲突工位JSON")]
        public string ConflictingStationsJson { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "绑定证据JSON")]
        public string EvidenceJson { get; set; }
    }
}