#nullable enable
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.Understanding.SclXml;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

/// <summary>计划 4 Task 8~10 审查修正（I-1~I-5、M-1~M-4）的回归。</summary>
public class SclReviewFixTests
{
    static IrStatement[] Translate(string xml, System.Func<string, bool>? knownBool = null) =>
        SclTranslator.Translate(XElement.Parse(xml), "Blk/3", null, knownBool).ToArray();

    static string[] Text(IrStatement[] statements) => statements.Select(IrText.Statement).ToArray();

    static IrBlock BlockOf(IrStatement[] statements, string iface = "") =>
        IrBlock.FromUnits("B", [new IrUnit(1, "B", 3, "SCL", "StructuredText", statements, [], [])], InterfaceReader.Read(iface));

    // ---- I-1：类型不明的逻辑运算不透明 ----

    [Fact]
    public void LogicOnOperandsOfUnknownTypeIsAnOpaqueFunction()
    {
        var s = Assert.Single(Translate(Build("$W", ":=", "$X", "AND", "NOT", "$Y", ";")));
        Assert.Equal("[Blk/3/1] W := AND(X, NOT(Y))", IrText.Statement(s));
        Assert.Equal(IrKinds.Func, s.Value!.Kind);
        Assert.Equal(IrKinds.Func, s.Value.Args![1].Kind);   // NOT 也是不透明函数，不是逻辑取反
    }

    [Fact]
    public void OneKnownBoolOperandIsNotEnough() =>
        // 博图不开 IEC 检查时 Bool 可以隐式转成字，一个操作数是 Bool 不能断定另一个也是
        Assert.Equal(["[Blk/3/1] W := OR(A, X)"], Text(Translate(Build("$W", ":=", "$A", "OR", "$X", ";"), n => n == "A")));

    [Fact]
    public void ComparisonsAndBoolConstantsAreBoolOperands() =>
        Assert.Equal(["[Blk/3/1] W := ((N == 1) OR FALSE)"], Text(Translate(Build("$W", ":=", "$N", "=", "#1", "OR", "FALSE", ";"))));

    [Fact]
    public void BoolTargetMakesItsLogicOperandsBool() =>
        Assert.Equal(["[Blk/3/1] Y := (X AND NOT(Z))"], Text(Translate(Build("$Y", ":=", "$X", "AND", "NOT", "$Z", ";"), n => n == "Y")));

    [Fact]
    public void ConditionOperandsAreBoolEverywhereInTheSegment() =>
        // IF 的条件一定是 Bool：A、B 在条件里直接参与逻辑运算 → 其它语句里也按 Bool
        Assert.Equal(["[Blk/3/1] W := (A OR B)", "[Blk/3/2] 若 (A AND B) 则 置位 Y（保持为 true）"],
            Text(Translate(Build("$W", ":=", "$A", "OR", "$B", ";", "IF", "$A", "AND", "$B", "THEN", "$Y", ":=", "TRUE", ";", "END_IF", ";"))));

    [Fact]
    public void LogicInsideAComparisonInAConditionStaysOpaque() =>
        // (W AND 16#FF) <> 0：AND 在比较里，不是条件本身，操作数类型不明
        Assert.Equal(["[Blk/3/1] 若 (AND(W, 16#FF) <> 0) 则 置位 Y（保持为 true）"],
            Text(Translate(Build("IF", "(", "$W", "AND", "16#FF", ")", T("<>"), "#0", "THEN", "$Y", ":=", "TRUE", ";", "END_IF", ";"))));

    [Fact]
    public void BoolTagsFromThePointTableMakeLogicTranslatable()
    {
        var raw = Build("$Y", ":=", V("I_A", "GlobalVariable"), "AND", V("I_B", "GlobalVariable"), ";");
        var unit = new PipelineUnit(1, "Blk", 7, "SCL", raw, "", [], 3);
        Assert.Equal("[Blk/3/1] Y := AND(I_A, I_B)", IrText.Statement(Assert.Single(UnitTranslator.Translate(unit).Statements)));
        PipelineTag[] tags = [new("I_A", "%I0.0", "Bool"), new("I_B", "%I0.1", "Bool")];
        Assert.Equal("[Blk/3/1] Y := (I_A AND I_B)", IrText.Statement(Assert.Single(UnitTranslator.Translate(unit, tags).Statements)));
    }

