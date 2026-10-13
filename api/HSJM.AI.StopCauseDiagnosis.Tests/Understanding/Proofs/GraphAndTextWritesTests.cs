#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>Task 16 迭代 1（P1~P4、顺控写入口径）：代码证明在真实项目上被这些写入/提及判定整体挡掉。</summary>
public class GraphAndTextWritesTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrExpr And(params IrExpr[] xs) => IrExpr.And(xs);
    static IrStatement A(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);
    static IrStatement Cond(string id, string label, IrExpr value) => new(id, IrStatementKinds.Condition, label, IrExpr.True, value);
    static IrStatement Text(string id, string kind, string text) => new(id, kind, null, IrExpr.True, null, null, text);
    static IrUnit U(long id, string block, params IrStatement[] s) => new(id, block, 1, "LAD", "FlgNet", s, [], []);
    static IrUnit Unit(long id, string block, string language, string root, params IrStatement[] s) => new(id, block, 1, language, root, s, [], []);
    static IrBlock B(string name, params IrUnit[] units) => IrBlock.FromUnits(name, units, []);
    static readonly Func<string, bool> NotFc = _ => false;

    static readonly PipelineTag[] Io =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("A", "%I0.0", "Bool"), new("B", "%I0.1", "Bool"),
        new("I5", "%I5.0", "Bool"), new("K", "%M1.3", "Bool"),
    ];

    static NetworkProofs Proofs(IReadOnlyList<IrUnit> all, Func<IrUnit, bool>? isFcUnit = null)
    {
        var q = new UnderstandingRuleSource.QWriteIndex(Io, all);
        return new NetworkProofs(Io, q.Count, q.HasUnresolved, new ProjectWrites(all, Io, isFcUnit), _ => null, NotFc);
    }

    static string[] Show(IEnumerable<ProvenRelation> rs) => rs.Select(r => $"{r.Operator}|{string.Join("&", r.Members)}|{r.Proof}").ToArray();

    /// <summary>Q1 := M ∧ A；Q2 := ¬M ∧ B（M 别处写入，本段不写时可证互斥）。</summary>
    static IrStatement[] Cross(string block, string m) =>
        [A($"{block}/1/1", "Q1", And(V(m), V("A"))), A($"{block}/1/2", "Q2", And(IrExpr.Not(V(m)), V("B")))];

    static IrUnit Writer(long id, string name) => U(id, "FB_W" + id, A($"FB_W{id}/1/1", name, V("B")));

    // ───────────── P1：顺控条件不是写入 ─────────────

    [Fact]
    public void TranslatorEmitsTransitionInterlockAndSupervisionAsConditions()
    {
        var b = new FlgNetBuilder();
        var tr = b.Part("TrCoil");
        var chain = b.Chain(Rail(), ("A", false));
        b.Wire(Pin(chain, "out"), Pin(tr, "in"));
        var ir = UnitTranslator.Translate(new PipelineUnit(1, "Blk", 7, "GRAPH", "<Transition Name=\"T1\">" + b.Build() + "</Transition>", "", [], 3));
        var s = Assert.Single(ir.Statements);
        Assert.Equal(IrStatementKinds.Condition, s.Kind);
        Assert.Equal("[Blk/3/1] 〔转移条件〕 := A", IrText.Statement(s));   // 文字不变（进缓存键）
    }

    [Fact]
    public void ConditionStatementsAreNotWrites()
    {
        var unit = U(1, "FB1", Cond("FB1/1/1", "〔转移 T1 条件〕", V("A")), Cond("FB1/1/2", "〔互锁条件〕", V("B")));
        var writes = new ProjectWrites([unit], Io);
        Assert.Empty(writes.Writers("Q1"));
        Assert.Empty(writes.Writers("DB.X"));
        Assert.Equal(0, writes.UncertainWriteCounts[ProjectWrites.UncertainKinds.AnyArea]);
        var block = B("FB1", unit);
        Assert.Empty(block.UncertainWriters("Q1"));
        Assert.Empty(block.WriteTargets);
    }

    [Fact]
    public void TransitionConditionsElsewhereNoLongerBlockAProof()
    {
        var unit = U(1, "OB1", Cross("OB1", "DB.M"));
        var graph = Unit(2, "FB_G", "GRAPH", "Graph", Cond("FB_G/1/T1/1", "〔转移 T1 条件〕", V("A")));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs([unit, graph, Writer(3, "DB.M")]).Prove(unit, B("OB1", unit))));
    }

    // ───────────── P2：符号名里的空白 ─────────────

    [Fact]
    public void SymbolWithSpacesIsAnOrdinarySymbol()
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "DB10053_Panel.PB[1].PB3_CYCLE START", V("A")))], Io);
        Assert.Empty(writes.Writers("Q1"));
        Assert.Single(writes.Writers("DB10053_Panel.PB[1].PB3_CYCLE START"));
        Assert.False(writes.HasUncertainWriter("DB10053_Panel.PB[1].PB3_CYCLE START"));
        Assert.Equal(0, writes.UncertainWriteCounts[ProjectWrites.UncertainKinds.AnyArea]);
    }

    [Fact]
    public void UnrecognizedAccessIsStillAnUnknownTarget()
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "〔未识别的访问 Address〕", V("A")))], Io);
        Assert.True(writes.HasUncertainWriter("Q1"));
        Assert.Equal(1, writes.UncertainWriteCounts[ProjectWrites.UncertainKinds.AnyArea]);
    }

    [Fact]
    public void CallInstanceAndBlockTargetsWithSpacesDoNotWriteEverything()
    {
        var unit = U(1, "OB1", [.. Cross("OB1", "DB.M"),
            new IrStatement("OB1/1/3", IrStatementKinds.Call, null, IrExpr.True, null,
                new IrCall("FB_X", "FB", "Rata Data[1]", IrExpr.True, [new IrPin("Out", "Output", "Bool", null, ["CommError _PLC"])]))]);
        var block = B("OB1", unit);
        Assert.Empty(block.UncertainWriters("Q1"));
        Assert.Empty(new ProjectWrites([unit], Io).UncertainWriters("Q1"));
    }

    [Fact]
    public void CoilWiredFromAPartOutputIsAnUnknownTarget()
    {
        var b = new FlgNetBuilder();
        var chain = b.Chain(Rail(), ("A", false), ("B", false));
        var coil = b.Part("Coil");
        b.Wire(Pin(chain, "out"), Pin(coil, "operand"));
        var ir = UnitTranslator.Translate(new PipelineUnit(1, "Blk", 7, "LAD", b.Build(), "", [], 3));
        var s = ir.Statements.Single(x => x.Kind == IrStatementKinds.Assign);
        Assert.StartsWith("〔", s.Target);
        Assert.True(new ProjectWrites([ir], Io).HasUncertainWriter("Q1"));
    }

    // ───────────── P3/P4：文字里的间接寻址 ─────────────

    [Theory]
    [InlineData("L QB[AR1,P#0.0]\nT #X", false)]           // 读指令
    [InlineData("A Q[AR1,P#0.0]\n= #Y", false)]
    [InlineData("L #X\nT QB[AR1,P#0.0]", true)]            // 写指令
    [InlineData("= Q[AR1,P#0.0]", true)]
    [InlineData("L 1\nT B[AR1,P#0.0]", true)]              // 跨区间接写：可能写到任何 I/Q/M
    [InlineData("L B[AR1,P#0.0]", false)]
    public void SourceTextIndirectAddressingCountsOnlyAsAWriteOperand(string stl, bool mentioned)
    {
        var unit = Unit(1, "FB1", "STL", "StatementList", Text("FB1/1/1", IrStatementKinds.Source, stl));
        Assert.Equal(mentioned, new ProjectWrites([unit], Io).MentionedInText("Q1"));
    }

    [Fact]
    public void ActionTextStillCountsAnyIndirectMention()
    {
        var unit = Unit(1, "FB1", "GRAPH", "Graph", Text("FB1/1/S1A1", IrStatementKinds.Action, "L QB[AR1,P#0.0]"));
        Assert.True(new ProjectWrites([unit], Io).MentionedInText("Q1"));
    }

    [Theory]
    [InlineData("FOR i[1] := 0 TO 19 DO\n  x := i[1];\nEND_FOR;")]
    [InlineData("I[2] := TRUE;")]
    [InlineData("T I[3]")]
    public void IntegerSubscriptIsNotIndirectAddressing(string text)
    {
        var unit = Unit(1, "FB1", "SCL", "StructuredText", Text("FB1/1/1", IrStatementKinds.Source, text));
        Assert.False(new ProjectWrites([unit], Io).MentionedInText("I5"));
    }

    [Fact]
    public void MemoryIndirectWriteStillCounts()
    {
        var unit = Unit(1, "FB1", "STL", "StatementList", Text("FB1/1/1", IrStatementKinds.Source, "L 1\nT IB[MD 10]"));
        Assert.True(new ProjectWrites([unit], Io).MentionedInText("I5"));
    }

    [Fact]
    public void FcTextIndirectWritesAreNotCountedByArea()
    {
        var fc = Unit(1, "FC1", "STL", "StatementList", Text("FC1/1/1", IrStatementKinds.Source, "L #X\nT QB[AR1,P#0.0]\nT B[AR1,P#2.0]\nL P#Q 1.0 BYTE 2"));
        Assert.True(new ProjectWrites([fc], Io).MentionedInText("Q1"));
        var withFc = new ProjectWrites([fc], Io, u => u.BlockName == "FC1");
        Assert.False(withFc.MentionedInText("Q1"));
        Assert.False(withFc.MentionedInText("K"));
        // 绝对地址与名字照常计
        var named = Unit(2, "FC2", "STL", "StatementList", Text("FC2/1/1", IrStatementKinds.Source, "= Q 0.0\nT Q2"));
        var fcNamed = new ProjectWrites([named], Io, _ => true);
        Assert.True(fcNamed.MentionedInText("Q1"));
        Assert.True(fcNamed.MentionedInText("Q2"));
    }

    // ───────────── 顺控结构语句与动作 ─────────────

    static IrUnit GraphUnit(params IrStatement[] extra) => Unit(1, "FB_G", "GRAPH", "Graph",
    [
        .. Cross("FB_G", "DB.M"),
        new("FB_G/1/S1", IrStatementKinds.Step, null, IrExpr.True, null, null, "步 1 Init"),
        new("FB_G/1/T1", IrStatementKinds.Transition, null, IrExpr.True, null, null, "转移 1 T1"),
        Cond("FB_G/1/T1/1", "〔转移 T1 条件〕", V("A")),
        new("FB_G/1/B1", IrStatementKinds.Branch, null, IrExpr.True, null, null, "分支 1（AltBegin）"),
        new("FB_G/1/C1", IrStatementKinds.Connection, null, IrExpr.True, null, null, "连接：步 1 → 转移 1"),
        .. extra
    ]);

    [Fact]
    public void GraphStructureStatementsDoNotWriteUserVariables()
    {
        var unit = GraphUnit(Text("FB_G/1/S1A1", IrStatementKinds.Action, "\"DB2\".Other := TRUE;"));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs([unit, Writer(2, "DB.M")]).Prove(unit, B("FB_G", unit))));
    }

    [Fact]
    public void ActionMentioningTheVariableMayWriteIt()
    {
        var unit = GraphUnit(Text("FB_G/1/S1A1", IrStatementKinds.Action, "DB.M := TRUE;"));
        Assert.Empty(Proofs([unit, Writer(2, "DB.M")]).Prove(unit, B("FB_G", unit)));
    }

    [Fact]
    public void ActionWritingAnOutputThroughItsAddressMayWriteIt()
    {
        var unit = GraphUnit(Text("FB_G/1/S1A1", IrStatementKinds.Action, "Q 0.0"));
        Assert.Empty(Proofs([unit, Writer(2, "DB.M")]).Prove(unit, B("FB_G", unit)));
    }

    [Fact]
    public void ActionCallingABlockMayWriteAnything()
    {
        var call = new IrStatement("FB_G/1/S1A1", IrStatementKinds.Action, null, IrExpr.True, null, null, "FC_Write();") { TextCallsBlock = true };
        var unit = GraphUnit(call);
        Assert.Empty(Proofs([unit, Writer(2, "DB.M")]).Prove(unit, B("FB_G", unit)));
    }

    [Fact]
    public void TranslatorMarksActionsThatCallABlock()
    {
        var raw = "<Actions><Action Qualifier=\"N\"><Access Scope=\"Call\"><CallInfo Name=\"FC1\" BlockType=\"FC\" /></Access></Action>"
            + "<Action Qualifier=\"N\"><Token Text=\"X\" /></Action></Actions>";
        var ir = UnitTranslator.Translate(new PipelineUnit(1, "Blk", 7, "GRAPH", raw, "", [], 3));
        Assert.Equal([true, false], ir.Statements.Select(s => s.TextCallsBlock).ToArray());
    }
}
