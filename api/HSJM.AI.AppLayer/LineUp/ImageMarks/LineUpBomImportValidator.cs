using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public static class LineUpBomImportText
{
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string NormalizeKey(string? value) =>
        (Normalize(value) ?? string.Empty).ToLowerInvariant();
}

public enum LineUpBomImportRowStatus
{
    Create,
    Update,
    Error,
}

public sealed record LineUpBomImportValidatedRow(
    int RowIndex,
    string? RefNo,
    string? Name,
    string? DbAddress,
    string? TagCode,
    string? ModelNo,
    string? Brand,
    string? PartNo,
    string? ExistingIoAddress,
    LineUpBomImportRowStatus Status,
    long? MatchedBomId,
    IReadOnlyList<string> ErrorCodes);

/// <summary>
/// 导入行校验核心（纯函数，Preview 与 Commit 共用）。产线级语义（2026-09-29 v2 变更：不绑工站）：
/// 冲突判定键 = 产线内 TagCode（忽略大小写，含已绑工站行与产线直挂行）；
/// 阻断码：MissingRefNoAndTagCode / FileDuplicateTagCode（同文件内同标签重复）。
/// </summary>
public static class LineUpBomImportValidator
{
    private static readonly HashSet<string> BlockingCodes = new(StringComparer.Ordinal)
    {
        "MissingRefNoAndTagCode",
        "FileDuplicateTagCode",
    };

    public static IReadOnlyList<LineUpBomImportValidatedRow> Validate(
        IReadOnlyList<LineUpBomImportRawRow> rows,
        IReadOnlyList<LineUpImageBomData> existingLineBoms)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(existingLineBoms);

        var existingByTag = existingLineBoms
            .Where(bom => LineUpBomImportText.Normalize(bom.TagCode) is not null)
            .GroupBy(bom => LineUpBomImportText.NormalizeKey(bom.TagCode), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seenFileTags = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LineUpBomImportValidatedRow>(rows.Count);

        foreach (var row in rows)
        {
            var errors = new List<string>();
            var refNo = LineUpBomImportText.Normalize(row.RefNo);
            var tagCode = LineUpBomImportText.Normalize(row.TagCode);
            if (refNo is null && tagCode is null)
                errors.Add("MissingRefNoAndTagCode");

            LineUpImageBomData? existing = null;
            if (tagCode is not null)
            {
                existingByTag.TryGetValue(LineUpBomImportText.NormalizeKey(tagCode), out existing);
                if (!seenFileTags.Add(LineUpBomImportText.NormalizeKey(tagCode)))
                    errors.Add("FileDuplicateTagCode");
            }

            var blocked = errors.Any(BlockingCodes.Contains);
            var status = blocked
                ? LineUpBomImportRowStatus.Error
                : existing is not null
                    ? LineUpBomImportRowStatus.Update
                    : LineUpBomImportRowStatus.Create;

            result.Add(new LineUpBomImportValidatedRow(
                row.RowIndex,
                refNo,
                LineUpBomImportText.Normalize(row.Name),
                LineUpBomImportText.Normalize(row.DbAddress),
                tagCode,
                LineUpBomImportText.Normalize(row.ModelNo),
                LineUpBomImportText.Normalize(row.Brand),
                LineUpBomImportText.Normalize(row.PartNo),
                existing?.IoAddress,
                status,
                existing?.Id,
                errors));
        }

        return result;
    }
}
