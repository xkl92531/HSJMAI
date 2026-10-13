#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

/// <summary>candidates 命令的支撑函数：IO 点计数、标准答案范围告警、材料文件名。</summary>
public class CandidateSupportTests
{
    [Fact]
    public void IoTouchedCountsVariablesInsideHoistedNodes()
    {
        // 4 个触点并联的 O 块驱动两个线圈：扇出 ≥2 且表达式较长，翻译器把它提为节点，线圈的值只剩节点引用
        var b = new FlgNetBuilder();
        var or = b.Or("A", "B", "C", "D");
        var y1 = b.Part("Coil"); b.Wire(Id(b.Access("Y1")), Pin(y1, "operand"));
        var y2 = b.Part("Coil"); b.Wire(Id(b.Access("Y2")), Pin(y2, "operand"));
        b.Wire(Pin(or, "out"), Pin(y1, "in"), Pin(y2, "in"));
        var ir = UnitTranslator.Translate(new PipelineUnit(1, "Blk", 10, "LAD", b.Build(), "", [], 1));
        Assert.NotEmpty(ir.Nodes);

        var io = new HashSet<string>(["a", "c", "Y1", "Other"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, UnitLocator.IoTouched(ir, io.Contains));
    }

    [Fact]
    public void IoTouchedCountsCallPinsAndDistinctNamesOnce()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("Valve", "V1", ("IWP", "Input", "Bool"), ("QFwd", "Output", "Bool"));
        b.Wire(Id(b.Access("CYL_W")), Pin(call, "IWP"));
        b.Wire(Pin(call, "QFwd"), Id(b.Access("YV_W")));
        b.Series("Coil", "M1", ("cyl_w", false));
        var ir = UnitTranslator.Translate(new PipelineUnit(1, "Blk", 10, "LAD", b.Build(), "", ["Valve"], 1));
        var io = new HashSet<string>(["CYL_W", "YV_W"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, UnitLocator.IoTouched(ir, io.Contains));
    }

    static EvalFact Fact(string block, int? network) => new("Mutex", "Mutex|A|B|" + block + network, "d", "c", block, network, [], "t");

    [Fact]
    public void TruthFactsOutsideUnitsAreCounted()
    {
        var truth = new TruthFile("p", "s", "t", [Fact("FB1", 2), Fact("FB1", 3), Fact("FB2", null)], [new EvalUnitRef("FB1", 2)]);
        Assert.Equal(2, LogicEvaluator.TruthOutsideUnits(truth));
        Assert.Equal(0, LogicEvaluator.TruthOutsideUnits(truth with { Units = null }));
    }

    [Theory]
    [InlineData("FB1", 2, "FB1_2")]
    [InlineData("FB #1 a/b:c", 3, "FB #1 a_b_c_3")]
    [InlineData("CON", null, "CON_")]
    [InlineData("con.x", 1, "con_.x_1")]
    [InlineData("LPT1", null, "LPT1_")]
    public void FileNameIsSafeOnWindows(string block, int? network, string expected) =>
        Assert.Equal(expected, CandidateFileNames.Of(block, network));

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("com9", "com9_")]
    [InlineData("AUX.md", "AUX_.md")]
    [InlineData("COM10", "COM10")]
    [InlineData("CONSOLE", "CONSOLE")]
    public void ReservedDeviceNamesGetUnderscore(string name, string expected) =>
        Assert.Equal(expected, CandidateFileNames.AvoidReserved(name));

    [Fact]
    public void DuplicateNamesGetSequenceNumbers()
    {
        var units = new[] { (1L, "A:B", (int?)1), (2L, "A/B", 1), (3L, "a_b", 1), (4L, "C", 2) };
        var names = CandidateFileNames.Assign(units);
        Assert.Equal(["A_B_1", "A_B_1_2", "a_b_1_3", "C_2"], units.Select(u => names[u.Item1]));
    }
}
