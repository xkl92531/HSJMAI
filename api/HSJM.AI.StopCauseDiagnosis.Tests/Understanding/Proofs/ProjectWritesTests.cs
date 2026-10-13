#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class ProjectWritesTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement A(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);
    static IrStatement Src(string id, string text) => new(id, IrStatementKinds.Source, null, IrExpr.True, null, null, text);
    static IrUnit U(long id, string block, params IrStatement[] s) => new(id, block, 1, "LAD", "FlgNet", s, [], []);

    [Fact]
    public void WritersMatchExactParentAndChildButNotSiblings()
    {
        var writes = new ProjectWrites([
            U(1, "B", A("B/1/1", "DB.X.a", V("I"))),
            U(2, "FC1", A("FC1/1/1", "DB.Y", V("I"))),
            U(3, "B2", new IrStatement("B2/1/1", IrStatementKinds.Set, "Q102.3", V("I"), null)),
        ], []);
        Assert.Single(writes.Writers("DB.X.a"));
        Assert.Single(writes.Writers("DB.X"));        // 子成员被写
        Assert.Single(writes.Writers("DB.Y.b"));      // 整个父结构被写（FC 里的写入也算）
        Assert.Empty(writes.Writers("DB.Z"));
        Assert.Single(writes.Writers("q102.3"));
        Assert.Empty(writes.Writers("Q102.4"));
    }

    [Fact]
    public void CallOutputTargetsAndInstanceCountAsWrites()
    {
        var call = new IrCall("FB_T", "FB", "Inst", IrExpr.True, [new IrPin("O", "Output", "Bool", null, ["Q1"])]);
        var writes = new ProjectWrites([U(1, "OB1", new IrStatement("OB1/1/1", IrStatementKinds.Call, null, IrExpr.True, null, call))], []);
        Assert.Equal(("OB1/1/1"), writes.Writers("Q1").Single().StatementId);
        Assert.Single(writes.Writers("Inst.O"));
    }

    [Fact]
    public void NonLiteralSubscriptMayWriteAnyElement()
    {
        var writes = new ProjectWrites([
            U(1, "B", A("B/1/1", "DB.Arr[#i]", V("I")), A("B/1/2", "DB.Brr[1]", V("I"))),
        ], []);
        Assert.Single(writes.Writers("DB.Arr[3]"));
        Assert.Single(writes.Writers("DB.Arr[3].x"));
        Assert.Single(writes.Writers("DB.Arr"));
        Assert.Single(writes.Writers("DB.Brr[ 1 ]"));
        Assert.Empty(writes.Writers("DB.Brr[2]"));
        Assert.True(ProjectWrites.Overlaps("DB.Arr[#i]", "db.arr[7]"));
        Assert.False(ProjectWrites.Overlaps("DB.Brr[1]", "DB.Brr[2]"));
        Assert.False(ProjectWrites.Overlaps("DB.X", "DB.XY"));
    }

    static readonly PipelineTag[] MemoryTags =
    [
        new("K", "%M1.3", "Bool"), new("MByte", "%MB1", "Byte"), new("I1", "%I0.0", "Bool"), new("Q1", "%Q0.0", "Bool")
    ];

    [Theory]
    [InlineData("%MB1")]
    [InlineData("MByte")]          // 覆盖它的字节标签
    [InlineData("%MW0")]           // 覆盖字节 0~1 的字
    [InlineData("MByte.%X3")]      // 字节标签的位切片，精确到位
    [InlineData("%M1.3")]
    [InlineData("MB[AR1,P#0.0]")]  // 间接寻址：算不出范围，视为与 M 区所有点重叠
    public void AddressAliasesOfAMemoryBitCountAsWrites(string target)
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", target, V("X")))], MemoryTags);
        Assert.Single(writes.Writers("K"));
        Assert.Empty(writes.Writers("I1"));
        Assert.Empty(writes.Writers("Q1"));
    }

    [Theory]
    [InlineData("%M1.4")]
    [InlineData("MByte.%X4")]
    [InlineData("%MB2")]
    [InlineData("%IB1")]
    public void NonOverlappingAddressesAreNotWrites(string target)
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", target, V("X")))], MemoryTags);
        Assert.Empty(writes.Writers("K"));
    }

    [Fact]
    public void AbsoluteAddressesOverlapAcrossFormsAndGermanMnemonics()
    {
        Assert.True(ProjectWrites.Overlaps("%M1.3", "%MB1"));
        Assert.True(ProjectWrites.Overlaps("%E0.1", "%I0.1"));
        Assert.True(ProjectWrites.Overlaps("%AW2", "%Q3.7"));
        Assert.False(ProjectWrites.Overlaps("%M1.3", "%M1.4"));
        Assert.False(ProjectWrites.Overlaps("%M1.3", "%I1.3"));
        Assert.True(ProjectWrites.Overlaps("%DB5.DBW2", "%DB5.DBX3.1"));
        Assert.False(ProjectWrites.Overlaps("%DB5.DBW2", "%DB6.DBX3.1"));
    }

    [Fact]
    public void AbsoluteDbWritesOverlapEverySymbolicNameButNoProcessImagePoint()
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "%DB5.DBX0.0", V("X")))], MemoryTags);
        Assert.Single(writes.Writers("DB_Data.Flag"));   // 绝对地址换算不到符号：视为同区所有点
        Assert.Single(writes.Writers("%DB5.DBB0"));
        Assert.Empty(writes.Writers("%DB6.DBB0"));
        Assert.Empty(writes.Writers("K"));
        Assert.Empty(writes.Writers("I1"));

        var symbolic = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "DB_Data.Flag", V("X")))], MemoryTags);
        Assert.Single(symbolic.Writers("%DB5.DBX0.0"));
        Assert.Empty(symbolic.Writers("K"));
    }

    [Fact]
    public void TextMentionIsReported()
    {
        var writes = new ProjectWrites([U(1, "FB_S", new IrStatement("FB_S/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "L Q_X\n= Q_Y"))], []);
        Assert.True(writes.MentionedInText("Q_Y"));
        Assert.False(writes.MentionedInText("Q_Z"));
        Assert.False(writes.MentionedInText("Q_"));
    }

    [Fact]
    public void TextMentionHandlesStructuredNamesCaseAndNonAsciiNames()
    {
        var writes = new ProjectWrites([
            U(1, "FB_S", Src("FB_S/1/1", "\"DB_Data\".Cmd.Run := TRUE;\n停启动按钮 := FALSE;")),
            U(2, "G", new IrStatement("G/1/1", IrStatementKinds.Action, null, IrExpr.True, null, null, "N  Valve_A"))
        ], []);
        Assert.True(writes.MentionedInText("\"db_data\".cmd.run"));
        Assert.True(writes.MentionedInText("\"DB_Data\".Cmd"));
        Assert.False(writes.MentionedInText("\"DB_Data\".Cmd.Ru"));
        Assert.True(writes.MentionedInText("启动按钮"));     // 与 [A-Za-z0-9_] 边界的正则同口径
        Assert.False(writes.MentionedInText("启动按钮2"));
        Assert.True(writes.MentionedInText("valve_a"));
        Assert.False(writes.MentionedInText("Valve"));
        Assert.False(writes.MentionedInText(""));
    }

    // 用户裁定 2026-10-02：文字里「"DB名".成员路径」按去引号后的成员路径判提及；字面量下标精确到元素，非字面量下标算整个数组；
    // 只写「"DB名"」不带成员仍算整个 DB 被提到
    [Theory]
    [InlineData("\"DB_A\".Act[37].ON := TRUE;", "DB_A.Act[36].ON", false)]
    [InlineData("\"DB_A\".Act[37].ON := TRUE;", "DB_A.Act[37].ON", true)]
    [InlineData("\"DB_A\".Act[37].ON := TRUE;", "DB_A.Act[37].OFF", false)]
    [InlineData("\"DB_A\".Act[37].ON := TRUE;", "DB_A.Other", false)]
    [InlineData("\"DB_A\".Act[37] := X;", "DB_A.Act[37].ON", true)]          // 整个元素被写
    [InlineData("\"DB_A\".Act[#i].ON := TRUE;", "DB_A.Act[36].ON", true)]    // 非字面量下标：整个数组
    [InlineData("\"DB_A\".Act := X;", "DB_A.Act[36].ON", true)]
    [InlineData("MOVE(IN := X, OUT => \"DB_A\");", "DB_A.Act[36].ON", true)] // 只写 DB 名：整个 DB
    [InlineData("BLKMOV(SRCBLK := \"DB_A\");", "DB_A.Act[36].ON", true)]
    [InlineData("\"DB_B\".PB[1].\"PB3 START\" := TRUE;", "DB_B.PB[1].PB3 START", true)]
    [InlineData("\"DB_B\".PB[1].\"PB3 START\" := TRUE;", "DB_B.PB[2].PB3 START", false)]
    [InlineData("IF NOT\"DB_A\".Act[36].ON THEN", "DB_A.Act[36].ON", true)]  // 引号紧贴关键字：补空格，不粘成一个词
    [InlineData("X := \"DB_A\".Act[36].ON;", "\"DB_A\".Act[36].ON", true)] // 查询名本身带引号
    // 审查 I-2：块移动/填充类指令与带参数的调用把数组元素当起始地址，按整个数组算；下标里带空格/表达式的也按整个数组
    [InlineData("FILL_BLK(IN := FALSE, COUNT := 50, OUT => \"DB\".Arr[0]);", "DB.Arr[5]", true)]
    [InlineData("MOVE_BLK(IN := \"DB\".Src[0], COUNT := 4, OUT => \"DB\".Arr[0]);", "DB.Arr[3].ON", true)]
    [InlineData("UMOVE_BLK(IN := X, COUNT := 4, OUT => \"DB\".Arr[0]);", "DB.Arr[3]", true)]
    [InlineData("#r := Serialize(SRC_VARIABLE := X, DEST_ARRAY => \"DB\".Buf[0], POS := #p);", "DB.Buf[9]", true)]
    [InlineData("CALL BLKMOV\n SRCBLK := P#M0.0 BYTE 4\n DSTBLK := \"DB\".Arr[0]", "DB.Arr[3]", true)]
    [InlineData("\"FB_X_DB\"(InOutPar := \"DB\".Arr[2]);", "DB.Arr[7]", true)]
    [InlineData("\"DB\".Arr[ 36 ].ON := TRUE;", "DB.Arr[5].ON", true)]
    [InlineData("\"DB\".Arr[1, 2] := TRUE;", "DB.Arr[1,2]", true)]
    [InlineData("\"DB\".Arr[#i + 1] := TRUE;", "DB.Arr[4]", true)]
    // 审查 M-4：单引号字符串里的双引号打乱配对时只会多算
    [InlineData("#s := 'a\"b'; \"DB\".Arr[3] := TRUE;", "DB.Arr[3]", true)]
    public void QuotedDbNamesAreJudgedByTheMemberPath(string text, string name, bool mentioned)
    {
        var writes = new ProjectWrites([U(1, "FB_S", Src("FB_S/1/1", text))], []);
        Assert.Equal(mentioned, writes.MentionedInText(name));
    }

    [Theory]
    [InlineData("= M 1.3")]
    [InlineData("T MB1")]
    [InlineData("L %MW0")]
    [InlineData("= MByte")]
    [InlineData("T MB [AR1,P#0.0]")]
    public void TextMentionOfAnAddressAliasCountsForTheTag(string text)
    {
        var writes = new ProjectWrites([U(1, "FB_S", Src("FB_S/1/1", text))], MemoryTags);
        Assert.True(writes.MentionedInText("K"));
        Assert.False(writes.MentionedInText("I1"));
    }

    [Fact]
    public void RootIsTheFirstSegment()
    {
        Assert.Equal("DB", ProjectWrites.Root("DB.X[1].a"));
        Assert.Equal("Arr", ProjectWrites.Root("Arr[#i]"));
        Assert.Equal("K", ProjectWrites.Root("K"));
    }

    [Fact]
    public void ExpandNodesInUsesOnlyTheUnitsNodeTable()
    {
        var unit = new IrUnit(1, "B", 1, "LAD", "FlgNet", [], [new IrNode("n1", IrExpr.And([V("A"), new IrExpr(IrKinds.Node, "n2")])), new IrNode("n2", V("B"))], []);
        var expanded = IrBlock.ExpandNodesIn(unit, new IrExpr(IrKinds.Node, "n1"));
        Assert.Equal(new[] { "A", "B" }, expanded.Vars().ToArray());
    }

    static readonly PipelineTag[] Tags =
    [
        new("Q3", "%Q0.3", "Bool"), new("Q4", "%Q0.4", "Bool"), new("Q5", "%Q0.5", "Bool"), new("Q7", "%Q0.7", "Bool"),
        new("K", "%M1.3", "Bool"), new("I1", "%I0.0", "Bool")
    ];

    [Fact]
    public void ConstantOutputsAreOutputsOnlyAssignedFromConstantsOrUnwrittenMemoryBits()
    {
        IrUnit[] units =
        [
            U(1, "OB1",
                A("OB1/1/1", "Q3", V("K")),                 // K：程序从不写入的 %M → 常量样
                A("OB1/1/2", "Q4", V("DB.x")),              // DB 位可能由 HMI 写 → 不是常量
                A("OB1/1/3", "Q5", IrExpr.Const("FALSE")),
                A("OB1/1/4", "Q7", V("K"))),
            U(2, "OB2", A("OB2/1/1", "Q7", V("I1"))),       // Q7 另有一处由输入写入 → 不是常量
        ];
        var writes = new ProjectWrites(units, Tags);
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, units);
        var set = ConstantOutputs.Find(units, Tags, writes, q.Count);
        Assert.Contains("Q3", set);
        Assert.Contains("Q5", set);
        Assert.DoesNotContain("Q4", set);
        Assert.DoesNotContain("Q7", set);
    }

    [Fact]
    public void OutputsFromUnrecognizedOrConditionalWritesAreNotConstant()
    {
        IrUnit[] units =
        [
            U(1, "OB1",
                A("OB1/1/1", "Q3", IrExpr.Unwired()),                                            // 认不出的值不是常数
                new IrStatement("OB1/1/2", IrStatementKinds.Assign, "Q4", V("I1"), IrExpr.Const("TRUE")),   // 带条件
                new IrStatement("OB1/1/3", IrStatementKinds.Set, "Q5", IrExpr.True, null),
                A("OB1/1/4", "Q7", new IrExpr(IrKinds.Cmp, ">", [V("K"), IrExpr.Const("1")]))),
        ];
        var writes = new ProjectWrites(units, Tags);
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, units);
        Assert.Empty(ConstantOutputs.Find(units, Tags, writes, q.Count));
    }

    [Fact]
    public void MemoryBitWrittenThroughAnAliasIsNotConstantLike()
    {
        PipelineTag[] tags = [.. Tags, new("MByte", "%MB1", "Byte")];
        IrUnit[] units = [U(1, "OB1", A("OB1/1/1", "Q3", V("K")), A("OB1/1/2", "MByte", IrExpr.Const("0")))];
        var writes = new ProjectWrites(units, tags);
        var unique = ConstantOutputs.UniqueTags(tags);
        Assert.False(ConstantOutputs.IsConstantLike("K", unique, writes));
        var q = new UnderstandingRuleSource.QWriteIndex(tags, units);
        Assert.Empty(ConstantOutputs.Find(units, tags, writes, q.Count));
    }

    [Fact]
    public void ConstantLikeNeedsAUniqueMemoryTagNeverWrittenNorMentioned()
    {
        PipelineTag[] tags = [new("K", "%M1.3", "Bool"), new("D", "%M2.0", "Bool"), new("D", "%M2.1", "Bool"), new("T", "%M3.0", "Bool"), new("I1", "%I0.0", "Bool")];
        var writes = new ProjectWrites([U(1, "FB_S", Src("FB_S/1/1", "= T"))], tags);
        var unique = ConstantOutputs.UniqueTags(tags);
        Assert.True(ConstantOutputs.IsConstantLike("k", unique, writes));
        Assert.False(ConstantOutputs.IsConstantLike("D", unique, writes));    // 点名不唯一
        Assert.False(ConstantOutputs.IsConstantLike("T", unique, writes));    // 文字里提到
        Assert.False(ConstantOutputs.IsConstantLike("I1", unique, writes));   // 不在 %M 区
        Assert.False(ConstantOutputs.IsConstantLike("DB.x", unique, writes)); // 不在点表
    }

    static Original353ProjectResult Result(params PipelineRule[] rules) =>
        new(1, "v", "fp", [new Original353UnitResult(10, rules, [], [])], rules.Length, 0, []);

    [Fact]
    public void FiltersDropSameStateContradictingAProvenMutexAndRulesOnConstantOutputs()
    {
        var same = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var onConstant = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q3", "Member")], "旧");
        var kept = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var feedback = new Original353ConditionalFeedback(
            new PipelineRule("IMPLIES", [new("Q3", "Source"), new("I1", "Target")], "旧"), [], "AfterSettling", true, []);
        var result = new Original353ProjectResult(1, "v", "fp",
            [new Original353UnitResult(10, [same, onConstant, kept], [], []) { ConditionalFeedbackRules = [feedback] }], 3, 0, [])
            { ConditionalFeedbackCount = 1 };
        var (filtered, contradicted, constant) = RuleFilters.Apply(result, [("q2", "Q1", UnderstandingProofs.CodeNetwork)],
            new HashSet<string>(["Q3"], System.StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, contradicted);
        Assert.Equal(2, constant);
        Assert.Equal(kept.Key, Assert.Single(filtered.Units.Single().Rules).Key);
        Assert.Empty(filtered.Units.Single().ConditionalFeedbackRules);
        Assert.Equal(1, filtered.RuleCount);
        Assert.Equal(0, filtered.ConditionalFeedbackCount);
    }

    [Theory]
    [InlineData(UnderstandingProofs.ReviewCallSiteDefaults)]
    [InlineData(UnderstandingProofs.AiFbContract)]
    [InlineData(UnderstandingProofs.AiNetwork)]
    [InlineData((string?)null)]
    public void OnlyCodeProvenMutexRefutesSameState(string? proof)
    {
        var same = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var (filtered, contradicted, constant) = RuleFilters.Apply(Result(same), [("Q1", "Q2", proof)], new HashSet<string>());
        Assert.Equal(0, contradicted);
        Assert.Equal(0, constant);
        Assert.Equal(same.Key, Assert.Single(filtered.Units.Single().Rules).Key);
    }

    [Theory]
    [InlineData(UnderstandingProofs.CodeCallSite)]
    [InlineData(UnderstandingProofs.CodeNetwork)]
    public void CodeProofsOfAnyKindRefuteSameState(string proof)
    {
        var same = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var (filtered, contradicted, _) = RuleFilters.Apply(Result(same), [("Q2", "q1", proof)], new HashSet<string>());
        Assert.Equal(1, contradicted);
        Assert.Empty(filtered.Units.Single().Rules);
        Assert.Equal(0, filtered.RuleCount);
    }

    [Fact]
    public void SafetyEvidenceOfADroppedRuleIsDroppedToo()
    {
        var onConstant = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q3", "Member")], "旧");
        var kept = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var result = new Original353ProjectResult(1, "v", "fp",
            [new Original353UnitResult(10, [onConstant, kept], [], [])
            {
                SafetyRules = [new Original353SafetyRule(onConstant, "End", [], [], "h1"), new Original353SafetyRule(kept, "End", [], [], "h2")]
            }], 2, 0, []) { SafetyRuleCount = 2 };
        var (filtered, _, constant) = RuleFilters.Apply(result, [], new HashSet<string>(["Q3"], System.StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, constant);
        Assert.Equal(kept.Key, Assert.Single(filtered.Units.Single().SafetyRules).Rule.Key);
        Assert.Equal(1, filtered.SafetyRuleCount);
    }

    // ---- 审查修正（I1~I4、M4） ----

    // 修正 2：不确定写入按区收窄。每个目标单独写一次，看它对 K(%M1.3)、Q1(%Q0.0)、I1(%I0.0)、符号名 DB.x、
    // DB 绝对地址 %DB5.DBX0.0 各算什么：C=确定写入，U=只可能写入，-=不算写入。
    [Theory]
    [InlineData("?", "-", "-", "-", "-", "-")]                         // 未接线的输出引脚：不是写入
    [InlineData("P#M0.0 BYTE 10", "C", "-", "-", "-", "-")]            // 指针常数按区与字节范围换算
    [InlineData("P#M1.3 BOOL 1", "C", "-", "-", "-", "-")]
    [InlineData("P#M2.0 BYTE 1", "-", "-", "-", "-", "-")]
    [InlineData("P#Q0.0 WORD 1", "-", "C", "-", "-", "-")]
    [InlineData("P#DB5.DBX0.0 WORD 2", "-", "-", "-", "U", "C")]       // DB 范围确定，换不到符号名
    [InlineData("P#DB5.DBX4.0 BYTE 1", "-", "-", "-", "U", "-")]
    [InlineData("P#M1.3", "U", "-", "-", "-", "-")]                    // 没有类型与长度：该区内不确定
    [InlineData("P#M0.0 FOO 3", "U", "-", "-", "-", "-")]
    [InlineData("P#DBX0.0 BYTE 4", "-", "-", "-", "U", "U")]           // DB 号未知：DB 区内不确定
    [InlineData("P#0.0 BYTE 10", "U", "U", "U", "U", "U")]             // 区未知：对全部点不确定
    [InlineData("QB[AR1,P#0.0]", "-", "U", "-", "-", "-")]             // 带区字母的间接寻址：只在该区内不确定
    [InlineData("MW[AR1,P#0.0]", "U", "-", "-", "-", "-")]
    [InlineData("M?LReal[AR1,P#0.0]", "U", "-", "-", "-", "-")]
    [InlineData("%M:Byte:1", "U", "-", "-", "-", "-")]
    [InlineData("DBX[AR1,P#0.0]", "-", "-", "-", "U", "U")]            // DB/DI 间接：只影响 DB 区
    [InlineData("DIX[AR1,P#0.0]", "-", "-", "-", "U", "U")]
    [InlineData("L[AR1,P#0.0]", "-", "-", "-", "U", "-")]              // 局部区间接：只影响符号名（局部变量）
    [InlineData("B[AR1,P#0.0]", "U", "U", "U", "U", "U")]              // 跨区间接：区未知
    [InlineData("[AR1,P#0.0]", "U", "U", "U", "U", "U")]
    [InlineData("〔未识别的访问 Address〕", "U", "U", "U", "U", "U")]   // 认不出的访问文字
    [InlineData("My Var", "-", "-", "-", "-", "U")]                    // 含空格、不在点表的符号：普通符号名（空格在博图符号里合法）
    [InlineData("Spaced Bit", "C", "-", "-", "-", "-")]                // 含空格但在点表里：按地址换算
    [InlineData("Arr[?]", "-", "-", "-", "-", "U")]                    // 下标读不到：只是 Arr 的非字面量下标（符号名对 DB 绝对地址仍不确定）
    public void UncertainWritesAreScopedToTheirArea(string target, string k, string q1, string i1, string dbx, string dbAbs)
    {
        PipelineTag[] tags = [.. MemoryTags, new("Spaced Bit", "%M1.3", "Bool")];
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", target, V("X")))], tags);
        string Kind(string name) =>
            writes.Writers(name).Count == 0 ? "-" : writes.HasUncertainWriter(name) ? "U" : "C";
        Assert.Equal(new[] { k, q1, i1, dbx, dbAbs }, new[] { Kind("K"), Kind("Q1"), Kind("I1"), Kind("DB.x"), Kind("%DB5.DBX0.0") });
        Assert.Equal(writes.HasUncertainWriter("K"), writes.UncertainWriters("K").Count == 1);
    }

    [Fact]
    public void UnreadableSubscriptIsANonLiteralSubscript()
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "DB.Arr[?]", V("X")))], MemoryTags);
        Assert.Single(writes.Writers("DB.Arr[3]"));
        Assert.True(writes.HasUncertainWriter("DB.Arr[3]"));
        Assert.Empty(writes.Writers("DB.Brr"));
    }

    [Fact]
    public void UnresolvedMemoryWritesMakeMemoryBitsNonConstant()
    {
        var unique = ConstantOutputs.UniqueTags(MemoryTags);
        Assert.False(ConstantOutputs.IsConstantLike("K",
            unique, new ProjectWrites([U(1, "OB1", A("OB1/1/1", "MW[AR1,P#0.0]", V("X")))], MemoryTags)));
        Assert.True(ConstantOutputs.IsConstantLike("K",
            unique, new ProjectWrites([U(1, "OB1", A("OB1/1/1", "QB[AR1,P#0.0]", V("X")), A("OB1/1/2", "?", V("X")))], MemoryTags)));
    }

    [Fact]
    public void UncertainWriteCountsByKind()
    {
        var writes = new ProjectWrites([U(1, "OB1",
            A("OB1/1/1", "?", V("X")),
            A("OB1/1/2", "P#M0.0 BYTE 10", V("X")),
            A("OB1/1/3", "P#M1.3", V("X")),
            A("OB1/1/4", "QB[AR1,P#0.0]", V("X")),
            A("OB1/1/5", "DBX[AR1,P#0.0]", V("X")),
            A("OB1/1/6", "%DB5.DBX0.0", V("X")),
            A("OB1/1/7", "L[AR1,P#0.0]", V("X")),
            A("OB1/1/8", "B[AR1,P#0.0]", V("X")),
            A("OB1/1/9", "My Var", V("X")),
            A("OB1/1/10", "DB.x", V("X")))], MemoryTags);
        var counts = writes.UncertainWriteCounts;
        Assert.Equal(1, counts[ProjectWrites.UncertainKinds.UnwiredOutput]);
        Assert.Equal(1, counts[ProjectWrites.UncertainKinds.PointerResolved]);
        Assert.Equal(2, counts[ProjectWrites.UncertainKinds.AreaWide]);          // P#M1.3、QB[..]
        Assert.Equal(1, counts[ProjectWrites.UncertainKinds.DbWide]);            // DBX[..]
        Assert.Equal(1, counts[ProjectWrites.UncertainKinds.DbAbsolute]);        // %DB5.DBX0.0（对符号名不确定）
        Assert.Equal(1, counts[ProjectWrites.UncertainKinds.LocalIndirect]);
        Assert.Equal(1, counts[ProjectWrites.UncertainKinds.AnyArea]);           // B[..]（My Var 是普通符号名）
    }

    [Fact]
    public void CertainAndUncertainWritersAreDistinguished()
    {
        var writes = new ProjectWrites([
            U(1, "OB1", A("OB1/1/1", "DB.Arr[3]", V("X")), A("OB1/1/2", "DB.Arr", V("X")), A("OB1/1/3", "DB.Arr[3].a", V("X"))),
            U(2, "OB2", A("OB2/1/1", "DB.Arr[#i]", V("X")), A("OB2/1/2", "%MB1", V("X")), A("OB2/1/3", "%M:Byte:1", V("X"))),
        ], MemoryTags);
        Assert.Equal(4, writes.Writers("DB.Arr[3]").Count);
        Assert.Equal(("OB2/1/1"), Assert.Single(writes.UncertainWriters("DB.Arr[3]")).StatementId);   // 非字面量下标只是「可能」
        Assert.Equal(2, writes.Writers("K").Count);
        Assert.Equal(("OB2/1/3"), Assert.Single(writes.UncertainWriters("K")).StatementId);          // 算不出范围的整区写入
        Assert.False(writes.HasUncertainWriter("I1"));
        Assert.Empty(writes.Writers("I1"));
    }

    [Fact]
    public void AbsoluteDbWritesAreUncertainForSymbolicNames()
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "%DB5.DBX0.0", V("X")), A("OB1/1/2", "DB_Data.Flag", V("X")))], MemoryTags);
        Assert.Equal(2, writes.Writers("DB_Data.Flag").Count);
        Assert.Equal(("OB1/1/1"), Assert.Single(writes.UncertainWriters("DB_Data.Flag")).StatementId);
    }

    [Theory]
    [InlineData("T B [AR1,P#0.0]")]       // 跨区寄存器间接
    [InlineData("L P#M 0.0 BYTE 10")]     // 指针常数覆盖整区
    public void TextUnresolvableAddressingMentionsMemoryBits(string text)
    {
        var writes = new ProjectWrites([U(1, "FB_S", Src("FB_S/1/1", text))], MemoryTags);
        Assert.True(writes.MentionedInText("K"));
    }

    [Theory]
    [InlineData("DB.Cmd := X;", "DB.Cmd.Run", true)]         // 文字整体写父结构
    [InlineData("DB.Cmd.Other := X;", "DB.Cmd.Run", false)]  // 兄弟成员
    [InlineData("Arr[#i] := TRUE;", "Arr[3]", true)]         // 非字面量下标可能同址
    [InlineData("Arr[#i].x := TRUE;", "Arr[3].x", true)]
    [InlineData("Arr[2] := TRUE;", "Arr[3]", false)]
    [InlineData("Arr := Brr;", "Arr[3].x", true)]
    [InlineData("DB.Cmd.Run.y := X;", "DB.Cmd.Run", true)]   // 子成员
    public void TextMentionFollowsTheSameOverlapRulesAsWriters(string text, string name, bool expected)
    {
        var writes = new ProjectWrites([U(1, "FB_S", Src("FB_S/1/1", text))], []);
        Assert.Equal(expected, writes.MentionedInText(name));
    }

    [Fact]
    public void OutputWrittenDifferentConstantsIsNotConstant()
    {
        IrUnit[] units =
        [
            U(1, "OB1", A("OB1/1/1", "Q3", IrExpr.Const("TRUE")), A("OB1/1/2", "Q5", IrExpr.Const("?")), A("OB1/1/3", "Q7", V("K"))),
            U(2, "FC2", A("FC2/1/1", "Q3", IrExpr.Const("FALSE")), A("FC2/1/2", "Q7", V("K2"))),
        ];
        PipelineTag[] tags = [.. Tags, new("K2", "%M1.4", "Bool")];
        var writes = new ProjectWrites(units, tags);
        var q = new UnderstandingRuleSource.QWriteIndex(tags, units);
        Assert.Empty(ConstantOutputs.Find(units, tags, writes, q.Count));
    }

    [Fact]
    public void SameConstantWrittenTwiceUnderDifferentCaseIsConstant()
    {
        IrUnit[] units = [U(1, "OB1", A("OB1/1/1", "Q3", IrExpr.Const("TRUE")), A("OB1/1/2", "q3", IrExpr.Const("true")),
            A("OB1/1/3", "Q4", IrExpr.Const("TRUE")), A("OB1/1/4", "q4", V("I1")))];
        var writes = new ProjectWrites(units, Tags);
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, units);
        Assert.Equal(new[] { "Q3" }, ConstantOutputs.Find(units, Tags, writes, q.Count).ToArray());
    }

    [Fact]
    public void OutputAlsoWrittenThroughByteAliasIsNotConstant()
    {
        IrUnit[] units = [U(1, "OB1", A("OB1/1/1", "Q3", IrExpr.Const("TRUE")), A("OB1/1/2", "%QB0", IrExpr.Const("0")))];
        var writes = new ProjectWrites(units, Tags);
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, units);
        Assert.Empty(ConstantOutputs.Find(units, Tags, writes, q.Count));
    }

    [Fact]
    public void FeedbackContradictingACodeProvenMutexIsDropped()
    {
        var feedback = new Original353ConditionalFeedback(
            new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧"), [], "AfterSettling", true, []);
        var result = new Original353ProjectResult(1, "v", "fp",
            [new Original353UnitResult(10, [], [], []) { ConditionalFeedbackRules = [feedback] }], 0, 0, []) { ConditionalFeedbackCount = 1 };
        var (filtered, contradicted, constant) = RuleFilters.Apply(result, [("Q1", "Q2", UnderstandingProofs.CodeCallSite)], new HashSet<string>());
        Assert.Equal(1, contradicted);
        Assert.Equal(0, constant);
        Assert.Empty(filtered.Units.Single().ConditionalFeedbackRules);
        Assert.Equal(0, filtered.ConditionalFeedbackCount);
    }

    [Fact]
    public void ManyWritesUnderOneRootAreQueriedFast()
    {
        var statements = Enumerable.Range(0, 20_000).Select(i => A($"OB1/1/{i}", $"DB_Big.S{i % 200}.m{i}", V("X"))).ToArray();
        var texts = Enumerable.Range(0, 200).Select(i => Src($"FB_S/1/{i}", $"DB_Big.S{i}.n{i} := TRUE;")).ToArray();
        var writes = new ProjectWrites([U(1, "OB1", statements), U(2, "FB_S", texts)], MemoryTags);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < 5_000; i++)
        {
            hits += writes.Writers($"DB_Big.S{i * 4 % 200}.m{i * 4}").Count;
            if (writes.MentionedInText($"DB_Big.S{i % 200}.q{i}")) hits++;
        }
        watch.Stop();
        Assert.Equal(5_000, hits);
        Assert.True(watch.Elapsed < System.TimeSpan.FromSeconds(2), $"耗时 {watch.Elapsed}");
    }

    // ---- 修正 3：提及对称、可能写入预计算、块移动写整数组、小项 ----

    [Theory]
    [InlineData("Arr[2] := TRUE;", "Arr[#i]", true)]          // 查询侧下标不是字面量：文字写任一元素都算提到
    [InlineData("Arr[2].x := TRUE;", "Arr[#i].x", true)]
    [InlineData("Arr[#j] := TRUE;", "Arr[#i]", true)]
    [InlineData("Brr[2] := TRUE;", "Arr[#i]", false)]
    [InlineData("Arr[2].y := TRUE;", "Arr[#i].x", false)]
    public void NonLiteralQuerySubscriptMatchesAnyTextElement(string text, string name, bool expected)
    {
        var writes = new ProjectWrites([U(1, "FB_S", Src("FB_S/1/1", text))], []);
        Assert.Equal(expected, writes.MentionedInText(name));
    }

    static IrStatement Box(string id, string target, string instruction, string port = "OUT") =>
        new(id, IrStatementKinds.Assign, target, IrExpr.True, new IrExpr(IrKinds.Instr, instruction + "|" + port, []));

    [Theory]
    [InlineData("MOVE_BLK")]
    [InlineData("MOVE_BLK_VARIANT")]
    [InlineData("UMOVE_BLK")]
    [InlineData("FILL_BLK")]
    [InlineData("UFILL_BLK")]
    [InlineData("FieldWrite<Type=Bool>")]
    [InlineData("Deserialize")]
    [InlineData("SomethingNew[Inst]")]      // 不认识的指令：输出到数组元素时同样按可能写整个数组
    public void BlockWriteInstructionOutputToAnElementMayWriteTheWholeArray(string instruction)
    {
        var writes = new ProjectWrites([U(1, "OB1", Box("OB1/1/1", "DB.Arr[0]", instruction))], []);
        Assert.Single(writes.Writers("DB.Arr[3]"));
        Assert.True(writes.HasUncertainWriter("DB.Arr[3]"));
        Assert.Single(writes.Writers("DB.Arr[3].x"));
        Assert.Single(writes.Writers("DB.Arr[0]"));
        Assert.False(writes.HasUncertainWriter("DB.Arr[0]"));    // 首元素确定被写
        Assert.Empty(writes.Writers("DB.Brr[3]"));
    }

    [Theory]
    [InlineData("MOVE_BLK_VARIANT", true)]
    [InlineData("Serialize", true)]
    [InlineData("Deserialize", true)]
    [InlineData("SomethingNew", false)]     // 不认识的指令只对数组元素放宽
    public void BlockWriteInstructionOutputToAStructureMayWriteTheWholeRoot(string instruction, bool wholeRoot)
    {
        var writes = new ProjectWrites([U(1, "OB1", Box("OB1/1/1", "DB.S.x", instruction))], []);
        Assert.Single(writes.Writers("DB.S.x.y"));
        Assert.Equal(wholeRoot ? 1 : 0, writes.Writers("DB.Other").Count);
        Assert.Equal(wholeRoot, writes.HasUncertainWriter("DB.Other"));
    }

    [Theory]
    [InlineData("BLKMOV", "Any", true)]     // 指针/ANY/VARIANT 形参：一段内存
    [InlineData("UserFb", "Variant", true)]
    [InlineData("UserFb", "Pointer", true)]
    [InlineData("FILL", "Bool", true)]      // 系统块移动/填充
    [InlineData("UserFb", "Bool", false)]   // 普通 Bool 输出只写那一个元素
    public void CallOutputToAnArrayElement(string callee, string pinType, bool wholeArray)
    {
        var call = new IrCall(callee, "FC", "", IrExpr.True, [new IrPin("DSTBLK", "Output", pinType, null, ["DB.Arr[0]"])]);
        var writes = new ProjectWrites([U(1, "OB1", new IrStatement("OB1/1/1", IrStatementKinds.Call, null, IrExpr.True, null, call))], []);
        Assert.Equal(wholeArray ? 1 : 0, writes.Writers("DB.Arr[5]").Count);
        Assert.Single(writes.Writers("DB.Arr[0]"));
    }

    [Theory]
    [InlineData("Arr[#bar1]")]
    [InlineData("M[#car2]")]
    [InlineData("Arr[#xp#1]")]
    public void SubscriptNamesThatContainRegisterLettersAreNotIndirectAddressing(string target)
    {
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", target, V("X")))], MemoryTags);
        Assert.Empty(writes.Writers("K"));
        Assert.Equal(0, writes.UncertainWriteCounts[ProjectWrites.UncertainKinds.AnyArea]);
        Assert.Equal(0, writes.UncertainWriteCounts[ProjectWrites.UncertainKinds.AreaWide]);
    }

    [Fact]
    public void UnalignedPointerConstantsCoverWholeBytes()
    {
        PipelineTag[] tags = [new("M10", "%M1.0", "Bool"), new("M37", "%M3.7", "Bool"), new("M40", "%M4.0", "Bool"), new("M07", "%M0.7", "Bool")];
        var writes = new ProjectWrites([U(1, "OB1", A("OB1/1/1", "P#M1.3 BYTE 2", V("X")))], tags);
        Assert.Single(writes.Writers("M10"));
        Assert.Single(writes.Writers("M37"));
        Assert.Empty(writes.Writers("M40"));
        Assert.Empty(writes.Writers("M07"));
    }

    [Theory]
    [InlineData("%Local0.0")]
    [InlineData("%LocalB3")]
    [InlineData("%Local:LReal:64")]
    public void LocalAreaAbsoluteWritesMayWriteLocalVariables(string target)
    {
        var writes = new ProjectWrites([U(1, "FB1", A("FB1/1/1", target, V("X")))], MemoryTags);
        Assert.True(writes.HasUncertainWriter("#tmp"));
        Assert.Empty(writes.Writers("K"));
        Assert.Equal(1, writes.UncertainWriteCounts[ProjectWrites.UncertainKinds.LocalIndirect]);
    }

    [Fact]
    public void ManyAbsoluteDbWritesDoNotSlowDownSymbolicQueries()
    {
        var statements = Enumerable.Range(0, 5_000).Select(i => A($"OB1/1/{i}", $"%DB10.DBX{i / 8}.{i % 8}", V("X"))).ToArray();
        var writes = new ProjectWrites([U(1, "OB1", statements), U(2, "OB2", A("OB2/1/1", "DB.Own", V("X")))], MemoryTags);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long total = 0;
        for (var i = 0; i < 5_000; i++)
        {
            total += writes.Writers($"DB.S{i}.x").Count;
            if (writes.HasUncertainWriter($"DB.S{i}.y")) total++;
            total += writes.UncertainWriters($"DB.S{i}.z").Count;
        }
        total += writes.Writers("%DB10.DBX0.0").Count;
        watch.Stop();
        Assert.Equal(5_000L * 5_000 + 5_000 + 5_000L * 5_000 + 2, total);
        Assert.True(watch.Elapsed < System.TimeSpan.FromSeconds(2), $"耗时 {watch.Elapsed}");
        Assert.Equal(5_001, writes.Writers("DB.Own").Count);
        Assert.Equal(5_000, writes.UncertainWriters("DB.Own").Count);
    }

    [Fact]
    public void ManyCrossAreaIndirectWritesDoNotSlowDownQueries()
    {
        var statements = Enumerable.Range(0, 3_000).Select(i => A($"OB1/1/{i}", $"B[AR1,P#{i}.0]", V("X"))).ToArray();
        var writes = new ProjectWrites([U(1, "OB1", [.. statements, A("OB1/2/1", "%MB1", V("X"))])], MemoryTags);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < 5_000; i++)
        {
            if (writes.HasUncertainWriter($"DB.S{i}.x")) hits++;
            if (writes.HasUncertainWriter(i % 2 == 0 ? "K" : "Q1")) hits++;
        }
        watch.Stop();
        Assert.Equal(10_000, hits);
        Assert.True(watch.Elapsed < System.TimeSpan.FromSeconds(2), $"耗时 {watch.Elapsed}");
        Assert.Equal(3_001, writes.Writers("K").Count);
        Assert.Equal(3_000, writes.UncertainWriters("K").Count);
        Assert.Contains(("OB1/2/1"), writes.Writers("K").Select(w => w.StatementId));
    }
}
