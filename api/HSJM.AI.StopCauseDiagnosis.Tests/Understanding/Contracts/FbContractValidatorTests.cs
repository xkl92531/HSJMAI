#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class FbContractValidatorTests
{
    static IrExpr V(string n) => IrExpr.Var(n);

    static AiContractRelation R(string kind, string[]? outputs = null, string[]? inputs = null, string? pos = null,
        string? delay = null, string? alarm = null, params string[] evidence)
        => new(kind, outputs ?? [], inputs ?? [], pos, delay, null, alarm, evidence, null);

    static ValidatedRelation One(AiContractRelation r, FbContract? proven = null, IrBlock? block = null) =>
        FbContractValidator.Validate(new AiFbContract("FB_V", "", [r]), block ?? IrBlockTests.Valve(), proven ?? FbContract.Empty("FB_V")).Relations[0];

    /// <summary>在 Valve() 之外追加一个程序段（编号 FB_V/7/*）与若干形参，不改共享夹具。</summary>
    static IrBlock Ext(IrStatement[] extra, params IfaceMember[] iface)
    {
        var v = IrBlockTests.Valve();
        var u = new IrUnit(7, "FB_V", 7, "LAD", "FlgNet", extra, [], []);
        return IrBlock.FromUnits("FB_V", [.. v.Units, u], [.. v.Interface, .. iface]);
    }

    static IrStatement Assign(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);

    // ---------- 出处 ----------

    [Fact]
    public void RejectsMissingOrUnknownEvidence()
    {
        Assert.Equal("EvidenceMissing", One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP")).RejectReason);
        Assert.Equal("EvidenceUnknown:FB_V/9/9", One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "FB_V/9/9")).RejectReason);
    }

    [Theory]
    [InlineData("[FB_V/5/1]")]
    [InlineData(" [FB_V/5/1] ")]
    [InlineData("[FB/5/1]")]
    [InlineData("FB/5/1")]
    [InlineData("fb/5/1")]
    public void BracketedOrGenericEvidenceIdsAreNormalised(string evidence)
    {
        var v = One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: evidence));
        Assert.Null(v.RejectReason);
        Assert.Equal(new[] { "FB_V/5/1" }, v.Relation.Evidence);
    }

    [Fact]
    public void NormalisedEvidenceIsUsedForTheMentionCheck()
        => Assert.Equal("EvidenceDoesNotMention:Cmd.IWP",
            One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "[FB/6/3]")).RejectReason);

    [Fact]
    public void EvidenceStillUnknownAfterNormalisationIsRejected()
    {
        Assert.Equal("EvidenceUnknown:FB_V/9/9", One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "[FB/9/9]")).RejectReason);
        Assert.Equal("EvidenceUnknown:FB_X/5/1", One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "[FB_X/5/1]")).RejectReason);
    }

    [Theory]
    [InlineData("[FB_V/5/1]", "FB_V", "FB_V/5/1")]
    [InlineData("FB/5/1", "FB_V", "FB_V/5/1")]
    [InlineData("FB/5/1", "FB", "FB/5/1")]   // 块名本身就叫 FB：不替换
    [InlineData("FBX/5/1", "FB_V", "FBX/5/1")]
    [InlineData("  FB_V/5/1\t", "FB_V", "FB_V/5/1")]
    public void NormalizeEvidence(string id, string block, string expected)
        => Assert.Equal(expected, FbContractValidator.NormalizeEvidence(id, block));

    [Fact]
    public void EvidenceMustMentionThePrimaryVariables()
    {
        Assert.Equal("EvidenceDoesNotMention:Cmd.IWP",
            One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "FB_V/6/3")).RejectReason);
        Assert.Equal("EvidenceDoesNotMention:A_Word.Alarm1",
            One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime", alarm: "A_Word.Alarm1",
                evidence: "FB_V/5/1")).RejectReason);
        Assert.Equal("EvidenceDoesNotMention:I_FwdCmd",
            One(R("gate", ["O_Out"], ["I_FwdCmd"], evidence: "FB_G/1/3"), block: GateChain()).RejectReason);
        var proven = new FbContract("FB_V", [], [], [new FbContractContradiction("I_FwdPos1", "I_FwdCmd", [])]);
        Assert.Equal("EvidenceDoesNotMention:I_FwdCmd",
            One(R("contradiction", inputs: ["I_FwdPos1", "I_FwdCmd"], evidence: "FB_V/5/1"), proven).RejectReason);
    }

    [Fact]
    public void EvidenceMentionCountsNodeExpansionAndCallPins()
    {
        // FB_V/5/1 的 I_FwdPos2 只经由节点 n1 出现
        Assert.Contains("I_FwdPos2", IrBlockTests.Valve().StatementVars("FB_V/5/1"));
        var call = new IrCall("FB_Sub", "FB", "Inst", IrExpr.True,
            [new IrPin("IN", "Input", "Bool", V("I_X"), []), new IrPin("OUT", "Output", "Bool", null, ["Q_Y"])]);
        var b = Ext([new("FB_V/7/1", IrStatementKinds.Call, null, IrExpr.True, null, call)]);
        var vars = b.StatementVars("FB_V/7/1");
        Assert.Contains("I_X", vars);
        Assert.True(vars.Contains("q_y"));   // 忽略大小写
        Assert.Empty(b.StatementVars("FB_V/9/9"));
    }

    // ---------- feedback ----------

    [Fact]
    public void AcceptsFeedbackWhosePositionVarIsDerivedFromInputs()
    {
        var v = One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime", evidence: "FB_V/5/1"));
        Assert.Null(v.RejectReason);
        Assert.False(v.Proven);
    }

    [Theory]
    [InlineData("Cmd.Nothing", "FB_V/5/1", "PositionVarNotWritten")]
    [InlineData("Cmd.Flag", "FB_V/6/3", "PositionVarNotFromInputs")]
    public void RejectsFeedbackThatIrDoesNotSupport(string pos, string evidence, string reason)
        => Assert.Equal(reason, One(R("feedback", ["O_FwdOut"], pos: pos, evidence: evidence)).RejectReason);

    // 评测迭代 2 修正 B：到位变量是本块从未写入的 Bool 输入/InOut 形参 → 视为来自输入
    [Theory]
    [InlineData("Input")]
    [InlineData("InOut")]
    public void AcceptsFeedbackWhosePositionVarIsAnUnwrittenBoolInputFormal(string section)
    {
        var v = IrBlockTests.Valve();
        var block = IrBlock.FromUnits("FB_V", v.Units,
            v.Interface.Select(m => m.Name == "I_FwdPos1" ? m with { Section = section } : m).ToArray());
        var r = One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/5/1"), block: block);
        Assert.Null(r.RejectReason);
        Assert.False(r.Proven);
    }

    [Fact]
    public void InputFormalPositionVarWrittenInTheBodyKeepsTheOriginalChecks()
    {
        // 体内写了该形参（且只依赖全局量）：按原逻辑，依赖闭包里没有输入形参 → 拒绝
        var block = Ext([Assign("FB_V/7/1", "I_FwdPos1", V("AlwaysTRUE"))]);
        Assert.Equal("PositionVarNotFromInputs",
            One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/7/1"), block: block).RejectReason);
    }

    [Fact]
    public void InputFormalPositionVarMentionedInSourceTextIsNotTrusted()
    {
        var block = Ext([new IrStatement("FB_V/7/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "      = #I_FwdPos1;")]);
        // 审查 I-2 起文字里提到即算写入（写入值未知），在「到位变量不来自输入」一步被拒
        Assert.Equal("PositionVarNotFromInputs",
            One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/5/1"), block: block).RejectReason);
    }

    // 审查 Minor-1：顺控动作 / 未识别元素的文字里写了该形参，同样不当作未写入
    [Theory]
    [InlineData(IrStatementKinds.Action)]
    [InlineData(IrStatementKinds.Unrecognized)]
    public void InputFormalPositionVarMentionedInGraphTextIsNotTrusted(string kind)
    {
        var block = Ext([new IrStatement("FB_V/7/1", kind, "", IrExpr.True, null, null, "#I_FwdPos1 := TRUE")]);
        Assert.False(block.IsUnwrittenBoolInputFormal("I_FwdPos1"));
        // 审查 I-2 起文字里提到即算写入（写入值未知），在「到位变量不来自输入」一步被拒
        Assert.Equal("PositionVarNotFromInputs",
            One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/5/1"), block: block).RejectReason);
    }

    // 审查 Minor-3：内层调用的输出引脚写到该形参 → 不算未写入，按原逻辑
    [Theory]
    [InlineData("I_FwdPos1")]
    [InlineData("I_FwdPos1.%X0")]
    public void InputFormalWrittenThroughACallPinKeepsTheOriginalChecks(string target)
    {
        var call = new IrCall("FB_Inner", "FB", "Inner", IrExpr.True, [new IrPin("Q", "Output", "Bool", null, [target])]);
        var block = Ext([new IrStatement("FB_V/7/1", IrStatementKinds.Call, null, IrExpr.True, null, call)]);
        Assert.False(block.IsUnwrittenBoolInputFormal("I_FwdPos1"));
        Assert.NotNull(One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/5/1"), block: block).RejectReason);
    }

    [Theory]
    [InlineData("C_SensorNumber")]   // 非 Bool 输入形参
    [InlineData("O_BkdOut")]         // 未写入的输出形参
    public void OtherUnwrittenFormalsAreStillNotWritten(string pos)
    {
        var block = Ext([Assign("FB_V/7/1", "Cmd.X", IrExpr.And([V(pos), V("I_FwdPos1")]))]);
        Assert.Equal("PositionVarNotWritten",
            One(R("feedback", ["O_FwdOut"], pos: pos, evidence: "FB_V/7/1"), block: block).RejectReason);
    }

    [Fact]
    public void FeedbackWithTwoOutputsIsRejected()
        => Assert.Equal("FeedbackShape",
            One(R("feedback", ["O_FwdOut", "O_BkdOut"], pos: "Cmd.IWP", evidence: "FB_V/5/1")).RejectReason);

    [Theory]
    [InlineData("C_Nothing", "DelayParamUnknown")]
    [InlineData("NaN", "DelayParamUnknown")]
    [InlineData("Infinity", "DelayParamUnknown")]
    [InlineData("10.5", null)]
    public void FeedbackDelayParamMustBeFormalOrFiniteNumber(string delay, string? reason)
        => Assert.Equal(reason, One(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", delay: delay, evidence: "FB_V/5/1")).RejectReason);

    // ---------- timeoutAlarm ----------

    [Fact]
    public void TimeoutAlarmMustDependOnOutputPositionAndDelay()
    {
        Assert.Null(One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime", alarm: "A_Word.Alarm1",
            evidence: "FB_V/5/3")).RejectReason);
        Assert.Null(One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: "10", alarm: "A_Word.Alarm1",
            evidence: "FB_V/5/3")).RejectReason);
        Assert.Equal("AlarmNotOutput", One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime", alarm: "Cmd.FwdErr",
            evidence: "FB_V/5/3")).RejectReason);
        Assert.Equal("AlarmIndependentOfPosition", One(R("timeoutAlarm", ["O_FwdOut"], pos: "I_FwdCmd2", delay: "C_FwdPresetTime",
            alarm: "A_Word.Alarm1", evidence: "FB_V/5/3")).RejectReason);
    }

    [Fact]
    public void TimeoutAlarmShapeAndArity()
    {
        Assert.Equal("AlarmShape", One(R("timeoutAlarm", ["O_FwdOut", "O_BkdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime",
            alarm: "A_Word.Alarm1", evidence: "FB_V/5/3")).RejectReason);
        Assert.Equal("AlarmShape", One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime",
            evidence: "FB_V/5/3")).RejectReason);
        Assert.Equal("AlarmShape", One(R("timeoutAlarm", ["O_FwdOut"], delay: "C_FwdPresetTime", alarm: "A_Word.Alarm1",
            evidence: "FB_V/5/3")).RejectReason);   // 缺 positionVar
        Assert.Equal("AlarmNotWritten", One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime",
            alarm: "A_Word.Alarm9", evidence: "FB_V/5/3")).RejectReason);
        Assert.Equal("AlarmIndependentOfOutput", One(R("timeoutAlarm", ["O_BkdOut"], pos: "Cmd.IWP", delay: "C_FwdPresetTime",
            alarm: "A_Word.Alarm1", evidence: "FB_V/5/3")).RejectReason);
    }

    [Theory]
    [InlineData(null, "AlarmDelayMissing")]
    [InlineData("C_Nothing", "AlarmIndependentOfDelay")]
    [InlineData("O_BkdOut", "AlarmIndependentOfDelay")]   // 是形参，但不在报警的依赖闭包里
    [InlineData("NaN", "AlarmIndependentOfDelay")]
    [InlineData("Infinity", "AlarmIndependentOfDelay")]
    public void TimeoutAlarmDelayIsRequiredAndMustFeedTheAlarm(string? delay, string reason)
        => Assert.Equal(reason, One(R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.IWP", delay: delay, alarm: "A_Word.Alarm1",
            evidence: "FB_V/5/3")).RejectReason);

    [Fact]
    public void GlobalReadByOutputDoesNotLinkAlarmToOutput()
    {
        // O_FwdOut := AlwaysTRUE AND Cmd.QFwd；报警只读到 AlwaysTRUE（未被本块写入的全局量）→ 与输出无关
        var b = Ext([Assign("FB_V/7/1", "A_Word.Alarm2", IrExpr.And([V("AlwaysTRUE"), V("I_FwdPos1")]))]);
        Assert.Equal("AlarmIndependentOfOutput", One(R("timeoutAlarm", ["O_FwdOut"], pos: "I_FwdPos1", delay: "10",
            alarm: "A_Word.Alarm2", evidence: "FB_V/7/1"), block: b).RejectReason);
    }

    // ---------- mutex ----------

    static IrBlock WithBkdOut() => Ext([Assign("FB_V/7/1", "O_BkdOut", IrExpr.And([V("AlwaysTRUE"), V("Cmd.QBkd")]))]);

    [Fact]
    public void MutexIsProvenOnlyWhenPhaseOneProvedIt()
    {
        var b = WithBkdOut();
        Assert.Equal("MutexShape", One(R("mutex", ["O_FwdOut", "A_Word"], evidence: "FB_V/5/2"), block: b).RejectReason); // A_Word 不是 Bool
        Assert.Equal("MutexShape", One(R("mutex", ["O_FwdOut", "O_X"], evidence: "FB_V/5/2"), block: b).RejectReason);    // O_X 不是形参
        Assert.Equal("MutexShape", One(R("mutex", ["O_FwdOut", "o_fwdout"], evidence: "FB_V/5/2"), block: b).RejectReason); // 大小写不同的同一形参
        var unproven = One(R("mutex", ["O_FwdOut", "O_BkdOut"], evidence: ["FB_V/5/2", "FB_V/7/1"]), block: b);
        Assert.Null(unproven.RejectReason);
        Assert.False(unproven.Proven);
        var proven = new FbContract("FB_V", [new FbContractMutex("O_BkdOut", "O_FwdOut", ["FB_V/5/2"])], [], []);
        var v = One(R("mutex", ["O_FwdOut", "O_BkdOut"], evidence: ["FB_V/5/2", "FB_V/7/1"]), proven, b);
        Assert.Null(v.RejectReason);
        Assert.True(v.Proven);   // 顺序无关
        Assert.Equal("EvidenceDoesNotMention:O_BkdOut",
            One(R("mutex", ["O_FwdOut", "O_BkdOut"], evidence: "FB_V/5/2"), proven, b).RejectReason);
    }

    // ---------- gate ----------

    /// <summary>独立小块（单一程序段 FB_G/1/*）：本地变量 Stat（默认 Static），带三个输入与一个输出。</summary>
    static IrBlock Small(string statSection, params IrStatement[] statements) => IrBlock.FromUnits("FB_G",
        [new IrUnit(1, "FB_G", 1, "LAD", "FlgNet", statements, [], [])],
        [
            new IfaceMember("Input", "I_Go", "Bool", null, null, []),
            new IfaceMember("Input", "I_FwdCmd", "Bool", null, null, []),
            new IfaceMember("Input", "I_BkdCmd", "Bool", null, null, []),
            new IfaceMember("Output", "O_Out", "Bool", null, null, []),
            new IfaceMember(statSection, "Stat", "Struct", null, null, []),
        ]);

    /// <summary>写入者都在读者之前：Stat.QBkd := I_BkdCmd；Stat.QFwd := I_FwdCmd AND NOT Stat.QBkd；O_Out := AlwaysTRUE AND Stat.QFwd。</summary>
    static IrBlock GateChain(string statSection = "Static") => Small(statSection,
        Assign("FB_G/1/1", "Stat.QBkd", V("I_BkdCmd")),
        Assign("FB_G/1/2", "Stat.QFwd", IrExpr.And([V("I_FwdCmd"), IrExpr.Not(V("Stat.QBkd"))])),
        Assign("FB_G/1/3", "O_Out", IrExpr.And([V("AlwaysTRUE"), V("Stat.QFwd")])));

    [Theory]
    [InlineData("Static")]
    [InlineData("Temp")]
    public void GateAcceptedWhenLocalWritersPrecedeTheOutput(string section)
    {
        var v = One(R("gate", ["O_Out"], ["I_FwdCmd"], evidence: "FB_G/1/2"), block: GateChain(section));
        Assert.Null(v.RejectReason);
        Assert.False(v.Proven);
    }

    [Fact]
    public void GateThroughGlobalIsNotExpanded()
    {
        // 全局量（不在本块接口里）即使本块唯一写入也不代入：其它块也可能写它
        var b = Small("Static",
            Assign("FB_G/1/1", "DB_Global.Run", V("I_Go")),
            Assign("FB_G/1/2", "O_Out", V("DB_Global.Run")));
        Assert.Equal("GateNotNecessary", One(R("gate", ["O_Out"], ["I_Go"], evidence: "FB_G/1/1"), block: b).RejectReason);
    }

    [Fact]
    public void GateThroughStaticWrittenAfterTheReaderIsNotExpanded()
    {
        // 读者在前、写入者在后：读到的是上一周期的值，不能代入
        var b = Small("Static",
            Assign("FB_G/1/1", "O_Out", V("Stat.Run")),
            Assign("FB_G/1/2", "Stat.Run", V("I_Go")));
        Assert.Equal("GateNotNecessary", One(R("gate", ["O_Out"], ["I_Go"], evidence: "FB_G/1/2"), block: b).RejectReason);
    }

    [Fact]
    public void NestedExpansionComparesAgainstTheExpandedWriter()
    {
        // Stat.B 的写入者在 O_Out 之前、但在 Stat.A 的写入者之后 → 展开 Stat.A 时不能再代入 Stat.B
        var b = Small("Static",
            Assign("FB_G/1/1", "Stat.A", V("Stat.B")),
            Assign("FB_G/1/2", "Stat.B", V("I_Go")),
            Assign("FB_G/1/3", "O_Out", V("Stat.A")));
        Assert.Equal("GateNotNecessary", One(R("gate", ["O_Out"], ["I_Go"], evidence: "FB_G/1/2"), block: b).RejectReason);
    }

    [Fact]
    public void GateNeedsInputInEveryOutputDependency()
    {
        // Valve()：O_FwdOut（FB_V/5/2）在 Cmd.QFwd 的写入者（FB_V/6/2）之前读它 → 不代入 → 不必要
        Assert.Equal("GateNotNecessary", One(R("gate", ["O_FwdOut"], ["I_FwdCmd"], evidence: "FB_V/6/2")).RejectReason);
        Assert.Equal("GateInputNotInOutputDependency",
            One(R("gate", ["O_FwdOut"], ["I_FwdPos2"], evidence: "FB_V/6/2")).RejectReason);
        Assert.Equal("GateShape", One(R("gate", ["O_FwdOut"], ["I_FwdCmd", "I_FwdPos1"], evidence: "FB_V/6/2")).RejectReason);
        Assert.Equal("GateShape", One(R("gate", [], ["I_FwdCmd"], evidence: "FB_V/6/2")).RejectReason);
    }

    [Fact]
    public void GateInputOnlyOnNegatedPathIsNotNecessary()
        // O_Out 只经 NOT Stat.QBkd 读到 I_BkdCmd → 在依赖闭包里但不是必要条件
        => Assert.Equal("GateNotNecessary",
            One(R("gate", ["O_Out"], ["I_BkdCmd"], evidence: "FB_G/1/1"), block: GateChain()).RejectReason);

    [Fact]
    public void GateInputInOnlyOneOrBranchIsNotNecessary()
    {
        var b = Ext([Assign("FB_V/7/1", "O_BkdOut", IrExpr.Or([V("I_FwdCmd"), V("I_FwdPos1")]))]);
        Assert.Equal("GateNotNecessary", One(R("gate", ["O_BkdOut"], ["I_FwdCmd"], evidence: "FB_V/7/1"), block: b).RejectReason);
    }

    [Fact]
    public void GateWithoutUniqueOutputDefinitionIsNotNecessary()
    {
        // O_FwdOut 被写两次 → 没有唯一定义
        var b = Ext([Assign("FB_V/7/1", "O_FwdOut", V("I_FwdCmd"))]);
        Assert.Equal("GateNotNecessary", One(R("gate", ["O_FwdOut"], ["I_FwdCmd"], evidence: "FB_V/7/1"), block: b).RejectReason);
    }

    [Fact]
    public void GateProvenByPhaseOne()
    {
        var proven = new FbContract("FB_V", [], [new FbContractGate("I_FwdPos2", ["O_FwdOut", "O_BkdOut"], ["FB_V/6/2"])], []);
        var v = One(R("gate", ["O_FwdOut"], ["I_FwdPos2"], evidence: "FB_V/5/1"), proven);
        Assert.Null(v.RejectReason);
        Assert.True(v.Proven);
    }

    // ---------- contradiction ----------

    [Fact]
    public void UnprovenContradictionWithoutAlarmIsRejected()
        => Assert.Equal("ContradictionUnproven",
            One(R("contradiction", inputs: ["I_FwdPos1", "I_FwdPos2"], evidence: "FB_V/5/1")).RejectReason);

    [Fact]
    public void ContradictionShapeAndProof()
    {
        Assert.Equal("ContradictionShape",
            One(R("contradiction", inputs: ["I_FwdPos1", "I_FwdPos2", "I_FwdCmd"], evidence: "FB_V/5/1")).RejectReason);
        Assert.Equal("ContradictionShape",
            One(R("contradiction", inputs: ["I_FwdPos1", "i_fwdpos1"], evidence: "FB_V/5/1")).RejectReason);
        var proven = new FbContract("FB_V", [], [], [new FbContractContradiction("I_FwdPos2", "I_FwdPos1", ["FB_V/5/1"])]);
        var v = One(R("contradiction", inputs: ["I_FwdPos1", "I_FwdPos2"], evidence: "FB_V/5/1"), proven);
        Assert.Null(v.RejectReason);
        Assert.True(v.Proven);
        // 未经第一期证明：即使报警依赖两个输入也不接受
        Assert.Equal("ContradictionUnproven",
            One(R("contradiction", inputs: ["I_FwdPos1", "I_FwdPos2"], alarm: "A_Word.Alarm1", evidence: "FB_V/5/1")).RejectReason);
    }

    // ---------- 评测迭代 3 修正 E：纯拷贝别名 ----------

    /// <summary>Cmd.Copy := copy；Cmd.Use := Cmd.Copy ∧ I_FwdCmd（证据 FB_V/7/2 只提到拷贝 Cmd.Copy，不提 I_FwdPos1）。</summary>
    static IrBlock WithCopy(IrExpr copy, params IrStatement[] more) =>
        Ext([Assign("FB_V/7/1", "Cmd.Copy", copy), Assign("FB_V/7/2", "Cmd.Use", IrExpr.And([V("Cmd.Copy"), V("I_FwdCmd")])), .. more]);

    [Fact]
    public void EvidenceMentioningAPureCopyCountsAsMentioningThePrimary()
    {
        // 拷贝里本块未写入的全局量 AlwaysTRUE 视为常真文字去掉
        var block = WithCopy(IrExpr.And([V("AlwaysTRUE"), V("I_FwdPos1")]));
        Assert.Null(One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/7/2"), block: block).RejectReason);
    }

    [Fact]
    public void EvidenceMentioningACopyOfACopyAlsoCounts()
    {
        var block = Ext([Assign("FB_V/7/1", "Cmd.Copy1", V("I_FwdPos1")), Assign("FB_V/7/3", "Cmd.Copy", IrExpr.And([V("AlwaysTRUE"), V("Cmd.Copy1")])),
            Assign("FB_V/7/2", "Cmd.Use", IrExpr.And([V("Cmd.Copy"), V("I_FwdCmd")]))]);
        Assert.Null(One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/7/2"), block: block).RejectReason);
    }

    public static TheoryData<IrExpr> NotACopy() => new()
    {
        IrExpr.Not(IrExpr.Var("I_FwdPos1")),                                                     // 取反
        IrExpr.And([IrExpr.Var("AlwaysTRUE"), IrExpr.Var("I_FwdPos1"), IrExpr.Var("I_FwdCmd")]),  // 多一个文字（形参）
        IrExpr.And([IrExpr.Var("I_FwdPos1"), IrExpr.Var("Cmd.QFwd")]),                            // 多一个文字（本块写入、非常真的变量；Cmd.Flag := AlwaysTRUE 是常真，迭代 4 起会被去掉）
        IrExpr.Or([IrExpr.Var("I_FwdPos1"), IrExpr.Var("I_FwdPos2")]),                            // 或
        IrExpr.And([IrExpr.Var("I_FwdPos1"), new IrExpr(IrKinds.Cmp, ">", [IrExpr.Var("Cmd.Cnt"), IrExpr.Const("3")])]),  // 不透明
    };

    [Theory]
    [MemberData(nameof(NotACopy))]
    public void ExpressionThatIsNotAPureCopyIsNotAnAlias(IrExpr copy)
        => Assert.Equal("EvidenceDoesNotMention:I_FwdPos1",
            One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/7/2"), block: WithCopy(copy)).RejectReason);

    [Fact]
    public void VariableWrittenTwiceIsNotAnAlias()
    {
        var block = WithCopy(IrExpr.And([V("AlwaysTRUE"), V("I_FwdPos1")]), Assign("FB_V/7/4", "Cmd.Copy", V("I_FwdPos1")));
        Assert.Equal("EvidenceDoesNotMention:I_FwdPos1",
            One(R("feedback", ["O_FwdOut"], pos: "I_FwdPos1", evidence: "FB_V/7/2"), block: block).RejectReason);
    }

    /// <summary>Cmd.FwdPos := I_FwdPos1；O_Pos := pos；A_Word.Alarm2 置位条件依赖 Cmd.QFwd、Cmd.FwdPos 与延时，但不依赖 O_Pos。</summary>
    static IrBlock AlarmWithPositionCopy(IrExpr pos, params IrStatement[] more) =>
        Ext([
            Assign("FB_V/7/1", "Cmd.FwdPos", V("I_FwdPos1")),
            Assign("FB_V/7/2", "O_Pos", pos),
            new IrStatement("FB_V/7/3", IrStatementKinds.Set, "A_Word.Alarm2",
                IrExpr.And([V("Cmd.QFwd"), IrExpr.Not(V("Cmd.FwdPos")), new IrExpr(IrKinds.Cmp, ">=", [V("Cmd.FwdTime"), V("C_FwdPresetTime")])]), null),
            .. more
        ], new IfaceMember("Output", "O_Pos", "Bool", null, null, []));

    static AiContractRelation AlarmOnOPos() =>
        R("timeoutAlarm", ["O_FwdOut"], pos: "O_Pos", delay: "C_FwdPresetTime", alarm: "A_Word.Alarm2", evidence: "FB_V/7/3");

    [Fact]
    public void AlarmDependingOnTheOriginalOfACopiedPositionVarIsAccepted()
        => Assert.Null(One(AlarmOnOPos(), block: AlarmWithPositionCopy(IrExpr.And([V("AlwaysTRUE"), V("Cmd.FwdPos")]))).RejectReason);

    [Fact]
    public void AlarmIsStillIndependentWhenThePositionVarIsNotAPureCopy()
    {
        Assert.Equal("AlarmIndependentOfPosition",
            One(AlarmOnOPos(), block: AlarmWithPositionCopy(IrExpr.Not(V("Cmd.FwdPos")))).RejectReason);
        Assert.Equal("AlarmIndependentOfPosition",
            One(AlarmOnOPos(), block: AlarmWithPositionCopy(IrExpr.And([V("Cmd.FwdPos"), V("I_FwdCmd")]))).RejectReason);
        // 定义不唯一
        Assert.Equal("AlarmIndependentOfPosition",
            One(AlarmOnOPos(), block: AlarmWithPositionCopy(V("Cmd.FwdPos"), Assign("FB_V/7/4", "O_Pos", V("I_FwdPos2")))).RejectReason);
    }

    // 迭代 4 修正 I：常真枢纽（Cmd.En := AlwaysTRUE，O_Pos := G ∧ Cmd.En）不得让只读 AlwaysTRUE 的报警配上到位变量
    [Fact]
    public void AlarmDependingOnlyOnAConstantIsNotLinkedThroughAConstantCopy()
    {
        var block = Ext([
            Assign("FB_V/7/1", "Cmd.En", V("AlwaysTRUE")),
            Assign("FB_V/7/2", "O_Pos", IrExpr.And([V("DB_G.Sq"), V("Cmd.En")])),
            new IrStatement("FB_V/7/3", IrStatementKinds.Set, "A_Word.Alarm2",
                IrExpr.And([V("Cmd.QFwd"), V("AlwaysTRUE"), new IrExpr(IrKinds.Cmp, ">=", [V("Cmd.FwdTime"), V("C_FwdPresetTime")])]), null),
        ], new IfaceMember("Output", "O_Pos", "Bool", null, null, []));
        Assert.Equal("AlarmIndependentOfPosition", One(AlarmOnOPos(), block: block).RejectReason);
    }

    // 迭代 3 审查 Minor-5：E1 对互斥同样生效（O_FwdOut := AlwaysTRUE ∧ Cmd.QFwd，出处只提到 Cmd.QFwd）
    [Fact]
    public void MutexEvidenceMentioningAnAliasOfTheOutputIsAccepted()
    {
        var block = Ext([Assign("FB_V/7/1", "O_BkdOut", IrExpr.And([V("I_FwdCmd"), IrExpr.Not(V("Cmd.QFwd"))]))]);
        Assert.Null(One(R("mutex", ["O_FwdOut", "O_BkdOut"], evidence: "FB_V/7/1"), block: block).RejectReason);
        Assert.Equal("EvidenceDoesNotMention:O_FwdOut",
            One(R("mutex", ["O_FwdOut", "O_BkdOut"], evidence: "FB_V/6/3"), block: block).RejectReason);
    }

    // ---------- 其它 ----------

    [Fact]
    public void OtherIsInformationalOnly()
        => Assert.Equal("Informational", One(R("other", evidence: "FB_V/5/1")).RejectReason);

    [Fact]
    public void AcceptedAndRejectedPartitionRelations()
    {
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "",
            [R("other", evidence: "FB_V/5/1"), R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "FB_V/5/1")]),
            IrBlockTests.Valve(), FbContract.Empty("FB_V"));
        Assert.Equal("FB_V", c.FbName);
        Assert.Single(c.Accepted);
        Assert.Equal("feedback", c.Accepted.Single().Relation.Kind);
        Assert.Single(c.Rejected);
    }

    // ---------- 整分支复审 N-1：已证明矛盾的报警位与延时按程序体核对 ----------

    static AiContractRelation Contra(string? alarm, double? seconds = 5) =>
        new("contradiction", [], ["I_FwdPos1", "I_FwdPos2"], null, null, seconds, alarm, ["FB_V/5/1"], null);

    static FbContract ProvenSetting(string? target, string? preset) =>
        new("FB_V", [], [], [new FbContractContradiction("I_FwdPos2", "I_FwdPos1", ["FB_V/5/1"], target, preset)]);

    [Theory]
    [InlineData("Cmd.FwdErr", "Cmd.FwdErr", true)]        // 就是被置位的目标
    [InlineData("A_Word.Alarm1", "Cmd.FwdErr", true)]     // 目标的纯拷贝别名（A_Word.Alarm1 := Cmd.FwdErr）
    [InlineData("#a_word.alarm1", "Cmd.FwdErr", true)]    // 写法差异（# 前缀、大小写）
    [InlineData("O_FwdOut", "Cmd.FwdErr", false)]         // AI 指错了报警位
    [InlineData("A_Word.Alarm1", null, false)]            // 第一期没记下目标：无从核对
    public void ProvenContradictionKeepsTheAlarmOnlyWhenItIsTheSetTarget(string alarm, string? target, bool kept)
    {
        var v = One(Contra(alarm), ProvenSetting(target, "C_FwdPresetTime"));
        Assert.Null(v.RejectReason);
        Assert.True(v.Proven);
        Assert.Equal(kept ? alarm : null, v.Relation.Alarm);
    }

    [Fact]
    public void ProvenContradictionDelayComesFromTheTimerPresetNotFromTheAi()
    {
        var withPreset = One(Contra("Cmd.FwdErr", 5), ProvenSetting("Cmd.FwdErr", "T#2S")).Relation;
        Assert.Equal("T#2S", withPreset.DelayParam);
        Assert.Null(withPreset.DelaySeconds);
        var without = One(Contra("Cmd.FwdErr", 5), ProvenSetting("Cmd.FwdErr", null)).Relation;
        Assert.Null(without.DelayParam);
        Assert.Null(without.DelaySeconds);
    }

    [Fact]
    public void UnprovenContradictionAlarmMustDependOnBothInputs()
    {
        // 未证明的矛盾仍被拒；报警位按超时报警口径核对（Output 写入且依赖闭包含两个输入），不通过就清掉
        var dependent = One(Contra("A_Word.Alarm1"));
        Assert.Equal("ContradictionUnproven", dependent.RejectReason);
        Assert.Equal("A_Word.Alarm1", dependent.Relation.Alarm);
        Assert.Null(dependent.Relation.DelaySeconds);
        Assert.Null(One(Contra("O_FwdOut")).Relation.Alarm);   // O_FwdOut 不依赖到位输入
    }

    // ---------- 已校验的超时报警派生到位反馈（AI 漏给 feedback 时不丢反馈） ----------

    static AiContractRelation Alarm1(string output = "O_FwdOut", string pos = "Cmd.IWP", string? delay = "C_FwdPresetTime") =>
        R("timeoutAlarm", [output], pos: pos, delay: delay, alarm: "A_Word.Alarm1", evidence: "FB_V/5/3");

    static ValidatedFbContract Validate(params AiContractRelation[] relations) =>
        FbContractValidator.Validate(new AiFbContract("FB_V", "", relations), IrBlockTests.Valve(), FbContract.Empty("FB_V"));

    [Fact]
    public void AcceptedTimeoutAlarmWithoutFeedbackDerivesOne()
    {
        var c = Validate(Alarm1());
        Assert.Equal(2, c.Accepted.Count);
        var derived = Assert.Single(c.Accepted, v => v.Relation.Kind == "feedback");
        Assert.Equal(FbContractValidator.DerivedFromTimeoutAlarm, derived.DerivedFrom);
        Assert.False(derived.Proven);
        Assert.Equal(new[] { "O_FwdOut" }, derived.Relation.Outputs);
        Assert.Equal("Cmd.IWP", derived.Relation.PositionVar);
        Assert.Equal("C_FwdPresetTime", derived.Relation.DelayParam);
        Assert.Null(derived.Relation.Alarm);
        // 出处 = 报警出处 + 写入到位变量的语句（报警出处 FB_V/5/3 本身没提到 Cmd.IWP）
        Assert.Equal(new[] { "FB_V/5/3", "FB_V/5/1" }, derived.Relation.Evidence);
        Assert.StartsWith(FbContractValidator.DerivedFeedbackText, derived.Relation.Text);
        Assert.Null(c.Accepted.Single(v => v.Relation.Kind == "timeoutAlarm").DerivedFrom);
    }

    [Theory]
    [InlineData("O_FwdOut", "Cmd.IWP")]
    [InlineData("o_fwdout", "CMD.IWP")]   // 去重忽略大小写
    public void AcceptedAiFeedbackForTheSamePairIsNotDuplicated(string output, string pos)
    {
        var feedback = R("feedback", [output], pos: pos, delay: "C_FwdPresetTime", evidence: "FB_V/5/1");
        var c = Validate(feedback, Alarm1());
        Assert.Null(c.Relations[0].RejectReason);
        var only = Assert.Single(c.Relations, v => v.Relation.Kind == "feedback");
        Assert.Null(only.DerivedFrom);
        Assert.Same(feedback, only.Relation);   // AI 给出的 feedback 原样保留，不被覆盖
    }

    [Fact]
    public void RejectedTimeoutAlarmDerivesNothing()
    {
        var c = Validate(Alarm1(pos: "I_FwdCmd2"));            // AlarmIndependentOfPosition
        Assert.Equal("AlarmIndependentOfPosition", Assert.Single(c.Relations).RejectReason);
        Assert.DoesNotContain(c.Relations, v => v.Relation.Kind == "feedback");
        Assert.DoesNotContain(Validate(Alarm1(delay: null)).Relations, v => v.Relation.Kind == "feedback");   // AlarmDelayMissing
    }

    [Fact]
    public void RejectedAiFeedbackStillLetsTheAlarmDeriveOne()
    {
        // AI 给的 feedback 出处没提到到位变量被拒 → 同一对没有被接受的 feedback，由报警派生
        var c = Validate(R("feedback", ["O_FwdOut"], pos: "Cmd.IWP", evidence: "FB_V/6/3"), Alarm1());
        Assert.Equal("EvidenceDoesNotMention:Cmd.IWP", c.Relations[0].RejectReason);
        Assert.Equal(FbContractValidator.DerivedFromTimeoutAlarm,
            Assert.Single(c.Accepted, v => v.Relation.Kind == "feedback").DerivedFrom);
    }

    [Fact]
    public void TwoAlarmsOnTheSamePairDeriveOnce()
    {
        var c = Validate(Alarm1(), Alarm1(delay: "10"));
        Assert.Single(c.Relations, v => v.Relation.Kind == "feedback");
    }

    [Fact]
    public void DerivedFeedbackPassesTheSameFeedbackChecks()
    {
        // 到位变量是本块写入但不来自任何输入的变量（写入在测试里自建）：报警可以通过（依赖闭包含它），
        // 派生反馈按 feedback 口径被拒、且标明是派生的
        var b = Ext(
        [
            Assign("FB_V/7/1", "Cmd.Flag2", V("G_NotAnInput")),
            Assign("FB_V/7/2", "A_Word.Alarm2", IrExpr.And([V("Cmd.QFwd"), V("Cmd.Flag2"), V("C_FwdPresetTime")]))
        ]);
        var alarm = R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.Flag2", delay: "C_FwdPresetTime", alarm: "A_Word.Alarm2", evidence: "FB_V/7/2");
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "", [alarm]), b, FbContract.Empty("FB_V"));
        Assert.Null(c.Relations[0].RejectReason);
        var derived = Assert.Single(c.Relations, v => v.Relation.Kind == "feedback");
        Assert.Equal("DerivedFromTimeoutAlarm:PositionVarNotFromInputs", derived.RejectReason);
        Assert.Equal(FbContractValidator.DerivedFromTimeoutAlarm, derived.DerivedFrom);
        Assert.DoesNotContain(c.Accepted, v => v.Relation.Kind == "feedback");
    }

    // ---------- 审查修正：到位变量与输出同源 / 延时按程序核对 / 未写入输入形参的出处 ----------

    [Theory]
    [InlineData("O_FwdOut")]   // 到位变量就是输出本身
    [InlineData("Cmd.QFwd")]   // 到位变量是输出的纯拷贝（O_FwdOut := AlwaysTRUE ∧ Cmd.QFwd）
    [InlineData("cmd.qfwd")]
    public void TimeoutAlarmWhosePositionVarIsTheOutputIsRejectedAndDerivesNothing(string pos)
    {
        // 报警「依赖到位变量」与「依赖输出」两项被同一个变量满足，什么都没证明
        var c = Validate(Alarm1(pos: pos));
        Assert.Equal("AlarmPositionIsOutput", Assert.Single(c.Relations).RejectReason);
        Assert.Empty(c.Accepted);
    }

    [Fact]
    public void PositionVarThatOnlyDependsOnTheOutputIsStillAccepted()
    {
        // 到位变量合法地依赖输出（如旁路 QFwd ∧ ByPass）不是「就是输出」
        var b = Ext([
            Assign("FB_V/7/1", "Cmd.Pos3", IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), V("I_Bypass")]), V("I_FwdPos1")])),
            Assign("FB_V/7/2", "A_Word.Alarm2", IrExpr.And([V("Cmd.QFwd"), IrExpr.Not(V("Cmd.Pos3")), V("C_FwdPresetTime")]))
        ], new IfaceMember("Input", "I_Bypass", "Bool", null, null, []));
        var alarm = R("timeoutAlarm", ["O_FwdOut"], pos: "Cmd.Pos3", delay: "C_FwdPresetTime", alarm: "A_Word.Alarm2", evidence: "FB_V/7/2");
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "", [alarm]), b, FbContract.Empty("FB_V"));
        Assert.Null(c.Relations[0].RejectReason);
        Assert.Single(c.Accepted, v => v.DerivedFrom == FbContractValidator.DerivedFromTimeoutAlarm);
    }

    [Fact]
    public void DerivedFeedbackDropsAiNumbersForTheDelay()
    {
        // 报警延时是 AI 给的数（未与程序核对）：派生反馈不带它，代入时描述写「程序设定」
        var alarm = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "10", 10, "A_Word.Alarm1", ["FB_V/5/3"], null);
        var derived = Assert.Single(Validate(alarm).Accepted, v => v.Relation.Kind == "feedback").Relation;
        Assert.Null(derived.DelayParam);
        Assert.Null(derived.DelaySeconds);
        // 形参延时保留（代入时按调用实参 / 接口初值解析），AI 给的秒数仍不带
        var formal = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", 10, "A_Word.Alarm1", ["FB_V/5/3"], null);
        var kept = Assert.Single(Validate(formal).Accepted, v => v.Relation.Kind == "feedback").Relation;
        Assert.Equal("C_FwdPresetTime", kept.DelayParam);
        Assert.Null(kept.DelaySeconds);
    }

    [Theory]
    [InlineData("FB_V/7/2", false)]              // 报警出处没提到未写入的输入形参 I_FwdPos2 → 不派生
    [InlineData("FB_V/7/2,FB_V/7/1", true)]      // 出处提到了 → 派生
    public void UnwrittenInputFormalPositionVarNeedsTheAlarmEvidenceToMentionIt(string evidence, bool derived)
    {
        var b = Ext([
            Assign("FB_V/7/1", "Cmd.Err2", IrExpr.And([V("Cmd.QFwd"), IrExpr.Not(V("I_FwdPos2")),
                new IrExpr(IrKinds.Cmp, ">=", [V("Cmd.FwdTime"), V("C_FwdPresetTime")])])),
            Assign("FB_V/7/2", "A_Word.Alarm2", V("Cmd.Err2"))
        ]);
        var alarm = R("timeoutAlarm", ["O_FwdOut"], pos: "I_FwdPos2", delay: "C_FwdPresetTime", alarm: "A_Word.Alarm2",
            evidence: evidence.Split(','));
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "", [alarm]), b, FbContract.Empty("FB_V"));
        Assert.Null(c.Relations[0].RejectReason);
        Assert.Equal(derived, c.Accepted.Any(v => v.Relation.Kind == "feedback"));
        if (!derived)
            Assert.Equal("DerivedFromTimeoutAlarm:EvidenceDoesNotMention:I_FwdPos2",
                Assert.Single(c.Relations, v => v.Relation.Kind == "feedback").RejectReason);
    }

    // ---------- 派生反馈审查延伸：AI 直接给的 feedback 同样不得「到位变量就是输出」 ----------

    [Theory]
    [InlineData("O_FwdOut")]   // 到位变量就是输出本身
    [InlineData("Cmd.QFwd")]   // 输出的纯拷贝来源（O_FwdOut := AlwaysTRUE ∧ Cmd.QFwd）
    [InlineData("cmd.qfwd")]
    public void AiFeedbackWhosePositionVarIsTheOutputIsRejected(string pos)
    {
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "", [R("feedback", ["O_FwdOut"], pos: pos, evidence: "FB_V/5/2")]),
            IrBlockTests.Valve(), FbContract.Empty("FB_V"));
        Assert.Equal("FeedbackPositionIsOutput", Assert.Single(c.Relations).RejectReason);
        Assert.Empty(c.Accepted);   // 不产规则，也不进证伪（CallSiteSubstitution 只用 Accepted）
    }

    [Fact]
    public void AiFeedbackWhosePositionVarIsAnOutputSideAliasIsRejected()
    {
        // 输出直接读到、且本块写入的内部变量（O2 := Cmd.Run ∧ X），到位变量 Cmd.Pos 是它的纯拷贝
        var b = Ext([
            Assign("FB_V/7/1", "Cmd.Run", V("I_FwdCmd")),
            Assign("FB_V/7/2", "O_BkdOut", IrExpr.And([V("Cmd.Run"), V("I_FwdPos1")])),
            Assign("FB_V/7/3", "Cmd.Pos", V("Cmd.Run")),
        ]);
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "", [R("feedback", ["O_BkdOut"], pos: "Cmd.Pos", evidence: "FB_V/7/3")]),
            b, FbContract.Empty("FB_V"));
        Assert.Equal("FeedbackPositionIsOutput", Assert.Single(c.Relations).RejectReason);
    }

    [Fact]
    public void AiFeedbackWhosePositionVarOnlyDependsOnTheOutputIsStillAccepted()
    {
        var b = Ext([
            Assign("FB_V/7/1", "Cmd.Pos3", IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), V("I_Bypass")]), V("I_FwdPos1")])),
        ], new IfaceMember("Input", "I_Bypass", "Bool", null, null, []));
        var c = FbContractValidator.Validate(new AiFbContract("FB_V", "", [R("feedback", ["O_FwdOut"], pos: "Cmd.Pos3", evidence: "FB_V/7/1")]),
            b, FbContract.Empty("FB_V"));
        Assert.Null(Assert.Single(c.Relations).RejectReason);
    }
}
