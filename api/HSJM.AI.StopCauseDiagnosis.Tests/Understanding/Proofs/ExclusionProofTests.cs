#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class ExclusionProofTests
{
    static IrExpr V(string n, int t) => IrExpr.Var(ExclusionProof.At(n, t));
    static IrExpr Eq(IrExpr x, string c) => new(IrKinds.Cmp, "==", [x, IrExpr.Const(c)]);
    static IrExpr Gt(IrExpr x, string c) => new(IrKinds.Cmp, ">", [x, IrExpr.Const(c)]);
    static readonly Func<string, bool> Certain = _ => true;
    static readonly Func<string, int, int, bool> Never = (_, _, _) => false;

    /// <summary>唯一写入者在 writtenAt 的变量：两次读都在写之前（含写入语句本身读到的旧值）或都在写之后，值相同。</summary>
    static Func<string, int, int, bool> SingleWriters(params (string Name, int WrittenAt)[] writers) => (name, t1, t2) =>
        writers.Any(w => w.Name == name && (t2 <= w.WrittenAt || t1 > w.WrittenAt));

    /// <summary>两个输出（Q 开头）写入之后到扫描结束不再被改写；其余按 inner。</summary>
    static Func<string, int, int, bool> Kept(Func<string, int, int, bool> inner) => (name, t1, t2) =>
        t2 == ExclusionProof.End && name.StartsWith('Q') || inner(name, t1, t2);

    /// <summary>语句顺序不可信的程序段：段内写入只列定义本身以外的额外写入（按名字精确比较）；其它变量段内不变、段后不被改写。</summary>
    static UnorderedSegment Unordered(params (string Name, int At)[] extraWrites) =>
        new(n => extraWrites.Where(w => string.Equals(w.Name, n, StringComparison.OrdinalIgnoreCase)).Select(w => w.At), _ => true, _ => true);

    [Fact]
    public void LaterStatementReadingTheNewValueProvesTheMutex()
    {
        var a = new TimedDef("Q2", 0, V("B", 0));
        var b = new TimedDef("Q1", 1, IrExpr.And([V("A", 1), IrExpr.Not(V("Q2", 1))]));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, SingleWriters(("Q2", 0), ("Q1", 1)), Certain));
    }

    [Fact]
    public void EarlierStatementReadingTheOldValueIsACounterexample()
    {
        var a = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), IrExpr.Not(V("Q2", 0))]));
        var b = new TimedDef("Q2", 1, V("B", 1));
        Assert.Equal(ProofVerdict.Counterexample, ExclusionProof.Mutex(a, b, SingleWriters(("Q1", 0), ("Q2", 1)), Certain));
    }

    [Fact]
    public void SameVariableReadAtTheSameTimeConflicts()
    {
        var a = new TimedDef("Q1", 5, IrExpr.And([V("X", 5), V("Y", 5)]));
        var b = new TimedDef("Q2", 5, IrExpr.Not(V("X", 5)));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Kept(Never), Certain));
    }

    [Fact]
    public void EqualToTwoDifferentConstantsConflictsOnlyWhenTheVariableIsStable()
    {
        var a = new TimedDef("Q1", 1, Eq(V("N", 1), "1"));
        var b = new TimedDef("Q2", 2, Eq(V("N", 2), "2"));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Kept((n, _, _) => n == "N"), Certain));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Kept(Never), Certain));
    }

    [Fact]
    public void SameOpaqueAtomWithOppositePolarityConflicts()
    {
        var gt = Gt(V("X", 3), "5");
        Assert.Equal(ProofVerdict.Proven,
            ExclusionProof.Mutex(new TimedDef("Q1", 3, gt), new TimedDef("Q2", 3, IrExpr.Not(gt)), Kept(Never), Certain));
    }

    [Fact]
    public void OpaqueOrUncertainLiteralsAreNeverACounterexample()
    {
        var withOpaque = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), Gt(V("X", 0), "5")]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(withOpaque, new TimedDef("Q2", 1, V("B", 1)), Never, Certain));
        var inner = new TimedDef("Q1", 0, V("Inner", 0));
        Assert.Equal(ProofVerdict.NotProven,
            ExclusionProof.Mutex(inner, new TimedDef("Q2", 1, V("B", 1)), Never, n => n != "Inner"));
    }

    [Fact]
    public void AlwaysFalseDefinitionIsNotAProof() =>
        Assert.Equal(ProofVerdict.NotProven,
            ExclusionProof.Mutex(new TimedDef("Q1", 0, IrExpr.Const("FALSE")), new TimedDef("Q2", 1, V("B", 1)), Never, Certain));

    [Fact]
    public void StampOnlyStampsUnstampedNamesAndSplitRoundTrips()
    {
        var e = ExclusionProof.Stamp(IrExpr.And([IrExpr.Var("A"), V("B", 3)]), 7);
        Assert.Equal(new[] { ("A", 7), ("B", 3) }, e.Vars().Select(ExclusionProof.Split).ToArray());
        Assert.Equal(("Plain", -1), ExclusionProof.Split("Plain"));
        Assert.Equal("(A AND B)", ExclusionProof.Unstamped(IrText.Expr(e)));
    }

    // ---- 裁定 F3/G3：规则②只用于无状态纯函数原子 ----

    static ProofVerdict OppositeAtoms(IrExpr atom) =>
        ExclusionProof.Mutex(new TimedDef("Q1", 3, atom), new TimedDef("Q2", 3, IrExpr.Not(atom)), (_, _, _) => true, Certain);

    [Fact]
    public void TwoUnwiredAtomsAreNotTheSameValue() => Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(IrExpr.Unwired()));

    [Fact]
    public void TwoEdgesWithTheSameTextAreNotTheSameValue()
    {
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Rise, null, [V("X", 3)])));
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Fall, null, [V("X", 3)])));
    }

    [Fact]
    public void StatefulOrUnrecognizedAtomsAreNotTheSameValue()
    {
        var port = new IrExpr(IrKinds.Port, "IN", [V("X", 3)]);
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Instr, "CTU[C1]|Q", [port])));
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Calc, "IN1+1", [port])));
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Counter, "CTU|C1|Q", [port])));
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Timer, "TON|T1|Q", [port])));
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(new IrExpr(IrKinds.Unknown, "接口初值:Cmd")));
        // 比较里套着边沿/未接线，同样不是纯函数
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(Gt(new IrExpr(IrKinds.Rise, null, [V("X", 3)]), "0")));
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(Gt(IrExpr.Unwired(), "0")));
        // 不认识的函数（可能是块调用）不是纯函数
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(Gt(new IrExpr(IrKinds.Func, "FC_Foo", [V("X", 3)]), "0")));
    }

    [Fact]
    public void PureComparisonsAndArithmeticStillConflict()
    {
        Assert.Equal(ProofVerdict.Proven, OppositeAtoms(Gt(new IrExpr(IrKinds.Arith, "+", [V("X", 3), V("Y", 3)]), "5")));
        Assert.Equal(ProofVerdict.Proven, OppositeAtoms(Gt(new IrExpr(IrKinds.Func, "Abs", [V("X", 3)]), "5")));
        Assert.Equal(ProofVerdict.Proven, OppositeAtoms(new IrExpr(IrKinds.Xor, null, [V("X", 3), V("Y", 3)])));
    }

    [Fact]
    public void AtomWithAnUnstampedVariableIsNotTheSameValue() =>
        Assert.Equal(ProofVerdict.NotProven, OppositeAtoms(Gt(IrExpr.Var("X"), "5")));

    [Fact]
    public void TaggedCallOutputAtomsKeepTheirIdentity()
    {
        Assert.True(ExclusionProof.IsTaggedAtom(ExclusionProof.TaggedAtom("Valve1@4", 2, "被调块内部变量 Busy")));
        Assert.False(ExclusionProof.IsTaggedAtom(IrExpr.Unwired()));
        Assert.Equal(ProofVerdict.Proven, OppositeAtoms(ExclusionProof.TaggedAtom("Valve1@4", 2, "被调块内部变量 Busy")));
    }

    [Fact]
    public void AtomTextIsComparedCaseSensitively()
    {
        // 博图字符串比较区分大小写：S = 'a' 时 (S == 'a') 与 NOT(S == 'A') 同为真
        var a = new TimedDef("Q1", 3, Eq(V("S", 3), "'a'"));
        var b = new TimedDef("Q2", 3, IrExpr.Not(Eq(V("S", 3), "'A'")));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, (_, _, _) => true, Certain));
        Assert.Equal(ProofVerdict.Proven, OppositeAtoms(Eq(V("S", 3), "'a'")));
    }

    [Fact]
    public void DefinitionWhoseEveryTermContradictsItselfIsNotAProof()
    {
        // Q1 := X AND NOT X（模型里恒假）：互斥是空洞的，不算证明
        var a = new TimedDef("Q1", 0, IrExpr.And([V("X", 0), IrExpr.Not(V("X", 0))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, new TimedDef("Q2", 1, V("B", 1)), (_, _, _) => true, Certain));
    }

    // ---- 审查 I-1：输出写入之后不得再被改写 ----

    [Fact]
    public void OutputRewrittenAfterItsDefinitionIsNotProven()
    {
        // 顺序可信：Q2 在第 2 条语句又被写（stable(Q2, 1, End) 不成立）
        var a = new TimedDef("Q1", 0, V("X", 0));
        var b = new TimedDef("Q2", 1, IrExpr.Not(V("X", 1)));
        Func<string, int, int, bool> rewritten = (n, t1, t2) => n == "X" || n == "Q1" || n == "Q2" && t2 <= 2;
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, rewritten, Certain));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, (_, _, _) => true, Certain));
        // 顺序不可信：本段另有语句（序号 2，含不确定写入）可能写 Q2
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(("Q2", 2)), Certain));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
        // 同一个输出不构成互斥
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, a with { WriteAt = 1 }, Unordered(), Certain));
    }

    [Fact]
    public void ReadAtTheEndOfScanInsideADefinitionIsRejected()
    {
        // 定义里的读取不可能发生在扫描结束：带 End 时刻的读取一律拒绝（两个版本）
        var a = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), IrExpr.Not(V("Q2", ExclusionProof.End))]));
        var b = new TimedDef("Q2", 1, V("B", 1));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, (_, _, _) => true, Certain));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(b, a, (_, _, _) => true, Certain));
    }

    [Fact]
    public void UnstampedVariableIsNeverAssumedStable()
    {
        var a = new TimedDef("Q1", 0, IrExpr.Var("X"));
        var b = new TimedDef("Q2", 1, IrExpr.Not(IrExpr.Var("X")));
        Assert.NotEqual(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, (_, _, _) => true, Certain));
    }

    // ---- 裁定 F1/G1：顺序不可信段里，本段被写的变量每次读取都是自由值 ----

    [Fact]
    public void UnorderedSegmentDoesNotTrustTheLaterStatementReadingTheNewValue()
    {
        // 语句顺序可信时 Proven（见 LaterStatementReadingTheNewValueProvesTheMutex）；FlgNet 里 Q1 读到的 Q2 可能是旧值
        var a = new TimedDef("Q2", 0, V("B", 0));
        var b = new TimedDef("Q1", 1, IrExpr.And([V("A", 1), IrExpr.Not(V("Q2", 1))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
    }

    [Fact]
    public void UnorderedSegmentDoesNotTrustTheEarlierStatementReadingTheOldValue()
    {
        // 语句顺序可信时是反例；FlgNet 里 Q1 读到的 Q2 可能是新值 → 既不能证明也不能当反例
        var a = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), IrExpr.Not(V("Q2", 0))]));
        var b = new TimedDef("Q2", 1, V("B", 1));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
    }

    [Fact]
    public void UnorderedSegmentNoLongerProvesAnInterlockThroughTheOutputs()
    {
        // Q1 := A AND NOT Q2，Q2 := B AND NOT Q1：顺序可信时成立；FlgNet 里两输出的读取都是自由值，代价是这类互锁不再代码证明
        var a = new TimedDef("Q1", 1, IrExpr.And([V("A", 1), IrExpr.Not(V("Q2", 1))]));
        var b = new TimedDef("Q2", 0, IrExpr.And([V("B", 0), IrExpr.Not(V("Q1", 0))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, SingleWriters(("Q1", 1), ("Q2", 0)), Certain));
    }

    [Fact]
    public void UnorderedSegmentProvesCrossLockedInputsThatTheSegmentDoesNotWrite()
    {
        // Q1 := A AND NOT B，Q2 := B AND NOT A；A、B 本段不写 → 按稳定性规则 Proven
        var a = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), IrExpr.Not(V("B", 0))]));
        var b = new TimedDef("Q2", 1, IrExpr.And([V("B", 1), IrExpr.Not(V("A", 1))]));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
        // 只有 B 本段被写：靠 A 的互串仍成立；A、B 都被写（例如写它们的父结构）→ 读取都是自由值
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(("B", 2)), Certain));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(("A", 2), ("B", 2)), Certain));
    }

    [Fact]
    public void BranchCommonContactCopiedIntoTwoCoilsIsNotProven()
    {
        // ─┤Start├─┤/Q2├─┬─┤/Q3├─( Q2 )      s0  Q2 := Start AND NOT Q2 AND NOT Q3
        //                └───────( Q3 )      s1  Q3 := Start AND NOT Q2（公共触点 ┤/Q2├ 被复制进 s1）
        // 实际：Start=1、Q2/Q3 原为 0 时，本扫描结束 Q2=1 且 Q3=1
        var q2 = new TimedDef("Q2", 0, IrExpr.And([V("Start", 0), IrExpr.Not(V("Q2", 0)), IrExpr.Not(V("Q3", 0))]));
        var q3 = new TimedDef("Q3", 1, IrExpr.And([V("Start", 1), IrExpr.Not(V("Q2", 1))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(q2, q3, Unordered(), Certain));
    }

    [Fact]
    public void ContactUpstreamOfAMidRungCoilCopiedIntoTheNextCoilIsNotProven()
    {
        // ─┤/Q2├─┤/Q3├─( Q2 )─( Q3 )          s0  Q2 := NOT Q2 AND NOT Q3；s1  Q3 := NOT Q2 AND NOT Q3（读取都在中间线圈上游）
        // 实际：两者原为 0 时本扫描结束同为 1
        var q2 = new TimedDef("Q2", 0, IrExpr.And([IrExpr.Not(V("Q2", 0)), IrExpr.Not(V("Q3", 0))]));
        var q3 = new TimedDef("Q3", 1, IrExpr.And([IrExpr.Not(V("Q2", 1)), IrExpr.Not(V("Q3", 1))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(q2, q3, Unordered(), Certain));
    }

    [Fact]
    public void UnorderedSegmentCounterexampleOnlyFromVariablesTheSegmentDoesNotWrite()
    {
        // Q1、Q2 只读本段不写的 A、B：是反例
        Assert.Equal(ProofVerdict.Counterexample,
            ExclusionProof.Mutex(new TimedDef("Q1", 0, V("A", 0)), new TimedDef("Q2", 1, V("B", 1)), Unordered(), Certain));
        // 读到本段写入的变量（自由值并非真正任意）：不当反例
        var a = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), IrExpr.Not(V("Q1", 0))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, new TimedDef("Q2", 1, V("B", 1)), Unordered(), Certain));
    }

    [Fact]
    public void ReadsOfAVariableWrittenInTheSegmentAreNeverTheSameValue()
    {
        // M 本段有写入：即使同一语句同一时刻的正反两次读也不冲突（中间可能夹着写入）
        var a = new TimedDef("Q1", 1, IrExpr.Not(V("M", 1)));
        var b = new TimedDef("Q2", 1, V("M", 1));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(("M", 0)), Certain));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
        // 规则②同理：原子里的变量本段被写
        var gt = Gt(V("M", 1), "5");
        Assert.Equal(ProofVerdict.NotProven,
            ExclusionProof.Mutex(new TimedDef("Q1", 1, gt), new TimedDef("Q2", 1, IrExpr.Not(gt)), Unordered(("M", 0)), Certain));
    }

    [Fact]
    public void MidRungResetAndBranchSetMakeTheReadsFree()
    {
        // ─┤V├─(R V)─┤/V├─(Q2)─ ：s1 Q2 := V AND NOT V（两次读分别在复位前后）
        var q3 = new TimedDef("Q3", 2, V("B", 2));
        var q2 = new TimedDef("Q2", 1, IrExpr.And([V("V", 1), IrExpr.Not(V("V", 1))]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(q2, q3, Unordered(("V", 0)), Certain));
        // ─┤Btn├─┤/Mem├─┬─(S Mem) / └─┤Mem├─(Q)：s1 Q := Btn AND NOT Mem AND Mem
        var q = new TimedDef("Q", 1, IrExpr.And([V("Btn", 1), IrExpr.Not(V("Mem", 1)), V("Mem", 1)]));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(q, q3, Unordered(("Mem", 0)), Certain));
        // 本段不写 Mem 时同一语句的两次读是同一个值 → 定义恒假 → 不算证明
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(q, q3, Unordered(), Certain));
    }

    [Fact]
    public void WritesAreQueriedForEveryReadNameIncludingOverlaps()
    {
        // 读 DB.S.X，本段第 2 条语句写父结构 DB.S：调用方按重叠口径对 DB.S.X 返回 2 → 读取是自由值
        var a = new TimedDef("Q1", 0, V("DB.S.X", 0));
        var b = new TimedDef("Q2", 1, IrExpr.Not(V("DB.S.X", 1)));
        var asked = new List<string>();
        IEnumerable<int> Overlapping(string n)
        {
            asked.Add(n);
            return n.StartsWith("DB.S", StringComparison.OrdinalIgnoreCase) ? [2] : [];
        }
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, new UnorderedSegment(Overlapping, _ => true, _ => true), Certain));
        Assert.Contains("DB.S.X", asked);
        Assert.Contains("Q1", asked);
        Assert.Contains("Q2", asked);
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, new UnorderedSegment(_ => [], _ => true, _ => true), Certain));
    }

    [Fact]
    public void OutputsThatAliasEachOtherAreTheSameWrite()
    {
        // %Q0.1 与 Q0.1 是同一个点：调用方问其中一个时返回两条写入 → NotProven
        var a = new TimedDef("%Q0.1", 0, V("X", 0));
        var b = new TimedDef("Q0.1", 1, IrExpr.Not(V("X", 1)));
        IEnumerable<int> Alias(string n) => n.TrimStart('%').Equals("Q0.1", StringComparison.OrdinalIgnoreCase) ? [0, 1] : [];
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, new UnorderedSegment(Alias, _ => true, _ => true), Certain));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
    }

    [Fact]
    public void UnorderedSegmentHonoursSteadinessAndWritesAfterTheSegment()
    {
        // X 段内不写：同段不同语句两次读，只有 Steady(X) 才算同一个值
        var a = new TimedDef("Q1", 0, IrExpr.And([V("A", 0), V("X", 0)]));
        var b = new TimedDef("Q2", 1, IrExpr.Not(V("X", 1)));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(), Certain));
        Assert.NotEqual(ProofVerdict.Proven,
            ExclusionProof.Mutex(a, b, new UnorderedSegment(_ => [], n => n != "X", _ => true), Certain));
        // Q2 段后还会被别处改写：Q2@End 不等于本段写入后的值
        var c = new TimedDef("Q2", 0, V("B", 0));
        var d = new TimedDef("Q1", 1, IrExpr.Not(V("B", 1)));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(c, d, Unordered(), Certain));
        Assert.Equal(ProofVerdict.NotProven,
            ExclusionProof.Mutex(c, d, new UnorderedSegment(_ => [], _ => true, n => n != "Q2"), Certain));
    }

    static IrUnit Unit(string language, string rootKind, params IrStatement[] statements) =>
        new(1, "B", 1, language, rootKind, statements, [], []);

    [Fact]
    public void StatementOrderIsJudgedByTheProgramSegmentRoot()
    {
        var assign = new IrStatement("B/1/1", IrStatementKinds.Assign, "Q1", IrExpr.True, IrExpr.Var("A"));
        Assert.True(ExclusionProof.StatementOrderTrusted(Unit("SCL", "StructuredText", assign)));
        Assert.True(ExclusionProof.StatementOrderTrusted(Unit("STL", "StatementList", assign)));
        // 块语言是块级的：LAD 块里的 SCL 程序段可信，SCL 块语言下根是 FlgNet 的程序段不可信
        Assert.True(ExclusionProof.StatementOrderTrusted(Unit("LAD", "StructuredText", assign)));
        Assert.False(ExclusionProof.StatementOrderTrusted(Unit("SCL", "FlgNet", assign)));
        Assert.False(ExclusionProof.StatementOrderTrusted(Unit("LAD", "FlgNet", assign)));
        Assert.False(ExclusionProof.StatementOrderTrusted(Unit("GRAPH", "Graph", assign)));
        Assert.False(ExclusionProof.StatementOrderTrusted(Unit("SCL", "Text", assign)));
        Assert.False(ExclusionProof.StatementOrderTrusted(Unit("SCL", "Transition", assign)));
        // 带 FlgNet 痕迹（指令盒语句 / 节点）的一律不可信
        var box = new IrStatement("B/1/2", IrStatementKinds.Box, null, IrExpr.True, IrExpr.Var("X"));
        Assert.False(ExclusionProof.StatementOrderTrusted(Unit("SCL", "StructuredText", assign, box)));
        Assert.False(ExclusionProof.StatementOrderTrusted(
            new IrUnit(1, "B", 1, "SCL", "StructuredText", [assign], [new IrNode("N1", IrExpr.Var("A"))], [])));
    }
}
