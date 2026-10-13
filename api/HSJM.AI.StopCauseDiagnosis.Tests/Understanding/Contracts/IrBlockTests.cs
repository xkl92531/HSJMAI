#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class IrBlockTests
{
    static IrExpr V(string n) => IrExpr.Var(n);

    internal static IrBlock Valve()
    {
        // FB 的简化程序体：IWP 由传感器个数选择分支；QFwd 驱动 O_FwdOut；超时报警依赖 QFwd、IWP、C_FwdPresetTime
        var n1 = new IrNode("n1", IrExpr.And([V("AlwaysTRUE"), V("I_FwdPos1"), V("I_FwdPos2")]));
        var u5 = new IrUnit(1, "FB_V", 5, "LAD", "FlgNet",
        [
            new("FB_V/5/1", IrStatementKinds.Assign, "Cmd.IWP", IrExpr.True,
                IrExpr.Or([
                    IrExpr.And([new IrExpr(IrKinds.Node, "n1"), new IrExpr(IrKinds.Cmp, "==", [V("C_SensorNumber"), IrExpr.Const("2")])]),
                    IrExpr.And([V("AlwaysTRUE"), V("I_FwdPos1"), new IrExpr(IrKinds.Cmp, "==", [V("C_SensorNumber"), IrExpr.Const("1")])])
                ])),
            new("FB_V/5/2", IrStatementKinds.Assign, "O_FwdOut", IrExpr.True, IrExpr.And([V("AlwaysTRUE"), V("Cmd.QFwd")])),
            new("FB_V/5/3", IrStatementKinds.Assign, "A_Word.Alarm1", IrExpr.True, V("Cmd.FwdErr")),
        ], [n1], []);
        var u6 = new IrUnit(2, "FB_V", 6, "LAD", "FlgNet",
        [
            new("FB_V/6/1", IrStatementKinds.Set, "Cmd.FwdErr",
                IrExpr.And([V("Cmd.QFwd"), IrExpr.Not(V("Cmd.IWP")),
                    new IrExpr(IrKinds.Cmp, ">=", [V("Cmd.FwdTime"), V("C_FwdPresetTime")])]), null),
            new("FB_V/6/2", IrStatementKinds.Assign, "Cmd.QFwd", IrExpr.True, IrExpr.And([V("I_FwdCmd"), IrExpr.Not(V("Cmd.QBkd"))])),
            new("FB_V/6/3", IrStatementKinds.Assign, "Cmd.Flag", IrExpr.True, V("AlwaysTRUE")),
        ], [], []);
        var iface = new[]
        {
            new IfaceMember("Input", "I_FwdPos1", "Bool", null, null, []),
            new IfaceMember("Input", "I_FwdPos2", "Bool", null, null, []),
            new IfaceMember("Input", "I_FwdCmd", "Bool", null, null, []),
            new IfaceMember("Input", "C_SensorNumber", "Int", "1", null, []),
            new IfaceMember("Input", "C_FwdPresetTime", "Real", "10.0", null, []),
            new IfaceMember("Output", "O_FwdOut", "Bool", null, null, []),
            new IfaceMember("Output", "O_BkdOut", "Bool", null, null, []),
            new IfaceMember("Output", "A_Word", "\"UDT_Alarm\"", null, null, []),
            new IfaceMember("Static", "Cmd", "Struct", null, null, []),
        };
        return IrBlock.FromUnits("FB_V", [u5, u6], iface);
    }

    [Fact]
    public void DefinitionExpandsNodesAndRequiresASingleUnguardedAssign()
    {
        var b = Valve();
        var def = b.Definition("Cmd.IWP")!;
        Assert.Contains("I_FwdPos2", def.Vars());
        Assert.Null(b.Definition("Cmd.FwdErr"));      // Set 写入，不是唯一赋值
        Assert.Null(b.Definition("NoSuchVar"));
    }

    [Fact]
    public void DependencyClosureFollowsWritersTransitively()
    {
        var closure = Valve().DependencyClosure("A_Word.Alarm1");
        Assert.Contains("Cmd.FwdErr", closure);
        Assert.Contains("Cmd.QFwd", closure);
        Assert.Contains("Cmd.IWP", closure);
        Assert.Contains("C_FwdPresetTime", closure);
        Assert.Contains("I_FwdPos1", closure);         // 经 Cmd.IWP 的定义
        Assert.Contains("I_FwdCmd", closure);          // 经 Cmd.QFwd 的定义
    }

    [Fact]
    public void FormalsAndStatementIndex()
    {
        var b = Valve();
        Assert.True(b.IsBoolFormal("I_FwdPos1", "Input"));
        Assert.False(b.IsBoolFormal("C_SensorNumber", "Input"));
        Assert.True(b.IsBoolFormal("o_fwdout", "Output"));
        Assert.True(b.StatementsById.ContainsKey("FB_V/6/1"));
        Assert.Equal(new[] { "AlwaysTRUE", "Cmd.QFwd" }, b.DirectSources("O_FwdOut").OrderBy(x => x).ToArray());
        Assert.True(b.HasBody);
        Assert.False(b.IsTextOnly);
    }

    [Fact]
    public void IsLocalMeansRootIsStaticOrTempMember()
    {
        var b = IrBlock.FromUnits("FB_L", [], [
            new IfaceMember("Input", "I_A", "Bool", null, null, []),
            new IfaceMember("Static", "Cmd", "Struct", null, null, []),
            new IfaceMember("Temp", "tmp", "Bool", null, null, []),
        ]);
        Assert.True(b.IsLocal("Cmd.QFwd"));
        Assert.True(b.IsLocal("cmd[1].x"));
        Assert.True(b.IsLocal("TMP"));
        Assert.False(b.IsLocal("I_A"));
        Assert.False(b.IsLocal("AlwaysTRUE"));
        Assert.False(b.IsLocal("DB_G.Cmd"));
        Assert.False(b.IsLocal("CmdX.Y"));
        Assert.False(b.IsLocal(""));
    }

    [Fact]
    public void OrderOfFollowsUnitThenStatementOrder()
    {
        var b = Valve();
        Assert.True(b.OrderOf("FB_V/5/1") < b.OrderOf("FB_V/5/3"));
        Assert.True(b.OrderOf("FB_V/5/3") < b.OrderOf("FB_V/6/1"));
        Assert.Equal(-1, b.OrderOf("FB_V/9/9"));
    }

    static IrBlock WithExtra(IrStatement extra)
    {
        var u = new IrUnit(9, "FB_X", 1, "SCL", "StructuredText",
        [
            new("FB_X/1/1", IrStatementKinds.Assign, "Cmd.IWP", IrExpr.True, V("I_FwdPos1")),
            new("FB_X/1/2", IrStatementKinds.Assign, "Cmd.QFwd", IrExpr.True, V("I_FwdCmd")),
            extra,
        ], [], []);
        return IrBlock.FromUnits("FB_X", [u], []);
    }

    [Fact]
    public void ControlFlowAnywhereInTheBlockSuppressesDefinitions()
    {
        foreach (var kind in new[] { IrStatementKinds.Jump, IrStatementKinds.JumpNot, IrStatementKinds.Return })
        {
            var b = WithExtra(new("FB_X/1/3", kind, "L1", V("X"), null));
            Assert.True(b.HasControlFlow);
            Assert.Null(b.Definition("Cmd.IWP"));
            Assert.Null(b.Definition("Cmd.QFwd"));
        }
        Assert.False(Valve().HasControlFlow);
    }

    [Fact]
    public void SourceTextMentioningAVariableSuppressesOnlyThatDefinition()
    {
        var b = WithExtra(new("FB_X/1/3", IrStatementKinds.Source, null, IrExpr.True, null, Text: "IF #x THEN\n  cmd.iwp := x;\nEND_IF;"));
        Assert.Null(b.Definition("Cmd.IWP"));
        Assert.NotNull(b.Definition("Cmd.QFwd"));
    }

    [Fact]
    public void SourceTextMatchesWholeNamesOnly()
    {
        var b = WithExtra(new("FB_X/1/3", IrStatementKinds.Source, null, IrExpr.True, null, Text: "Cmd.IWP2 := 1; XCmd.QFwd := 0; Cmd.QFwd.Sub := 1; Cmd.QFwd[1] := 0;"));
        Assert.NotNull(b.Definition("Cmd.IWP"));   // Cmd.IWP2 / XCmd.* 不是它
        // 审查 I-2：文字里写它的成员（Cmd.QFwd.Sub、Cmd.QFwd[1]）就改了它本身，唯一定义失效
        Assert.Null(b.Definition("Cmd.QFwd"));
        Assert.True(b.IsWritten("Cmd.QFwd"));
    }

    // ---- 评测迭代 3：纯拷贝别名 ----

    static IrBlock Chain(params (string Target, IrExpr Value)[] assigns)
    {
        var u = new IrUnit(1, "FB_A", 1, "LAD", "FlgNet",
            assigns.Select((a, i) => new IrStatement($"FB_A/1/{i + 1}", IrStatementKinds.Assign, a.Target, IrExpr.True, a.Value)).ToArray(), [], []);
        return IrBlock.FromUnits("FB_A", [u],
            [new IfaceMember("Input", "I", "Bool", null, null, []), new IfaceMember("Static", "Cmd", "Struct", null, null, [])]);
    }

    [Fact]
    public void AliasesAreBidirectionalAndTransitive()
    {
        var b = Chain(("Cmd.A", IrExpr.And([V("AlwaysTRUE"), V("I")])), ("Cmd.B", V("Cmd.A")), ("Cmd.C", IrExpr.And([V("Cmd.B"), IrExpr.True])));
        Assert.Equal(new[] { "Cmd.A", "Cmd.B", "Cmd.C", "I" }, b.Aliases("I").Order().ToArray());
        var fromC = b.Aliases("cmd.c");   // 忽略大小写
        Assert.Equal(4, fromC.Count);
        Assert.Contains("I", fromC);
        Assert.Contains("Cmd.A", fromC);
        Assert.Equal(new[] { "X" }, b.Aliases("X").ToArray());
    }

    [Fact]
    public void AliasChainIsCutAfterEightHops()
    {
        var steps = Enumerable.Range(1, 9).Select(i => ($"Cmd.V{i}", V(i == 1 ? "I" : $"Cmd.V{i - 1}"))).ToArray();
        var aliases = Chain(steps).Aliases("I");
        Assert.Contains("Cmd.V8", aliases);
        Assert.DoesNotContain("Cmd.V9", aliases);
    }

    [Fact]
    public void AliasCycleTerminates()
    {
        var b = Chain(("Cmd.A", V("Cmd.B")), ("Cmd.B", V("Cmd.A")));
        Assert.Equal(new[] { "Cmd.A", "Cmd.B" }, b.Aliases("Cmd.A").Order().ToArray());
    }

    [Fact]
    public void WrittenGlobalIsNotDroppedAsAConstant()
    {
        // G 在本块被写入：不是常真文字，Cmd.A := G ∧ I 不是 I 的拷贝
        var b = Chain(("G", V("I")), ("Cmd.A", IrExpr.And([V("G"), V("Cmd.Z")])));
        Assert.DoesNotContain("Cmd.A", b.Aliases("Cmd.Z"));
    }

    // ---- 迭代 4 修正 I（迭代 3 审查 Minor-1）：常量的拷贝仍是常量，不当别名 ----

    [Fact]
    public void CopyOfAConstantIsNotAnAlias()
    {
        // Cmd.En := AlwaysTRUE；P := G ∧ Cmd.En → P 不是任何变量的别名，Cmd.En 也不是 AlwaysTRUE 的别名
        var b = Chain(("Cmd.En", V("AlwaysTRUE")), ("Cmd.P", IrExpr.And([V("G"), V("Cmd.En")])));
        Assert.Equal(new[] { "Cmd.P" }, b.Aliases("Cmd.P").ToArray());
        Assert.Equal(new[] { "Cmd.En" }, b.Aliases("Cmd.En").ToArray());
        Assert.Equal(new[] { "AlwaysTRUE" }, b.Aliases("AlwaysTRUE").ToArray());
    }

    [Fact]
    public void CopyOfAConstantTrueLiteralIsNotAnAlias()
    {
        var b = Chain(("Cmd.T", IrExpr.True), ("Cmd.P", IrExpr.And([V("Cmd.T"), V("I")])), ("Cmd.Q", V("G")));
        Assert.Contains("I", b.Aliases("Cmd.P"));          // Cmd.T 恒真，去掉后 P 是 I 的拷贝
        Assert.Equal(new[] { "Cmd.Q" }, b.Aliases("Cmd.Q").ToArray());   // 单独拷贝一个常真全局量也不算
    }

    // ---- 迭代 3 审查 Minor-2：文字段里写过的变量不当「未写入」，也不当唯一定义 ----

    static IrBlock ChainWithText(string text, params (string Target, IrExpr Value)[] assigns)
    {
        var b = Chain(assigns);
        var u = new IrUnit(2, "FB_A", 2, "GRAPH", "Actions",
            [new IrStatement("FB_A/2/1", IrStatementKinds.Action, "", IrExpr.True, null, null, text)], [], []);
        return IrBlock.FromUnits("FB_A", [.. b.Units, u], b.Interface);
    }

    [Fact]
    public void GlobalWrittenInAnActionIsNotDroppedAsAConstant()
    {
        var b = ChainWithText("G := FALSE", ("Cmd.P", IrExpr.And([V("G"), V("I")])));
        Assert.DoesNotContain("I", b.Aliases("Cmd.P"));
        Assert.Contains("I", ChainWithText("X := FALSE", ("Cmd.P", IrExpr.And([V("G"), V("I")]))).Aliases("Cmd.P"));
    }

    [Fact]
    public void VariableAlsoWrittenInAnActionIsNotACopy()
    {
        var b = ChainWithText("Cmd.P := TRUE", ("Cmd.P", V("I")));
        Assert.DoesNotContain("I", b.Aliases("Cmd.P"));
    }

    // ---- 迭代 3 审查 Minor-5 补测 ----

    [Fact]
    public void ControlFlowSuppressesAliases()
    {
        var u = new IrUnit(1, "FB_A", 1, "LAD", "FlgNet",
        [
            new IrStatement("FB_A/1/1", IrStatementKinds.Assign, "Cmd.A", IrExpr.True, V("I")),
            new IrStatement("FB_A/1/2", IrStatementKinds.Jump, "L1", V("X"), null),
        ], [], []);
        var b = IrBlock.FromUnits("FB_A", [u], Chain().Interface);
        Assert.Equal(new[] { "I" }, b.Aliases("I").ToArray());
    }

    [Fact]
    public void SelfCopyIsNotAnAlias()
    {
        var b = Chain(("Cmd.A", IrExpr.And([V("AlwaysTRUE"), V("Cmd.A")])));
        Assert.Equal(new[] { "Cmd.A" }, b.Aliases("Cmd.A").ToArray());
    }

    // ---- 迭代 4 审查 Minor-4：只有 Static 与全局量可由「唯一定义全是常真」推出常真；Temp、形参不算 ----

    [Theory]
    [InlineData("Temp")]
    [InlineData("Input")]
    public void TempOrFormalDefinedAsAConstantIsNotAConstant(string section)
    {
        var u = new IrUnit(1, "FB_A", 1, "LAD", "FlgNet",
        [
            new IrStatement("FB_A/1/1", IrStatementKinds.Assign, "K", IrExpr.True, V("AlwaysTRUE")),
            new IrStatement("FB_A/1/2", IrStatementKinds.Assign, "Cmd.P", IrExpr.True, IrExpr.And([V("K"), V("I")])),
        ], [], []);
        var b = IrBlock.FromUnits("FB_A", [u],
        [
            new IfaceMember("Input", "I", "Bool", null, null, []), new IfaceMember("Static", "Cmd", "Struct", null, null, []),
            new IfaceMember(section, "K", "Bool", null, null, []),
        ]);
        Assert.DoesNotContain("I", b.Aliases("Cmd.P"));
    }

    [Fact]
    public void StaticDefinedAsAConstantIsStillAConstant()
    {
        var b = Chain(("Cmd.K", V("AlwaysTRUE")), ("Cmd.P", IrExpr.And([V("Cmd.K"), V("I")])));
        Assert.Contains("I", b.Aliases("Cmd.P"));
    }

    [Fact]
    public void DeepConstantChainIsEvaluatedOnce()
    {
        // 每层两个常真文字：没有缓存时最坏 2^d 次；这里只要正确且很快结束
        var steps = new List<(string, IrExpr)> { ("Cmd.C0", V("AlwaysTRUE")) };
        for (var i = 1; i <= 30; i++) steps.Add(($"Cmd.C{i}", IrExpr.And([V($"Cmd.C{i - 1}"), V($"Cmd.C{i - 1}x")])));
        for (var i = 0; i < 30; i++) steps.Add(($"Cmd.C{i}x", V($"Cmd.C{i}")));
        steps.Add(("Cmd.P", IrExpr.And([V("Cmd.C30"), V("I")])));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("I", Chain([.. steps]).Aliases("Cmd.P"));
        Assert.True(sw.Elapsed < System.TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CommentOnlySourceDoesNotSuppressDefinitions()
    {
        var b = WithExtra(new("FB_X/1/3", IrStatementKinds.Source, null, IrExpr.True, null, Text: "// 气缸控制说明"));
        Assert.False(b.HasControlFlow);
        Assert.NotNull(b.Definition("Cmd.IWP"));
        Assert.NotNull(b.Definition("Cmd.QFwd"));
    }

    // ---- 整分支审查 I-4：父/子符号的重叠写入 ----

    /// <summary>Valve() 之外追加一段：其中 extra 为额外写入（父结构整体写 / 子成员写 / 下标写法）。</summary>
    static IrBlock ValveWith(params IrStatement[] extra)
    {
        var v = Valve();
        return IrBlock.FromUnits("FB_V", [.. v.Units, new IrUnit(9, "FB_V", 9, "LAD", "FlgNet", extra, [], [])], v.Interface);
    }

    static IrStatement CallWriting(string pinSection, string target) =>
        new("FB_V/9/1", IrStatementKinds.Call, null, IrExpr.True, null,
            new IrCall("FB_Sub", "FB", "Sub", IrExpr.True,
                [new IrPin("Out", pinSection, "Struct", pinSection == "InOut" ? V(target) : null, [target])]));

    [Theory]
    [InlineData("Output")]   // 调用输出引脚整体写父结构
    [InlineData("InOut")]    // InOut 引脚接父结构
    public void CallPinWritingTheParentStructRemovesTheDefinition(string section)
    {
        var b = ValveWith(CallWriting(section, "Cmd"));
        Assert.Null(b.Definition("Cmd.IWP"));
        Assert.True(b.IsWritten("Cmd.IWP"));
        Assert.Contains("Cmd", b.OverlappingTargets("Cmd.IWP"));
    }

    [Fact]
    public void MoveIntoTheParentStructRemovesTheDefinition()
    {
        var b = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "cmd", IrExpr.True, V("Other")));
        Assert.Null(b.Definition("Cmd.IWP"));
        Assert.Null(b.Definition("Cmd.QFwd"));
    }

    [Fact]
    public void WritingAChildMemberRemovesTheDefinitionOfTheParent()
    {
        var b = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.IWP.%X0", IrExpr.True, V("Other")));
        Assert.Null(b.Definition("Cmd.IWP"));
        Assert.NotNull(b.Definition("Cmd.QFwd"));   // 兄弟成员不受影响
    }

    [Fact]
    public void SiblingWritesDoNotOverlap()
    {
        var b = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.IWPX", IrExpr.True, V("Other")),
            new IrStatement("FB_V/9/2", IrStatementKinds.Assign, "CmdX", IrExpr.True, V("Other")));
        Assert.NotNull(b.Definition("Cmd.IWP"));
        Assert.Empty(b.OverlappingTargets("Cmd.IWP"));
        Assert.False(b.IsWritten("Cmd.Nothing"));
    }

    [Fact]
    public void NonLiteralIndexMayOverlapAnyElement()
    {
        var u = new IrUnit(9, "FB_X", 1, "LAD", "FlgNet",
        [
            new("FB_X/1/1", IrStatementKinds.Assign, "Arr[3].b", IrExpr.True, V("I1")),
            new("FB_X/1/2", IrStatementKinds.Assign, "Arr[2].b", IrExpr.True, V("I2")),
            new("FB_X/1/3", IrStatementKinds.Assign, "Brr[#i].b", IrExpr.True, V("I3")),
            new("FB_X/1/4", IrStatementKinds.Assign, "Brr[ 4 ].b", IrExpr.True, V("I4")),
        ], [], []);
        var b = IrBlock.FromUnits("FB_X", [u], []);
        Assert.NotNull(b.Definition("Arr[3].b"));    // 字面量不同的下标不重叠
        Assert.Null(b.Definition("Brr[ 4 ].b"));     // 与 Brr[#i] 可能同址
        Assert.Null(b.Definition("Brr[#i].b"));
        Assert.True(b.IsWritten("Brr[7]"));
    }

    [Fact]
    public void SegmentsSplitMembersAndIndexes()
    {
        Assert.Equal(new[] { "\"DB.x\"", "A", "[Idx[1]]", "b", "%X3" }, IrBlock.Segments("\"DB.x\".A[Idx[1]].b.%X3"));
    }

    static IfaceMember Mem(string section, string name, string type = "Bool") => new(section, name, type, null, null, []);

    static IrBlock OneUnit(IfaceMember[] iface, params IrStatement[] statements) =>
        IrBlock.FromUnits("FB_W", [new IrUnit(1, "FB_W", 1, "LAD", "FlgNet", statements, [], [])], iface);

    static IrStatement Asg(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);

    [Fact]
    public void BlockMoveToAnElementMayWriteTheWholeArray()
    {
        var move = new IrExpr(IrKinds.Instr, "MOVE_BLK|OUT", [new IrExpr(IrKinds.Port, "IN", [V("Src[0]")])]);
        var b = OneUnit([Mem("Static", "Arr", "Array[0..9] of Bool")],
            Asg("W/1/1", "Arr[1]", V("I_A")), Asg("W/1/2", "Arr[0]", move));
        Assert.Null(b.Definition("Arr[1]"));
        Assert.True(b.IsWritten("Arr[5]"));
    }

    [Fact]
    public void BlockMoveToAStructureMayWriteTheWholeRoot()
    {
        var call = new IrCall("FILL_BLK", "", "", IrExpr.True, [new IrPin("OUT", "Output", "Any", null, ["Cmd.Part"])]);
        var b = OneUnit([Mem("Static", "Cmd", "Struct")],
            Asg("W/1/1", "Cmd.X", V("I_A")), new IrStatement("W/1/2", IrStatementKinds.Call, null, IrExpr.True, null, call));
        Assert.Null(b.Definition("Cmd.X"));
    }

    [Fact]
    public void PlainMoveToAnotherElementDoesNotBlockTheDefinition()
    {
        var b = OneUnit([Mem("Static", "Arr", "Array[0..9] of Bool")],
            Asg("W/1/1", "Arr[1]", V("I_A")), Asg("W/1/2", "Arr[0]", V("I_B")));
        Assert.NotNull(b.Definition("Arr[1]"));
    }

    [Fact]
    public void UnrecognizedTargetMayWriteAnything()
    {
        var b = OneUnit([Mem("Static", "S"), Mem("Temp", "T")],
            Asg("W/1/1", "S", V("I_A")), Asg("W/1/2", "T", V("I_A")), Asg("W/1/3", "〔未识别的访问 LocalVariable〕", V("I_B")));
        Assert.Null(b.Definition("S"));
        Assert.Null(b.Definition("T"));
        Assert.True(b.IsWritten("Anything"));
    }

    [Fact]
    public void AreaWritesAreNarrowedByArea()
    {
        var b = OneUnit([Mem("Static", "S"), Mem("Temp", "T"), Mem("Output", "O")],
            Asg("W/1/1", "S", V("I_A")), Asg("W/1/2", "T", V("I_A")), Asg("W/1/3", "O", V("I_A")),
            Asg("W/1/4", "QB[AR1,P#0.0]", V("I_B")));
        Assert.NotNull(b.Definition("S"));        // Q 区写入改不到本块 Static/Temp/输出形参
        Assert.NotNull(b.Definition("T"));
        Assert.NotNull(b.Definition("O"));
        Assert.True(b.IsWritten("Flag"));         // 符号全局名可能在 Q 区
        Assert.True(b.IsWritten("%Q4.1"));
        Assert.False(b.IsWritten("%M4.1"));
        var db = OneUnit([Mem("Static", "S"), Mem("Temp", "T")],
            Asg("W/1/1", "S", V("I_A")), Asg("W/1/2", "T", V("I_A")), Asg("W/1/3", "DIX[AR1,P#0.0]", V("I_B")));
        Assert.Null(db.Definition("S"));          // DB 区（背景 DB）可能改到 Static
        Assert.NotNull(db.Definition("T"));
        var local = OneUnit([Mem("Static", "S"), Mem("Temp", "T")],
            Asg("W/1/1", "S", V("I_A")), Asg("W/1/2", "T", V("I_A")), Asg("W/1/3", "L[AR1,P#0.0]", V("I_B")));
        Assert.NotNull(local.Definition("S"));
        Assert.Null(local.Definition("T"));
    }

    [Fact]
    public void AbsoluteAddressWritesOverlapByRange()
    {
        var b = OneUnit([], Asg("W/1/1", "%MB10", V("I_A")), Asg("W/1/2", "%M10.3", V("I_B")), Asg("W/1/3", "%M11.0", V("I_B")));
        Assert.Null(b.Definition("%M10.3"));      // %MB10 覆盖它
        Assert.True(b.IsWritten("Flag"));
        Assert.True(b.IsWritten("%M10.5"));
        Assert.False(b.IsWritten("%M12.0"));
    }

    [Fact]
    public void CallInstanceAndSideWritesCountAsWrites()
    {
        var call = new IrCall("FB_Sub", "FB", "Sub", IrExpr.True, []);
        var b = OneUnit([Mem("Static", "Sub", "FB_Sub"), Mem("Static", "Tmr", "TON_TIME")],
            Asg("W/1/1", "Sub.Q", V("I_A")),
            new IrStatement("W/1/2", IrStatementKinds.Call, null, IrExpr.True, null, call),
            new IrStatement("W/1/3", IrStatementKinds.Assign, "X", IrExpr.True, V("I_A")) { SideWrites = ["Tmr"] });
        Assert.Null(b.Definition("Sub.Q"));
        Assert.True(b.IsWritten("Tmr.Q"));
    }

    [Fact]
    public void SpacesInsideIndexesAndQuotesAreNotUnrecognizedTargets()
    {
        var b = OneUnit([Mem("Static", "S")], Asg("W/1/1", "S", V("I_A")),
            Asg("W/1/2", "Brr[ 4 ].b", V("I_B")), Asg("W/1/3", "\"My DB\".x", V("I_B")));
        Assert.NotNull(b.Definition("S"));
    }

    [Fact]
    public void QuestionMarkTargetMayWriteAnything()
    {
        // 「?」：连线指向不存在的访问（XML 损坏），按本块可能写任何量处理（ProjectWrites 仍不算写入）
        var b = OneUnit([Mem("Static", "S"), Mem("Temp", "T"), Mem("Output", "O")],
            Asg("W/1/1", "S", V("I_A")), Asg("W/1/2", "T", V("I_A")), Asg("W/1/3", "O", V("I_A")), Asg("W/1/4", "?", V("I_B")));
        Assert.Null(b.Definition("S"));
        Assert.Null(b.Definition("T"));
        Assert.Null(b.Definition("O"));
        Assert.True(b.IsWritten("Anything"));
        Assert.True(b.IsWritten("%Q1.0"));
        var pinTarget = OneUnit([Mem("Static", "S")], Asg("W/1/1", "S", V("I_A")),
            new IrStatement("W/1/2", IrStatementKinds.Call, null, IrExpr.True, null,
                new IrCall("FB_Sub", "FB", "", IrExpr.True, [new IrPin("Q", "Output", "Bool", null, ["?"])])));
        Assert.Null(pinTarget.Definition("S"));
    }
}
