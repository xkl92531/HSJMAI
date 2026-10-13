using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_ImportBatch")]
public sealed class LineUpImportBatchEntity : BaseField
{
    public LineUpImportBatchEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public string StoredFilePath { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? PreviewJson { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? SummaryJson { get; set; }
    [SugarColumn(IsNullable = false)] public string Status { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public string? ErrorSummary { get; set; }
    public long LineId { get; set; }
    public long DeviceId { get; set; }
    [SugarColumn(IsNullable = true)] public long? PlcProjectId { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceFileName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceFileHash { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public string? ParsingLeaseToken { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ParsingLeaseExpiresAt { get; set; }
    public int PreviewSchemaVersion { get; set; }
    public int PreviewRevision { get; set; }
    [SugarColumn(IsNullable = true)] public string? PreviewHash { get; set; }
    [SugarColumn(IsNullable = true)] public string? CommitRequestHash { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? CommitReceiptJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? CommitMode { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CommittedAt { get; set; }
    [SugarColumn(IsNullable = true)] public long? LegacyBotuProjectId { get; set; }
}
