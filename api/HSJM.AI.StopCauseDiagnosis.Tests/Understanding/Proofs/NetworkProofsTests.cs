#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class NetworkProofsTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrExpr N(string n) => IrExpr.Not(V(n));
    static IrExpr And(params IrExpr[] xs) => IrExpr.And(xs);
    static IrStatement A(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);
    static IrUnit U(long id, string block, params IrStatement[] s) => new(id, block, 1, "LAD", "FlgNet", s, [], []);
    static IrUnit Scl(long id, string block, params IrStatement[] s) => new(id, block, 1, "SCL", "StructuredText", s, [], []);
    static IrBlock B(string name, params IrUnit[] units) => IrBlock.FromUnits(name, units, []);
    static IfaceMember M(string section, string name, string type = "Bool") => new(section, name, type, null, null, []);
    static IrExpr Ton(string instance, IrExpr input) => new(IrKinds.Timer, $"TON|{instance}|Q",
        [new IrExpr(IrKinds.Port, "IN", [input]), new IrExpr(IrKinds.Port, "PT", [IrExpr.Const("T#0.5s")])]);

    /// <summary>不是 FC 的块判定（测试里显式传，不设默认值）。</summary>
    static readonly Func<string, bool> NotFc = _ => false;

    /// <param name="isFcBlock">FC 块判定，必填（与生产同口径，不给默认值）。</param>
    /// <param name="qUnits">Q 区写入索引用的程序段（生产上不含 FC，见 UnderstandingRuleSource）；默认同 all。</param>
    static NetworkProofs Proofs(Func<string, bool> isFcBlock, IReadOnlyList<PipelineTag> tags, IReadOnlyList<IrUnit> all,
        Func<string, IrBlock?>? blockOf = null, IReadOnlyList<IrUnit>? qUnits = null)
    {
        var q = new UnderstandingRuleSource.QWriteIndex(tags, qUnits ?? all);
        return new NetworkProofs(tags, q.Count, q.HasUnresolved, new ProjectWrites(all, tags), blockOf ?? (_ => null), isFcBlock);
    }

    static string[] Show(IEnumerable<ProvenRelation> rs) =>
        rs.Select(r => $"{r.Operator}|{string.Join("&", r.Members)}|{r.Proof}").ToArray();

    /// <summary>Q1、Q2 输出；A、B、C 输入；DB.M 由别处的程序写入。</summary>
    static readonly PipelineTag[] Io =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("Q3", "%Q0.2", "Bool"),
        new("A", "%I0.0", "Bool"), new("B", "%I0.1", "Bool"), new("C", "%I0.2", "Bool"),
        new("Flag", "%M10.0", "Bool"), new("FlagByte", "%MB10", "Byte"),
    ];

    /// <summary>别的块里对 name 的普通写入（让它「程序里有写入者」）。</summary>
    static IrUnit Writer(long id, string name) => U(id, "FB_W" + id, A($"FB_W{id}/1/1", name, V("C")));

    /// <summary>Q1 := M ∧ A；Q2 := ¬M ∧ B（M 在本段不写、别处写入时可证互斥）。</summary>
    static IrUnit CrossOnM(long id, string block, string m, params IrStatement[] extra) =>
        U(id, block, [A($"{block}/1/1", "Q1", And(V(m), V("A"))), A($"{block}/1/2", "Q2", And(N(m), V("B"))), .. extra]);

    // ───────────── 计划里的样例 ─────────────

    static IrStatement[] S8()
    {
        var timer = Ton("T1", And(N("KA63"), N("KA70"), V("DB.OK12")));
        return
        [
            A("FB350/1/P5/1", "KA62", timer),
            A("FB350/1/P5/2", "KA63", And(N("DB.OK12"), N("KA62"))),
        ];
    }

    static readonly PipelineTag[] S8Tags = [new("KA62", "%Q1305.4", "Bool"), new("KA63", "%Q1305.5", "Bool"), new("KA70", "%Q1305.6", "Bool")];

    [Fact]
    public void GraphSegmentCannotUseTheStatementOrderForTheS8Interlock()   // 样例 S8，裁定 F1：顺控段语句顺序不可信
    {
        var unit = new IrUnit(1, "FB350", 1, "GRAPH", "Graph", S8(), [], []);
        var proofs = Proofs(NotFc, S8Tags, [unit]);
        var block = B("FB350", unit);
        Assert.Empty(proofs.Prove(unit, block));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, block, "KA62", "KA63", out _));
    }

    [Fact]
    public void SclSegmentLaterStatementReadingTheEarlierOutputProvesTheMutex()   // 样例 S8 的结构放在顺序可信的段里
    {
        var unit = Scl(1, "FB350", S8());
        Assert.Equal(["AT_MOST_ONE|KA62&KA63|Code:Network"], Show(Proofs(NotFc, S8Tags, [unit]).Prove(unit, B("FB350", unit))));
    }

    static readonly PipelineTag[] S3Tags = [new("Q102.6", "%Q102.6", "Bool"), new("Q105.5", "%Q105.5", "Bool"),
        new("Q100.3", "%Q100.3", "Bool"), new("Q103.2", "%Q103.2", "Bool"), new("I1", "%I1.0", "Bool"), new("I2", "%I1.1", "Bool")];

    static IrUnit S3Unit() => U(1, "FB10061",
        A("FB10061/1/2", "Q102.6", IrExpr.Or([V("Q100.3"), V("Q103.2")])),
        A("FB10061/1/3", "Q105.5", IrExpr.Or([V("Q100.3"), V("Q103.2")])));

    [Fact]
    public void TwoOutputsAssignedTheSameExpressionAreSameState()   // 样例 S3（Q103.2 也有程序写入者）
    {
        var unit = S3Unit();
        var other = U(2, "FB10060", A("FB10060/1/1", "Q100.3", V("I1")), A("FB10060/1/2", "Q103.2", V("I2")));
        var proofs = Proofs(NotFc, S3Tags, [unit, other]);
        var block = B("FB10061", unit);
        Assert.Equal(["SAME_STATE|Q102.6&Q105.5|Code:Network"], Show(proofs.Prove(unit, block)));
        Assert.True(proofs.SameStateProven(unit, block, "q105.5", "Q102.6"));
    }

    [Fact]
    public void SameExpressionOverAVariableWithoutProgramWriterIsNotSameState()   // 裁定 F8：程序不写的量两次读之间可能被程序外改写
    {
        var unit = S3Unit();
        var other = U(2, "FB10060", A("FB10060/1/1", "Q100.3", V("I1")));
        var proofs = Proofs(NotFc, S3Tags, [unit, other]);
        Assert.Empty(proofs.Prove(unit, B("FB10061", unit)));
        Assert.False(proofs.SameStateProven(unit, B("FB10061", unit), "Q102.6", "Q105.5"));
    }

    [Fact]
    public void OutputCopyingAnInputIsSameState()   // 样例 S7
    {
        PipelineTag[] tags = [new("Q108.7", "%Q108.7", "Bool"), new("I101.5", "%I101.5", "Bool")];
        var unit = U(1, "FB10102", A("FB10102/2/2", "Q108.7", V("I101.5")));
        var proofs = Proofs(NotFc, tags, [unit]);
        var block = B("FB10102", unit);
        Assert.Equal(["SAME_STATE|I101.5&Q108.7|Code:Network"], Show(proofs.Prove(unit, block)));
        Assert.True(proofs.SameStateProven(unit, block, "i101.5", "Q108.7"));
        // 程序写入的输入：扫描结束时的映像不一定还是读到的值
        var written = Proofs(NotFc, tags, [unit, U(2, "OB1", A("OB1/1/1", "I101.5", IrExpr.True))]);
        Assert.Empty(written.Prove(unit, block));
        Assert.False(written.SameStateProven(unit, block, "I101.5", "Q108.7"));
    }

    [Fact]
    public void OutputsOfUnwrittenMemoryBitsGiveNothing()   // 样例 W3
    {
        PipelineTag[] tags = [new("Q3024.6", "%Q3024.6", "Bool"), new("Q3024.7", "%Q3024.7", "Bool"), new("AlwaysFALSE", "%M1.3", "Bool")];
        var unit = U(1, "FB221", A("FB221/20/2", "Q3024.7", V("AlwaysFALSE")), A("FB221/20/3", "Q3024.6", V("AlwaysFALSE")));
        Assert.Empty(Proofs(NotFc, tags, [unit]).Prove(unit, B("FB221", unit)));
    }

    [Fact]
    public void EarlierStatementReadingALaterOutputIsACounterexampleOnlyWhenTheOrderIsTrusted()
    {
        IrStatement[] body = [A("OB1/1/1", "Q1", And(V("A"), N("Q2"))), A("OB1/1/2", "Q2", V("DB.b"))];
        var scl = Scl(1, "OB1", body);
        var proofs = Proofs(NotFc, Io, [scl]);
        Assert.Empty(proofs.Prove(scl, B("OB1", scl)));
        Assert.Equal(ProofVerdict.Counterexample, proofs.MutexVerdict(scl, B("OB1", scl), "Q1", "Q2", out _));
        // FlgNet：Q2 本段被写，Q1 读到的 Q2 是独立的未知值 → 既不能证明也不能反证
        var flg = U(1, "OB1", body);
        Assert.Equal(ProofVerdict.NotProven, Proofs(NotFc, Io, [flg]).MutexVerdict(flg, B("OB1", flg), "Q1", "Q2", out _));
    }

    [Fact]
    public void EqualityOnAProgramWrittenIntegerProvesTheMutex()   // 样例 S5
    {
        var unit = U(1, "FB10149",
            A("FB10149/7/8", "Q1", new IrExpr(IrKinds.Cmp, "==", [V("Panel.Using"), IrExpr.Const("1")])),
            A("FB10149/7/9", "Q2", new IrExpr(IrKinds.Cmp, "==", [V("Panel.Using"), IrExpr.Const("2")])));
        var writer = U(2, "FB_Panel", A("FB_Panel/1/1", "Panel.Using", IrExpr.Const("1")));
        var block = B("FB10149", unit);
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [unit, writer]).Prove(unit, block)));
        // 程序从不写入（只有 HMI 写）的整数：两次读之间可能变化 → 不证明
        Assert.Empty(Proofs(NotFc, Io, [unit]).Prove(unit, block));
        // 与不确定写入（DB 绝对地址换算不到符号名）重叠：视为不稳定（Task 3 裁定）
        var dbAbsolute = U(3, "FB_Abs", A("FB_Abs/1/1", "%DB5.DBW0", IrExpr.Const("2")));
        Assert.Empty(Proofs(NotFc, Io, [unit, writer, dbAbsolute]).Prove(unit, block));
    }

    static readonly IrBlock Tower = IrBlock.FromUnits("FB_T",
    [
        U(10, "FB_T",
            A("FB_T/1/1", "O_G", IrExpr.Or([And(V("I_G"), N("I_Y")), V("I_T")])),
            A("FB_T/2/1", "O_Y", IrExpr.Or([V("I_Y"), V("I_T")])))
    ],
    [M("Input", "I_G"), M("Input", "I_Y"), M("Input", "I_T"), M("Output", "O_G"), M("Output", "O_Y")]);

    static readonly PipelineTag[] TowerTags =
        [new("QG", "%Q0.0", "Bool"), new("QY", "%Q0.1", "Bool"), new("QA", "%Q0.2", "Bool"), new("QB", "%Q0.3", "Bool"),
         new("QC", "%Q0.4", "Bool"), new("A", "%I0.0", "Bool"), new("B", "%I0.1", "Bool"), new("C", "%I0.2", "Bool")];

    static IrStatement CallT(string id, IrExpr? test, bool wireOutputs, string instance = "TL[1]") => new(id, IrStatementKinds.Call, null, IrExpr.True, null,
        new IrCall("FB_T", "FB", instance, IrExpr.True,
        [
            new IrPin("I_G", "Input", "Bool", V("A"), []), new IrPin("I_Y", "Input", "Bool", V("B"), []),
            new IrPin("I_T", "Input", "Bool", test, []),
            new IrPin("O_G", "Output", "Bool", null, wireOutputs ? ["QG"] : []),
            new IrPin("O_Y", "Output", "Bool", null, wireOutputs ? ["QY"] : []),
        ]));

    static NetworkProofs TowerProofs(IrUnit unit) => Proofs(NotFc, TowerTags, [unit, .. Tower.Units], n => n == "FB_T" ? Tower : null);

    [Fact]
    public void CallOutputsMutexThatNeedsAnUnwiredDefaultIsReview()   // 样例 S1
    {
        var unit = U(1, "FB10061", CallT("FB10061/1/1", null, true));
        Assert.Equal(["AT_MOST_ONE|QG&QY|Review:CallSiteDefaults"], Show(TowerProofs(unit).Prove(unit, B("FB10061", unit))));
    }

    [Fact]
    public void CallOutputsWithAWiredOverrideAreACounterexample()   // 样例 W1 的结构
    {
        var unit = U(1, "FB10061", CallT("FB10061/1/1", V("C"), true));
        var proofs = TowerProofs(unit);
        var block = B("FB10061", unit);
        Assert.Empty(proofs.Prove(unit, block));
        Assert.Equal(ProofVerdict.Counterexample, proofs.MutexVerdict(unit, block, "QG", "QY", out _));
    }

    static IrStatement[] S2() =>
    [
        CallT("FB10061/1/1", null, false),
        A("FB10061/1/2", "QA", V("TL[1].O_G")),
        A("FB10061/1/3", "QB", V("TL[1].O_G")),
        A("FB10061/1/4", "QC", V("TL[1].O_Y")),
    ];

    [Fact]
    public void InstanceOutputReadsAreExpandedAndSameCopiesAreSameState()   // 样例 S2，放在顺序可信的段里
    {
        var unit = Scl(1, "FB10061", S2());
        var block = IrBlock.FromUnits("FB10061", [unit], [M("Static", "TL", "Array")]);
        var shown = Show(TowerProofs(unit).Prove(unit, block));
        Assert.Contains("SAME_STATE|QA&QB|Code:Network", shown);
        Assert.Contains("AT_MOST_ONE|QA&QC|Review:CallSiteDefaults", shown);
        Assert.Contains("AT_MOST_ONE|QB&QC|Review:CallSiteDefaults", shown);
        Assert.Equal(3, shown.Length);
    }

    [Fact]
    public void InstanceOutputReadsAreNotExpandedInAFlgNetSegment()   // 裁定 F1：不按语句序号推断调用与读取的先后
    {
        var unit = U(1, "FB10061", S2());
        var block = IrBlock.FromUnits("FB10061", [unit], [M("Static", "TL", "Array")]);
        var proofs = TowerProofs(unit);
        Assert.Empty(proofs.Prove(unit, block));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, block, "QA", "QC", out _));
    }

    [Fact]
    public void ToRuleCarriesProofAndEvidence()
    {
        var rule = NetworkProofs.ToRule(new ProvenRelation("AT_MOST_ONE", ["Q1", "Q2"], UnderstandingProofs.CodeNetwork, ["OB1/1/1", "OB1/1/2"]), "m1");
        Assert.Equal(UnderstandingProofs.CodeNetwork, rule.Proof);
        Assert.All(rule.Signals, s => Assert.Equal("Member", s.Role));
        Assert.Equal(["OB1/1/1", "OB1/1/2"], rule.Trace!.Evidence);
        Assert.Equal(UnderstandingProofs.CodeNetwork, rule.Trace.Origin);
        Assert.Equal("m1", rule.Trace.Model);
    }

    // ───────────── 段内顺序不可信（裁定 F1）的反例 ─────────────

    [Fact]
    public void FlgNetOutputsCrossLockedByEachOthersNormallyClosedContactAreNotProven()
    {
        IrStatement[] body = [A("OB1/1/1", "Q1", And(V("A"), N("Q2"))), A("OB1/1/2", "Q2", And(V("B"), N("Q1")))];
        var flg = U(1, "OB1", body);
        var proofs = Proofs(NotFc, Io, [flg]);
        Assert.Empty(proofs.Prove(flg, B("OB1", flg)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(flg, B("OB1", flg), "Q1", "Q2", out _));
        // 同样的互锁在 SCL（顺序可信）里成立
        var scl = Scl(1, "OB1", body);
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [scl]).Prove(scl, B("OB1", scl))));
    }

    [Fact]
    public void FlgNetCommonContactBeforeABranchIsNotProven()   // ┤A├┤/Q2├┬┤/Q3├(Q2) / └(Q3)
    {
        var unit = U(1, "OB1",
            A("OB1/1/1", "Q2", And(V("A"), N("Q2"), N("Q3"))),
            A("OB1/1/2", "Q3", And(V("A"), N("Q2"))));
        var proofs = Proofs(NotFc, Io, [unit]);
        Assert.Empty(proofs.Prove(unit, B("OB1", unit)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, B("OB1", unit), "Q2", "Q3", out _));
    }

    [Fact]
    public void FlgNetMidRungCoilIsNotProven()   // ┤/Q2├┤/Q3├(Q2)(Q3)
    {
        var unit = U(1, "OB1",
            A("OB1/1/1", "Q2", And(N("Q2"), N("Q3"))),
            A("OB1/1/2", "Q3", And(N("Q2"), N("Q3"))));
        var proofs = Proofs(NotFc, Io, [unit]);
        Assert.Empty(proofs.Prove(unit, B("OB1", unit)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, B("OB1", unit), "Q2", "Q3", out _));
        Assert.False(proofs.SameStateProven(unit, B("OB1", unit), "Q2", "Q3"));
    }

    // ───────────── 写入回调四类 + 不确定写入/别名（硬性要求 3） ─────────────

    [Fact]
    public void ReadOfAVariableWrittenElsewhereIsStableWhenTheSegmentDoesNotWriteIt()   // 下列反例的对照
    {
        var unit = CrossOnM(1, "OB1", "DB.M");
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [unit, Writer(2, "DB.M")]).Prove(unit, B("OB1", unit))));
    }

    [Fact]
    public void TimerBoxBetweenTwoReadsOfItsOutputIsNotProven()
    {
        var scl = Scl(1, "OB1",
            A("OB1/1/1", "Q1", And(V("T.Q"), V("A"))),
            new IrStatement("OB1/1/2", IrStatementKinds.Assign, "DB.X", IrExpr.True, Ton("T", V("C"))) { SideWrites = ["T"] },
            A("OB1/1/3", "Q2", And(N("T.Q"), V("B"))));
        var proofs = Proofs(NotFc, Io, [scl]);
        Assert.Empty(proofs.Prove(scl, B("OB1", scl)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(scl, B("OB1", scl), "Q1", "Q2", out _));
    }

    [Fact]
    public void TimerInstanceMembersReadTwiceInAnotherSegmentAreNotStable()   // Task 4 收尾裁定：定时器/计数器实例访问即更新
    {
        var unit = CrossOnM(1, "OB1", "T.Q");
        var timer = U(2, "FB_X", new IrStatement("FB_X/1/1", IrStatementKinds.Assign, "DB.X", IrExpr.True, Ton("T", V("C"))) { SideWrites = ["T"] });
        Assert.Empty(Proofs(NotFc, Io, [unit, timer]).Prove(unit, B("OB1", unit)));
        // SCL 里以调用形式出现的 IEC 定时器（系统块，没有程序体）
        var call = U(2, "FB_X", new IrStatement("FB_X/1/1", IrStatementKinds.Call, null, IrExpr.True, null,
            new IrCall("TON", "FB", "T", IrExpr.True, [new IrPin("IN", "Input", "Bool", V("C"), [])])));
        Assert.Empty(Proofs(NotFc, Io, [unit, call]).Prove(unit, B("OB1", unit)));
    }

    static readonly IrCall FcCall = new("FC_X", "FC", "", IrExpr.True, []);
    static readonly IrCall FbCall = new("FB_C", "FB", "InstC", IrExpr.True, []);
    static IrStatement CallStatement(string id, IrCall call) => new(id, IrStatementKinds.Call, null, IrExpr.True, null, call);

    [Fact]
    public void FcCallInTheSegmentMayRewriteAnyGlobal()   // FC 内容不完整：视为可能写全部全局量
    {
        var fc = IrBlock.FromUnits("FC_X", [U(30, "FC_X", A("FC_X/1/1", "DB.Other", V("A")))], []);
        var unit = CrossOnM(1, "OB1", "DB.M", CallStatement("OB1/1/3", FcCall));
        var proofs = Proofs(NotFc, Io, [unit, Writer(2, "DB.M"), .. fc.Units], n => n == "FC_X" ? fc : null);
        Assert.Empty(proofs.Prove(unit, B("OB1", unit)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, B("OB1", unit), "Q1", "Q2", out _));
    }

    [Fact]
    public void FbCallIsAWriterOnlyOfWhatItsBodyWrites()
    {
        IrBlock Fb(string target) => IrBlock.FromUnits("FB_C", [U(20, "FB_C", A("FB_C/1/1", target, V("A")))], []);
        var unit = CrossOnM(1, "OB1", "DB.M", CallStatement("OB1/1/3", FbCall));
        var clean = Fb("DB.Other");
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"],
            Show(Proofs(NotFc, Io, [unit, Writer(2, "DB.M"), .. clean.Units], n => n == "FB_C" ? clean : null).Prove(unit, B("OB1", unit))));
        // 块体写到 DB.M（含父结构写入）
        foreach (var target in new[] { "DB.M", "DB" })
        {
            var dirty = Fb(target);
            Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M"), .. dirty.Units], n => n == "FB_C" ? dirty : null).Prove(unit, B("OB1", unit)));
        }
        // 块体再调用 FC：可能写任何全局量
        var nested = IrBlock.FromUnits("FB_C", [U(20, "FB_C", CallStatement("FB_C/1/1", FcCall))], []);
        Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M"), .. nested.Units], n => n == "FB_C" ? nested : null).Prove(unit, B("OB1", unit)));
        // 被调块没有程序体（导出缺失）：同样视为可能写任何量
        Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M")]).Prove(unit, B("OB1", unit)));
    }

    [Fact]
    public void InstructionBoxInOutWriteMakesTheReadsIndependent()
    {
        var box = new IrStatement("OB1/1/3", IrStatementKinds.Box, null, IrExpr.True,
            new IrExpr(IrKinds.Instr, "STRG_VAL|OUT", [new IrExpr(IrKinds.Port, "P", [V("DB.M")])])) { SideWrites = ["DB.M"] };
        var unit = CrossOnM(1, "OB1", "DB.M", box);
        var proofs = Proofs(NotFc, Io, [unit, Writer(2, "DB.M")]);
        Assert.Empty(proofs.Prove(unit, B("OB1", unit)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, B("OB1", unit), "Q1", "Q2", out _));
    }

    [Fact]
    public void EdgeMemoryBitWrittenInTheSegmentMakesTheReadsIndependent()
    {
        var edge = new IrStatement("OB1/1/3", IrStatementKinds.Assign, "Q3", IrExpr.True,
            And(V("C"), new IrExpr(IrKinds.Rise, null, [V("A")]))) { SideWrites = ["DB.M"] };
        var unit = CrossOnM(1, "OB1", "DB.M", edge);
        Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M")]).Prove(unit, B("OB1", unit)));
    }

    [Fact]
    public void TextStatementInTheSegmentMayWriteAnything()
    {
        foreach (var kind in new[] { IrStatementKinds.Source, IrStatementKinds.Action, IrStatementKinds.Unrecognized })
        {
            // 提到它（文字里的写入）与不提到它（文字里调用的块可能写它）都不行
            foreach (var body in new[] { "\"DB\".M := TRUE;", "FC_Write();" })
            {
                // 顺控动作按文字计写入：调用块的动作由翻译器标 TextCallsBlock（被调块可能写文字里没提到的变量）
                var text = new IrStatement("OB1/1/3", kind, null, IrExpr.True, null, null, body) { TextCallsBlock = body.Contains('(') };
                var unit = CrossOnM(1, "OB1", "DB.M", text);
                Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M")]).Prove(unit, B("OB1", unit)));
            }
        }
    }

    [Fact]
    public void UnknownWriteTargetInTheSegmentMayWriteAnything()   // 「?」：连线指向不存在的访问
    {
        var unit = CrossOnM(1, "OB1", "DB.M", A("OB1/1/3", "?", V("C")));
        Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M")]).Prove(unit, B("OB1", unit)));
    }

    [Fact]
    public void PointerAndReferencePinsMayWriteTheirActuals()
    {
        var fb = IrBlock.FromUnits("FB_C", [U(20, "FB_C", A("FB_C/1/1", "DB.Other", V("A")))], []);
        IrStatement Call(string type, IrExpr value) => CallStatement("OB1/1/3",
            new IrCall("FB_C", "FB", "InstC", IrExpr.True, [new IrPin("Src", "Input", type, value, [])]));
        NetworkProofs P(IrUnit u) => Proofs(NotFc, Io, [u, Writer(2, "DB.M"), .. fb.Units], n => n == "FB_C" ? fb : null);
        var plain = CrossOnM(1, "OB1", "DB.M", Call("Bool", V("DB.M")));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(P(plain).Prove(plain, B("OB1", plain))));
        var variant = CrossOnM(1, "OB1", "DB.M", Call("Variant", V("DB.M")));
        Assert.Empty(P(variant).Prove(variant, B("OB1", variant)));
        var dbAny = CrossOnM(1, "OB1", "DB.M", Call("DB_ANY", IrExpr.Const("5")));
        Assert.Empty(P(dbAny).Prove(dbAny, B("OB1", dbAny)));
    }

    [Fact]
    public void UnrecordedEdgeInATrustedOrderSegmentMayWriteAnything()   // 有边沿却没有 SideWrites 记录：不知道写到了哪里
    {
        IrStatement Edge(params string[] sideWrites) => new("OB1/1/2", IrStatementKinds.Assign, "DB.Y", IrExpr.True,
            new IrExpr(IrKinds.Rise, null, [V("C")])) { SideWrites = sideWrites.Length == 0 ? null : sideWrites };
        IrUnit Unit(IrStatement edge) => Scl(1, "OB1", A("OB1/1/1", "Q1", And(V("DB.M"), V("A"))), edge, A("OB1/1/3", "Q2", And(N("DB.M"), V("B"))));
        var recorded = Unit(Edge("DB.EdgeMem"));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [recorded, Writer(2, "DB.M")]).Prove(recorded, B("OB1", recorded))));
        var unrecorded = Unit(Edge());
        Assert.Empty(Proofs(NotFc, Io, [unrecorded, Writer(2, "DB.M")]).Prove(unrecorded, B("OB1", unrecorded)));
    }

    [Fact]
    public void CallInputThatTheCallItselfMayWriteIsNotTheSameValueInBothOutputs()   // 同一语句（同一时刻）的读取：顺序可信段也要看本语句的写入
    {
        IrStatement Call(bool inOut) => new("FB10061/1/1", IrStatementKinds.Call, null, IrExpr.True, null,
            new IrCall("FB_T", "FB", "TL[1]", IrExpr.True,
            [
                new IrPin("I_G", "Input", "Bool", V("A"), []), new IrPin("I_Y", "Input", "Bool", V("DB.M"), []),
                new IrPin("I_T", "Input", "Bool", IrExpr.Const("FALSE"), []),
                new IrPin("O_G", "Output", "Bool", null, ["QG"]), new IrPin("O_Y", "Output", "Bool", null, ["QY"]),
                .. inOut ? new[] { new IrPin("IO", "InOut", "Bool", V("DB.M"), ["DB.M"]) } : [],
            ]));
        var clean = Scl(1, "FB10061", Call(false));
        Assert.Equal(["AT_MOST_ONE|QG&QY|Code:CallSite"],
            Show(Proofs(NotFc, TowerTags, [clean, Writer(2, "DB.M"), .. Tower.Units], n => n == "FB_T" ? Tower : null).Prove(clean, B("FB10061", clean))));
        var written = Scl(1, "FB10061", Call(true));
        Assert.Empty(Proofs(NotFc, TowerTags, [written, Writer(2, "DB.M"), .. Tower.Units], n => n == "FB_T" ? Tower : null).Prove(written, B("FB10061", written)));
    }

    [Fact]
    public void ByteTagWriteInTheSegmentAliasesTheBitRead()   // 地址别名：%MB10 覆盖 %M10.0
    {
        var unit = CrossOnM(1, "OB1", "Flag");
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [unit, Writer(2, "Flag")]).Prove(unit, B("OB1", unit))));
        var aliased = CrossOnM(1, "OB1", "Flag", A("OB1/1/3", "FlagByte", V("DB.Val")));
        Assert.Empty(Proofs(NotFc, Io, [aliased, Writer(2, "Flag")]).Prove(aliased, B("OB1", aliased)));
    }

    [Fact]
    public void InOutFormalIsNeverStableInASegmentThatWritesAnything()   // 实参可能就是本段写的量（含本段的 %Q）
    {
        var unit = CrossOnM(1, "FB_IO", "IO");
        Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "IO")]).Prove(unit, IrBlock.FromUnits("FB_IO", [unit], [M("InOut", "IO")])));
        // 对照：同名的 Static 成员（写 %Q 改不到它）
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"],
            Show(Proofs(NotFc, Io, [unit, Writer(2, "IO")]).Prove(unit, IrBlock.FromUnits("FB_IO", [unit], [M("Static", "IO")]))));
    }

    [Fact]
    public void StaticMayBeWrittenThroughTheOwnInstanceDbName()
    {
        var iface = new[] { M("Static", "St") };
        IrUnit Unit(params IrStatement[] extra) => CrossOnM(1, "FB_O", "St", extra);
        var stWriter = U(2, "FB_O", A("FB_O/2/1", "St", V("C")));
        IrBlock Block(IrUnit u) => IrBlock.FromUnits("FB_O", [u, stWriter], iface);
        var clean = Unit();
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [clean, stWriter]).Prove(clean, Block(clean))));
        // 不知道本块的背景 DB：任何全局 DB 名写入都可能是它
        var unknown = Unit(A("FB_O/1/3", "\"Any_DB\".X", V("C")));
        Assert.Empty(Proofs(NotFc, Io, [unknown, stWriter]).Prove(unknown, Block(unknown)));
        // 已知唯一全局背景 DB：写别的 DB 无关，写它本身才算
        var caller = U(3, "OB1", CallStatement("OB1/1/1", new IrCall("FB_O", "FB", "\"Inst_O\"", IrExpr.True, []) { InstanceScope = "GlobalVariable" }));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [unknown, stWriter, caller]).Prove(unknown, Block(unknown))));
        var own = Unit(A("FB_O/1/3", "\"Inst_O\".X", V("C")));
        Assert.Empty(Proofs(NotFc, Io, [own, stWriter, caller]).Prove(own, Block(own)));
    }

    [Fact]
    public void OutputWithAnotherWriterIsNotADefinition()
    {
        var unit = U(1, "OB1", A("OB1/1/1", "Q1", And(V("A"), N("B"))), A("OB1/1/2", "Q2", And(V("B"), V("C"))));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [unit]).Prove(unit, B("OB1", unit))));
        var set = U(2, "OB2", new IrStatement("OB2/1/1", IrStatementKinds.Set, "Q1", V("C"), null));
        Assert.Empty(Proofs(NotFc, Io, [unit, set]).Prove(unit, B("OB1", unit)));
    }

    [Fact]
    public void OutputWrittenInAnFcIsNotADefinitionEvenThoughTheQIndexSkipsFc()   // ProjectWrites 含 FC 的写入
    {
        var unit = U(1, "OB1", A("OB1/1/1", "Q1", And(V("A"), N("B"))), A("OB1/1/2", "Q2", And(V("B"), V("C"))));
        var fc = U(5, "FC9", new IrStatement("FC9/1/1", IrStatementKinds.Set, "Q1", V("C"), null));
        Assert.Empty(Proofs(NotFc, Io, [unit, fc], qUnits: [unit]).Prove(unit, B("OB1", unit)));
    }

    [Fact]
    public void FcCallAfterTheOutputsInATrustedOrderSegmentMayRewriteThem()
    {
        IrStatement[] defs = [A("OB1/1/1", "Q1", And(V("A"), N("B"))), A("OB1/1/2", "Q2", And(V("B"), V("C")))];
        var clean = Scl(1, "OB1", defs);
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [clean]).Prove(clean, B("OB1", clean))));
        var after = Scl(1, "OB1", [.. defs, CallStatement("OB1/1/3", FcCall)]);
        Assert.Empty(Proofs(NotFc, Io, [after]).Prove(after, B("OB1", after)));
        // 同步也一样：两条同值赋值之后的 FC 可能改写其中一个
        var same = Scl(1, "OB1", A("OB1/1/1", "Q1", And(V("A"), V("B"))), A("OB1/1/2", "Q2", And(V("A"), V("B"))));
        Assert.Equal(["SAME_STATE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [same]).Prove(same, B("OB1", same))));
        var sameAfter = Scl(1, "OB1", [.. same.Statements, CallStatement("OB1/1/3", FcCall)]);
        Assert.Empty(Proofs(NotFc, Io, [sameAfter]).Prove(sameAfter, B("OB1", sameAfter)));
    }

    [Fact]
    public void CallBetweenTwoReadsInATrustedOrderSegmentIsABarrier()   // 即使块体不写它
    {
        var fb = IrBlock.FromUnits("FB_C", [U(20, "FB_C", A("FB_C/1/1", "DB.Other", V("A")))], []);
        var unit = Scl(1, "OB1",
            A("OB1/1/1", "Q1", And(V("DB.M"), V("A"))), CallStatement("OB1/1/2", FbCall), A("OB1/1/3", "Q2", And(N("DB.M"), V("B"))));
        Assert.Empty(Proofs(NotFc, Io, [unit, Writer(2, "DB.M"), .. fb.Units], n => n == "FB_C" ? fb : null).Prove(unit, B("OB1", unit)));
    }

    [Fact]
    public void InstanceWrittenBetweenTheCallAndTheReadIsNotExpanded()
    {
        var unit = Scl(1, "FB10061",
            CallT("FB10061/1/1", null, false),
            A("FB10061/1/2", "TL[1].O_G", V("C")),
            A("FB10061/1/3", "QA", V("TL[1].O_G")),
            A("FB10061/1/4", "QC", V("TL[1].O_Y")));
        var block = IrBlock.FromUnits("FB10061", [unit], [M("Static", "TL", "Array")]);
        Assert.Equal(ProofVerdict.NotProven, TowerProofs(unit).MutexVerdict(unit, block, "QA", "QC", out _));
    }

    // ───────────── Task 6 审查回归（探针 P1~P6） ─────────────

    [Fact]
    public void VariableIndexReadsAreIndependentEvenWhenTheIndexIsRewritten()   // C-1：P1a、P1b、P1c
    {
        IrStatement[] cross = [A("OB1/1/1", "Q1", And(V("Arr[Idx]"), V("B"))), A("OB1/1/2", "Idx", V("C")), A("OB1/1/3", "Q2", And(N("Arr[Idx]"), V("C")))];
        foreach (var unit in new[] { U(1, "OB1", cross), Scl(1, "OB1", cross) })
        {
            var proofs = Proofs(NotFc, Io, [unit, Writer(2, "Arr")]);
            Assert.Empty(proofs.Prove(unit, B("OB1", unit)));
            Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, B("OB1", unit), "Q1", "Q2", out _));
        }
        var same = U(1, "OB1", A("OB1/1/1", "Q1", V("Arr[Idx]")), A("OB1/1/2", "Idx", V("C")), A("OB1/1/3", "Q2", V("Arr[Idx]")));
        var p = Proofs(NotFc, Io, [same, Writer(2, "Arr")]);
        Assert.Empty(p.Prove(same, B("OB1", same)));
        Assert.False(p.SameStateProven(same, B("OB1", same), "Q1", "Q2"));
        // 下标本段不写也一样（裁定：非字面量下标的读取一律独立）
        var unwritten = U(1, "OB1", A("OB1/1/1", "Q1", And(V("Arr[Idx]"), V("B"))), A("OB1/1/3", "Q2", And(N("Arr[Idx]"), V("C"))));
        Assert.Empty(Proofs(NotFc, Io, [unwritten, Writer(2, "Arr"), Writer(3, "Idx")]).Prove(unwritten, B("OB1", unwritten)));
        // 对照：字面量下标照常
        var literal = U(1, "OB1", A("OB1/1/1", "Q1", And(V("Arr[2]"), V("B"))), A("OB1/1/3", "Q2", And(N("Arr[2]"), V("C"))));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"], Show(Proofs(NotFc, Io, [literal, Writer(2, "Arr")]).Prove(literal, B("OB1", literal))));
    }

    [Fact]
    public void VariableIndexRewrittenByACallOutputOrATempIsIndependent()   // C-1：P1d、P1f
    {
        var fb = IrBlock.FromUnits("FB_C", [U(20, "FB_C", A("FB_C/1/1", "DB.Other", V("A")))], [M("Output", "O")]);
        var call = CallStatement("OB1/1/2", new IrCall("FB_C", "FB", "InstC", IrExpr.True, [new IrPin("O", "Output", "Int", null, ["Idx"])]));
        var viaCall = U(1, "OB1", A("OB1/1/1", "Q1", And(V("Arr[Idx]"), V("B"))), call, A("OB1/1/3", "Q2", And(N("Arr[Idx]"), V("C"))));
        Assert.Empty(Proofs(NotFc, Io, [viaCall, Writer(2, "Arr"), .. fb.Units], n => n == "FB_C" ? fb : null).Prove(viaCall, B("OB1", viaCall)));
        var temp = U(1, "FB_Y", A("FB_Y/1/1", "Q1", And(V("Arr[#i]"), V("B"))), A("FB_Y/1/2", "i", V("C")), A("FB_Y/1/3", "Q2", And(N("Arr[#i]"), V("C"))));
        Assert.Empty(Proofs(NotFc, Io, [temp, Writer(2, "Arr")]).Prove(temp, IrBlock.FromUnits("FB_Y", [temp], [M("Temp", "i", "Int")])));
    }

    /// <summary>FB_X：块内 TON 的多重背景 T1；FB_Y：块内无程序体系统块 MC_Power 的多重背景 Pwr。</summary>
    static readonly IrUnit FbXTimer = U(30, "FB_X",
        new IrStatement("FB_X/1/1", IrStatementKinds.Assign, "DB.X", IrExpr.True, Ton("T1", V("C"))) { SideWrites = ["T1"] });
    static readonly IrUnit FbYPower = U(31, "FB_Y", CallStatement("FB_Y/1/1",
        new IrCall("MC_Power", "FB", "Pwr", IrExpr.True, [new IrPin("Enable", "Input", "Bool", V("C"), [])])));

    [Fact]
    public void TimerAndSystemBlockInstancesAreVolatileUnderAnyOuterPath()   // I-1：P2a、P2b、P2d、P2e
    {
        IrUnit Caller(long id, string block, string callee, string instance) =>
            U(id, block, CallStatement($"{block}/9/1", new IrCall(callee, "FB", instance, IrExpr.True, []) { InstanceScope = "GlobalVariable" }));
        var fbX = IrBlock.FromUnits("FB_X", [FbXTimer], [M("Static", "T1", "TON_TIME")]);
        IrBlock? Blocks(string n) => n == "FB_X" ? fbX : null;
        // P2a：全局背景 DB 下的 TON 成员
        var global = CrossOnM(1, "OB1", "FB_X_DB.T1.Q");
        Assert.Empty(Proofs(NotFc, Io, [global, FbXTimer, Caller(2, "OB9", "FB_X", "FB_X_DB")], Blocks).Prove(global, B("OB1", global)));
        // P2b：同步
        var same = U(1, "OB1", A("OB1/1/1", "Q1", V("FB_X_DB.T1.Q")), A("OB1/1/2", "Q2", V("FB_X_DB.T1.Q")));
        Assert.Empty(Proofs(NotFc, Io, [same, FbXTimer, Caller(2, "OB9", "FB_X", "FB_X_DB")], Blocks).Prove(same, B("OB1", same)));
        // P2d：调用方多重背景 X 下的 TON 成员
        var multi = CrossOnM(1, "FB_P", "X.T1.Q");
        var callX = U(2, "FB_P", CallStatement("FB_P/2/1", new IrCall("FB_X", "FB", "X", IrExpr.True, [])));
        Assert.Empty(Proofs(NotFc, Io, [multi, callX, FbXTimer], Blocks)
            .Prove(multi, IrBlock.FromUnits("FB_P", [multi, callX], [M("Static", "X", "FB_X")])));
        // P2e：多重背景 Yi 下无程序体系统块 Pwr 的成员
        var power = CrossOnM(1, "FB_P2", "Yi.Pwr.Status");
        var callY = U(2, "FB_P2", CallStatement("FB_P2/2/1", new IrCall("FB_Y", "FB", "Yi", IrExpr.True, [])));
        var fbY = IrBlock.FromUnits("FB_Y", [FbYPower], [M("Static", "Pwr", "MC_Power")]);
        Assert.Empty(Proofs(NotFc, Io, [power, callY, FbYPower], n => n == "FB_Y" ? fbY : null)
            .Prove(power, IrBlock.FromUnits("FB_P2", [power, callY], [M("Static", "Yi", "FB_Y")])));
    }

    [Fact]
    public void SameStateComparesExpressionsCaseSensitively()   // I-2：P4a、P4b
    {
        foreach (var (lower, upper) in new[] { ("'a'", "'A'"), ("'abc'", "'ABC'") })
        {
            var unit = U(1, "OB1",
                A("OB1/1/1", "Q1", new IrExpr(IrKinds.Cmp, "==", [V("DB.Ch"), IrExpr.Const(lower)])),
                A("OB1/1/2", "Q2", new IrExpr(IrKinds.Cmp, "==", [V("DB.Ch"), IrExpr.Const(upper)])));
            var proofs = Proofs(NotFc, Io, [unit, Writer(2, "DB.Ch")]);
            Assert.Empty(proofs.Prove(unit, B("OB1", unit)));
            Assert.False(proofs.SameStateProven(unit, B("OB1", unit), "Q1", "Q2"));
        }
    }

    [Fact]
    public void FcBlockMustBeDeclaredAndItsSegmentsAreNotProven()   // I-3：P3d、P3f
    {
        var q = new UnderstandingRuleSource.QWriteIndex(Io, []);
        Assert.Throws<ArgumentNullException>(() => new NetworkProofs(Io, q.Count, q.HasUnresolved, new ProjectWrites([], Io), _ => null, null!));
        var iface = new[] { M("Input", "I_X") };
        var caller = U(9, "OB1", CallStatement("OB1/1/1", new IrCall("FC1", "FC", "", IrExpr.True, [new IrPin("I_X", "Input", "Bool", V("Flag"), [])])));
        var flg = U(1, "FC1",
            new IrStatement("FC1/1/1", IrStatementKinds.Reset, "Flag", V("C"), null),
            A("FC1/1/2", "Q1", IrExpr.Or([And(V("I_X"), V("A"), N("I_X")), V("B")])),
            A("FC1/1/3", "Q2", And(N("B"), V("C"))));
        Assert.Empty(Proofs(n => n == "FC1", Io, [flg, caller, Writer(2, "I_X")]).Prove(flg, IrBlock.FromUnits("FC1", [flg], iface)));
        var scl = Scl(1, "FC1", A("FC1/1/1", "Q1", And(V("I_X"), V("A"))), new IrStatement("FC1/1/2", IrStatementKinds.Reset, "Flag", V("C"), null),
            A("FC1/1/3", "Q2", And(N("I_X"), V("B"))));
        Assert.Empty(Proofs(n => n == "FC1", Io, [scl, caller, Writer(2, "I_X")]).Prove(scl, IrBlock.FromUnits("FC1", [scl], iface)));
    }

    static IrUnit Lad(FlgNetBuilder b)
    {
        var r = FlgNetTranslator.TranslateFull(XElement.Parse(b.Build()), "OB1/1");
        return new IrUnit(1, "OB1", 1, "LAD", "FlgNet", r.Statements, r.Nodes, r.UnknownParts, InstructionNames: r.Instructions);
    }

    [Fact]
    public void EnoOfAnInstructionIsNotItsEn()   // C-2：P5、P6（真实翻译器）
    {
        // P5：┤A├[ADD en]─eno─[NOT]─(Q1)；┤A├┤B├(Q2)
        var b = new FlgNetBuilder("GlobalVariable");
        var a = b.Contact("A"); b.Wire(FlgNetBuilder.Rail(), FlgNetBuilder.Pin(a, "in"));
        var add = b.Part("Add"); b.Wire(FlgNetBuilder.Pin(a, "out"), FlgNetBuilder.Pin(add, "en"));
        var not = b.Part("Not"); b.Wire(FlgNetBuilder.Pin(add, "eno"), FlgNetBuilder.Pin(not, "in"));
        b.Coil("Coil", "Q1", FlgNetBuilder.Pin(not, "out"));
        b.Series("Coil", "Q2", ("A", false), ("B", false));
        var p5 = Lad(b);
        var proofs = Proofs(NotFc, Io, [p5]);
        Assert.Empty(proofs.Prove(p5, B("OB1", p5)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(p5, B("OB1", p5), "Q1", "Q2", out _));
        // P6：┤A├[ADD en]─eno─(Q1)；┤A├(Q2)
        var c = new FlgNetBuilder("GlobalVariable");
        var a2 = c.Contact("A"); c.Wire(FlgNetBuilder.Rail(), FlgNetBuilder.Pin(a2, "in"));
        var add2 = c.Part("Add"); c.Wire(FlgNetBuilder.Pin(a2, "out"), FlgNetBuilder.Pin(add2, "en"));
        c.Coil("Coil", "Q1", FlgNetBuilder.Pin(add2, "eno"));
        c.Series("Coil", "Q2", ("A", false));
        var p6 = Lad(c);
        var shown = Show(Proofs(NotFc, Io, [p6]).Prove(p6, B("OB1", p6)));
        Assert.DoesNotContain(shown, r => r.Contains("Q1", StringComparison.Ordinal));
        Assert.False(Proofs(NotFc, Io, [p6]).SameStateProven(p6, B("OB1", p6), "Q1", "Q2"));
    }

    [Fact]
    public void ProofThatHoldsForAnyUnwiredInputValueIsCodeEvenIfTheDefaultMakesItVacuous()   // Minor M-1：P14
    {
        var fb = IrBlock.FromUnits("FB_E",
            [U(50, "FB_E", A("FB_E/1/1", "O1", And(V("I1"), V("En1"))), A("FB_E/1/2", "O2", And(N("I1"), V("En1"))))],
            [M("Input", "I1"), M("Input", "En1"), M("Output", "O1"), M("Output", "O2")]);
        var unit = U(1, "OB1", CallStatement("OB1/1/1", new IrCall("FB_E", "FB", "E1", IrExpr.True,
        [
            new IrPin("I1", "Input", "Bool", V("A"), []), new IrPin("En1", "Input", "Bool", null, []),
            new IrPin("O1", "Output", "Bool", null, ["Q1"]), new IrPin("O2", "Output", "Bool", null, ["Q2"]),
        ])));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:CallSite"],
            Show(Proofs(NotFc, Io, [unit, .. fb.Units], n => n == "FB_E" ? fb : null).Prove(unit, B("OB1", unit))));
    }

    // ───────────── Task 6 复审回归（N4、N5） ─────────────

    /// <summary>FB_T：InOut 形参 TimerIO（IEC_TIMER）在块内作 TON 盒的实例（博图推荐的 FC/FB 内定时器写法）。</summary>
    static IrBlock TimerViaInOut(string name) => IrBlock.FromUnits(name,
        [U(60, name, new IrStatement($"{name}/1/1", IrStatementKinds.Assign, "TimerIO.IN", IrExpr.True, Ton("TimerIO", V("C"))) { SideWrites = ["TimerIO"] })],
        [M("InOut", "TimerIO", "IEC_TIMER")]);

    static IrUnit CallsTimerBlock(string callee, string blockType, string actual) => U(9, "OB9", CallStatement("OB9/1/1",
        new IrCall(callee, blockType, blockType == "FB" ? callee + "_DB" : "", IrExpr.True,
            [new IrPin("TimerIO", "InOut", "IEC_TIMER", V(actual), [actual])]) { InstanceScope = blockType == "FB" ? "GlobalVariable" : null }));

    [Fact]
    public void TimerPassedThroughAnInOutParameterIsVolatileAtTheCaller()   // R-2：N4、N4b、N4c、N4d
    {
        var fb = TimerViaInOut("FB_T");
        IrBlock? FbOnly(string n) => n == "FB_T" ? fb : null;
        // N4：调用方读实参 DB.Tmr 的成员
        var cross = CrossOnM(1, "OB1", "DB.Tmr.Q");
        Assert.Empty(Proofs(NotFc, Io, [cross, CallsTimerBlock("FB_T", "FB", "DB.Tmr"), .. fb.Units], FbOnly).Prove(cross, B("OB1", cross)));
        // N4b：同步
        var same = U(1, "OB1", A("OB1/1/1", "Q1", V("DB.Tmr.Q")), A("OB1/1/2", "Q2", V("DB.Tmr.Q")));
        var p = Proofs(NotFc, Io, [same, CallsTimerBlock("FB_T", "FB", "DB.Tmr"), .. fb.Units], FbOnly);
        Assert.Empty(p.Prove(same, B("OB1", same)));
        Assert.False(p.SameStateProven(same, B("OB1", same), "Q1", "Q2"));
        // N4c：被调块是 FC
        var fc = TimerViaInOut("FC_T");
        Assert.Empty(Proofs(n => n == "FC_T", Io, [cross, CallsTimerBlock("FC_T", "FC", "DB.Tmr"), .. fc.Units], n => n == "FC_T" ? fc : null)
            .Prove(cross, B("OB1", cross)));
        // N4d：整个定时器背景 DB 作实参
        var whole = CrossOnM(1, "OB1", "\"T2_DB\".Q");
        Assert.Empty(Proofs(NotFc, Io, [whole, CallsTimerBlock("FB_T", "FB", "\"T2_DB\""), .. fb.Units], FbOnly).Prove(whole, B("OB1", whole)));
        // 对照：同样的调用但形参类型不是定时器/计数器、块内也没有用它当实例 → 实参的成员照常稳定
        var plainFb = IrBlock.FromUnits("FB_T", [U(60, "FB_T", A("FB_T/1/1", "TimerIO.IN", V("C")))], [M("InOut", "TimerIO", "UDT_Plain")]);
        var plainCall = U(9, "OB9", CallStatement("OB9/1/1", new IrCall("FB_T", "FB", "FB_T_DB", IrExpr.True,
            [new IrPin("TimerIO", "InOut", "UDT_Plain", V("DB.Tmr"), ["DB.Tmr"])]) { InstanceScope = "GlobalVariable" }));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"],
            Show(Proofs(NotFc, Io, [cross, plainCall, .. plainFb.Units], n => n == "FB_T" ? plainFb : null).Prove(cross, B("OB1", cross))));
    }

    /// <summary>FB_S：程序体是 SCL 源码（IR 里只有一条文字语句），Static T1 是 TOF_TIME。</summary>
    static IrBlock SclTimerFb(string staticType) => IrBlock.FromUnits("FB_S",
        [new IrUnit(70, "FB_S", 1, "SCL", "StructuredText",
            [new IrStatement("FB_S/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "#T1(IN := #Start, PT := T#2s);\n#Done := #T1.Q;")], [], [])],
        [M("Input", "Start"), M("Output", "Done"), M("Static", "T1", staticType)]);

    [Fact]
    public void TimerInsideATextBodiedFbIsVolatileUnderItsInstancePath()   // R-1：N5、N5b
    {
        foreach (var staticType in new[] { "TOF_TIME", "Struct" })   // 有类型（按类型认出）/ 没有类型（按程序体不可分析兜底）
        {
            var fb = SclTimerFb(staticType);
            IrBlock? Blocks(string n) => n == "FB_S" ? fb : null;
            // N5：OB9 以全局背景 FB_S_DB 调用，OB1 读 FB_S_DB.T1.Q 两次
            var caller = U(9, "OB9", CallStatement("OB9/1/1", new IrCall("FB_S", "FB", "FB_S_DB", IrExpr.True, []) { InstanceScope = "GlobalVariable" }));
            var cross = CrossOnM(1, "OB1", "FB_S_DB.T1.Q");
            Assert.Empty(Proofs(NotFc, Io, [cross, caller, .. fb.Units], Blocks).Prove(cross, B("OB1", cross)));
            // N5b：FB_P 的多重背景 S1
            var multi = CrossOnM(1, "FB_P", "S1.T1.Q");
            var callS1 = U(2, "FB_P", CallStatement("FB_P/2/1", new IrCall("FB_S", "FB", "S1", IrExpr.True, [])));
            Assert.Empty(Proofs(NotFc, Io, [multi, callS1, .. fb.Units], Blocks)
                .Prove(multi, IrBlock.FromUnits("FB_P", [multi, callS1], [M("Static", "S1", "FB_S")])));
        }
    }

    [Fact]
    public void TimerTypedStaticOfAnAnalysableFbIsVolatileEvenIfTheIrNeverUsesItAsABox()   // 按类型认出（与程序体是否可分析无关）
    {
        var fb = IrBlock.FromUnits("FB_K", [U(80, "FB_K", A("FB_K/1/1", "Done", V("Start")))],
            [M("Input", "Start"), M("Output", "Done"), M("Static", "Tm", "Array[0..3] of IEC_TIMER")]);
        var caller = U(9, "OB9", CallStatement("OB9/1/1", new IrCall("FB_K", "FB", "FB_K_DB", IrExpr.True, []) { InstanceScope = "GlobalVariable" }));
        var cross = CrossOnM(1, "OB1", "FB_K_DB.Tm[1].Q");
        Assert.Empty(Proofs(NotFc, Io, [cross, caller, .. fb.Units], n => n == "FB_K" ? fb : null).Prove(cross, B("OB1", cross)));
        // 对照：同一个块的普通输出照常稳定
        var done = CrossOnM(1, "OB1", "FB_K_DB.Done");
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:Network"],
            Show(Proofs(NotFc, Io, [done, caller, .. fb.Units], n => n == "FB_K" ? fb : null).Prove(done, B("OB1", done))));
    }

    [Fact]
    public void GlobalTimerDbCalledOnlyInSourceTextIsNotStable()   // N5c 对照
    {
        var text = U(9, "FB_Z", new IrStatement("FB_Z/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "\"T_DB\".TON(IN := \"Start\", PT := T#1s);"));
        var cross = CrossOnM(1, "OB1", "\"T_DB\".Q");
        Assert.Empty(Proofs(NotFc, Io, [cross, text]).Prove(cross, B("OB1", cross)));
    }

    // ───────────── 控制流、FC（硬性要求 1、8） ─────────────

    [Fact]
    public void BlockWithControlFlowGivesNoProofs()   // 裁定 F2
    {
        var unit = CrossOnM(1, "FB_J", "DB.M");
        var jump = U(3, "FB_J", new IrStatement("FB_J/2/1", IrStatementKinds.Jump, "L1", V("C"), null));
        var proofs = Proofs(NotFc, Io, [unit, jump, Writer(2, "DB.M")]);
        var block = B("FB_J", unit, jump);
        Assert.Empty(proofs.Prove(unit, block));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, block, "Q1", "Q2", out _));
        var copy = U(1, "FB_J", A("FB_J/1/1", "Q1", V("A")));
        var block2 = B("FB_J", copy, jump);
        Assert.False(Proofs(NotFc, Io, [copy, jump]).SameStateProven(copy, block2, "Q1", "A"));
    }

    [Fact]
    public void FcBlockSegmentsAreNotProven()   // 用户：FC 完全不管
    {
        var unit = CrossOnM(1, "FC10", "DB.M");
        var proofs = Proofs(n => n == "FC10", Io, [unit, Writer(2, "DB.M")]);
        Assert.Empty(proofs.Prove(unit, B("FC10", unit)));
        Assert.Equal(ProofVerdict.NotProven, proofs.MutexVerdict(unit, B("FC10", unit), "Q1", "Q2", out _));
    }

    // ───────────── 带标签原子的身份（硬性要求 6） ─────────────

    /// <summary>被调块：前一段 M := TON(...).Q（内部部件 → 不透明原子），后一段 O1 := M、O2 := NOT M。</summary>
    static readonly IrBlock StateFb = IrBlock.FromUnits("FB_S",
    [
        U(40, "FB_S", A("FB_S/1/1", "M", Ton("T", V("I_A")))),
        U(41, "FB_S", A("FB_S/2/1", "O1", V("M")), A("FB_S/2/2", "O2", N("M"))),
    ],
    [M("Input", "I_A"), M("Output", "O1"), M("Output", "O2"), M("Static", "M"), M("Static", "T", "TON_TIME")]);

    static IrStatement CallS(string id, string instance, string? o1, string? o2) => new(id, IrStatementKinds.Call, null, IrExpr.True, null,
        new IrCall("FB_S", "FB", instance, IrExpr.True,
        [
            new IrPin("I_A", "Input", "Bool", V("A"), []),
            new IrPin("O1", "Output", "Bool", null, o1 is null ? [] : [o1]),
            new IrPin("O2", "Output", "Bool", null, o2 is null ? [] : [o2]),
        ]));

    static NetworkProofs StateProofs(IrUnit unit) => Proofs(NotFc, Io, [unit, .. StateFb.Units], n => n == "FB_S" ? StateFb : null);

    [Fact]
    public void OutputsOfOneCallShareTheCallsInternalState()
    {
        var unit = U(1, "OB1", CallS("OB1/1/1", "I1", "Q1", "Q2"));
        Assert.Equal(["AT_MOST_ONE|Q1&Q2|Code:CallSite"], Show(StateProofs(unit).Prove(unit, B("OB1", unit))));
    }

    [Fact]
    public void TwoCallsDoNotShareInternalState()
    {
        var unit = U(1, "OB1", CallS("OB1/1/1", "I1", "Q1", null), CallS("OB1/1/2", "I2", null, "Q2"));
        Assert.Empty(StateProofs(unit).Prove(unit, B("OB1", unit)));
        // 同一实例调用两次也一样（第二次调用前内部状态已变）
        var twice = U(1, "OB1", CallS("OB1/1/1", "I1", "Q1", null), CallS("OB1/1/2", "I1", null, "Q2"));
        Assert.Empty(StateProofs(twice).Prove(twice, B("OB1", twice)));
    }

    [Fact]
    public void EachReadOfAnInstanceOutputIsIndependent()
    {
        var unit = Scl(1, "OB1", CallS("OB1/1/1", "I1", null, null), A("OB1/1/2", "Q1", V("I1.O1")), A("OB1/1/3", "Q2", N("I1.O1")));
        Assert.Equal(ProofVerdict.NotProven, StateProofs(unit).MutexVerdict(unit, B("OB1", unit), "Q1", "Q2", out _));
    }
}
