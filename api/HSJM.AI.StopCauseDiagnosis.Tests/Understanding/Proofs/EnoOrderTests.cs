#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>Task 16 迭代 1：LAD 里挂在调用盒 ENO 之后的拷贝结构上在调用之后执行——实例输出读取按本次调用展开，
/// 同步判定接受两条拷贝里同一调用的 ENO 原子。</summary>
public class EnoOrderTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("Q3", "%Q0.2", "Bool"),
        new("I_X", "%I0.2", "Bool"), new("I_Y", "%I0.3", "Bool"),
    ];

    /// <summary>FB_L：O1 := X ∧ ¬Y；O2 := Y。</summary>
    static PipelineUnit Callee()
    {
        var b = new FlgNetBuilder();
        b.Series("Coil", "O1", ("X", false), ("Y", true));
        b.Series("Coil", "O2", ("Y", false));
        return new(10, "FB_L", 110, "LAD", b.Build(), "", [], 1, FlgNetBuilder.Interface(["X", "Y"], ["O1", "O2"]), "FB");
    }

    /// <summary>FB_C 网络：调用 FB_L（EN 未接线，实例 Inst，输出不接线）；Q1/Q3 := ENO ∧ Inst.O1、Q2 := ENO ∧ Inst.O2
    /// （afterEno=false 时这三个触点直接接左母线，不经过盒子）。extraWrite：另有一条线圈写 Inst.O1。</summary>
    static PipelineUnit Caller(bool afterEno, bool extraWrite = false)
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("FB_L", "Inst", ("X", "Input", "Bool"), ("Y", "Input", "Bool"), ("O1", "Output", "Bool"), ("O2", "Output", "Bool"));
        b.Wire(Id(b.Access("I_X", "GlobalVariable")), Pin(call, "X"));
        b.Wire(Id(b.Access("I_Y", "GlobalVariable")), Pin(call, "Y"));
        var o1 = b.Contact("Inst.O1", scope: "LocalVariable");
        var o2 = b.Contact("Inst.O2", scope: "LocalVariable");
        var o1b = b.Contact("Inst.O1", scope: "LocalVariable");
        if (afterEno) b.Wire(Pin(call, "eno"), Pin(o1, "in"), Pin(o2, "in"), Pin(o1b, "in"));
        else b.Wire(Rail(), Pin(o1, "in"), Pin(o2, "in"), Pin(o1b, "in"));
        b.Coil("Coil", "Q1", Pin(o1, "out"));
        b.Coil("Coil", "Q2", Pin(o2, "out"));
        b.Coil("Coil", "Q3", Pin(o1b, "out"));
        if (extraWrite) b.Series("Coil", "Inst.O1", ("I_X", false));
        return new(11, "FB_C", 120, "LAD", b.Build(), "", ["FB_L"], 1, "", "FB");
    }

    static (NetworkProofs Proofs, IrUnit Unit, IrBlock Block) Setup(PipelineUnit caller)
    {
        var source = new PipelineSnapshot(1, [Callee(), caller], Tags);
        var irs = source.Units.Select(u => UnitTranslator.Translate(u, Tags)).ToArray();
        var blocks = new Dictionary<string, IrBlock>(StringComparer.OrdinalIgnoreCase);
        IrBlock Block(string n) => blocks.TryGetValue(n, out var x) ? x : blocks[n] = IrBlock.Load(source, n);
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, irs);
        var proofs = new NetworkProofs(Tags, q.Count, q.HasUnresolved, new ProjectWrites(irs, Tags), n => Block(n), _ => false);
        return (proofs, irs[1], Block("FB_C"));
    }

    static string[] Show(IEnumerable<ProvenRelation> rs) => rs.Select(r => $"{r.Operator}|{string.Join("&", r.Members)}|{r.Proof}").Order().ToArray();

    [Fact]
    public void TranslatorRecordsTheCallPartUidMatchingTheEnoAtom()
    {
        var ir = UnitTranslator.Translate(Caller(afterEno: true), Tags);
        var call = ir.Statements.Single(s => s.Call is not null).Call!;
        var q1 = ir.Statements.Single(s => s.Target == "Q1");
        var atom = q1.Value!.Args!.Single(a => a.Kind == IrKinds.Unknown);
        Assert.Equal(("FB_L", call.PartUid), FlgNetTranslator.EnoPartUid(atom));
    }

    [Fact]
    public void CopiesAfterTheCallEnoAreExpandedAndProven()
    {
        var (proofs, unit, block) = Setup(Caller(afterEno: true));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network", "AT_MOST_ONE|Q2&Q3|Code:Network", "SAME_STATE|Q1&Q3|Code:Network"],
            Show(proofs.Prove(unit, block)));
    }

    [Fact]
    public void CopiesNotWiredThroughTheBoxAreIndependentReads()   // 裁定 F1：没有结构证据时读取各自独立
    {
        var (proofs, unit, block) = Setup(Caller(afterEno: false));
        Assert.Empty(proofs.Prove(unit, block));
    }

    [Fact]
    public void AnotherWriterOfTheInstanceInTheSegmentBlocksTheExpansion()
    {
        var (proofs, unit, block) = Setup(Caller(afterEno: true, extraWrite: true));
        Assert.Empty(proofs.Prove(unit, block));
    }
}
