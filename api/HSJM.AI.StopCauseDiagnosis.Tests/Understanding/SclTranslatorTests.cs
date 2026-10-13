#nullable enable
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.Understanding.SclXml;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class SclTranslatorTests
{
    static IrStatement[] Translate(string xml, System.Func<string, bool>? knownBool = null) =>
        SclTranslator.Translate(XElement.Parse(xml), "Blk/3", null, knownBool).ToArray();

    static string[] Text(IrStatement[] statements) => statements.Select(IrText.Statement).ToArray();

    static IrBlock BlockOf(IrStatement[] statements) =>
        IrBlock.FromUnits("B", [new IrUnit(1, "B", 3, "SCL", "StructuredText", statements, [], [])], []);

    [Fact]
    public void AssignmentWithLogicIsTranslated() =>
        // 审查 I-1：操作数都确定是 Bool 时才按逻辑运算翻译
        Assert.Equal(["[Blk/3/1] Y := (A AND NOT(B))"],
            Text(Translate(Build("$Y", ":=", "$A", "AND", "NOT", "$B", ";"), n => n is "A" or "B")));

    [Fact]
    public void ComparisonArithmeticAndParenthesesAreTranslated() =>
        Assert.Equal(["[Blk/3/1] Y := ((N == 1) OR ((M + 2) > 5))"],
            Text(Translate(Build("$Y", ":=", "$N", "=", "#1", "OR", "(", "$M", "+", "#2", ")", ">", "#5", ";"))));

    [Fact]
    public void UnsupportedStatementStaysSourceAndBlocksDefinitionsItMentions()
    {
        var statements = Translate(Build("$Arr", ":=", "$A", ";",
            "FOR", "$i", ":=", "#1", "TO", "#3", "DO", "$Arr", ":=", "$i", ";", "END_FOR", ";",
            "$Z", ":=", "$A", ";"));
        Assert.Equal([IrStatementKinds.Assign, IrStatementKinds.Source, IrStatementKinds.Assign], statements.Select(s => s.Kind).ToArray());
        Assert.Equal("FOR i := 1 TO 3 DO Arr := i ; END_FOR ;", statements[1].Text);
        var block = BlockOf(statements);
        Assert.Null(block.Definition("Arr"));                // 源码文字里还写了它
        Assert.Equal("A", IrText.Expr(block.Definition("Z")!));
        Assert.False(block.IsTextOnly);
    }

    [Fact]
    public void RegionIsTransparent() =>
        Assert.Equal(["[Blk/3/1] Y := A"],
            Text(Translate(Build("REGION", "<Text>区域</Text>", "$Y", ":=", "$A", ";", "END_REGION"))));

    [Fact]
    public void TimerInstanceCallBecomesAnExecuteStatement() =>
        Assert.Equal(["[Blk/3/1] 执行 TON[T1](IN=A, PT=T#2s).Q（输出未直接接线，结果可能在别处读取）"],
            Text(Translate(Build(Timer("T1", "TON", "A", "T#2s"), ";"))));

    [Fact]
    public void ReturnInsideIfIsAGuardedReturnAndMakesTheBlockControlFlow()
    {
        var statements = Translate(Build("IF", "$A", "THEN", "RETURN", ";", "END_IF", ";", "$Z", ":=", "$B", ";"));
        Assert.Equal(["[Blk/3/1] 若 A 则 返回（本块后续语句不执行）", "[Blk/3/2] Z := B"], Text(statements));
        var block = BlockOf(statements);
        Assert.True(block.HasControlFlow);
        Assert.Null(block.Definition("Z"));
    }

    [Fact]
    public void IfWhoseBodyWritesItsOwnConditionStaysSource()
    {
        var statements = Translate(Build("IF", "$A", "THEN", "$A", ":=", "FALSE", ";", "$B", ":=", "TRUE", ";", "END_IF", ";"),
            n => n is "A" or "B");
        var s = Assert.Single(statements);
        Assert.Equal(IrStatementKinds.Source, s.Kind);
        Assert.Equal("IF A THEN A := FALSE ; B := TRUE ; END_IF ;", s.Text);
    }

    [Fact]
    public void GotoInsideOpaqueTextAddsAReturnMarker()
    {
        var statements = Translate(Build("GOTO", "<Access Scope=\"Label\"><Label Name=\"L1\" /></Access>", ";"));
        Assert.Equal([IrStatementKinds.Source, IrStatementKinds.Return], statements.Select(s => s.Kind).ToArray());
        Assert.True(SclTranslator.IsSourceControlMarker(statements[1]));
    }

    [Fact]
    public void CommentOnlyBodyIsASingleSourceStatement()
    {
        var s = Assert.Single(Translate("<StructuredText><LineComment><Text>只有注释</Text></LineComment></StructuredText>"));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    // ---- 裁定 F5：块调用与可能写入的调用不当无副作用函数 ----

    [Fact]
    public void BlockCallInsideAnExpressionMakesTheStatementOpaque()
    {
        var statements = Translate(Build("$Y", ":=", Block("FcGet", "FC", ("N", T(":=") + C("1"))), ";", "$Z", ":=", "$A", ";"));
        Assert.Equal([IrStatementKinds.Source, IrStatementKinds.Assign], statements.Select(s => s.Kind).ToArray());
        Assert.Equal("Y := FcGet(N := 1) ;", statements[0].Text);
        Assert.Null(BlockOf(statements).Definition("Y"));
    }

    [Fact]
    public void BlockCallStatementStaysSource()
    {
        var s = Assert.Single(Translate(Build(Block("FbDo", "FB", ("In1", T(":=") + V("A"))), ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
        Assert.Equal("FbDo(In1 := A) ;", s.Text);
    }

    [Fact]
    public void InOutOrOutputSectionParameterMakesTheCallOpaque()
    {
        var call = "<Access Scope=\"Call\"><Instruction Name=\"LIMIT\">" + T("(")
            + "<Parameter Name=\"MN\" Section=\"Input\">" + C("0") + "</Parameter>" + T(",")
            + "<Parameter Name=\"IN\" Section=\"InOut\">" + V("X") + "</Parameter>" + T(")") + "</Instruction></Access>";
        var s = Assert.Single(Translate(Build("$Y", ":=", call, ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    [Fact]
    public void OutputArrowParameterMakesTheCallOpaque()
    {
        var s = Assert.Single(Translate(Build("$Y", ":=", Instr("FIND", ("IN1", T(":=") + V("S")), ("OUT", T("=>") + V("P"))), ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    [Fact]
    public void InstructionThatMayWriteItsArgumentsIsOpaque()
    {
        // Strg_TO_Chars 的 Chars 是输入输出参数（导出里写作 :=）：不在纯函数指令表里，整条不透明
        var s = Assert.Single(Translate(Build("$Y", ":=",
            Instr("Strg_TO_Chars", ("Strg", T(":=") + V("S")), ("pChars", T(":=") + C("0")), ("Chars", T(":=") + V("Arr"))), ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    [Fact]
    public void PureConversionInstructionIsAFunction() =>
        Assert.Equal(["[Blk/3/1] Y := INT_TO_WORD(N)"],
            Text(Translate(Build("$Y", ":=", Instr("INT_TO_WORD", (null, V("N"))), ";"))));

    [Fact]
    public void UnnamedInstanceCallStaysSource()
    {
        // #R_TRIG[1](CLK := A)：实例调用（指令无 Name），写实例，保留为源码
        var s = Assert.Single(Translate(Build(V("R_TRIG"), Instr(null, ("CLK", T(":=") + V("A"))), ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    [Fact]
    public void LiteralTokensAreConstants() =>
        Assert.Equal(["[Blk/3/1] Z := 16#FF"], Text(Translate(Build("$Z", ":=", "16#FF", ";"))));

    [Fact]
    public void TimerExecuteStatementWritesItsInstance()
    {
        var statements = Translate(Build(Timer("T1", "TON", "A", "T#2s"), ";"));
        Assert.Equal(["T1"], statements[0].SideWrites);
        Assert.True(BlockOf(statements).IsWritten("T1.Q"));
    }

    [Fact]
    public void TimerWithAnExtraParameterStaysSource()
    {
        var call = V("T1") + T(".") + Instr("TONR", ("IN", T(":=") + V("A")), ("R", T(":=") + V("Rst")), ("PT", T(":=") + C("T#2s")));
        Assert.Equal(IrStatementKinds.Source, Assert.Single(Translate(Build(call, ";"))).Kind);
    }

    // ---- 裁定 F7：全部翻不了时，合成的返回语句不让块变成可读 ----

    [Fact]
    public void FullyOpaqueBodyWithReturnIsStillTextOnly()
    {
        var statements = Translate(Build("CASE", "$N", "OF", "#1", ":", "RETURN", ";", "END_CASE", ";"));
        Assert.Equal([IrStatementKinds.Source, IrStatementKinds.Return], statements.Select(s => s.Kind).ToArray());
        var block = BlockOf(statements);
        Assert.True(block.IsTextOnly);
        Assert.True(block.HasControlFlow);
    }

    // ---- 带条件逐条写出的前提 ----

    [Fact]
    public void OpaqueStatementMentioningTheConditionRootKeepsTheIfAsSource()
    {
        // FOR 里整体写 Cmd（条件读的是 Cmd.Run）：不能逐条带条件写出
        var statements = Translate(Build("IF", "$Cmd.Run", "THEN",
            "FOR", "$i", ":=", "#1", "TO", "#3", "DO", "$Cmd", ":=", "$Init", ";", "END_FOR", ";",
            "$Y", ":=", "$A", ";", "END_IF", ";"));
        Assert.Equal(IrStatementKinds.Source, Assert.Single(statements).Kind);
    }

    [Fact]
    public void BlockCallInsideIfKeepsTheIfAsSource()
    {
        // 被调块可能经全局量改写条件变量
        var statements = Translate(Build("IF", "$A", "THEN", Block("FbDo", "FB", ("In1", T(":=") + V("X"))), ";",
            "$Y", ":=", "$B", ";", "END_IF", ";"));
        Assert.Equal(IrStatementKinds.Source, Assert.Single(statements).Kind);
    }

    // ---- SCL 符号里的位切片与下标表达式 ----

    [Fact]
    public void BitSliceTokenIsPartOfTheSymbol()
    {
        // #sxSendBuf.STW1.%X4 := #A：切片以 Token 出现在 Symbol 里，不能丢（否则成了写整个 STW1）
        var target = "<Access Scope=\"LocalVariable\"><Symbol><Component Name=\"sxSendBuf\" />" + T(".")
            + "<Component Name=\"STW1\" />" + T(".") + T("%X4") + "</Symbol></Access>";
        Assert.Equal(["[Blk/3/1] sxSendBuf.STW1.%X4 := A"], Text(Translate(Build(target, ":=", "$A", ";"))));
    }

    [Fact]
    public void IndexExpressionKeepsItsOperators()
    {
        // Valve[2 + PBValve].Bwd：下标是表达式，不能写成二维下标 [2,PBValve]
        var source = "<Access Scope=\"LocalVariable\"><Symbol><Component Name=\"Valve\">" + T("[") + C("2") + "<Blank />" + T("+")
            + "<Blank />" + V("PBValve") + T("]") + "</Component>" + T(".") + "<Component Name=\"Bwd\" /></Symbol></Access>";
        Assert.Equal(["[Blk/3/1] Y := Valve[2+PBValve].Bwd"], Text(Translate(Build("$Y", ":=", source, ";"))));
    }

    // ---- Task 9：IF 合并为条件表达式 ----

    [Fact]
    public void IfElseOnABoolTargetBecomesOneAssignment() =>
        Assert.Equal(["[Blk/3/1] Y := ((A AND B) OR (NOT(A) AND C))"],
            Text(Translate(Build("IF", "$A", "THEN", "$Y", ":=", "$B", ";", "ELSE", "$Y", ":=", "$C", ";", "END_IF", ";"), n => n == "Y")));

    [Fact]
    public void IfElseOnAnUnknownTypeBecomesASelect() =>
        Assert.Equal(["[Blk/3/1] Y := SEL(G=A, 真=B, 假=C)"],
            Text(Translate(Build("IF", "$A", "THEN", "$Y", ":=", "$B", ";", "ELSE", "$Y", ":=", "$C", ";", "END_IF", ";"))));

    [Fact]
    public void ConstantAssignmentsInSomeBranchesBecomeSetAndReset() =>
        Assert.Equal(["[Blk/3/1] 若 A 则 置位 Y（保持为 true）", "[Blk/3/2] 若 B 则 复位 Y（保持为 false）"],
            Text(Translate(Build("IF", "$A", "THEN", "$Y", ":=", "TRUE", ";", "END_IF", ";",
                "IF", "$B", "THEN", "$Y", ":=", "FALSE", ";", "END_IF", ";"))));

    [Fact]
    public void ElsifChainUsesTheEarlierConditionsNegated() =>
        Assert.Equal(["[Blk/3/1] Y := (A OR (NOT(A) AND NOT(B) AND C))"],
            Text(Translate(Build("IF", "$A", "THEN", "$Y", ":=", "TRUE", ";", "ELSIF", "$B", "THEN", "$Y", ":=", "FALSE", ";",
                "ELSE", "$Y", ":=", "$C", ";", "END_IF", ";"), n => n == "Y")));

    [Fact]
    public void InBranchSequentialReadsAreSubstituted() =>
        // X 只在一个分支赋常数 → 置位；Y 读到的是同一分支里 X 的新值（TRUE），代入后不再引用 X
        Assert.Equal(["[Blk/3/1] 若 A 则 置位 X（保持为 true）", "[Blk/3/2] Y := ((A AND B) OR (NOT(A) AND Y))"],
            Text(Translate(Build("IF", "$A", "THEN", "$X", ":=", "TRUE", ";", "$Y", ":=", "$X", "AND", "$B", ";", "END_IF", ";"),
                n => n is "X" or "Y")));

    [Fact]
    public void ReadingAnEarlierWrittenTargetFallsBackToGuardedStatements() =>
        Assert.Equal(["[Blk/3/1] 若 C 则 X := TRUE", "[Blk/3/2] 若 NOT(C) 则 Y := X", "[Blk/3/3] 若 NOT(C) 则 X := FALSE"],
            Text(Translate(Build("IF", "$C", "THEN", "$X", ":=", "TRUE", ";", "ELSE", "$Y", ":=", "$X", ";", "$X", ":=", "FALSE", ";",
                "END_IF", ";"), n => n is "X" or "Y")));

    [Fact]
    public void NestedIfIsMergedWithTheOuterCondition() =>
        Assert.Equal(["[Blk/3/1] Y := ((A AND ((B AND C) OR (NOT(B) AND Y))) OR (NOT(A) AND Y))"],
            Text(Translate(Build("IF", "$A", "THEN", "IF", "$B", "THEN", "$Y", ":=", "$C", ";", "END_IF", ";", "END_IF", ";"),
                n => n == "Y")));

    [Fact]
    public void OverlappingTargetsAreNotMerged()
    {
        // 整体写 Cmd 与写 Cmd.Run 互相覆盖，按目标分别合并会丢先后：退回带条件逐条写出
        Assert.Equal(["[Blk/3/1] 若 A 则 Cmd.Run := TRUE", "[Blk/3/2] 若 A 则 Cmd := Init"],
            Text(Translate(Build("IF", "$A", "THEN", "$Cmd.Run", ":=", "TRUE", ";", "$Cmd", ":=", "$Init", ";", "END_IF", ";"))));
    }

    [Fact]
    public void ReadingAPartOfAnAssignedTargetIsNotMerged()
    {
        // 先整体写 Cmd 再读 Cmd.Run：读到的是新值，按名字代入认不出 → 不合并
        Assert.Equal(["[Blk/3/1] 若 A 则 Cmd := Init", "[Blk/3/2] 若 A 则 Y := Cmd.Run"],
            Text(Translate(Build("IF", "$A", "THEN", "$Cmd", ":=", "$Init", ";", "$Y", ":=", "$Cmd.Run", ";", "END_IF", ";"),
                n => n == "Y")));
    }

    [Fact]
    public void WordLogicOnAnUnknownTargetIsASelectNotABoolFormula() =>
        // 变量类型不明时 AND 可能是按位与（字），不当布尔量合并
        Assert.Equal(["[Blk/3/1] W := SEL(G=A, 真=AND(X, Y), 假=Z)"],
            Text(Translate(Build("IF", "$A", "THEN", "$W", ":=", "$X", "AND", "$Y", ";", "ELSE", "$W", ":=", "$Z", ";", "END_IF", ";"))));

    [Fact]
    public void OpaqueStatementNotTouchingTheConditionIsGuarded()
    {
        var statements = Translate(Build("IF", "$A", "THEN",
            "FOR", "$i", ":=", "#1", "TO", "#3", "DO", "$Arr", ":=", "$i", ";", "END_FOR", ";", "END_IF", ";"));
        var s = Assert.Single(statements);
        Assert.Equal(IrStatementKinds.Source, s.Kind);
        Assert.StartsWith("（仅当 A 时执行）\n", s.Text);
    }
}