    [Fact]
    public void BoolArrayElementsFromTheInterfaceAreBool()
    {
        var raw = Build("$W", ":=", "$Arr[1]", "AND", "$B", ";");
        var iface = "<Interface><Sections><Section Name=\"Static\"><Member Name=\"Arr\" Datatype=\"Array[0..3] of Bool\" />"
            + "<Member Name=\"B\" Datatype=\"Bool\" /></Section></Sections></Interface>";
        var unit = new PipelineUnit(1, "Blk", 7, "SCL", raw.Replace("<Component Name=\"Arr[1]\" />",
            "<Component Name=\"Arr\"><Access Scope=\"LiteralConstant\"><Constant><ConstantValue>1</ConstantValue></Constant></Access></Component>"), "", [], 3, iface);
        Assert.Equal("[Blk/3/1] W := (Arr[1] AND B)", IrText.Statement(Assert.Single(UnitTranslator.Translate(unit).Statements)));
    }

    // ---- I-2：文字段里提到的名字算可能写入；整体写父结构使成员失效 ----

    [Fact]
    public void NameWrittenInsideOpaqueSourceIsWritten()
    {
        // CASE 里改写 I_Enable，再 O_Out := I_Enable AND X
        var statements = Translate(Build("CASE", "$N", "OF", "#1", ":", "$I_Enable", ":=", "FALSE", ";", "END_CASE", ";",
            "$O_Out", ":=", "$I_Enable", "AND", "$X", ";"), n => n is "I_Enable" or "X" or "O_Out");
        var block = BlockOf(statements);
        Assert.True(block.IsWritten("I_Enable"));
        Assert.NotEmpty(block.Writers("I_Enable"));
        Assert.Null(block.Definition("I_Enable"));
    }

    [Fact]
    public void WholeParentWrittenInSourceInvalidatesTheMemberDefinition()
    {
        var statements = Translate(Build("$Cmd.Run", ":=", "$A", ";",
            "FOR", "$i", ":=", "#1", "TO", "#3", "DO", "$Cmd", ":=", "$Init", ";", "END_FOR", ";"));
        var block = BlockOf(statements);
        Assert.Null(block.Definition("Cmd.Run"));
        Assert.True(block.IsWritten("Cmd.Run"));
        Assert.True(block.IsWritten("Cmd.Run.Sub"));
    }

    [Fact]
    public void IndexedArrayWrittenInSourceCountsForItsElements()
    {
        var statements = Translate(Build("FOR", "$i", ":=", "#1", "TO", "#3", "DO", "$Arr[i]", ":=", "FALSE", ";", "END_FOR", ";"));
        Assert.True(BlockOf(statements).IsWritten("Arr[2]"));
    }

    [Fact]
    public void GuardNoteOfAnOpaqueStatementIsNotAWrite()
    {
        // 审查 M-4：「（仅当 A 时执行）」只是读 A，不算文字里写了 A
        var statements = Translate(Build("IF", "$A", "THEN",
            "FOR", "$i", ":=", "#1", "TO", "#3", "DO", "$Arr[i]", ":=", "$i", ";", "END_FOR", ";", "END_IF", ";"));
        Assert.StartsWith("（仅当 A 时执行）", Assert.Single(statements).Text);
        var block = BlockOf(statements);
        Assert.False(block.IsWritten("A"));
        Assert.True(block.IsWritten("Arr[1]"));
        Assert.False(new ProjectWrites([block.Units[0]], []).MentionedInText("A"));
    }

    [Fact]
    public void NamesInCommentsAreNotWrites()
    {
        // 只有说明注释的 SCL 程序段（FB3100_ValveControl 网络 1 即如此）：注释里的名字不是写入
        var comment = new IrStatement("B/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null,
            "// 3. C_SensorNumber 为 2 时 …\n(* C_Mode 0 *)\nX := 1; // Y\nZ := 'a//b'; W := 0;");
        var block = IrBlock.FromUnits("B", [new IrUnit(1, "B", 1, "SCL", "StructuredText", [comment], [], [])], []);
        Assert.False(block.IsWritten("C_SensorNumber"));
        Assert.False(block.IsWritten("C_Mode"));
        Assert.False(block.IsWritten("Y"));
        Assert.True(block.IsWritten("X"));
        Assert.True(block.IsWritten("W"));   // 字符串里的 // 不是注释
    }

    // ---- I-3：IF 内被写的变量出现在条件或下标里 → 整段不透明 ----

