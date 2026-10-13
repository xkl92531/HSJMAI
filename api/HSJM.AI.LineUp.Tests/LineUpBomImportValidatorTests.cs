using HSJM.AI.AppLayer.LineUp.ImageMarks;
using HSJM.AI.IDbService.LineUp;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

public sealed class LineUpBomImportValidatorTests
{
    private static LineUpBomImportRawRow Row(
        int rowIndex = 2,
        string? refNo = "R1",
        string? tagCode = "Tag1") =>
        new(rowIndex, refNo, "名称", "DB1.DBX0.0", tagCode, "型号", "品牌", "料号", null, null);

    private static LineUpImageBomData ExistingBom(long id, string? tagCode, string ioAddress = "%I1066.1") =>
        new(id, null, "Old", "旧名", tagCode, null, null, null, ioAddress, null, null, null, null, null);

    [Fact]
    public void Validate_NewRow_IsCreate()
    {
        var result = LineUpBomImportValidator.Validate([Row()], []);

        var row = Assert.Single(result);
        Assert.Equal(LineUpBomImportRowStatus.Create, row.Status);
        Assert.Null(row.MatchedBomId);
        Assert.Empty(row.ErrorCodes);
    }

    [Fact]
    public void Validate_ExistingTagCodeInLine_IgnoringCase_IsUpdate()
    {
        var existing = ExistingBom(55, "tag1"); // 大小写不同
        var result = LineUpBomImportValidator.Validate([Row(tagCode: "TAG1")], [existing]);

        var row = Assert.Single(result);
        Assert.Equal(LineUpBomImportRowStatus.Update, row.Status);
        Assert.Equal(55, row.MatchedBomId);
        Assert.Equal("%I1066.1", row.ExistingIoAddress);
    }

    [Fact]
    public void Validate_RefNoAndTagCodeBothNull_IsError()
    {
        var result = LineUpBomImportValidator.Validate(
            [Row(refNo: null, tagCode: null)], []);

        Assert.Equal(LineUpBomImportRowStatus.Error, result[0].Status);
        Assert.Contains("MissingRefNoAndTagCode", result[0].ErrorCodes);
    }

    [Fact]
    public void Validate_DuplicateTagInSameFile_SecondRowIsError()
    {
        var rows = new[] { Row(rowIndex: 2, tagCode: "Tag1"), Row(rowIndex: 3, tagCode: "TAG1") };
        var result = LineUpBomImportValidator.Validate(rows, []);

        Assert.Equal(LineUpBomImportRowStatus.Create, result[0].Status);
        Assert.Equal(LineUpBomImportRowStatus.Error, result[1].Status);
        Assert.Contains("FileDuplicateTagCode", result[1].ErrorCodes);
    }

    [Fact]
    public void Validate_DistinctTagsInSameFile_BothCreate()
    {
        var rows = new[] { Row(rowIndex: 2, tagCode: "Tag1"), Row(rowIndex: 3, tagCode: "Tag2") };
        var result = LineUpBomImportValidator.Validate(rows, []);

        Assert.All(result, row => Assert.Equal(LineUpBomImportRowStatus.Create, row.Status));
    }

    [Fact]
    public void Validate_NoTagRow_AlwaysCreateEvenIfRefNoMatchesExisting()
    {
        var existing = ExistingBom(55, null); // 已有无标签行
        var result = LineUpBomImportValidator.Validate(
            [Row(refNo: "Old", tagCode: null)], [existing]);

        var row = Assert.Single(result);
        Assert.Equal(LineUpBomImportRowStatus.Create, row.Status);
        Assert.Null(row.MatchedBomId);
    }

    [Fact]
    public void Validate_TextIsTrimmedAndBlankBecomesNull()
    {
        var result = LineUpBomImportValidator.Validate(
            [new LineUpBomImportRawRow(2, "   ", " 名称 ", null, null, null, null, null, null, null)], []);

        var row = Assert.Single(result);
        Assert.Null(row.RefNo); // 纯空白 → null
        Assert.Equal("名称", row.Name); // 已 trim
        Assert.Equal(LineUpBomImportRowStatus.Error, row.Status); // 编号与标签均为空
        Assert.Contains("MissingRefNoAndTagCode", row.ErrorCodes);
    }
}
