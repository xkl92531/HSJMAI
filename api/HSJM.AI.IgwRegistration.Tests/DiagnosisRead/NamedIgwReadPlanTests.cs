using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>
/// ReadTagsByFixedDeviceAsync 的 IGW 分支（气缸信号采集用）：短名先按 iotag 换成「表名.点名」再精确读。
/// IQ 表的点名常带地址里的小数点（I106.2_SG），以前会被当成「块名.成员」拆开而读不到。
/// </summary>
public class NamedIgwReadPlanTests
{
    private static Dictionary<string, string> ByShortName(params (string Short, string Full)[] rows) =>
        rows.ToDictionary(r => r.Short, r => r.Full, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Io_table_name_with_a_dot_is_resolved_through_the_io_table()
    {
        var requests = PLC_VariableAccessService.PlanNamedIgwReads(
            ["I106.2_SG"], ByShortName(("I106.2_SG", "IQ.I106.2_SG")), out var fallback, out var nameByKey);

        Assert.Equal(new PLC_ExactReadRequest("IQ.I106.2_SG", "IQ", "I106.2_SG"), Assert.Single(requests));
        Assert.Empty(fallback);
        Assert.Equal("I106.2_SG", nameByKey["IQ.I106.2_SG"]);
    }

    [Fact]
    public void Short_names_and_block_paths_keep_working()
    {
        var requests = PLC_VariableAccessService.PlanNamedIgwReads(
            ["OP030_CYL01_W", "DB6000_StdSignal.Ready"],
            ByShortName(("OP030_CYL01_W", "PLC1IO.OP030_CYL01_W")),
            out var fallback, out _);

        Assert.Equal(
            [
                new PLC_ExactReadRequest("PLC1IO.OP030_CYL01_W", "PLC1IO", "OP030_CYL01_W"),
                new PLC_ExactReadRequest("DB6000_StdSignal.Ready", "DB6000_StdSignal", "Ready"),
            ],
            requests);
        Assert.Empty(fallback);
    }

    [Fact]
    public void Unknown_short_name_without_a_block_is_reported_unreadable()
    {
        var requests = PLC_VariableAccessService.PlanNamedIgwReads(["NoSuchTag"], ByShortName(), out var fallback, out _);

        Assert.Empty(requests);
        var row = Assert.Single(fallback);
        Assert.Equal(("NoSuchTag", false, "IgwNoBlockSegment"), (row.TagName, row.IsGood, row.ErrorMessage));
    }
}
