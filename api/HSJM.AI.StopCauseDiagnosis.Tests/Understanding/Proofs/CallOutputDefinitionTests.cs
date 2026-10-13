#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class CallOutputDefinitionTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement A(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);

    static IfaceMember M(string section, string name, string type = "Bool", string? start = null) => new(section, name, type, start, null, []);

    static readonly IfaceMember[] BaseIface =
    [
        M("Input", "I_A"), M("Input", "I_B"), M("Input", "I_Test"),
        M("Output", "O1"), M("Output", "O2"), M("Output", "O3"), M("Output", "O4"),
        M("Static", "Cmd", "Struct"),
    ];

    static IrBlock Callee(params IrStatement[] extra)
    {
        var unit = new IrUnit(1, "FB_T", 1, "LAD", "FlgNet",
        [
            A("FB_T/1/1", "O1", IrExpr.And([V("I_A"), IrExpr.Not(V("I_B"))])),
            A("FB_T/1/2", "O2", IrExpr.Or([V("I_B"), V("I_Test")])),
            A("FB_T/1/3", "O3", V("Cmd.X")),
            A("FB_T/1/4", "Cmd.X", V("I_A")),
            A("FB_T/1/5", "O4", IrExpr.And([V("G"), V("I_A")])),
            A("FB_T/1/6", "G", V("I_B")),
            .. extra
        ], [], []);
        return IrBlock.FromUnits("FB_T", [unit], BaseIface);
    }

    static IrUnit Unit(long id, string rootKind, params IrStatement[] statements) =>
        new(id, "FB_U", (int)id, rootKind == "StructuredText" ? "SCL" : "LAD", rootKind, statements, [], []);

    static IrBlock Block(IReadOnlyList<IfaceMember> iface, params IrUnit[] units) => IrBlock.FromUnits("FB_U", units, iface);

    static readonly IrCall Call = new("FB_T", "FB", "Inst", IrExpr.True,
    [
        new IrPin("I_A", "Input", "Bool", V("X"), []), new IrPin("I_B", "Input", "Bool", V("Y"), []),
    ]);

    static string? Text(IrBlock callee, string output, bool useDefaults, out bool used) =>
        CallOutputDefinition.Resolve(callee, Call, output, e => e, useDefaults, "Inst@3", out used) is { } e ? IrText.Expr(e) : null;

    static IrExpr? Resolve(IrBlock callee, string output, IrCall? call = null, string tag = "Inst@3") =>
        CallOutputDefinition.Resolve(callee, call ?? Call, output, e => e, true, tag, out _);

    static IEnumerable<IrExpr> Atoms(IrExpr e) =>
        e.Kind == IrKinds.Unknown ? [e] : (e.Args ?? []).SelectMany(Atoms);

    [Fact]
    public void WiredInputsAreReplacedByActuals()
    {
        Assert.Equal("(X AND NOT(Y))", Text(Callee(), "O1", true, out var used));
        Assert.False(used);
    }

    [Fact]
    public void UnwiredInputUsesTheInterfaceDefaultOnlyWhenAllowed()
    {
        Assert.Equal("(Y OR FALSE)", Text(Callee(), "O2", true, out var used));
        Assert.True(used);
        Assert.Contains("接口初值 I_Test", Text(Callee(), "O2", false, out _));
    }

    [Fact]
    public void InternalWrittenAfterTheOutputIsOpaque() =>
        Assert.Contains("被调块内部 Cmd.X", Text(Callee(), "O3", true, out _));

    [Fact]
    public void GlobalWrittenByTheCalleeIsOpaque() =>
        Assert.Contains("被调块读写的全局量 G", Text(Callee(), "O4", true, out _));

    [Fact]
    public void ControlFlowOrNonOutputGivesNull()
    {
        var withJump = Callee(new IrStatement("FB_T/1/7", IrStatementKinds.Jump, "L1", V("I_A"), null));
        Assert.Null(Text(withJump, "O1", true, out _));
        Assert.Null(Text(Callee(), "I_A", true, out _));
    }

    [Fact]
    public void ExpandCallerIsAppliedToWiredActuals()
    {
        var r = CallOutputDefinition.Resolve(Callee(), Call, "O1", e => e.Kind == IrKinds.Var && e.Name == "X" ? V("Z") : e,
            true, "Inst@3", out _);
        Assert.Equal("(Z AND NOT(Y))", IrText.Expr(r!));
    }

    [Fact]
    public void AllOpaqueAtomsAreTaggedAtoms()
    {
        foreach (var output in new[] { "O3", "O4" })
        {
            var atoms = Atoms(Resolve(Callee(), output)!).ToArray();
            Assert.NotEmpty(atoms);
            Assert.All(atoms, a => Assert.True(ExclusionProof.IsTaggedAtom(a)));
            Assert.All(atoms, a => Assert.StartsWith("Inst@3|", a.Name));
        }
    }

    [Fact]
    public void AtomTagWithSeparatorIsRejected()
    {
        Assert.Throws<ArgumentException>(() => CallOutputDefinition.Resolve(Callee(), Call, "O1", e => e, true, "a|b", out _));
        Assert.Throws<ArgumentException>(() => CallOutputDefinition.Resolve(Callee(), Call, "O1", e => e, true, "", out _));
    }

    [Fact]
    public void DifferentInstancesGiveDifferentAtoms()
    {
        var a = Atoms(Resolve(Callee(), "O3", tag: "Inst@3")!).Single();
        var b = Atoms(Resolve(Callee(), "O3", tag: "Inst2@5")!).Single();
        Assert.NotEqual(a.Name, b.Name);
    }

    [Fact]
    public void DifferentTagsNeverShareAnAtom()
    {
        // 调用方对同一输出的两次读取（不能证明之间无写入时）须用不同标签：两组原子不得有任何一个文字相同
        var outputs = new[] { "O2", "O3", "O4" };
        IEnumerable<string> Names(string tag) => outputs.SelectMany(o =>
            Atoms(CallOutputDefinition.Resolve(Callee(), Call, o, e => e, false, tag, out _)!)).Select(a => a.Name!);
        var first = Names("Inst@3#1").ToArray();
        var second = Names("Inst@3#2").ToArray();
        Assert.NotEmpty(first);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void ReadsOfAGlobalWrittenInTheSameFlgNetNetworkAreIndependent()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O1")],
            Unit(1, "FlgNet", A("U/1/1", "G", V("I_A")), A("U/1/2", "O1", IrExpr.And([V("G"), IrExpr.Not(V("G"))]))));
        var atoms = Atoms(Resolve(callee, "O1")!).ToArray();
        Assert.Equal(2, atoms.Length);
        Assert.NotEqual(atoms[0].Name, atoms[1].Name);
    }

    [Fact]
    public void ReadsInAFlgNetNetworkThatCallsAnotherBlockAreIndependent()
    {
        // 段内有调用：被调块内部可能改写任何量，段内顺序又不可信
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Static", "S")],
            Unit(1, "FlgNet",
                new IrStatement("U/1/1", IrStatementKinds.Call, null, IrExpr.True, null, new IrCall("FC_X", "FC", "", IrExpr.True, [])),
                A("U/1/2", "O1", IrExpr.And([V("S"), IrExpr.Not(V("S"))]))));
        var atoms = Atoms(Resolve(callee, "O1")!).ToArray();
        Assert.Equal(2, atoms.Length);
        Assert.NotEqual(atoms[0].Name, atoms[1].Name);
    }

    [Fact]
    public void OutputNotUniquelyDefinedGivesNull()
    {
        var twice = Callee(A("FB_T/1/7", "O1", V("I_B")));
        Assert.Null(Resolve(twice, "O1"));
        var setOnly = Block([M("Input", "I_A"), M("Output", "O1")],
            Unit(1, "FlgNet", new IrStatement("U/1/1", IrStatementKinds.Set, "O1", V("I_A"), null)));
        Assert.Null(Resolve(setOnly, "O1"));
        var nonBool = Block([M("Input", "I_A"), M("Output", "O1", "Int")], Unit(1, "FlgNet", A("U/1/1", "O1", V("I_A"))));
        Assert.Null(Resolve(nonBool, "O1"));
    }

    [Fact]
    public void OutputMentionedInAnyTextGivesNull()
    {
        foreach (var kind in new[] { IrStatementKinds.Source, IrStatementKinds.Action, IrStatementKinds.Unrecognized })
        {
            var callee = Callee(new IrStatement("FB_T/1/7", kind, null, IrExpr.True, null, Text: "O1 := FALSE;"));
            Assert.Null(Resolve(callee, "O1"));
        }
    }

    // ---- 读取时刻：被调块内部变量 ----

    [Fact]
    public void LocalWrittenInAnEarlierNetworkIsExpanded()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O3"), M("Static", "Cmd", "Struct")],
            Unit(1, "FlgNet", A("U/1/1", "Cmd.X", V("I_A"))),
            Unit(2, "FlgNet", A("U/2/1", "O3", V("Cmd.X"))));
        Assert.Equal("X", IrText.Expr(Resolve(callee, "O3")!));
    }

    [Fact]
    public void LocalWrittenEarlierInTheSameFlgNetNetworkIsNotExpanded()
    {
        // FlgNet 段内语句序号不是执行顺序：靠「段内先写后读」才能得到的定义视为无法解析
        var callee = Block([M("Input", "I_A"), M("Output", "O3"), M("Static", "Cmd", "Struct")],
            Unit(1, "FlgNet", A("U/1/1", "Cmd.X", V("I_A")), A("U/1/2", "O3", V("Cmd.X"))));
        var text = IrText.Expr(Resolve(callee, "O3")!);
        Assert.Contains("被调块内部 Cmd.X", text);
        Assert.NotEqual("X", text);
    }

    [Fact]
    public void LocalWrittenEarlierInTheSameSclNetworkIsExpanded()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O3"), M("Static", "Cmd", "Struct")],
            Unit(1, "StructuredText", A("U/1/1", "Cmd.X", V("I_A")), A("U/1/2", "O3", V("Cmd.X"))));
        Assert.Equal("X", IrText.Expr(Resolve(callee, "O3")!));
    }

    [Fact]
    public void LocalWrittenLaterInTheSameSclNetworkIsOpaque()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O3"), M("Static", "Cmd", "Struct")],
            Unit(1, "StructuredText", A("U/1/1", "O3", V("Cmd.X")), A("U/1/2", "Cmd.X", V("I_A"))));
        Assert.Contains("被调块内部 Cmd.X", IrText.Expr(Resolve(callee, "O3")!));
    }

    [Fact]
    public void ReadsOfAVariableWrittenInTheSameFlgNetNetworkAreIndependent()
    {
        // ┤V├(R V)┤/V├(O1)：同一语句里两次读 V 不是同一时刻，不能当作 V ∧ ¬V
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Static", "V")],
            Unit(1, "FlgNet",
                new IrStatement("U/1/1", IrStatementKinds.Reset, "V", V("I_A"), null),
                A("U/1/2", "O1", IrExpr.And([V("V"), IrExpr.Not(V("V"))]))));
        var atoms = Atoms(Resolve(callee, "O1")!).ToArray();
        Assert.Equal(2, atoms.Length);
        Assert.NotEqual(atoms[0].Name, atoms[1].Name);
    }

    [Fact]
    public void ReadsOfAVariableNotWrittenInTheReaderNetworkShareOneAtom()
    {
        // 另一段写的 V 没有唯一定义（置位/复位）：本段内取值不变，两次读是同一个量
        var callee = Block([M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1"), M("Static", "V")],
            Unit(1, "FlgNet",
                new IrStatement("U/1/1", IrStatementKinds.Set, "V", V("I_A"), null),
                new IrStatement("U/1/2", IrStatementKinds.Reset, "V", V("I_B"), null)),
            Unit(2, "FlgNet", A("U/2/1", "O1", IrExpr.And([V("V"), IrExpr.Not(V("V"))]))));
        var atoms = Atoms(Resolve(callee, "O1")!).ToArray();
        Assert.Equal(2, atoms.Length);
        Assert.Equal(atoms[0].Name, atoms[1].Name);
    }

    [Fact]
    public void SharedInternalDefinitionGivesTheSameAtomsInBothOutputs()
    {
        // O1 := M ∧ A、O2 := ¬M ∧ B，M 在前一段唯一定义：两输出展开 M 得到的原子相同（同一读取），可据此证明互斥
        var callee = Block([M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1"), M("Output", "O2"), M("Static", "M"), M("Static", "S")],
            Unit(1, "FlgNet",
                new IrStatement("U/1/1", IrStatementKinds.Set, "S", V("I_A"), null),
                new IrStatement("U/1/2", IrStatementKinds.Reset, "S", V("I_B"), null)),
            Unit(2, "FlgNet", A("U/2/1", "M", V("S"))),
            Unit(3, "FlgNet", A("U/3/1", "O1", IrExpr.And([V("M"), V("I_A")])), A("U/3/2", "O2", IrExpr.And([IrExpr.Not(V("M")), V("I_B")]))));
        var a = Atoms(Resolve(callee, "O1")!).Single();
        var b = Atoms(Resolve(callee, "O2")!).Single();
        Assert.Equal(a.Name, b.Name);
    }

    [Fact]
    public void ExpansionStopsAtTheDepthLimit()
    {
        var iface = new List<IfaceMember> { M("Input", "I_A"), M("Output", "O1") };
        var units = new List<IrUnit>();
        for (var i = 0; i < 9; i++)
        {
            iface.Add(M("Static", $"L{i}"));
            units.Add(Unit(i + 1, "FlgNet", A($"U/{i + 1}/1", $"L{i}", i == 0 ? V("I_A") : V($"L{i - 1}"))));
        }
        units.Add(Unit(20, "FlgNet", A("U/20/1", "O1", V("L8"))));
        var text = IrText.Expr(Resolve(Block(iface, [.. units]), "O1")!);
        Assert.Contains("被调块内部 L", text);
        Assert.DoesNotContain("X", text.Replace("被调块内部", ""));
    }

    // ---- 输入形参 ----

    [Fact]
    public void InputWrittenInsideTheCalleeIsOpaque()
    {
        foreach (var target in new[] { "I_A", "I_A.x", "I_A[1]" })
        {
            var callee = Block([M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1")],
                Unit(1, "FlgNet", A("U/1/1", target, V("I_B"))),
                Unit(2, "FlgNet", A("U/2/1", "O1", V("I_A"))));
            Assert.Contains("被调块改写的输入 I_A", IrText.Expr(Resolve(callee, "O1")!));
        }
    }

    [Fact]
    public void InputMentionedInTextIsOpaque()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O1")],
            Unit(1, "StructuredText", new IrStatement("U/1/1", IrStatementKinds.Source, null, IrExpr.True, null, Text: "I_A := FALSE;")),
            Unit(2, "FlgNet", A("U/2/1", "O1", V("I_A"))));
        Assert.Contains("被调块改写的输入 I_A", IrText.Expr(Resolve(callee, "O1")!));
    }

    [Fact]
    public void InOutIsOpaque()
    {
        var callee = Block([M("InOut", "P"), M("Output", "O1")], Unit(1, "FlgNet", A("U/1/1", "O1", V("P"))));
        var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("P", "InOut", "Bool", V("Q"), ["Q"])]);
        Assert.Contains("输入输出形参 P", IrText.Expr(Resolve(callee, "O1", call)!));
    }

    [Fact]
    public void BoolConstantActualIsNormalized()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O1")], Unit(1, "FlgNet", A("U/1/1", "O1", V("I_A"))));
        foreach (var (value, expected) in new[] { ("1", "TRUE"), ("true", "TRUE"), ("0", "FALSE"), ("False", "FALSE") })
        {
            var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("I_A", "Input", "Bool", IrExpr.Const(value), [])]);
            Assert.Equal(expected, IrText.Expr(Resolve(callee, "O1", call)!));
        }
    }

    [Fact]
    public void MemberOfAnInputNeedsAVariableActual()
    {
        var callee = Block([M("Input", "S", "UDT_X"), M("Output", "O1")], Unit(1, "FlgNet", A("U/1/1", "O1", V("S.Ok"))));
        var byVar = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("S", "Input", "UDT_X", V("DB1.Axis"), [])]);
        Assert.Equal("DB1.Axis.Ok", IrText.Expr(Resolve(callee, "O1", byVar)!));
        var byExpr = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("S", "Input", "UDT_X", new IrExpr(IrKinds.Unknown, "未识别"), [])]);
        Assert.Contains("成员取自非变量实参 S.Ok", IrText.Expr(Resolve(callee, "O1", byExpr)!));
    }

    [Fact]
    public void UnwiredNonBoolInputUsesItsStartValueOrZero()
    {
        var callee = Block([M("Input", "N", "Int", "3"), M("Input", "K", "Int"), M("Output", "O1")],
            Unit(1, "FlgNet", A("U/1/1", "O1", IrExpr.And([new IrExpr(IrKinds.Cmp, ">", [V("N"), IrExpr.Const("5")]),
                new IrExpr(IrKinds.Cmp, "==", [V("K"), IrExpr.Const("0")])]))));
        var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("N", "Input", "Int", IrExpr.Unwired(), [])]);
        var r = CallOutputDefinition.Resolve(callee, call, "O1", e => e, true, "T", out var used);
        Assert.Equal("((3 > 5) AND (0 == 0))", IrText.Expr(r!));
        Assert.True(used);
    }

    [Fact]
    public void BoolInputStartValueIsUsedWhenUnwired()
    {
        var callee = Block([M("Input", "I_A", "Bool", "TRUE"), M("Output", "O1")], Unit(1, "FlgNet", A("U/1/1", "O1", V("I_A"))));
        var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, []);
        Assert.Equal("TRUE", IrText.Expr(Resolve(callee, "O1", call)!));
    }

    // ---- 全局量 ----

    [Fact]
    public void UnwrittenGlobalIsKeptWhenTheCalleeWritesNoGlobals()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Static", "S")],
            Unit(1, "FlgNet", A("U/1/1", "S", V("I_A"))),
            Unit(2, "FlgNet", A("U/2/1", "O1", IrExpr.And([V("\"DB\".Ready"), V("I_A")]))));
        Assert.Equal("(\"DB\".Ready AND X)", IrText.Expr(Resolve(callee, "O1")!));
    }

    [Fact]
    public void GlobalIsOpaqueWhenTheCalleeHasCallsOrText()
    {
        var withCall = Block([M("Input", "I_A"), M("Output", "O1")],
            Unit(1, "FlgNet", A("U/1/1", "O1", V("G"))),
            Unit(2, "FlgNet", new IrStatement("U/2/1", IrStatementKinds.Call, null, IrExpr.True, null,
                new IrCall("FC_X", "FC", "", IrExpr.True, []))));
        Assert.Contains("被调块读写的全局量 G", IrText.Expr(Resolve(withCall, "O1")!));
        var withText = Block([M("Input", "I_A"), M("Output", "O1")],
            Unit(1, "FlgNet", A("U/1/1", "O1", V("G"))),
            Unit(2, "StructuredText", new IrStatement("U/2/1", IrStatementKinds.Source, null, IrExpr.True, null, Text: "H := TRUE;")));
        Assert.Contains("被调块读写的全局量 G", IrText.Expr(Resolve(withText, "O1")!));
    }

    [Fact]
    public void GlobalIsOpaqueWhenTheCalleeWritesAnyOtherGlobal()
    {
        // 另一个全局名可能与它同址（字节标签/位标签、绝对地址），保守视为可能被改写
        var callee = Block([M("Input", "I_A"), M("Output", "O1")],
            Unit(1, "FlgNet", A("U/1/1", "%M10.0", V("I_A"))),
            Unit(2, "FlgNet", A("U/2/1", "O1", V("Flag"))));
        Assert.Contains("被调块读写的全局量 Flag", IrText.Expr(Resolve(callee, "O1")!));
    }

    // ---- 被调块内部的有状态部件 ----

    [Fact]
    public void StatefulPartsBecomeTaggedAtomsPerOccurrence()
    {
        var timer = new IrExpr(IrKinds.Timer, "TON|Tmr|Q", [new IrExpr(IrKinds.Port, "IN", [V("I_A")])]);
        var rise = new IrExpr(IrKinds.Rise, null, [V("I_A")]);
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Static", "Tmr", "TON_TIME")],
            Unit(1, "FlgNet", A("U/1/1", "O1", IrExpr.And([timer, IrExpr.Not(timer), rise, IrExpr.Not(rise)]))));
        var atoms = Atoms(Resolve(callee, "O1")!).ToArray();
        Assert.Equal(4, atoms.Length);
        Assert.All(atoms, a => Assert.True(ExclusionProof.IsTaggedAtom(a)));
        Assert.Equal(4, atoms.Select(a => a.Name).Distinct().Count());
        Assert.DoesNotContain(IrKinds.Timer, Kinds(Resolve(callee, "O1")!));
    }

    static IEnumerable<string> Kinds(IrExpr e) => (e.Args ?? []).SelectMany(Kinds).Prepend(e.Kind);

    // ---- 审查回归（task-5-review 探针）：调用方两个输出 Q1/Q2，按 Task 6 用法加时刻后交 ExclusionProof.Mutex，stable 恒真 ----

    static ProofVerdict MutexOf(IrExpr q1, IrExpr q2) =>
        ExclusionProof.Mutex(new TimedDef("Q1", 1, ExclusionProof.Stamp(q1, 0)), new TimedDef("Q2", 2, ExclusionProof.Stamp(q2, 0)),
            (_, _, _) => true, _ => true);

    static readonly IrCall CallAB = new("FB_U", "FB", "Inst", IrExpr.True,
        [new IrPin("I_A", "Input", "Bool", V("X"), []), new IrPin("I_B", "Input", "Bool", V("Y"), [])]);

    static IrExpr Tq() => V("Tmr.Q");
    static IrExpr SelfContradictionOrB(IrExpr x) => IrExpr.Or([IrExpr.And([x, IrExpr.Not(x)]), V("I_B")]);
    static IrStatement WithSide(IrStatement s, params string[] side) => s with { SideWrites = side };
    static readonly IfaceMember[] TimerIface =
        [M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1"), M("Output", "O2"), M("Static", "Tmr", "TON_TIME"), M("Static", "V")];

    /// <summary>任一输出无法解析（null）即证明不了（NotProven）。</summary>
    static ProofVerdict Probe(IrBlock callee, IrCall? call = null) =>
        Resolve(callee, "O1", call ?? CallAB) is { } q1 && Resolve(callee, "O2", call ?? CallAB) is { } q2
            ? MutexOf(q1, q2) : ProofVerdict.NotProven;

    [Fact]
    public void ProbeControl_UnwrittenInstanceReadTwiceIsTheSameValue()
    {
        // P5 对照：本段没有任何东西写 Tmr，两次读 Tmr.Q 同值，(Tmr.Q ∧ ¬Tmr.Q) 恒假，O1 = Y 与 O2 = ¬Y 互斥成立
        var callee = Block(TimerIface,
            Unit(1, "FlgNet", A("U/1/1", "O1", SelfContradictionOrB(Tq())), A("U/1/2", "O2", IrExpr.Not(V("I_B")))));
        Assert.Equal(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void P1_BoxWritingTheInstanceInTheSameNetworkMakesReadsIndependent()
    {
        var ton = new IrExpr(IrKinds.Timer, "TON|Tmr|Q", [new IrExpr(IrKinds.Port, "IN", [V("I_A")])]);
        var callee = Block(TimerIface,
            Unit(1, "FlgNet",
                WithSide(new IrStatement("U/1/1", IrStatementKinds.Box, null, IrExpr.True, ton), "Tmr"),
                A("U/1/2", "O1", SelfContradictionOrB(Tq())), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void P1b_TimerEmbeddedInTheCoilWithSideWritesMakesReadsIndependent()
    {
        var ton = new IrExpr(IrKinds.Timer, "TON|Tmr|Q", [new IrExpr(IrKinds.Port, "IN", [V("I_A")])]);
        var value = IrExpr.Or([IrExpr.And([Tq(), ton, IrExpr.Not(Tq())]), V("I_B")]);
        var callee = Block(TimerIface,
            Unit(1, "FlgNet", WithSide(A("U/1/1", "O1", value), "Tmr"), A("U/1/2", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void P1c_SideWritesAloneMakeReadsIndependent()
    {
        // 只有 SideWrites（没有内嵌部件）：靠 IrBlock 的写入口径识别段内写入
        var callee = Block(TimerIface,
            Unit(1, "FlgNet", WithSide(A("U/1/1", "O1", SelfContradictionOrB(Tq())), "Tmr"), A("U/1/2", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void P2_UnrecognizedWriteTargetInTheSameNetworkMakesReadsIndependent()
    {
        var callee = Block(TimerIface,
            Unit(1, "FlgNet",
                A("U/1/1", "〔未识别的访问 LocalVariable〕", V("I_A")),
                A("U/1/2", "O1", SelfContradictionOrB(V("V"))), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void P3_BlockMoveOverwritingTheArrayBlocksTheDefinition()
    {
        // 网络 1 写 Arr[1]，网络 2 MOVE_BLK 输出到 Arr[0]（实际可覆盖 Arr[1]），网络 3 读 Arr[1] → 无法解析
        var move = new IrExpr(IrKinds.Instr, "MOVE_BLK|OUT", [new IrExpr(IrKinds.Port, "IN", [V("Src[0]")])]);
        var callee = Block(
            [M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1"), M("Output", "O2"),
             M("Static", "Arr", "Array[0..9] of Bool"), M("Static", "Src", "Array[0..9] of Bool")],
            Unit(1, "FlgNet", A("U/1/1", "Arr[1]", V("I_A"))),
            Unit(2, "FlgNet", A("U/2/1", "Arr[0]", move)),
            Unit(3, "FlgNet", A("U/3/1", "O1", IrExpr.And([V("Arr[1]"), V("I_B")]))),
            Unit(4, "FlgNet", A("U/4/1", "O2", IrExpr.Not(V("I_A")))));
        Assert.Contains("被调块内部 Arr[1]", IrText.Expr(Resolve(callee, "O1", CallAB)!));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void P6_EdgeMemoryBitWrittenAsSideWriteMakesGlobalsOpaque()
    {
        var callee = Block(TimerIface,
            Unit(1, "FlgNet", WithSide(A("U/1/1", "O2", V("I_A")), "%M10.0")),
            Unit(2, "FlgNet", A("U/2/1", "O1", V("Flag"))));
        var q1 = Resolve(callee, "O1", CallAB)!;
        Assert.Contains("被调块读写的全局量 Flag", IrText.Expr(q1));
        Assert.NotEqual(ProofVerdict.Proven, MutexOf(q1, IrExpr.Not(V("Flag"))));
    }

    [Fact]
    public void P9_GlobalWithACalleeStaticIndexIsOpaque()
    {
        var callee = Block([M("Output", "O1"), M("Static", "Idx", "Int")],
            Unit(1, "FlgNet", A("U/1/1", "Idx", IrExpr.Const("3"))),
            Unit(2, "FlgNet", A("U/2/1", "O1", V("\"DB\".Arr[Idx]"))));
        var q1 = Resolve(callee, "O1", CallAB)!;
        Assert.Equal(IrKinds.Unknown, q1.Kind);
        Assert.NotEqual(ProofVerdict.Proven, MutexOf(q1, IrExpr.Not(V("\"DB\".Arr[Idx]"))));
    }

    [Fact]
    public void P10_InputMemberWithAVariableIndexIsOpaque()
    {
        var callee = Block([M("Input", "S", "UDT_X"), M("Output", "O1"), M("Static", "Idx", "Int")],
            Unit(1, "FlgNet", A("U/1/1", "O1", V("S.Arr[Idx]"))));
        var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("S", "Input", "UDT_X", V("\"DB1\".Axis"), [])]);
        var q1 = Resolve(callee, "O1", call)!;
        Assert.Equal(IrKinds.Unknown, q1.Kind);
        Assert.NotEqual(ProofVerdict.Proven, MutexOf(q1, IrExpr.Not(V("\"DB1\".Axis.Arr[Idx]"))));
    }

    [Fact]
    public void P11_GlobalIndexedByAnInputIsOpaque()
    {
        var callee = Block([M("Input", "K", "Int"), M("Output", "O1")], Unit(1, "FlgNet", A("U/1/1", "O1", V("\"DB\".Arr[K]"))));
        var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("K", "Input", "Int", V("CurK"), [])]);
        var q1 = Resolve(callee, "O1", call)!;
        Assert.Equal(IrKinds.Unknown, q1.Kind);
        Assert.NotEqual(ProofVerdict.Proven, MutexOf(q1, IrExpr.Not(V("\"DB\".Arr[K]"))));
    }

    [Fact]
    public void LiteralIndexIsKept()
    {
        var callee = Block([M("Input", "S", "UDT_X"), M("Output", "O1")],
            Unit(1, "FlgNet", A("U/1/1", "O1", IrExpr.And([V("\"DB\".Arr[3]"), V("S.Arr[ 2 ]")]))));
        var call = new IrCall("FB_U", "FB", "Inst", IrExpr.True, [new IrPin("S", "Input", "UDT_X", V("\"DB1\".Axis"), [])]);
        Assert.Equal("(\"DB\".Arr[3] AND \"DB1\".Axis.Arr[ 2 ])", IrText.Expr(Resolve(callee, "O1", call)!));
    }

    [Fact]
    public void I1_GlobalNameOfTheCallsOwnInstanceIsOpaque()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Output", "O2")],
            Unit(1, "FlgNet", A("U/1/1", "O2", V("I_A"))),
            Unit(2, "FlgNet", A("U/2/1", "O1", V("\"Inst_DB\".O2"))));
        foreach (var instance in new[] { "\"Inst_DB\"", "Inst_DB", "inst_db" })
        {
            var call = new IrCall("FB_U", "FB", instance, IrExpr.True, [new IrPin("I_A", "Input", "Bool", V("X"), [])]);
            var q1 = Resolve(callee, "O1", call)!;
            Assert.Equal(IrKinds.Unknown, q1.Kind);
            Assert.NotEqual(ProofVerdict.Proven, MutexOf(q1, IrExpr.Not(V("\"Inst_DB\".O2"))));
        }
    }

    [Fact]
    public void FcCallsAreNotResolved()
    {
        // FC 形参按引用传递：输入与输出实参、块内全局写入都可能与调用方变量同址，符号对符号的同址别名又判不出 → 一律无法解析
        var fc = new IrCall("FC_U", "FC", "", IrExpr.True, [new IrPin("I_A", "Input", "Bool", V("X"), [])]);
        var clean = Block([M("Input", "I_A"), M("Output", "O1")], Unit(1, "FlgNet", A("U/1/1", "O1", V("I_A"))));
        Assert.Null(Resolve(clean, "O1", fc));
        Assert.Null(Resolve(clean, "O1", fc with { BlockType = "" }));
        Assert.Equal("X", IrText.Expr(Resolve(clean, "O1")!));   // 同一块按 FB 调用照常解析
    }

    [Fact]
    public void FbInputOverlappingAnOutputActualIsTheCopiedInValue()
    {
        // FB 的输入在调用时拷进背景 DB，块内写输出（调用后才拷出）不影响它：与 FC 的引用传递相反，照常解析
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Output", "O2")],
            Unit(1, "FlgNet", A("U/1/1", "O2", IrExpr.True)),
            Unit(2, "FlgNet", A("U/2/1", "O1", V("I_A"))));
        var fb = new IrCall("FB_U", "FB", "Inst", IrExpr.True,
            [new IrPin("I_A", "Input", "Bool", V("G"), []), new IrPin("O2", "Output", "Bool", null, ["G"])]);
        Assert.Equal("G", IrText.Expr(Resolve(callee, "O1", fb)!));
        Assert.Null(Resolve(callee, "O1", fb with { BlockType = "FC", Instance = "" }));
    }

    [Fact]
    public void AbsoluteAddressWriteInTheSameNetworkMakesGlobalReadsIndependent()
    {
        var callee = Block([M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1"), M("Output", "O2")],
            Unit(1, "FlgNet", A("U/1/1", "%M10.0", V("I_A")),
                A("U/1/2", "O1", SelfContradictionOrB(V("Flag"))), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    // ---- 复审回归（task-5-rereview 探针 R1~R11） ----

    static IrStatement R(string id, string target, IrExpr guard) => new(id, IrStatementKinds.Reset, target, guard, null);

    [Fact]
    public void R1_SymbolAliasResetInTheSameNetworkMakesGlobalReadsIndependent()
    {
        // ┤Flag├(R AliasBit)┤/Flag├(O1)：AliasBit 可能与 Flag 同址，IrBlock 认不出符号别名
        var callee = Block(TimerIface,
            Unit(1, "FlgNet", R("U/1/1", "AliasBit", V("Flag")),
                A("U/1/2", "O1", SelfContradictionOrB(V("Flag"))), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void R1b_SymbolResetAliasingAnAbsoluteAddressMakesReadsIndependent()
    {
        var callee = Block(TimerIface,
            Unit(1, "FlgNet", R("U/1/1", "Flag", V("%M10.0")),
                A("U/1/2", "O1", SelfContradictionOrB(V("%M10.0"))), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
    }

    [Fact]
    public void R2_InOutWriteInTheSameNetworkMakesGlobalReadsIndependent()
    {
        // InOut 写的是调用方变量 G（引用或拷出），IrBlock 认不出它与全局名 G 的关系
        var callee = Block([M("Input", "I_B"), M("InOut", "IO1"), M("Output", "O1"), M("Output", "O2")],
            Unit(1, "FlgNet", R("U/1/1", "IO1", V("G")),
                A("U/1/2", "O1", SelfContradictionOrB(V("G"))), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        var fb = new IrCall("FB_U", "FB", "Inst", IrExpr.True,
            [new IrPin("I_B", "Input", "Bool", V("Y"), []), new IrPin("IO1", "InOut", "Bool", V("G"), ["G"])]);
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee, fb));
    }

    [Fact]
    public void R3_OwnInstanceGlobalNameReadInANetworkThatWritesTheStaticIsIndependent()
    {
        var callee = Block(TimerIface.Append(M("Static", "S")).ToArray(),
            Unit(1, "FlgNet", R("U/1/1", "S", V("\"Inst\".S")),
                A("U/1/2", "O1", SelfContradictionOrB(V("\"Inst\".S"))), A("U/1/3", "O2", IrExpr.Not(V("I_B")))));
        Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
        var atoms = Atoms(Resolve(callee, "O1", CallAB)!).ToArray();
        Assert.Equal(2, atoms.Length);
        Assert.NotEqual(atoms[0].Name, atoms[1].Name);
    }

    [Fact]
    public void R7_WritingTheOwnInstanceByItsGlobalNameGivesNull()
    {
        foreach (var target in new[] { "\"Inst\".S", "Inst.S", "\"INST\".S" })
        {
            var callee = Block([M("Input", "I_A"), M("Input", "I_B"), M("Output", "O1"), M("Output", "O2"), M("Static", "S")],
                Unit(1, "FlgNet", A("U/1/1", "S", V("I_A"))),
                Unit(2, "FlgNet", A("U/2/1", target, IrExpr.Not(V("I_A")))),
                Unit(3, "FlgNet", A("U/3/1", "O1", IrExpr.And([V("S"), V("I_B")]))),
                Unit(4, "FlgNet", A("U/4/1", "O2", IrExpr.Not(V("I_A")))));
            Assert.Null(Resolve(callee, "O1", CallAB));
            Assert.NotEqual(ProofVerdict.Proven, Probe(callee));
        }
    }

    [Fact]
    public void R7b_OwnInstanceMentionedInTextGivesNull()
    {
        var callee = Block([M("Input", "I_A"), M("Output", "O1"), M("Static", "S")],
            Unit(1, "FlgNet", A("U/1/1", "S", V("I_A"))),
            Unit(2, "StructuredText", new IrStatement("U/2/1", IrStatementKinds.Source, null, IrExpr.True, null, Text: "\"Inst\".S := FALSE;")),
            Unit(3, "FlgNet", A("U/3/1", "O1", V("S"))));
        Assert.Null(Resolve(callee, "O1", CallAB));
    }
}
