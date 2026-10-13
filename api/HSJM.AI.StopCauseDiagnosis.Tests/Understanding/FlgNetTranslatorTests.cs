#nullable enable
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class FlgNetTranslatorTests
{
    static IReadOnlyList<string> Lines(FlgNetBuilder b)
    {
        var (statements, _, _) = FlgNetTranslator.Translate(XElement.Parse(b.Build()), "B/1");
        return statements.Select(IrText.Statement).ToArray();
    }

    [Fact]
    public void SeriesContactsDriveCoil()
    {
        var b = new FlgNetBuilder();
        b.Series("Coil", "X", ("A", false), ("B", true));
        Assert.Equal(["[B/1/1] X := (A AND NOT(B))"], Lines(b));
    }

    [Fact]
    public void EnoIsEnAndTheInstructionDidNotFail()   // ADD 溢出、DIV 除零、块内 ENO:=FALSE 时 en=1 而 eno=0
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var add = b.Part("Add"); b.Wire(Pin(a, "out"), Pin(add, "en"));
        b.Coil("Coil", "Q", Pin(add, "eno"));
        var line = Assert.Single(Lines(b), l => l.Contains("Q :="));
        Assert.Contains("A AND 〔Add（部件 ", line);
        Assert.Contains("）的 ENO：执行未出错〕", line);
        var call = b.Call("FB_U", ("x", "Input"));
        b.Coil("Coil", "R", Pin(call, "eno"));
        Assert.Contains(Lines(b), l => l.Contains("R := 〔FB_U（部件 ") && l.Contains("）的 ENO：执行未出错〕"));
    }

    [Fact]
    public void SetAndResetCoilsBecomeSetResetStatements()
    {
        var b = new FlgNetBuilder();
        b.Series("SCoil", "M", ("A", false));
        b.Series("RCoil", "M", ("R", false));
        Assert.Equal(["[B/1/1] 若 A 则 置位 M（保持为 true）", "[B/1/2] 若 R 则 复位 M（保持为 false）"], Lines(b));
    }

    [Fact]
    public void ComparisonWithConfigurationConstantSelectsSensors()
    {
        // (S1 AND N==1) OR (S1 AND S2 AND N==2) → IWP，与 FB3100 按传感器个数选择到位条件同构
        var b = new FlgNetBuilder();
        var c1 = b.Contact("S1"); b.Wire(Rail(), Pin(c1, "in"));
        var e1 = b.Part("Eq"); b.Wire(Pin(c1, "out"), Pin(e1, "pre"));
        b.Wire(Id(b.Access("N")), Pin(e1, "in1")); b.Wire(Id(b.Constant("1")), Pin(e1, "in2"));
        var c2 = b.Contact("S1"); b.Wire(Rail(), Pin(c2, "in"));
        var c3 = b.Contact("S2"); b.Wire(Pin(c2, "out"), Pin(c3, "in"));
        var e2 = b.Part("Eq"); b.Wire(Pin(c3, "out"), Pin(e2, "pre"));
        b.Wire(Id(b.Access("N")), Pin(e2, "in1")); b.Wire(Id(b.Constant("2")), Pin(e2, "in2"));
        var or = b.Part("O", 2); b.Wire(Pin(e1, "out"), Pin(or, "in1")); b.Wire(Pin(e2, "out"), Pin(or, "in2"));
        b.Coil("Coil", "IWP", Pin(or, "out"));
        Assert.Equal(["[B/1/1] IWP := ((S1 AND (N == 1)) OR (S1 AND S2 AND (N == 2)))"], Lines(b));
    }

    [Fact]
    public void AccumulatingTimerBecomesGuardedAssignmentAndComparison()
    {
        // 与 FB3100 第 13/14 网络同构：时钟上升沿且正在动作且未到上限时累加 0.1；累计 ≥ 预设即报警
        var b = new FlgNetBuilder();
        var edge = b.Part("PContact");
        b.Wire(Rail(), Pin(edge, "pre"));
        b.Wire(Id(b.Access("Clk")), Pin(edge, "operand"));
        b.Wire(Id(b.Access("Mem")), Pin(edge, "bit"));
        var moving = b.Contact("Moving"); b.Wire(Pin(edge, "out"), Pin(moving, "in"));
        var le = b.Part("Le"); b.Wire(Pin(moving, "out"), Pin(le, "pre"));
        b.Wire(Id(b.Access("T")), Pin(le, "in1")); b.Wire(Id(b.Access("P")), Pin(le, "in2"));
        var add = b.Part("Add", 2); b.Wire(Pin(le, "out"), Pin(add, "en"));
        b.Wire(Id(b.Access("T")), Pin(add, "in1")); b.Wire(Id(b.Constant("0.1")), Pin(add, "in2"));
        b.Wire(Pin(add, "out"), Id(b.Access("T")));
        var ge = b.Part("Ge"); b.Wire(Rail(), Pin(ge, "pre"));
        b.Wire(Id(b.Access("T")), Pin(ge, "in1")); b.Wire(Id(b.Access("P")), Pin(ge, "in2"));
        b.Coil("Coil", "Err", Pin(ge, "out"));
        Assert.Equal([
            "[B/1/1] Err := (T >= P)",
            "[B/1/2] 若 (上升沿(Clk) AND Moving AND (T <= P)) 则 T := (T + 0.1)"], Lines(b));
    }

    [Fact]
    public void TimerRendersInstanceAndPorts()
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var ton = b.InstancePart("TON", "Tmr");
        b.Wire(Pin(a, "out"), Pin(ton, "IN")); b.Wire(Id(b.Constant("T#2S")), Pin(ton, "PT"));
        b.Coil("Coil", "Y", Pin(ton, "Q"));
        Assert.Equal(["[B/1/1] Y := TON[Tmr](IN=A, PT=T#2S).Q"], Lines(b));
    }

    [Fact]
    public void UnknownPartIsExplicitAndRecorded()
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var foo = b.Part("FooBox"); b.Wire(Pin(a, "out"), Pin(foo, "in1"));
        b.Coil("Coil", "Y", Pin(foo, "out"));
        var (statements, _, unknown) = FlgNetTranslator.Translate(XElement.Parse(b.Build()), "B/1");
        Assert.Equal("[B/1/1] Y := 〔指令 FooBox(in1=A)〕.out", IrText.Statement(statements[0]));
        Assert.Equal(["FooBox"], unknown);
    }

    [Fact]
    public void UnwiredCoilInputIsNeverRenderedAsAlwaysTrue()
    {
        var b = new FlgNetBuilder();
        var coil = b.Part("Coil"); b.Wire(Id(b.Access("Y")), Pin(coil, "operand"));
        var line = Assert.Single(Lines(b));
        Assert.Equal("[B/1/1] Y := 〔未接线〕", line);
        Assert.DoesNotContain("始终成立", line);
        Assert.DoesNotContain("无输入连接", line);
    }

    // ── 修复轮 1 ──

    [Fact]
    public void TimerWithUnconnectedOutputBecomesBoxStatement()
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var ton = b.InstancePart("TON", "Tmr");
        b.Wire(Pin(a, "out"), Pin(ton, "IN")); b.Wire(Id(b.Constant("T#2S")), Pin(ton, "PT"));
        Assert.Equal(["[B/1/1] 执行 TON[Tmr](IN=A, PT=T#2S).Q（输出未直接接线，结果可能在别处读取）"], Lines(b));
    }

    [Fact]
    public void UnknownPartWithUnconnectedOutputIsBoxAndRecorded()
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var set = b.Part("BitSet"); b.Wire(Pin(a, "out"), Pin(set, "in"));
        var (statements, _, unknown) = FlgNetTranslator.Translate(XElement.Parse(b.Build()), "B/1");
        var box = Assert.Single(statements);
        Assert.Equal(IrStatementKinds.Box, box.Kind);
        Assert.Equal("[B/1/1] 执行 〔指令 BitSet(in=A)〕.out（输出未直接接线，结果可能在别处读取）", IrText.Statement(box));
        Assert.Equal(["BitSet"], unknown);
    }

    [Fact]
    public void NamedConstantRendersItsName()
    {
        var b = new FlgNetBuilder();
        var eq = b.Part("Eq"); b.Wire(Rail(), Pin(eq, "pre"));
        b.Wire(Id(b.Access("N")), Pin(eq, "in1")); b.Wire(Id(b.NamedConstant("NUM_SENSORS")), Pin(eq, "in2"));
        b.Coil("Coil", "Y", Pin(eq, "out"));
        Assert.Equal(["[B/1/1] Y := (N == NUM_SENSORS)"], Lines(b));
    }

    [Fact]
    public void NegatedCoilKeepsPlainTargetAndNegatesValue()
    {
        var b = new FlgNetBuilder();
        var coil = b.Series("Coil", "X", ("A", false));
        b.Negate(coil, "operand");
        Assert.Equal(["[B/1/1] X := NOT(A)"], Lines(b));
    }

    [Fact]
    public void NegatedSetCoilKeepsPlainTarget()
    {
        var b = new FlgNetBuilder();
        var coil = b.Series("SCoil", "M", ("A", false));
        b.Negate(coil, "operand");
        Assert.Equal(["[B/1/1] 若 A 则 置位 M（保持为 true）"], Lines(b));
    }

    [Fact]
    public void NegatedBoxOutputIsApplied()
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var c = b.Contact("B"); b.Wire(Rail(), Pin(c, "in"));
        var and = b.Part("A", 2); b.Wire(Pin(a, "out"), Pin(and, "in1")); b.Wire(Pin(c, "out"), Pin(and, "in2"));
        b.Negate(and, "out");
        b.Coil("Coil", "Y", Pin(and, "out"));
        Assert.Equal(["[B/1/1] Y := NOT((A AND B))"], Lines(b));
    }

    [Fact]
    public void NegatedCallOutputPinKeepsPlainTargetAndIsMarked()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("FbX", "Inst", ("Q1", "Output", "Bool"));
        b.Wire(Pin(call, "Q1"), Id(b.Access("Y")));
        b.Negate(call, "Q1");
        var (statements, _, _) = FlgNetTranslator.Translate(XElement.Parse(b.Build()), "B/1");
        var pin = Assert.Single(Assert.Single(statements).Call!.Pins);
        Assert.Equal(["Y"], pin.Targets);
        Assert.True(pin.Negated);
        Assert.Contains("  - 输出 Q1 (Bool) → Y（取反后写入）", IrText.Statement(statements[0]).Split('\n'));
    }

    [Fact]
    public void CallOutputFeedingLogicIsNotReportedAsUnwired()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("FbX", "Inst", ("Q1", "Output", "Bool"));
        b.Coil("Coil", "Y", Pin(call, "Q1"));
        var (statements, _, _) = FlgNetTranslator.Translate(XElement.Parse(b.Build()), "B/1");
        Assert.Equal(2, statements.Count);
        Assert.True(Assert.Single(statements[0].Call!.Pins).FeedsLogic);
        Assert.Contains("  - 输出 Q1 (Bool) → 接至本网络后续逻辑", IrText.Statement(statements[0]).Split('\n'));
        Assert.Equal("[B/1/2] Y := FbX[Inst].Q1", IrText.Statement(statements[1]));
    }

    [Fact]
    public void NegatedCallOutputFeedingLogicIsNegatedInValue()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("FbX", "Inst", ("Q1", "Output", "Bool"));
        b.Negate(call, "Q1");
        b.Coil("Coil", "Y", Pin(call, "Q1"));
        Assert.Equal("[B/1/2] Y := NOT(FbX[Inst].Q1)", Lines(b)[1]);
    }

    [Fact]
    public void CallOutputWrittenAndFeedingLogicListsBoth()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("FbX", "Inst", ("Q1", "Output", "Bool"));
        var coil = b.Part("Coil"); b.Wire(Id(b.Access("Z")), Pin(coil, "operand"));
        b.Wire(Pin(call, "Q1"), Id(b.Access("Y")), Pin(coil, "in"));
        var (statements, _, _) = FlgNetTranslator.Translate(XElement.Parse(b.Build()), "B/1");
        Assert.Contains("  - 输出 Q1 (Bool) → Y；并接至本网络后续逻辑", IrText.Statement(statements[0]).Split('\n'));
    }
}
