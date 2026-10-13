using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 工位主表，保存项目级工位目录与聚合置信度。
    /// </summary>
    [SugarTable("AI_PLC_StationCatalog", tableDescription: "工位主表")]
    public class PLC_StationCatalogEntity : BaseField
    {
        public PLC_StationCatalogEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "所属项目ID")]
        public long? ProjectId { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位编码，如 OP080")]
        public string StationCode { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位名称")]
        public string StationName { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位聚合置信度")]
        public decimal? Confidence { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位来源类型，如 IOTagSeed/StationMap")]
        public string SourceType { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位状态，如 Active/Review/Conflict")]
        public string Status { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位证据JSON")]
        public string EvidenceJson { get; set; }

        [SugarColumn(IsNullable = true, ColumnDescription = "工位摘要JSON")]
        public string SummaryJson { get; set; }
    }
}