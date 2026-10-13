#nullable enable
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>指令盒（非 FB/FC 调用的部件）在执行时改写的变量：InOut 端口、背景实例、边沿存储位，
/// 都要被 IrBlock 与 ProjectWrites 计为写入。</summary>
public class InstructionBoxWritesTests
{
    static IrUnit Unit(FlgNetBuilder b)
    {
        var result = FlgNetTranslator.TranslateFull(XElement.Parse(b.Build()), "B/1");
        return new IrUnit(1, "B", 1, "LAD", "FlgNet", result.Statements, result.Nodes, result.UnknownParts);
    }

    static (IrBlock Block, ProjectWrites Writes, IrUnit Unit) Translate(FlgNetBuilder b)
    {
        var unit = Unit(b);
        return (IrBlock.FromUnits("B", [unit], []), new ProjectWrites([unit], []), unit);
    }

    [Fact]
    public void InOutPortOfAnUnmodelledInstructionIsAWrite()
    {
        // STRG_VAL：IN 读字符串，P（InOut，读写位置）接 Pos，OUT 接 Val
        var b = new FlgNetBuilder();
        var box = b.Part("STRG_VAL");
        b.Wire(Rail(), Pin(box, "en"));
        b.Wire(Id(b.Access("Text")), Pin(box, "IN"));
        b.Wire(Id(b.Access("Pos")), Pin(box, "P"));
        b.Wire(Pin(box, "OUT"), Id(b.Access("Val")));
        var (block, writes, _) = Translate(b);
        Assert.True(block.IsWritten("Pos"));
        Assert.NotEmpty(writes.Writers("Pos"));
        Assert.True(block.IsWritten("Val"));
        Assert.NotEmpty(writes.Writers("Val"));
    }

    [Fact]
    public void TimerInstanceIsWrittenAsAWhole()
    {
        // ─┤A├─[TON T]─( Q1 )：TON 每次执行都更新实例 T（Q、ET 等成员）
        var b = new FlgNetBuilder();
        var contact = b.Chain(Rail(), ("A", false));
        var timer = b.InstancePart("TON", "T");
        b.Wire(Pin(contact, "out"), Pin(timer, "IN"));
        b.Wire(Id(b.Constant("T#1s", "TypedConstant")), Pin(timer, "PT"));
        b.Coil("Coil", "Q1", Pin(timer, "Q"));
        var (block, writes, unit) = Translate(b);
        Assert.True(block.IsWritten("T.Q"));
        Assert.True(block.IsWritten("T"));
        Assert.NotEmpty(writes.Writers("T.Q"));
        // 写入记在求值该定时器的语句（Q1 的线圈）上
        var q1 = unit.Statements.Single(s => s.Target == "Q1");
        Assert.Contains(writes.Writers("T"), w => w.StatementId == q1.Id);
        Assert.False(block.IsWritten("A"));
    }

    [Fact]
    public void TimerWithADanglingOutputStillWritesItsInstance()
    {
        // TON 的 Q 不接线（在别处读 T.Q）：翻译为「执行」语句，实例照样计为写入
        var b = new FlgNetBuilder();
        var contact = b.Chain(Rail(), ("A", false));
        var timer = b.InstancePart("TON", "T");
        b.Wire(Pin(contact, "out"), Pin(timer, "IN"));
        var (block, writes, _) = Translate(b);
        Assert.True(block.IsWritten("T.Q"));
        Assert.NotEmpty(writes.Writers("T.ET"));
    }

    [Fact]
    public void EdgeMemoryBitIsAWrite()
    {
        // ─┤P X├─( Q2 )，边沿存储位 EdgeMem 接在 bit 端口
        var b = new FlgNetBuilder();
        var edge = b.Part("PContact");
        b.Wire(Rail(), Pin(edge, "in"));
        b.Wire(Id(b.Access("X")), Pin(edge, "operand"));
        b.Wire(Id(b.Access("EdgeMem")), Pin(edge, "bit"));
        b.Coil("Coil", "Q2", Pin(edge, "out"));
        var (block, writes, _) = Translate(b);
        Assert.True(block.IsWritten("EdgeMem"));
        Assert.NotEmpty(writes.Writers("EdgeMem"));
        Assert.False(block.IsWritten("X"));
        Assert.Empty(writes.Writers("X"));
    }

    [Fact]
    public void EdgeCoilMemoryBitIsAWrite()
    {
        var b = new FlgNetBuilder();
        var coil = b.Coil("PCoil", "Pulse", Pin(b.Chain(Rail(), ("A", false)), "out"));
        b.Wire(Id(b.Access("EdgeMem")), Pin(coil, "bit"));
        var (block, writes, _) = Translate(b);
        Assert.True(block.IsWritten("EdgeMem"));
        Assert.NotEmpty(writes.Writers("EdgeMem"));
    }

    [Fact]
    public void InputsOfModelledPureInstructionsAreNotWrites()
    {
        // Move：in 只读；Add：in1/in2 只读，out 写 Sum
        var b = new FlgNetBuilder();
        var move = b.Part("Move");
        b.Wire(Rail(), Pin(move, "en"));
        b.Wire(Id(b.Access("Src")), Pin(move, "in"));
        b.Wire(Pin(move, "out1"), Id(b.Access("Dst")));
        var add = b.Part("Add", 2);
        b.Wire(Id(b.Access("L")), Pin(add, "in1"));
        b.Wire(Id(b.Access("R")), Pin(add, "in2"));
        b.Wire(Pin(add, "out"), Id(b.Access("Sum")));
        var (block, writes, _) = Translate(b);
        foreach (var read in new[] { "Src", "L", "R" })
        {
            Assert.False(block.IsWritten(read));
            Assert.Empty(writes.Writers(read));
        }
        Assert.True(block.IsWritten("Dst"));
        Assert.True(block.IsWritten("Sum"));
    }

    [Fact]
    public void SideWritesDoNotChangeTheRenderedText()
    {
        var b = new FlgNetBuilder();
        var contact = b.Chain(Rail(), ("A", false));
        var timer = b.InstancePart("TON", "T");
        b.Wire(Pin(contact, "out"), Pin(timer, "IN"));
        b.Coil("Coil", "Q1", Pin(timer, "Q"));
        var unit = Unit(b);
        var bare = unit with { Statements = unit.Statements.Select(s => s with { SideWrites = null }).ToArray() };
        Assert.Contains(unit.Statements, s => s.SideWrites is { Count: > 0 });
        Assert.Equal(IrText.Unit(bare), IrText.Unit(unit));
    }
}
