#nullable enable
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 跨 PLC：PLC1 的程序引用 PLC2 工程里的通信块（如 DB182_PLC1&lt;-&gt;PLC2），
/// 按 PLC1 读不到、在 PLC1 也找不到写入源。要改到块所在的工程去读、去找写入工位。
/// </summary>
public class CrossPlcRulesTests
{
    private const long Plc1 = 1;
    private const long Plc2 = 2;
    private const long Plc3 = 3;

    private static Dictionary<string, IReadOnlyCollection<long>> Owners(params (string Block, long[] Projects)[] rows) =>
        rows.ToDictionary(r => r.Block, r => (IReadOnlyCollection<long>)r.Projects, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("DB182_PLC1<->PLC2.OP080_Free", "DB182_PLC1<->PLC2")]
    [InlineData("\"DB182_PLC1<->PLC2\".OP080_Free", "DB182_PLC1<->PLC2")]
    [InlineData("DB345_OP30Cylinder.Valve[5].Position", "DB345_OP30Cylinder")]
    [InlineData("OP030_CYL05_W", null)]
    public void Block_name_is_the_first_segment(string symbol, string? expected)
    {
        Assert.Equal(expected, CrossPlcRules.BlockNameOf(symbol));
    }

    [Theory]
    [InlineData("DB182_PLC1<->PLC2.OP080_Free", "OP080_Free")]
    [InlineData("\"DB182_PLC1<->PLC2\".Station.OP080.Free", "Station.OP080.Free")]
    [InlineData("OP030_CYL05_W", null)]
    public void Member_path_is_everything_after_the_block(string symbol, string? expected)
    {
        Assert.Equal(expected, CrossPlcRules.MemberPathOf(symbol));
    }

    [Fact]
    public void A_block_that_exists_in_the_own_project_is_not_rerouted()
    {
        var owners = Owners(("DB10", [Plc1, Plc2]));

        Assert.Null(CrossPlcRules.ResolveOwnerProject("DB10.X", Plc1, owners));
    }

    [Fact]
    public void A_block_only_in_another_project_is_owned_by_that_project()
    {
        var owners = Owners(("DB182_PLC1<->PLC2", [Plc2]));

        Assert.Equal(Plc2, CrossPlcRules.ResolveOwnerProject("DB182_PLC1<->PLC2.OP080_Free", Plc1, owners));
    }

    [Fact]
    public void A_block_in_several_other_projects_is_ambiguous()
    {
        var owners = Owners(("DB182_PLC1<->PLC2", [Plc2, Plc3]));

        Assert.Null(CrossPlcRules.ResolveOwnerProject("DB182_PLC1<->PLC2.OP080_Free", Plc1, owners));
    }

    [Fact]
    public void Unknown_blocks_and_symbols_without_a_block_are_not_rerouted()
    {
        var owners = Owners(("DB182_PLC1<->PLC2", [Plc2]));

        Assert.Null(CrossPlcRules.ResolveOwnerProject("DB999.X", Plc1, owners));
        Assert.Null(CrossPlcRules.ResolveOwnerProject("OP030_CYL05_W", Plc1, owners));
    }

    [Fact]
    public void Unreadable_foreign_symbols_are_grouped_by_owner_project()
    {
        var owners = Owners(("DB182_PLC1<->PLC2", [Plc2]), ("DB183_PLC1<->PLC3", [Plc3]), ("DB10", [Plc1]));

        var plan = CrossPlcRules.PlanReroute(
            ["DB182_PLC1<->PLC2.A", "DB182_PLC1<->PLC2.B", "DB183_PLC1<->PLC3.C", "DB10.Local", "OP030_CYL05_W"],
            Plc1, owners);

        Assert.Equal(["DB182_PLC1<->PLC2.A", "DB182_PLC1<->PLC2.B"], plan[Plc2]);
        Assert.Equal(["DB183_PLC1<->PLC3.C"], plan[Plc3]);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void Foreign_signal_written_by_a_station_means_waiting_for_that_station()
    {
        Assert.Equal("WaitingFor:OP080", CrossPlcRules.TerminalOwnership(isForeign: true, writerStation: "OP080"));
    }

    [Fact]
    public void Foreign_signal_without_writer_station_stays_external_input()
    {
        Assert.Equal("ExternalInput", CrossPlcRules.TerminalOwnership(isForeign: true, writerStation: null));
        Assert.Equal("ExternalInput", CrossPlcRules.TerminalOwnership(isForeign: false, writerStation: "OP080"));
    }

    [Fact]
    public void Writer_station_is_the_unique_most_common_station()
    {
        Assert.Equal("OP080", CrossPlcRules.PickWriterStation(["OP080", "OP080", "OP090"]));
        Assert.Null(CrossPlcRules.PickWriterStation(["OP080", "OP090"]));
        Assert.Null(CrossPlcRules.PickWriterStation([null, ""]));
    }
}
