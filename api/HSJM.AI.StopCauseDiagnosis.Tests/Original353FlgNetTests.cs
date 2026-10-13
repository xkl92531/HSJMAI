#nullable enable
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353FlgNetTests
{
    static (Original353FlgNet Net, int Coil) TimerNetwork(bool passthrough)
    {
        var b = new FlgNetBuilder();
        var c = b.Chain(Rail(), ("X", false));
        var ton = b.Timer("TON", Pin(c, "out"));
        b.Coil("SCoil", "Alarm", Pin(ton, "Q"));
        var net = new Original353FlgNet(XElement.Parse(b.Build()), passthrough);
        var coil = net.Parts.Single(p => Original353FlgNet.A(p, "Name") == "SCoil");
        return (net, int.Parse(Original353FlgNet.A(coil, "UId")));
    }

    [Fact]
    public void TimerOutputIsOpaqueByDefault()
    {
        var (net, coil) = TimerNetwork(passthrough: false);
        Assert.Null(net.Pin(coil.ToString(), "in"));
    }

    [Fact]
    public void TimerPassthroughWrapsTheInputExpression()
    {
        var (net, coil) = TimerNetwork(passthrough: true);
        var term = net.Pin(coil.ToString(), "in");
        Assert.NotNull(term);
        Assert.Equal(Original353FlgNet.TimerKind, term!.Kind);
        Assert.Equal("TON", term.Name);
        Assert.Contains(PipelineTerms.Walk(term), t => t.Kind == "Signal" && t.Name == "X");
    }

    [Fact]
    public void OperandSymbolReturnsTheCoilTarget()
    {
        var (net, coil) = TimerNetwork(passthrough: false);
        Assert.Equal("Alarm", net.OperandSymbol(coil.ToString()));
    }

    internal static XElement ArrayAccess(int uid, params string[] indexes) =>
        new("Access", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", uid),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", "DB")),
                new XElement("Component", new XAttribute("Name", "Arr"), new XAttribute("AccessModifier", "Array"),
                    indexes.Select(i => new XElement("Access", new XAttribute("Scope", "LiteralConstant"),
                        new XElement("Constant", new XElement("ConstantValue", i)))))));

    [Fact]
    public void SymbolOfArrayWithOneIntegerIndexIsBracketed()
    {
        Assert.Equal("DB.Arr[3]", Original353FlgNet.Symbol(ArrayAccess(1, "3")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2")]
    [InlineData("x")]
    public void SymbolOfUnprovableArrayIndexIsNullNotThrow(string indexes)
    {
        Assert.Null(Original353FlgNet.Symbol(ArrayAccess(1, indexes.Split(',', StringSplitOptions.RemoveEmptyEntries))));
    }

    [Fact]
    public void SymbolWithTwoSymbolElementsIsNullNotThrow()
    {
        var access = ArrayAccess(1, "3");
        access.Add(new XElement(access.Element("Symbol")!));
        Assert.Null(Original353FlgNet.Symbol(access));
    }

    [Fact]
    public void PinOnAmbiguousLiteralOrCardIsNullNotThrow()
    {
        var net = new Original353FlgNet(new XElement("FlgNet",
            new XElement("Parts",
                new XElement("Access", new XAttribute("Scope", "LiteralConstant"), new XAttribute("UId", 1),
                    new XElement("Constant", new XElement("ConstantValue", "TRUE"), new XElement("ConstantValue", "FALSE"))),
                new XElement("Part", new XAttribute("Name", "Coil"), new XAttribute("UId", 2)),
                new XElement("Part", new XAttribute("Name", "O"), new XAttribute("UId", 3),
                    new XElement("TemplateValue", new XAttribute("Name", "Card"), 2), new XElement("TemplateValue", new XAttribute("Name", "Card"), 2)),
                new XElement("Part", new XAttribute("Name", "Coil"), new XAttribute("UId", 4))),
            new XElement("Wires",
                new XElement("Wire", new XAttribute("UId", 5), Id(1), Pin(2, "in")),
                new XElement("Wire", new XAttribute("UId", 6), Rail(), Pin(3, "in1"), Pin(3, "in2")),
                new XElement("Wire", new XAttribute("UId", 7), Pin(3, "out"), Pin(4, "in")))));
        Assert.Null(net.Pin("2", "in"));
        Assert.Null(net.Pin("4", "in"));
    }

    [Fact]
    public void GlobalWritesCountsOneSiteOncePerWire()
    {
        var b = new FlgNetBuilder("GlobalVariable");
        var coil = b.Part("Coil");
        b.Wire(Id(b.Access("YV_W")), Id(b.Access("yv_w")), Pin(coil, "operand"));
        b.Wire(Rail(), Pin(coil, "in"));
        var net = new Original353FlgNet(XElement.Parse(b.Build()));
        var write = Assert.Single(net.GlobalWrites());
        Assert.Equal((coil.ToString(), "operand"), (write.PartId, write.Port));
    }

    [Fact]
    public void OrBlockWithNegatedInputIsNull()
    {
        // F2: an O block input carrying <Negated Name="in1"/> is not the plain OR of its drivers
        var b = new FlgNetBuilder();
        var or = b.Or("A", "B");
        b.Negate(or, "in1");
        var coil = b.Coil("Coil", "Q", Pin(or, "out"));
        var net = new Original353FlgNet(XElement.Parse(b.Build()));
        Assert.Null(net.Pin(coil.ToString(), "in"));
    }

    [Fact]
    public void SymbolWithSliceAccessIsNull()
    {
        // F6: "Word".%X3 must not collapse to the whole variable "Word"
        var access = new XElement("Access", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", 1),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", "Word"), new XAttribute("SliceAccessModifier", "x3"))));
        Assert.Null(Original353FlgNet.Symbol(access));
    }

    [Fact]
    public void SymbolWithNestedVariableIndexIsNull()
    {
        // F6: DB.Arr[Idx[2]] must not be read as DB.Arr[2]
        var access = new XElement("Access", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", 1),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", "DB")),
                new XElement("Component", new XAttribute("Name", "Arr"), new XAttribute("AccessModifier", "Array"),
                    new XElement("Access", new XAttribute("Scope", "GlobalVariable"),
                        new XElement("Symbol", new XElement("Component", new XAttribute("Name", "Idx"), new XAttribute("AccessModifier", "Array"),
                            new XElement("Access", new XAttribute("Scope", "LiteralConstant"),
                                new XElement("Constant", new XElement("ConstantValue", "2")))))))));
        Assert.Null(Original353FlgNet.Symbol(access));
    }

    static IReadOnlyList<Original353ConditionalFeedback> FeedbackOf(Action<XElement> corrupt)
    {
        var b = new FlgNetBuilder("GlobalVariable");
        b.Call("FB", ("A", "Input"), ("B", "Input"), ("X", "Output"), ("Y", "Output"));
        var xml = XElement.Parse(b.Build());
        corrupt(xml);
        var unit = new PipelineUnit(1, "Caller", 1, "LAD", xml.ToString(SaveOptions.DisableFormatting), "", []);
        return Original353FeedbackGroups.Extract(new PipelineSnapshot(1, [unit], []), unit);
    }

    [Fact]
    public void FeedbackGroupsSkipNetworkWithDuplicateUIds()
    {
        // F9: duplicate part UIds make the reader constructor throw ArgumentException — skip, never crash
        Assert.Empty(FeedbackOf(xml => { var parts = xml.Element("Parts")!; parts.Add(new XElement(parts.Elements().First())); }));
    }

    [Fact]
    public void FeedbackGroupsSkipCallWithTwoCallInfos()
    {
        Assert.Empty(FeedbackOf(xml => { var info = xml.Descendants("CallInfo").Single(); info.AddAfterSelf(new XElement(info)); }));
    }

    [Fact]
    public void AccessPinsListsEveryVariableConnection()
    {
        var (net, _) = TimerNetwork(passthrough: false);
        var pins = net.AccessPins().Select(p => (p.Symbol, p.Port)).ToArray();
        Assert.Contains(("X", "operand"), pins);
        Assert.Contains(("Alarm", "operand"), pins);
    }
}
