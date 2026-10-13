using HSJM.AI.AppLayer.LineUp.ImageMarks;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

public sealed class LineUpBomImportParserTests
{
    private static MemoryStream Workbook(Action<ISheet> fill)
    {
        var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Sheet1");
        fill(sheet);
        var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        stream.Position = 0;
        return stream;
    }

    private static void Header(ISheet sheet)
    {
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("编号");
        header.CreateCell(1).SetCellValue("名称");
        header.CreateCell(2).SetCellValue("分配地址(可空)");
        header.CreateCell(3).SetCellValue("标签(可空)");
        header.CreateCell(4).SetCellValue("型号（可空）");
        header.CreateCell(5).SetCellValue("品牌（可空）");
        header.CreateCell(6).SetCellValue("料号（可空）");
        header.CreateCell(7).SetCellValue("工站编码（可空）");
        header.CreateCell(8).SetCellValue("工站名称（可空）");
    }

    [Fact]
    public void Parse_MapsAllColumnsAndPreservesExcelRowNumber()
    {
        using var stream = Workbook(sheet =>
        {
            Header(sheet);
            var row = sheet.CreateRow(3); // Excel 第 4 行
            row.CreateCell(0).SetCellValue("OP02001-CYL01-1");
            row.CreateCell(1).SetCellValue("1号气缸");
            row.CreateCell(2).SetCellValue("DB310.DBX0.0");
            row.CreateCell(3).SetCellValue("OP020_CYL01_W");
            row.CreateCell(4).SetCellValue("CDQ2B40");
            row.CreateCell(5).SetCellValue("SMC");
            row.CreateCell(6).SetCellValue("P-10021");
            row.CreateCell(7).SetCellValue(" OP02001 ");
            row.CreateCell(8).SetCellValue("装配工位");
        });

        var rows = LineUpBomImportParser.Parse(stream);

        var row = Assert.Single(rows);
        Assert.Equal(4, row.RowIndex);
        Assert.Equal("OP02001-CYL01-1", row.RefNo);
        Assert.Equal("1号气缸", row.Name);
        Assert.Equal("DB310.DBX0.0", row.DbAddress);
        Assert.Equal("OP020_CYL01_W", row.TagCode);
        Assert.Equal("CDQ2B40", row.ModelNo);
        Assert.Equal("SMC", row.Brand);
        Assert.Equal("P-10021", row.PartNo);
        Assert.Equal("OP02001", row.StationCode); // 已 trim
        Assert.Equal("装配工位", row.StationNameHint);
    }

    [Fact]
    public void Parse_NumericCellHasNoDecimalTail()
    {
        using var stream = Workbook(sheet =>
        {
            Header(sheet);
            var row = sheet.CreateRow(1);
            row.CreateCell(0).SetCellValue(12);
            row.CreateCell(7).SetCellValue(3.5);
        });

        var rows = LineUpBomImportParser.Parse(stream);

        Assert.Equal("12", rows[0].RefNo);
        Assert.Equal("3.5", rows[0].StationCode);
    }

    [Fact]
    public void Parse_BlankRowsAreSkipped()
    {
        using var stream = Workbook(sheet =>
        {
            Header(sheet);
            sheet.CreateRow(1); // 全空行
            var row = sheet.CreateRow(2);
            row.CreateCell(0).SetCellValue("R1");
            sheet.CreateRow(3); // 又一个全空行（含空格字符串）
            var row4 = sheet.CreateRow(4);
            row4.CreateCell(1).SetCellValue("   ");
        });

        var rows = LineUpBomImportParser.Parse(stream);

        var row = Assert.Single(rows);
        Assert.Equal("R1", row.RefNo);
    }

    [Fact]
    public void Parse_HeaderMismatch_ThrowsTemplateMismatch()
    {
        using var stream = Workbook(sheet =>
        {
            var header = sheet.CreateRow(0);
            header.CreateCell(0).SetCellValue("随便什么表头");
        });

        var error = Assert.Throws<LineUpImageMarkException>(() => LineUpBomImportParser.Parse(stream));
        Assert.Equal("LineUpImportTemplateMismatch", error.Code);
    }

    [Fact]
    public void Parse_NoDataRows_ThrowsEmpty()
    {
        using var stream = Workbook(Header);

        var error = Assert.Throws<LineUpImageMarkException>(() => LineUpBomImportParser.Parse(stream));
        Assert.Equal("LineUpImportEmpty", error.Code);
    }

    [Fact]
    public void Parse_MoreThanMaxRows_ThrowsTooManyRows()
    {
        using var stream = Workbook(sheet =>
        {
            Header(sheet);
            for (var i = 1; i <= LineUpBomImportParser.MaxRows + 1; i++)
                sheet.CreateRow(i).CreateCell(0).SetCellValue($"R{i}");
        });

        var error = Assert.Throws<LineUpImageMarkException>(() => LineUpBomImportParser.Parse(stream));
        Assert.Equal("LineUpImportTooManyRows", error.Code);
    }

    [Fact]
    public void Parse_NotAnExcelFile_ThrowsFileInvalid()
    {
        var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

        var error = Assert.Throws<LineUpImageMarkException>(() => LineUpBomImportParser.Parse(garbage));
        Assert.Equal("LineUpImportFileInvalid", error.Code);
    }
}
