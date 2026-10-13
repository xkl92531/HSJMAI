#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>K5：跨块执行顺序的同步（PLC2 FC10050 先调 FB10060_Mode 写 %Q140.3、再调 FB10061_LightStack 拷到 %Q142.6 的结构）。</summary>
public class CallOrderProofTests
{
    static readonly PipelineTag[] Tags =
    [
        new("QS", "%Q0.0", "Bool"), new("QT", "%Q0.1", "Bool"), new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool")
    ];

    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement A(string id, string target, IrExpr value, IrExpr? guard = null) =>
        new(id, IrStatementKinds.Assign, target, guard ?? IrExpr.True, value);
    static IrStatement Call(string id, string callee, string instance, IrExpr? en = null, IrExpr? guard = null) =>
        new(id, IrStatementKinds.Call, null, guard ?? IrExpr.True, null, new IrCall(callee, "FB", instance, en ?? IrExpr.Unwired(), []));
    static IrUnit U(long id, string block, int network, params IrStatement[] s) => new(id, block, network, "LAD", "FlgNet", s, [], []);
    static IrUnit Scl(long id, string block, int network, params IrStatement[] s) => new(id, block, network, "SCL", "StructuredText", s, [], []);

    static IrUnit WriterA() => U(1, "FB_A", 1, A("FB_A/1/1", "QS", V("I1")));
    static IrUnit CopyB(params IrStatement[] extra) => U(2, "FB_B", 1, [A("FB_B/1/1", "QT", V("QS")), .. extra]);
    static IrUnit CallsA(string caller = "FC_M", int network = 1) => U(3, caller, network, Call(caller + "/" + network + "/1", "FB_A", "IDB_A"));
    static IrUnit CallsB(string caller = "FC_M", int network = 2) => U(4, caller, network, Call(caller + "/" + network + "/1", "FB_B", "IDB_B"));