    [Fact]
    public void IfRewritingAnIndexItReadsIsOpaque()
    {
        var s = Assert.Single(Translate(Build("IF", "$Buf[Idx]", "THEN", "$Idx", ":=", "$Idx", "+", "#1", ";",
            "$Done", ":=", "TRUE", ";", "END_IF", ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    [Fact]
    public void IfRewritingAnIndexUsedInItsBodyIsOpaque()
    {
        var s = Assert.Single(Translate(Build("IF", "$A", "THEN", "$i", ":=", "$i", "+", "#1", ";",
            "$Y", ":=", "$Arr[i]", ";", "END_IF", ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    [Fact]
    public void IfRewritingItsConditionIsOpaqueEvenIfMergeable()
    {
        var s = Assert.Single(Translate(Build("IF", "$A", "THEN", "$Y", ":=", "#1", ";", "$A", ":=", "FALSE", ";", "END_IF", ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    // ---- I-4：纯函数的参数解析不了 → 整条不透明 ----

    [Fact]
    public void BlockCallInsideAPureFunctionArgumentMakesTheStatementOpaque()
    {
        var s = Assert.Single(Translate(Build("$Y", ":=",
            Instr("LIMIT", ("MN", T(":=") + C("0")), ("IN", T(":=") + Block("FcGet", "FC")), ("MX", T(":=") + C("10"))), ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }

    // ---- I-5：合并规模上限 ----

    [Fact]
    public void MergeThatWouldExplodeIsOpaque()
    {
        // IF E THEN IF b0 THEN W := W + 1; END_IF; … IF b19 THEN W := W + 20; END_IF; END_IF —— 合并后的值每层翻倍
        var words = new System.Collections.Generic.List<string> { "IF", "$E", "THEN" };
        for (var i = 0; i < 20; i++) words.AddRange(["IF", $"$b{i}", "THEN", "$W", ":=", "$W", "+", $"#{i + 1}", ";", "END_IF", ";"]);
        words.AddRange(["END_IF", ";"]);
        var watch = Stopwatch.StartNew();
        var statements = Translate(Build(words.ToArray()));
        var text = string.Join("\n", Text(statements));
        watch.Stop();
        Assert.Equal(IrStatementKinds.Source, Assert.Single(statements).Kind);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"用时 {watch.ElapsedMilliseconds} ms");
        Assert.True(text.Length < 20000);
    }

    [Fact]
    public void SmallNestedMergeStillWorks()
    {
        var words = new System.Collections.Generic.List<string> { "IF", "$E", "THEN" };
        for (var i = 0; i < 3; i++) words.AddRange(["IF", $"$b{i}", "THEN", "$W", ":=", "$W", "+", $"#{i + 1}", ";", "END_IF", ";"]);
        words.AddRange(["END_IF", ";"]);
        var s = Assert.Single(Translate(Build(words.ToArray())));
        Assert.Equal(IrStatementKinds.Assign, s.Kind);
    }

    // ---- Minor ----

    [Fact]
    public void PeekIsNotAPureFunction() =>
        Assert.Equal(IrStatementKinds.Source, Assert.Single(Translate(Build("$Y", ":=",
            Instr("PEEK", ("area", T(":=") + C("16#84")), ("dbNumber", T(":=") + C("1")), ("byteOffset", T(":=") + C("0"))), ";"))).Kind);

    [Fact]
    public void ConversionNamesAreCheckedAgainstDataTypes()
    {
        // 全大写也不能让 STRG_TO_CHARS（Chars 为输入输出）被当成转换函数；真转换照常
        Assert.Equal(IrStatementKinds.Source, Assert.Single(Translate(Build("$Y", ":=",
            Instr("STRG_TO_CHARS", ("Strg", T(":=") + V("S")), ("pChars", T(":=") + C("0")), ("Chars", T(":=") + V("Arr"))), ";"))).Kind);
        Assert.Equal(IrStatementKinds.Assign, Assert.Single(Translate(Build("$Y", ":=", Instr("WORD_TO_INT", (null, V("W"))), ";"))).Kind);
        Assert.Equal(IrStatementKinds.Assign, Assert.Single(Translate(Build("$Y", ":=", Instr("LREAL_TO_REAL", (null, V("R"))), ";"))).Kind);
    }

    [Fact]
    public void PokeInsideIfMakesTheWholeIfOpaque()
    {
        var poke = Instr("POKE", ("area", T(":=") + C("16#84")), ("dbNumber", T(":=") + C("1")), ("byteOffset", T(":=") + C("0")),
            ("value", T(":=") + V("X")));
        var s = Assert.Single(Translate(Build("IF", "$A", "THEN", poke, ";", "$Y", ":=", "$B", ";", "END_IF", ";")));
        Assert.Equal(IrStatementKinds.Source, s.Kind);
    }
}
