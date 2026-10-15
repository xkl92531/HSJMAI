using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 一键诊断走 IGW 时，全局 I/O 变量（如 OP030_CYL05_W）没有块名前缀，
/// 要按 I/O 表映射成「表名.变量名」再精确读，不能直接判为读取失败。
/// </summary>
public class StopCauseSignalSnapshotIgwReadTests
{
    private static Dictionary<string, List<string>> IoTagMap(params (string Symbol, string Full)[] rows) =>
        rows.ToDictionary(r => r.Symbol, r => new List<string> { r.Full }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Io_short_name_is_read_through_its_io_table()
    {
        var requests = StopCauseSignalSnapshotService.BuildIgwExactReadRequests(
            ["OP030_CYL05_W"],
            IoTagMap(("OP030_CYL05_W", "PLC1IO.OP030_CYL05_W")),
            out var unresolved);

        var request = Assert.Single(requests);
        Assert.Equal(new PLC_ExactReadRequest("OP030_CYL05_W", "PLC1IO", "OP030_CYL05_W"), request);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Io_short_name_lookup_ignores_case()
    {
        var requests = StopCauseSignalSnapshotService.BuildIgwExactReadRequests(
            ["op030_yv05_w"],
            IoTagMap(("OP030_YV05_W", "PLC1IO.OP030_YV05_W")),
            out var unresolved);

        Assert.Equal(new PLC_ExactReadRequest("op030_yv05_w", "PLC1IO", "OP030_YV05_W"), Assert.Single(requests));
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Block_qualified_symbol_is_read_as_before()
    {
        var requests = StopCauseSignalSnapshotService.BuildIgwExactReadRequests(
            ["DB345_OP30Cylinder.Valve[5].Position"],
            IoTagMap(),
            out var unresolved);

        Assert.Equal(new PLC_ExactReadRequest("DB345_OP30Cylinder.Valve[5].Position", "DB345_OP30Cylinder", "Valve[5].Position"), Assert.Single(requests));
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Short_name_missing_from_io_table_is_still_unresolved()
    {
        var requests = StopCauseSignalSnapshotService.BuildIgwExactReadRequests(
            ["OP030_Unknown"],
            IoTagMap(),
            out var unresolved);

        Assert.Empty(requests);
        Assert.Equal(["OP030_Unknown"], unresolved);
    }

    [Fact]
    public void Mixed_symbols_keep_one_request_per_symbol()
    {
        var requests = StopCauseSignalSnapshotService.BuildIgwExactReadRequests(
            ["OP030_CYL05_W", "\"OP030_CYL05_W\"", "DB10.Step3.T", "NoSuchTag"],
            IoTagMap(("OP030_CYL05_W", "PLC1IO.OP030_CYL05_W")),
            out var unresolved);

        Assert.Equal(
            [
                new PLC_ExactReadRequest("OP030_CYL05_W", "PLC1IO", "OP030_CYL05_W"),
                new PLC_ExactReadRequest("DB10.Step3.T", "DB10", "Step3.T"),
            ],
            requests);
        Assert.Equal(["NoSuchTag"], unresolved);
    }
}
