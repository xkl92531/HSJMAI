using HSJM.AI.AppLayer.LineUp.ImageMarks;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

public sealed class LineUpBomImportAppTests
{
    private sealed class FakeRepository : ILineUpImageMarkRepository
    {
        public bool PlcMissing;
        public bool LineMissing;
        public List<LineUpBomImportUpsert> ImportedUpserts { get; } = [];

        public Task<LineUpImageWorkspaceData?> GetStationWorkspaceAsync(long subStationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<LineUpImageHierarchyData?> GetHierarchyAsync(long subStationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<LineUpImageData?> GetImageAsync(long imageId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LineUpImageBomData>> GetBomsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CreateImageAsync(LineUpImageCreateData data, CancellationToken ct) => throw new NotSupportedException();
        public Task<LineUpImageSaveResult> SaveImageAsync(LineUpImageSaveData data, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SoftDeleteImageAsync(long imageId, CancellationToken ct) => throw new NotSupportedException();
        public Task<LineUpImageWorkspaceData?> FindFaultCandidatesAsync(string? tagCode, long? bomId, CancellationToken ct) => throw new NotSupportedException();
        public Task<LineUpImageBomData?> GetBomByIdAsync(long bomId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CreateBomAsync(LineUpBomDirectoryEntity entity, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateBomFieldsAsync(long bomId, string? refNo, string? tagCode, string? describe, string? dbAddress, string? modelNo, string? brand, string? ioAddress, string? partNo, long? plcId, string? plcCode, string? plcName, string? plcIp, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateBomAssociationAsync(long bomId, bool isAssociated, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SoftDeleteBomAsync(long bomId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LineUpPlcDeviceData>> ListPlcDevicesAsync(long? productionLineId, CancellationToken ct) => throw new NotSupportedException();
        public Task<LineUpPlcDeviceData?> GetPlcDeviceAsync(long plcId, CancellationToken ct) =>
            Task.FromResult(PlcMissing
                ? null
                : new LineUpPlcDeviceData(plcId, "PLC-CODE", "1号PLC", "192.168.5.2", 1));
        public Task<IReadOnlyList<LineUpImageBomData>> GetBomsByLineAsync(long productionLineId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LineUpImageBomData>>([
                new LineUpImageBomData(55, null, "Old", "旧名", "EXIST-TAG", null, null, null, "%I1.1", null, 9, null, null, null),
            ]);
        public Task<LineUpBomImportLineInfo?> GetProductionLineAsync(long productionLineId, CancellationToken ct) =>
            Task.FromResult(LineMissing
                ? null
                : new LineUpBomImportLineInfo(productionLineId, "L01", "产线1"));
        public Task<int> ImportBomsAsync(IReadOnlyList<LineUpBomImportUpsert> upserts, CancellationToken ct)
        {
            ImportedUpserts.AddRange(upserts);
            return Task.FromResult(upserts.Count);
        }
    }

    private static MemoryStream Template(params Action<IRow>[] rowFills)
    {
        var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Sheet1");
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("编号");
        for (var i = 0; i < rowFills.Length; i++)
            rowFills[i](sheet.CreateRow(i + 1));
        var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        stream.Position = 0;
        return stream;
    }

    private static void Cell(IRow row, int index, string value) => row.CreateCell(index).SetCellValue(value);

    [Fact]
    public async Task PreviewAsync_ReportsCreateUpdateErrorCounts()
    {
        var app = new LineUpBomImportApp(new FakeRepository());
        using var stream = Template(
            row => { Cell(row, 0, "R1"); Cell(row, 3, "NEW-TAG"); },   // create
            row => { Cell(row, 0, "R2"); Cell(row, 3, "EXIST-TAG"); }, // update（产线内已有，含绑工站行）
            row => { Cell(row, 1, "只有名称没有编号标签"); });           // error: 编号与标签均空
        var dto = await app.PreviewAsync(1, "点位.xlsx", stream, CancellationToken.None);

        Assert.Equal(3, dto.TotalRows);
        Assert.Equal(1, dto.CreateCount);
        Assert.Equal(1, dto.UpdateCount);
        Assert.Equal(1, dto.ErrorCount);
        Assert.Equal("update", dto.Rows[1].Status);
        Assert.Contains("MissingRefNoAndTagCode", dto.Rows[2].ErrorCodes);
    }

    [Fact]
    public async Task CommitAsync_OnlyValidRowsUpserted_PlcApplied_LineInfoFilled_IoAddressPreservedOnUpdate()
    {
        var repository = new FakeRepository();
        var app = new LineUpBomImportApp(repository);
        var request = new LineUpBomImportCommitRequest(1, 9,
        [
            new(2, "R1", "名1", "NEW-TAG", "DB1.DBX0.0", "M1", "B1", "P1"),
            new(3, "R2", "名2", "EXIST-TAG", null, null, null, null),
            new(4, null, null, null, null, null, null, null), // 非法行
        ]);
        var dto = await app.CommitAsync(request, CancellationToken.None);

        Assert.Equal(1, dto.CreatedCount);
        Assert.Equal(1, dto.UpdatedCount);
        Assert.Single(dto.FailedRows);
        Assert.Equal(2, repository.ImportedUpserts.Count);

        var create = repository.ImportedUpserts.Single(u => !u.IsUpdate);
        Assert.NotNull(create.Line);
        Assert.Equal(1, create.Line!.LineId);
        Assert.Equal("产线1", create.Line.Name);
        Assert.Equal(9, create.PlcId);
        Assert.Equal("192.168.5.2", create.PlcIp);

        var update = repository.ImportedUpserts.Single(u => u.IsUpdate);
        Assert.Equal(55, update.BomId);
        Assert.Equal("%I1.1", update.IoAddress); // 保留现有 IO 地址
        Assert.Equal(9, update.PlcId);
        Assert.Null(update.Line);
    }

    [Fact]
    public async Task CommitAsync_MissingPlc_ThrowsInvalidPlcDevice()
    {
        var repository = new FakeRepository { PlcMissing = true };
        var app = new LineUpBomImportApp(repository);
        var request = new LineUpBomImportCommitRequest(1, 9,
            [new(2, "R1", null, null, null, null, null, null)]);

        var error = await Assert.ThrowsAsync<LineUpImageMarkException>(
            () => app.CommitAsync(request, CancellationToken.None));
        Assert.Equal("LineUpInvalidPlcDevice", error.Code);
    }

    [Fact]
    public async Task CommitAsync_MissingLine_ThrowsInvalidProductionLine()
    {
        var repository = new FakeRepository { LineMissing = true };
        var app = new LineUpBomImportApp(repository);
        var request = new LineUpBomImportCommitRequest(1, 9,
            [new(2, "R1", null, null, null, null, null, null)]);

        var error = await Assert.ThrowsAsync<LineUpImageMarkException>(
            () => app.CommitAsync(request, CancellationToken.None));
        Assert.Equal("LineUpInvalidProductionLine", error.Code);
    }

    [Fact]
    public async Task PreviewAsync_NonExcelExtension_ThrowsFileInvalid()
    {
        var app = new LineUpBomImportApp(new FakeRepository());
        using var stream = new MemoryStream([1, 2, 3]);

        var error = await Assert.ThrowsAsync<LineUpImageMarkException>(
            () => app.PreviewAsync(1, "list.csv", stream, CancellationToken.None));
        Assert.Equal("LineUpImportFileInvalid", error.Code);
    }
}
