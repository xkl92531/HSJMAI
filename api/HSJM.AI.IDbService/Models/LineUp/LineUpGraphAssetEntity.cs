using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphAsset")]
public sealed class LineUpGraphAssetEntity : BaseField
{
    public LineUpGraphAssetEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public long PlcProjectId { get; set; }
    [SugarColumn(IsNullable = false)] public string StableKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceBlockName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string BlockType { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public int BlockNumber { get; set; }
    [SugarColumn(IsNullable = false)] public string ProgrammingLanguage { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public long CurrentPlcBlockId { get; set; }
    [SugarColumn(IsNullable = true)] public long? LatestRevisionId { get; set; }
}
