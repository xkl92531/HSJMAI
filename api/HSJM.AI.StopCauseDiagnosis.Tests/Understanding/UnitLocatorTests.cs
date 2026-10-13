#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class UnitLocatorTests
{
    static PipelineUnit Unit(string block, int network, FlgNetBuilder b, string[]? callees = null) =>
        new(1, block, network * 10, "LAD", b.Build(), "", callees ?? [], network);

    [Fact]
    public void FindsUnitWritingTheSourceSignal()
    {
        var reader = new FlgNetBuilder(); reader.Series("Coil", "M1", ("YV_W", false));
        var writer = new FlgNetBuilder(); writer.Series("Coil", "YV_W", ("A", false));
        var locator = new UnitLocator([Unit("Blk", 1, reader), Unit("Blk", 2, writer)]);
        var found = locator.Find([("YV_W", "Source"), ("CYL_W", "Target")]);
        Assert.Equal(2, found!.NetworkOrder);
    }

    [Fact]
    public void FindsUnitWhereCallOutputIsWired()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("Valve", "V1", ("QFwd", "Output", "Bool"));
        b.Wire(Pin(call, "QFwd"), Id(b.Access("YV_W", "GlobalVariable")));
        var locator = new UnitLocator([Unit("Caller", 5, b, ["Valve"])]);
        Assert.Equal("Caller", locator.Find([("YV_W", "Member"), ("YV_H", "Member")])!.BlockName);
    }

    [Fact]
    public void TouchesIsTrueForWriterAndReaderAndFalseForUnrelatedUnit()
    {
        var writer = new FlgNetBuilder(); writer.Series("Coil", "YV_W", ("A", false));
        var reader = new FlgNetBuilder(); reader.Series("Coil", "M1", ("CYL_W", false));
        var other = new FlgNetBuilder(); other.Series("Coil", "M2", ("B", false));
        var units = new[] { Unit("Blk", 1, writer), Unit("Blk", 2, reader), Unit("Blk", 3, other) };
        var locator = new UnitLocator(units);
        (string, string)[] signals = [("YV_W", "Source"), ("CYL_W", "Target")];
        Assert.True(locator.Touches(units[0], signals));
        Assert.True(locator.Touches(units[1], signals));
        Assert.False(locator.Touches(units[2], signals));
    }

    [Fact]
    public void CategoryByCalleeThenLanguageThenDefault()
    {
        var map = new CategoryMap(new() { ["Valve"] = "阀门" }, new() { ["SCL"] = "SCL" }, "普通逻辑");
        var b = new FlgNetBuilder();
        Assert.Equal("阀门", map.Of(new PipelineUnit(1, "X", 1, "LAD", b.Build(), "", ["Other", "Valve"], 1)));
        Assert.Equal("SCL", map.Of(new PipelineUnit(1, "X", 1, "SCL", "<StructuredText/>", "", [], 1)));
        Assert.Equal("普通逻辑", map.Of(new PipelineUnit(1, "X", 1, "LAD", b.Build(), "", [], 1)));
        Assert.Equal("普通逻辑", map.Of(null));
    }
}
