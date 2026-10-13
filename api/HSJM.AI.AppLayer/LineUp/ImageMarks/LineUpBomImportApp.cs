using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public sealed record LineUpBomImportPreviewRowDto(
    int RowIndex,
    string? RefNo,
    string? Name,
    string? TagCode,
    string? DbAddress,
    string? ModelNo,
    string? Brand,
    string? PartNo,
    string Status,
    IReadOnlyList<string> ErrorCodes);

public sealed record LineUpBomImportPreviewDto(
    int TotalRows,
    int CreateCount,
    int UpdateCount,
    int ErrorCount,
    IReadOnlyList<LineUpBomImportPreviewRowDto> Rows);

public sealed record LineUpBomImportCommitRow(
    int RowIndex,
    string? RefNo,
    string? Name,
    string? TagCode,
    string? DbAddress,
    string? ModelNo,
    string? Brand,
    string? PartNo);

public sealed record LineUpBomImportCommitRequest(
    long ProductionLineId,
    long PlcId,
    IReadOnlyList<LineUpBomImportCommitRow>? Rows);

public sealed record LineUpBomImportCommitFailureDto(
    int RowIndex,
    IReadOnlyList<string> ErrorCodes);

public sealed record LineUpBomImportCommitDto(
    int CreatedCount,
    int UpdatedCount,
    IReadOnlyList<LineUpBomImportCommitFailureDto> FailedRows);

/// <summary>BOM 点位清单模板导入（产线级，2026-09-29 v2：不绑工站，只绑产线+PLC设备）。
/// Preview 解析+校验不落库；Commit 重校验 + 单事务 upsert（产线内 TagCode 冲突覆盖更新）。
/// 导入产生的点位 StationId 为空（产线直挂），该产线任意子工站的点位清单均可见、可上图。</summary>
public sealed class LineUpBomImportApp(ILineUpImageMarkRepository repository) : ISelfScopedAutoInject
{
    public async Task<LineUpBomImportPreviewDto> PreviewAsync(
        long productionLineId,
        string? fileName,
        Stream source,
        CancellationToken cancellationToken)
    {
        if (productionLineId <= 0)
            throw Error("LineUpInvalidProductionLine", "The production line is invalid.");
        ArgumentNullException.ThrowIfNull(source);
        if (!IsExcelFileName(fileName))
            throw Error("LineUpImportFileInvalid", "Only .xlsx or .xls template files are supported.");

        var rawRows = LineUpBomImportParser.Parse(source);
        var validated = await ValidateAgainstDatabaseAsync(productionLineId, rawRows, cancellationToken);
        return ToPreviewDto(validated);
    }

    public async Task<LineUpBomImportCommitDto> CommitAsync(
        LineUpBomImportCommitRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw Error("LineUpImportInvalidRequest", "The import request is invalid.");
        if (request.ProductionLineId <= 0)
            throw Error("LineUpInvalidProductionLine", "The production line is invalid.");
        if (request.PlcId <= 0)
            throw Error("LineUpImportPlcRequired", "A PLC device is required.");
        if (request.Rows is null || request.Rows.Count == 0)
            throw Error("LineUpImportEmpty", "The template contains no point rows.");
        if (request.Rows.Count > LineUpBomImportParser.MaxRows)
            throw Error("LineUpImportTooManyRows",
                $"The template allows at most {LineUpBomImportParser.MaxRows} rows per import.");

        var plc = await repository.GetPlcDeviceAsync(request.PlcId, cancellationToken)
            ?? throw Error("LineUpInvalidPlcDevice", "The PLC device does not exist.");
        var line = await repository.GetProductionLineAsync(request.ProductionLineId, cancellationToken)
            ?? throw Error("LineUpInvalidProductionLine", "The production line does not exist.");

        var rawRows = request.Rows
            .Select(row => new LineUpBomImportRawRow(
                row.RowIndex, row.RefNo, row.Name, row.DbAddress, row.TagCode,
                row.ModelNo, row.Brand, row.PartNo, null, null))
            .ToArray();
        var validated = await ValidateAgainstDatabaseAsync(request.ProductionLineId, rawRows, cancellationToken);
        var validRows = validated
            .Where(row => row.Status != LineUpBomImportRowStatus.Error)
            .ToArray();

        var upserts = new List<LineUpBomImportUpsert>(validRows.Length);
        foreach (var row in validRows)
        {
            var isUpdate = row.Status == LineUpBomImportRowStatus.Update;
            upserts.Add(new LineUpBomImportUpsert(
                isUpdate,
                isUpdate ? row.MatchedBomId!.Value : 0,
                row.RefNo,
                row.Name,
                row.TagCode,
                row.DbAddress,
                row.ExistingIoAddress,
                row.ModelNo,
                row.Brand,
                row.PartNo,
                plc.Id,
                plc.Code,
                plc.Name,
                plc.Ip,
                isUpdate ? null : line));
        }

        var affected = await repository.ImportBomsAsync(upserts, cancellationToken);
        if (affected != upserts.Count)
            throw Error("LineUpImportCommitFailed", "The batch import did not complete.");

        var failedRows = validated
            .Where(row => row.Status == LineUpBomImportRowStatus.Error)
            .Select(row => new LineUpBomImportCommitFailureDto(row.RowIndex, row.ErrorCodes))
            .ToArray();
        return new LineUpBomImportCommitDto(
            upserts.Count(upsert => !upsert.IsUpdate),
            upserts.Count(upsert => upsert.IsUpdate),
            failedRows);
    }

    private async Task<IReadOnlyList<LineUpBomImportValidatedRow>> ValidateAgainstDatabaseAsync(
        long productionLineId,
        IReadOnlyList<LineUpBomImportRawRow> rawRows,
        CancellationToken cancellationToken)
    {
        var existingLineBoms = await repository.GetBomsByLineAsync(productionLineId, cancellationToken);
        return LineUpBomImportValidator.Validate(rawRows, existingLineBoms);
    }

    private static LineUpBomImportPreviewDto ToPreviewDto(
        IReadOnlyList<LineUpBomImportValidatedRow> validated)
    {
        var rows = validated
            .Select(row => new LineUpBomImportPreviewRowDto(
                row.RowIndex,
                row.RefNo,
                row.Name,
                row.TagCode,
                row.DbAddress,
                row.ModelNo,
                row.Brand,
                row.PartNo,
                row.Status switch
                {
                    LineUpBomImportRowStatus.Update => "update",
                    LineUpBomImportRowStatus.Error => "error",
                    _ => "create",
                },
                row.ErrorCodes))
            .ToArray();
        return new(
            validated.Count,
            rows.Count(row => row.Status == "create"),
            rows.Count(row => row.Status == "update"),
            rows.Count(row => row.Status == "error"),
            rows);
    }

    private static bool IsExcelFileName(string? fileName)
    {
        var name = fileName?.Trim();
        return name is not null &&
               (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase));
    }

    private static LineUpImageMarkException Error(string code, string summary) => new(code, summary);
}
