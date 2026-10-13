using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NPOI.SS.UserModel;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

/// <summary>模板单行原始数据（已完成 trim / 空串转 null；RowIndex 为 Excel 1 起行号）。</summary>
public sealed record LineUpBomImportRawRow(
    int RowIndex,
    string? RefNo,
    string? Name,
    string? DbAddress,
    string? TagCode,
    string? ModelNo,
    string? Brand,
    string? PartNo,
    string? StationCode,
    string? StationNameHint);

/// <summary>BOM 点位清单导入模板解析器：固定列位 A–K（A编号 B名称 C分配地址 D标签 E型号 F品牌 G料号 H工站编码 I工站名称 J/K参考列不读）。</summary>
public static class LineUpBomImportParser
{
    public const int MaxRows = 1000;
    private const string HeaderAnchor = "编号";

    public static IReadOnlyList<LineUpBomImportRawRow> Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        IWorkbook workbook;
        ISheet sheet;
        try
        {
            workbook = WorkbookFactory.Create(stream);
            sheet = workbook.GetSheetAt(0);
        }
        catch (Exception error)
        {
            // 透传真实失败原因（NPOI 具体异常），便于排查"改后缀的假 xlsx / 加密工作簿 / 截断文件"等场景
            var summary = error.Message.Contains("neither an OLE2 stream", StringComparison.Ordinal)
                ? "The file is not a real Excel workbook (likely a CSV/HTML table renamed to .xlsx, or a fake xls exported by another system). Open it in Excel/WPS, re-save as .xlsx, then retry."
                : $"The file is not a readable Excel workbook. ({error.GetType().Name}: {Truncate(error.Message, 200)})";
            throw new LineUpImageMarkException("LineUpImportFileInvalid", summary);
        }

        var headerAnchor = Text(sheet.GetRow(0)?.GetCell(0));
        if (headerAnchor is null || !headerAnchor.Contains(HeaderAnchor, StringComparison.Ordinal))
            throw new LineUpImageMarkException(
                "LineUpImportTemplateMismatch", "The file does not match the BOM point import template.");

        var rows = new List<LineUpBomImportRawRow>();
        for (var rowNum = 1; rowNum <= sheet.LastRowNum; rowNum++)
        {
            var row = sheet.GetRow(rowNum);
            var raw = new LineUpBomImportRawRow(
                rowNum + 1,
                Text(row?.GetCell(0)),
                Text(row?.GetCell(1)),
                Text(row?.GetCell(2)),
                Text(row?.GetCell(3)),
                Text(row?.GetCell(4)),
                Text(row?.GetCell(5)),
                Text(row?.GetCell(6)),
                Text(row?.GetCell(7)),
                Text(row?.GetCell(8)));
            if (raw.RefNo is null && raw.Name is null && raw.DbAddress is null && raw.TagCode is null &&
                raw.ModelNo is null && raw.Brand is null && raw.PartNo is null &&
                raw.StationCode is null && raw.StationNameHint is null)
                continue;
            rows.Add(raw);
            if (rows.Count > MaxRows)
                throw new LineUpImageMarkException(
                    "LineUpImportTooManyRows", $"The template allows at most {MaxRows} rows per import.");
        }

        if (rows.Count == 0)
            throw new LineUpImageMarkException(
                "LineUpImportEmpty", "The template contains no point rows.");
        return rows;
    }

    private static string? Text(ICell? cell)
    {
        if (cell is null) return null;
        return cell.CellType switch
        {
            CellType.String => Normalize(cell.StringCellValue),
            CellType.Numeric => NumericText(cell.NumericCellValue),
            CellType.Formula => cell.CachedFormulaResultType switch
            {
                CellType.String => Normalize(cell.StringCellValue),
                CellType.Numeric => NumericText(cell.NumericCellValue),
                _ => Normalize(cell.ToString()),
            },
            CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
            _ => Normalize(cell.ToString()),
        };
    }

    private static string? NumericText(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
