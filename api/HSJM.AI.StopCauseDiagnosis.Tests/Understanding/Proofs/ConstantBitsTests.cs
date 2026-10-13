#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>常量位（用户 2026-10-02 裁定：系统存储位当常量，全仓唯一按名字/注释的特例；另有按程序结构认出的常量位）。</summary>
public class ConstantBitsTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement A(string id, string target, IrExpr value, IrExpr? guard = null) => new(id, IrStatementKinds.Assign, target, guard ?? IrExpr.True, value);
    static IrStatement S(string id, string kind, string target, IrExpr guard) => new(id, kind, target, guard, null);
    static IrUnit U(long id, string block, params IrStatement[] s) => new(id, block, 1, "LAD", "FlgNet", s, [], []);
    static IrStatement CallOf(string id, string callee, IrExpr? en = null) =>
        new(id, IrStatementKinds.Call, null, IrExpr.True, null, new IrCall(callee, "FB", "IDB_" + callee, en ?? IrExpr.Unwired(), []));

    static PipelineTag M(string name, string address, string? comment = null) => new(name, address, "Bool") { Comment = comment };
    static readonly IrExpr Tautology = IrExpr.Or([V("AlwaysTRUE"), IrExpr.Not(V("AlwaysTRUE"))]);

    static IReadOnlyDictionary<string, bool> Find(IReadOnlyList<PipelineTag> tags, params IrUnit[] units) =>
        ConstantBits.Find(units, tags, new ProjectWrites(units, tags), IsProgramCycle);

    /// <summary>程序循环组织块（按 OB 事件类型 ProgramCycle，测试里用块名代替）；OB40/OB100 等不是。</summary>
    static bool IsProgramCycle(string block) => block is "OB1" or "OB123";

    static readonly PipelineTag[] SystemBits = [M("FirstScan", "%M1.0"), M("AlwaysTRUE", "%M1.2"), M("AlwaysFALSE", "%M1.3"), M("X", "%I0.0")];

    [Fact]
    public void NeverWrittenSystemMemoryBitsAreConstantsByName()
    {
        var bits = Find(SystemBits, U(1, "FB_A", A("FB_A/1/1", "Q", V("X"))));
        Assert.True(bits["AlwaysTRUE"]);
        Assert.False(bits["AlwaysFALSE"]);
        Assert.False(bits.ContainsKey("FirstScan"));
    }

    [Theory]
    [InlineData("始终为1", true)]
    [InlineData("恒为 1（高电平）", true)]
    [InlineData("总是为TRUE", true)]
    [InlineData("Always TRUE", true)]
    [InlineData("always = 1", true)]
    [InlineData("始终为0", false)]
    [InlineData("Always FALSE (low)", false)]
    public void CommentStatingAConstantValueIsUsed(string comment, bool value)
    {
        var bits = Find([M("Sys_Hi", "%M3.0", comment)], U(1, "FB_A", A("FB_A/1/1", "Q", V("X"))));
        Assert.Equal(value, bits["Sys_Hi"]);
    }

    [Theory]
    [InlineData("调试屏蔽使用 =1")]   // 只有「=1」不算
    [InlineData("Always ON during auto mode")]   // 审查 M-3：描述条件的英文注释不算
    [InlineData("自动模式下始终为1")]
    [InlineData("常闭信号")]
    [InlineData("始终为1，始终为0")]  // 自相矛盾
    [InlineData("")]
    public void CommentWithoutAClearConstantIsIgnored(string comment)
    {
        Assert.Empty(Find([M("Sys_Hi", "%M3.0", comment)], U(1, "FB_A", A("FB_A/1/1", "Q", V("X")))));
    }

    [Fact]
    public void NameAndCommentDisagreeingIsIgnored()
    {
        Assert.Empty(Find([M("AlwaysTRUE", "%M1.2", "始终为0")], U(1, "FB_A", A("FB_A/1/1", "Q", V("X")))));
    }

    [Fact]
    public void NameDoesNotOverrideAProgramWrite()
    {
        var bits = Find(SystemBits, U(1, "OB1", A("OB1/1/1", "AlwaysTRUE", V("X"))));
        Assert.False(bits.ContainsKey("AlwaysTRUE"));
    }

    [Fact]
    public void NameDoesNotApplyWhenMentionedInTextOrPossiblyWritten()
    {
        var text = U(1, "FB_T", new IrStatement("FB_T/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "AlwaysTRUE := FALSE;"));
        Assert.False(Find(SystemBits, text).ContainsKey("AlwaysTRUE"));
        var byteWrite = U(2, "FB_B", A("FB_B/1/1", "%MB1", V("X")));   // 同字节的整体写入
        Assert.False(Find(SystemBits, byteWrite).ContainsKey("AlwaysTRUE"));
    }

    [Fact]
    public void NameOnlyAppliesToBoolMemoryBits()
    {
        Assert.Empty(Find([new PipelineTag("AlwaysTRUE", "%DB1.DBX0.0", "Bool"), new PipelineTag("AlwaysFALSE", "%MB2", "Byte")],
            U(1, "FB_A", A("FB_A/1/1", "Q", V("X")))));
    }

    // 测试线写法：FB6000_StdSignal 里「若 (AlwaysTRUE OR NOT AlwaysTRUE) 则 置位 AlwaysTRUE / 复位 AlwaysFALSE」，OB1 无条件调用
    static IrUnit StdSignal(IrExpr? guard = null) => U(1, "FB_Std",
        S("FB_Std/1/1", IrStatementKinds.Set, "AlwaysTRUE", guard ?? Tautology),
        S("FB_Std/1/2", IrStatementKinds.Reset, "AlwaysFALSE", guard ?? Tautology));

    [Fact]
    public void TautologicalSetAndResetInABlockRunEveryScanAreConstants()
    {
        var bits = Find(SystemBits, StdSignal(), U(2, "OB1", CallOf("OB1/1/1", "FB_Std")));
        Assert.True(bits["AlwaysTRUE"]);
        Assert.False(bits["AlwaysFALSE"]);
    }

    // 审查 I-1：只有程序循环组织块（及从它无条件调用到的块）每个扫描都执行；中断/错误/启动 OB 里的写入使该位不是常量
    [Theory]
    [InlineData("OB40")]    // 硬件中断
    [InlineData("OB100")]   // 启动
    [InlineData("OB121")]   // 编程错误
    public void WriteOnlyReachedFromANonCycleOrganizationBlockIsNotAConstant(string ob)
    {
        Assert.Empty(Find(SystemBits, StdSignal(), U(2, ob, CallOf(ob + "/1/1", "FB_Std"))));
        Assert.False(Find(SystemBits, U(3, ob, A(ob + "/1/1", "AlwaysTRUE", IrExpr.Const("TRUE")))).ContainsKey("AlwaysTRUE"));
    }

    [Fact]
    public void ExtraWriteInAnInterruptMakesItNotAConstant()
    {
        var bits = Find(SystemBits, StdSignal(), U(2, "OB1", CallOf("OB1/1/1", "FB_Std")),
            U(3, "OB40", A("OB40/1/1", "AlwaysTRUE", IrExpr.Const("TRUE"))));
        Assert.False(bits.ContainsKey("AlwaysTRUE"));
    }

    [Fact]
    public void BlockCalledUnconditionallyThroughAnotherBlockRunsEveryScan()
    {
        var bits = Find(SystemBits, StdSignal(), U(2, "FB_Mid", CallOf("FB_Mid/1/1", "FB_Std")), U(3, "OB1", CallOf("OB1/1/1", "FB_Mid")));
        Assert.True(bits["AlwaysTRUE"]);
        // 中间块有条件调用下层：不算
        Assert.Empty(Find(SystemBits, StdSignal(), U(2, "FB_Mid", CallOf("FB_Mid/1/1", "FB_Std", V("X"))), U(3, "OB1", CallOf("OB1/1/1", "FB_Mid"))));
    }

    [Fact]
    public void CallWhoseEnIsASystemMemoryBitRunsEveryScan()   // PLC2：OB1 里 FC100 的 EN 接 AlwaysTRUE
    {
        var tags = SystemBits.Append(M("K", "%M5.0")).ToArray();
        var bits = Find(tags, U(1, "FB_W", A("FB_W/1/1", "K", IrExpr.Const("TRUE"))), U(2, "OB1", CallOf("OB1/1/1", "FB_W", V("AlwaysTRUE"))));
        Assert.True(bits["K"]);
        Assert.False(Find(tags, U(1, "FB_W", A("FB_W/1/1", "K", IrExpr.Const("TRUE"))), U(2, "OB1", CallOf("OB1/1/1", "FB_W", V("AlwaysFALSE"))))
            .ContainsKey("K"));
    }

    [Fact]
    public void OrganizationTypeIsReadFromTheExport()
    {
        const string xml = """
            <Document><SW.Blocks.OB ID="0"><AttributeList><Name>OB40_HW</Name><Number>40</Number><ProgrammingLanguage>LAD</ProgrammingLanguage>
              <SecondaryType>HardwareInterrupt</SecondaryType></AttributeList></SW.Blocks.OB></Document>
            """;
        var block = PipelineExportReader.Read(xml, 1, "OB40_HW.xml");
        Assert.All(block.Units, u => Assert.Equal("HardwareInterrupt", u.OrganizationType));
    }

    [Fact]
    public void ConditionalSetIsNotAConstant()
    {
        Assert.Empty(Find(SystemBits, StdSignal(V("X")), U(2, "OB1", CallOf("OB1/1/1", "FB_Std"))));
    }

    [Fact]
    public void WritingBlockThatMayNotRunIsNotAConstant()
    {
        Assert.Empty(Find(SystemBits, StdSignal(), U(2, "OB1", CallOf("OB1/1/1", "FB_Std", V("X")))));   // 有条件调用
        Assert.Empty(Find(SystemBits, StdSignal()));                                                     // 没人调用
        var jump = new IrStatement("OB1/1/2", IrStatementKinds.JumpNot, null, IrExpr.True, V("X"), null, "L1");
        Assert.Empty(Find(SystemBits, StdSignal(), U(2, "OB1", CallOf("OB1/1/1", "FB_Std"), jump)));     // 组织块有跳转
    }

    [Fact]
    public void ConflictingConstantWritesAreNotAConstant()
    {
        var other = U(3, "OB1", A("OB1/2/1", "AlwaysTRUE", IrExpr.Const("FALSE")));
        Assert.False(Find(SystemBits, StdSignal(), U(2, "OB1", CallOf("OB1/1/1", "FB_Std")), other).ContainsKey("AlwaysTRUE"));
    }

    [Fact]
    public void OutputsDrivenOnlyByAConstantBitAreConstantOutputs()
    {
        // 保留集 FB20_SafeControl 网络 9 的形态：两个恒真位常开触点串联，后面并联多个 %Q 线圈
        var tags = SystemBits.Concat([new PipelineTag("Q1", "%Q0.0", "Bool"), new PipelineTag("Q2", "%Q0.1", "Bool")]).ToArray();
        IrUnit[] units =
        [
            StdSignal(), U(2, "OB1", CallOf("OB1/1/1", "FB_Std")),
            U(3, "FB_Safe", A("FB_Safe/9/1", "Q1", IrExpr.And([V("AlwaysTRUE"), V("AlwaysTRUE")])),
                A("FB_Safe/9/2", "Q2", IrExpr.And([V("AlwaysTRUE"), V("AlwaysTRUE")])))
        ];
        var writes = new ProjectWrites(units, tags);
        var q = new UnderstandingRuleSource.QWriteIndex(tags, units);
        var bits = ConstantBits.Find(units, tags, writes, b => b == "OB1");
        Assert.Empty(ConstantOutputs.Find(units, tags, writes, q.Count));   // 不认常量位时：恒真位被程序写入，不算常量样
        Assert.Equal(new[] { "Q1", "Q2" }, ConstantOutputs.Find(units, tags, writes, q.Count, bits).Order().ToArray());
    }

    [Fact]
    public void ParallelCoilsOnAConstantBitLoseTheirSameStateInTheMerge()
    {
        var tags = new[] { M("AlwaysTRUE", "%M1.2"), new PipelineTag("Q1", "%Q0.0", "Bool"), new PipelineTag("Q2", "%Q0.1", "Bool") };
        var std = new FlgNetBuilder();
        var or = std.Part("O", 2);
        var c1 = std.Contact("AlwaysTRUE");
        var c2 = std.Contact("AlwaysTRUE", negated: true);
        std.Wire(FlgNetBuilder.Rail(), FlgNetBuilder.Pin(c1, "in"), FlgNetBuilder.Pin(c2, "in"));
        std.Wire(FlgNetBuilder.Pin(c1, "out"), FlgNetBuilder.Pin(or, "in1"));
        std.Wire(FlgNetBuilder.Pin(c2, "out"), FlgNetBuilder.Pin(or, "in2"));
        std.Coil("SCoil", "AlwaysTRUE", FlgNetBuilder.Pin(or, "out"));
        var ob = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" };
        ob.CallWired("FB_Std", "DB_Std");
        var safe = new FlgNetBuilder();
        safe.Series("Coil", "Q1", ("AlwaysTRUE", false), ("AlwaysTRUE", false));
        safe.Series("Coil", "Q2", ("AlwaysTRUE", false), ("AlwaysTRUE", false));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_Std", 110, "LAD", std.Build(), "", [], 1, FlgNetBuilder.Interface([], []), "FB"),
            new(11, "OB1", 111, "LAD", ob.Build(), "", ["FB_Std"], 1, "", "OB") { OrganizationType = "ProgramCycle" },
            new(12, "FB_Safe", 112, "LAD", safe.Build(), "", [], 9, FlgNetBuilder.Interface([], []), "FB"),
        ], tags);
        var old = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var generated = new Original353ProjectResult(1, "v", "fp",
            source.Units.Select(u => new Original353UnitResult(u.Id, u.Id == 112 ? [old] : [], [], [])).ToArray(), 1, 0, []);
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, new Dictionary<string, FbContractOutcome>(), "m1");
        Assert.Empty(result.Units.SelectMany(u => u.Rules));
        Assert.Equal(1, report.NoteCounts["ConstantBit"]);
        Assert.True(report.NoteCounts["ConstantOutputRule"] >= 1);
    }
}