    static IReadOnlyList<ProvenRelation> Prove(IReadOnlyList<IrUnit> units, Func<string, bool>? isFc = null, long unit = 2)
    {
        isFc ??= n => n.StartsWith("FC", StringComparison.Ordinal);
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, units.Where(u => !isFc(u.BlockName)));
        var proof = new CallOrderProof(units, Tags, new ProjectWrites(units, Tags), q.Count, q.HasUnresolved, isFc);
        return proof.Prove(units.Single(u => u.UnitId == unit));
    }

    static string[] Show(IEnumerable<ProvenRelation> rs) => rs.Select(r => $"{r.Operator}|{string.Join("&", r.Members)}|{r.Proof}").ToArray();

    static readonly string[] Expected = ["SAME_STATE|QS&QT|Code:CallOrder"];

    [Fact]
    public void CopyAfterTheWritingBlockIsCalledProvesSameState()
    {
        var r = Assert.Single(Prove([WriterA(), CopyB(), CallsA("OB1"), CallsB("OB1")], _ => false));
        Assert.Equal(Expected, Show([r]));
        Assert.Contains("FB_B/1/1", r.Evidence);
        Assert.Contains("FB_A/1/1", r.Evidence);
    }

    [Fact]
    public void CallerThatIsAnFcStillProves()   // 只用 FC 里的调用顺序（用户 2026-10-02）
    {
        Assert.Equal(Expected, Show(Prove([WriterA(), CopyB(), CallsA(), CallsB()])));
    }

    [Fact]
    public void TheWritingSegmentItselfProducesNothing()
    {
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB()], unit: 1));
    }

    [Fact]
    public void WritingBlockCalledTwiceProducesNothing()
    {
        var again = U(5, "OB1", 1, Call("OB1/1/1", "FB_A", "IDB_A2"));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), again]));
    }

    [Fact]
    public void CopyingBlockCalledTwiceProducesNothing()
    {
        var again = U(5, "OB1", 1, Call("OB1/1/1", "FB_B", "IDB_B2"));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), again]));
    }

    [Theory]
    [InlineData("A-en")]
    [InlineData("A-guard")]
    [InlineData("B-en")]
    [InlineData("B-guard")]
    public void ConditionalCallProducesNothing(string which)
    {
        IrStatement Cond(string id, string callee, string inst, bool conditional, bool viaEn) =>
            Call(id, callee, inst, conditional && viaEn ? V("I2") : null, conditional && !viaEn ? V("I2") : null);
        var viaEn = which.EndsWith("en", StringComparison.Ordinal);
        var a = U(3, "FC_M", 1, Cond("FC_M/1/1", "FB_A", "IDB_A", which.StartsWith('A'), viaEn));
        var b = U(4, "FC_M", 2, Cond("FC_M/2/1", "FB_B", "IDB_B", which.StartsWith('B'), viaEn));
        Assert.Empty(Prove([WriterA(), CopyB(), a, b]));
    }

    [Fact]
    public void CopyingBlockCalledBeforeTheWritingBlockProducesNothing()
    {
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(network: 3), CallsB(network: 2)]));
    }

    [Fact]
    public void BothCallsInOneLadderSegmentAreNotOrdered()   // 裁定 F1：FlgNet 段语句顺序不可信
    {
        var both = U(3, "FC_M", 1, Call("FC_M/1/1", "FB_A", "IDB_A"), Call("FC_M/1/2", "FB_B", "IDB_B"));
        Assert.Empty(Prove([WriterA(), CopyB(), both]));
    }

    [Fact]
    public void BothCallsInOneSclSegmentFollowTheStatementOrder()
    {
        var ordered = Scl(3, "FC_M", 1, Call("FC_M/1/1", "FB_A", "IDB_A"), Call("FC_M/1/2", "FB_B", "IDB_B"));
        Assert.Equal(Expected, Show(Prove([WriterA(), CopyB(), ordered])));
        var reversed = Scl(3, "FC_M", 1, Call("FC_M/1/1", "FB_B", "IDB_B"), Call("FC_M/1/2", "FB_A", "IDB_A"));
        Assert.Empty(Prove([WriterA(), CopyB(), reversed]));
    }

    [Fact]
    public void CallsFromDifferentCallersProduceNothing()
    {
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA("FC_M"), CallsB("FC_N")]));
    }

    [Fact]
    public void SourceWithAnotherWriterProducesNothing()
    {
        var other = U(6, "FB_C", 1, A("FB_C/1/1", "QS", V("I2")));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), other]));
    }

    [Fact]
    public void SourceWrittenInAnFcProducesNothing()   // FC 写入照样计入写入索引（多算只会少产）
    {
        var fc = U(6, "FC_W", 1, A("FC_W/1/1", "QS", V("I2")));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), fc]));
    }

    [Fact]
    public void SourceMentionedInTextProducesNothing()
    {
        var text = U(6, "FB_T", 1, new IrStatement("FB_T/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "QS := TRUE;"));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), text]));
    }

    [Fact]
    public void CallingBlockNamedInTextProducesNothing()   // 文字里可能另有一次调用
    {
        var text = U(6, "FB_T", 1, new IrStatement("FB_T/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "\"IDB_A\"();"));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), text]));
    }

    [Fact]
    public void CopyInsideAConditionalBranchProducesNothing()
    {
        var b = U(2, "FB_B", 1, A("FB_B/1/1", "QT", V("QS"), guard: V("I2")));
        Assert.Empty(Prove([WriterA(), b, CallsA(), CallsB()]));
    }

    [Fact]
    public void NegatedCopyProducesNothing()
    {
        var b = U(2, "FB_B", 1, A("FB_B/1/1", "QT", IrExpr.Not(V("QS"))));
        Assert.Empty(Prove([WriterA(), b, CallsA(), CallsB()]));
    }

    [Fact]
    public void CopyingBlockWithAJumpProducesNothing()
    {
        var jump = new IrStatement("FB_B/1/2", IrStatementKinds.JumpNot, null, IrExpr.True, V("I2"), null, "L1");
        Assert.Empty(Prove([WriterA(), CopyB(jump), CallsA(), CallsB()]));
    }

    [Fact]
    public void CallerWithAReturnProducesNothing()
    {
        var ret = U(5, "FC_M", 3, new IrStatement("FC_M/3/1", IrStatementKinds.Return, null, V("I2"), null));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB(), ret]));
    }

    [Fact]
    public void WritingOrCopyingBlockThatIsAnFcProducesNothing()
    {
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB()], n => n is "FB_A" or "FC_M"));
        Assert.Empty(Prove([WriterA(), CopyB(), CallsA(), CallsB()], n => n is "FB_B" or "FC_M"));
    }

    [Fact]
    public void CopyWithinTheWritingBlockIsLeftToTheNetworkProof()
    {
        var both = U(1, "FB_A", 1, A("FB_A/1/1", "QS", V("I1")), A("FB_A/1/2", "QT", V("QS")));
        Assert.Empty(Prove([both, CallsA()], unit: 1));
    }

    [Fact]
    public void CopiedVariableThatIsNotAnOutputProducesNothing()
    {
        var a = U(1, "FB_A", 1, A("FB_A/1/1", "I2", V("I1")));
        var b = U(2, "FB_B", 1, A("FB_B/1/1", "QT", V("I2")));
        Assert.Empty(Prove([a, b, CallsA(), CallsB()]));
    }
}
