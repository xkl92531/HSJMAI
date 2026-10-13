using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphAlias")]
public sealed class LineUpGraphAliasEntity : BaseField
{
    public LineUpGraphAliasEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public long BindingId { get; set; }
    [SugarColumn(IsNullable = false)] public string ElementSourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string DisplayName { get; set; } = string.Empty;
}
