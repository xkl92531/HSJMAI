#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Networks;

public class NetworkConstraintValidatorTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement A(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);

    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("Q3", "%Q0.2", "Bool"), new("Q4", "%Q0.4", "Bool"),
        new("Q5", "%Q0.5", "Bool"), new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool"), new("I3", "%I0.2", "Bool"),
        new("K", "%M1.2", "Bool"), new("IW", "%IW2", "Word"), new("Dup", "%I3.0", "Bool"), new("Dup", "%I3.1", "Bool"),
        new("Q6", "%Q0.6", "Bool"), new("Q8", "%Q1.0", "Bool"), new("Q9", "%Q1.1", "Bool")
    ];

    /// <summary>Q1 := DB.M ∧ I1、Q2 := ¬DB.M ∧ I2（DB.M 由别处程序写入，本段不写 → 可证互斥）；Q3 := K ∧ I1（与 Q2 有反例）；
    /// Mx := 上升沿(I3)；Mb := Q5（Q5 本段只读）；Q4 := I2（拷贝输入）；I1 时置位 Q6（没有唯一定义）。K 是程序从不写入的 %M（常量样）。</summary>
    static readonly IrUnit Unit = new(1, "OB1", 1, "LAD", "FlgNet",
    [
        A("OB1/1/1", "Q1", IrExpr.And([V("DB.M"), V("I1")])),
        A("OB1/1/2", "Q2", IrExpr.And([IrExpr.Not(V("DB.M")), V("I2")])),
        A("OB1/1/3", "Q3", IrExpr.And([V("K"), V("I1")])),
        A("OB1/1/4", "Mx", new IrExpr(IrKinds.Rise, null, [V("I3")])) with { SideWrites = ["EdgeMem"] },
        A("OB1/1/5", "Mb", V("Q5")),
        A("OB1/1/6", "Q4", V("I2")),
        new IrStatement("OB1/1/7", IrStatementKinds.Set, "Q6", V("I1"), null),
    ], [], []);

    /// <summary>FlgNet 里两输出互串常闭：Q8 := I1 ∧ ¬Q9、Q9 := I2 ∧ ¬Q8（读到的是本段写入的量 → 代码判不了）。</summary>
    static readonly IrUnit Interlock = new(3, "OB2", 1, "LAD", "FlgNet",
    [
        A("OB2/1/1", "Q8", IrExpr.And([V("I1"), IrExpr.Not(V("Q9"))])),
        A("OB2/1/2", "Q9", IrExpr.And([V("I2"), IrExpr.Not(V("Q8"))])),
    ], [], []);

    static readonly IrUnit Writer = new(2, "FB_W", 1, "LAD", "FlgNet", [A("FB_W/1/1", "DB.M", V("I3"))], [], []);

    /// <summary>M 区间接写入：K 只有「不确定写入」（真实项目里系统存储位常见的情形）。</summary>
    static readonly IrUnit IndirectM = new(4, "FB_X", 1, "LAD", "FlgNet", [A("FB_X/1/1", "MB[AR1,P#0.0]", V("I3"))], [], []);

    static NetworkConstraintValidator Validator(params string[] constantOutputs)
    {
        IrUnit[] all = [Unit, Writer, Interlock, IndirectM];
        var q = new UnderstandingRuleSource.QWriteIndex(Tags, all);
        var writes = new ProjectWrites(all, Tags);
        var proofs = new NetworkProofs(Tags, q.Count, q.HasUnresolved, writes, _ => null, _ => false);
        return new NetworkConstraintValidator(Tags, proofs, new HashSet<string>(constantOutputs, StringComparer.OrdinalIgnoreCase),
            NetworkConstraintValidator.UnknownConstants(Tags, writes));
    }

    static ValidatedNetworkConstraint Check(AiNetworkConstraint c, params string[] constantOutputs) =>
        Assert.Single(Validator(constantOutputs).Validate(new AiNetworkReply("", [c]), Unit, IrBlock.FromUnits("OB1", [Unit], [])));

    static ValidatedNetworkConstraint CheckInterlock(AiNetworkConstraint c) =>
        Assert.Single(Validator().Validate(new AiNetworkReply("", [c]), Interlock, IrBlock.FromUnits("OB2", [Interlock], [])));

    static AiNetworkConstraint C(string op, string[] evidence, bool delayed = false, params (string Name, string Role)[] signals) =>
        new(op, signals.Select(s => new AiNetworkSignal(s.Name, s.Role)).ToArray(), delayed, null, evidence);

    [Fact]
    public void MutexProvenByCodeIsCodeLevelAndNamesAndEvidenceAreNormalized()
    {
        var v = Check(C("AT_MOST_ONE", ["OB1/1/1", "[OB1/1/2]", "OB1/1/1"], false, ("q1", "Member"), ("Q2", "Member")));
        Assert.Null(v.RejectReason);
        Assert.Equal(UnderstandingProofs.CodeNetwork, v.Proof);
        Assert.Equal(["Q1", "Q2"], v.Constraint.Signals.Select(s => s.Name).ToArray());
        Assert.Equal(["OB1/1/1", "OB1/1/2"], v.Constraint.Evidence);
    }

    [Fact]
    public void MutexWithACounterexampleIsRejected() =>
        Assert.Equal("RefutedByIr", Check(C("AT_MOST_ONE", ["OB1/1/2", "OB1/1/3"], false, ("Q2", "Member"), ("Q3", "Member"))).RejectReason);

    [Fact]
    public void ACounterexampleOnAnyOutputPairRefutesWiderMutexesAndExactlyOne()
    {
        Assert.Equal("RefutedByIr", Check(C("AT_MOST_ONE", ["OB1/1/2", "OB1/1/3"], false,
            ("Q2", "Member"), ("Q3", "Member"), ("I1", "Member"))).RejectReason);
        Assert.Equal("RefutedByIr", Check(C("EXACTLY_ONE", ["OB1/1/2", "OB1/1/3"], false, ("Q2", "Member"), ("Q3", "Member"))).RejectReason);
    }

    [Fact]
    public void OutputMutexThatCodeCannotDecideStaysAi()   // 两个 %Q 只用 IR 互斥判定，不做下面的可满足性反驳（FlgNet 互锁读到的是本段写入的量）
    {
        var v = CheckInterlock(C("AT_MOST_ONE", ["OB2/1/1", "OB2/1/2"], false, ("Q8", "Member"), ("Q9", "Member")));
        Assert.Null(v.RejectReason);
        Assert.Equal(UnderstandingProofs.AiNetwork, v.Proof);
    }

    [Fact]
    public void MixedMutexIsRefutedWhenTheNetworkLetsBothBeTrue() =>   // Q1 := DB.M ∧ I1：Q1 为真时 I1 必为真
        Assert.Equal("RefutedByIr",
            Check(C("AT_MOST_ONE", ["OB1/1/1", "OB1/1/2"], false, ("Q1", "Member"), ("Q2", "Member"), ("I1", "Member"))).RejectReason);

    [Fact]
    public void InstantImplicationsOtherThanFeedbackAreOutOfScope()   // 即使逻辑上成立（Q4 := I2）
    {
        Assert.Equal("InstantImplication", Check(C("IMPLIES", ["OB1/1/6"], false, ("I2", "Source"), ("Q4", "Target"))).RejectReason);
        Assert.Equal("InstantImplication", Check(C("IMPLIES", ["OB1/1/1", "OB1/1/2"], false, ("Q1", "Source"), ("Q2", "Target"))).RejectReason);
    }

    [Fact]
    public void GateTheNetworkDoesNotForceIsRefuted()   // Q1 := DB.M ∧ I1
    {
        // I1 为假时 Q1 必为假 → 成立；I2 与 Q1 无关 → 反驳
        Assert.Null(Check(C("REQUIRES_0_WHEN_FALSE", ["OB1/1/1"], false, ("I1", "Source"), ("Q1", "Target"))).RejectReason);
        Assert.Equal("RefutedByIr",
            Check(C("REQUIRES_0_WHEN_FALSE", ["OB1/1/1", "OB1/1/2"], false, ("I2", "Source"), ("Q1", "Target"))).RejectReason);
    }

    [Fact]
    public void SameStateTheNetworkCanViolateIsRefuted() =>   // Q1 := DB.M ∧ I1、Q2 := ¬DB.M ∧ I2
        Assert.Equal("RefutedByIr", Check(C("SAME_STATE", ["OB1/1/1", "OB1/1/2"], false, ("Q1", "Member"), ("Q2", "Member"))).RejectReason);

    [Fact]
    public void ConstantLikeMemoryBitsAreUnknownConstantsNotFreeValues()   // Q3 := K ∧ I1：K 为真时 Q3 与 I1 同值，不能反驳
        => Assert.Null(Check(C("SAME_STATE", ["OB1/1/3"], false, ("Q3", "Member"), ("I1", "Member"))).RejectReason);

    [Fact]
    public void UnknownConstantsAreMemoryBitsWithoutCertainWriters()
    {
        IrUnit[] all = [Unit, Writer, IndirectM, new(5, "OB5", 1, "LAD", "FlgNet", [A("OB5/1/1", "M2", V("I1"))], [], [])];
        PipelineTag[] tags = [.. Tags, new("M2", "%M2.0", "Bool"), new("M3", "%M3.0", "Bool"), new("MD", "%M4.0", "Bool"), new("MD", "%M4.1", "Bool")];
        var unknown = NetworkConstraintValidator.UnknownConstants(tags, new ProjectWrites(all, tags));
        Assert.True(unknown("K"));      // 只有 M 区间接（不确定）写入
        Assert.True(unknown("m3"));     // 没有写入
        Assert.False(unknown("M2"));    // 程序确定写入
        Assert.False(unknown("I1"));    // 不是 %M
        Assert.False(unknown("MD"));    // 点表重名
    }

    [Fact]
    public void SignalWrittenHereWithoutAUniqueDefinitionCannotBeRefuted()   // Q6 只有置位：判不了，保持 AI 指认
        => Assert.Equal(UnderstandingProofs.AiNetwork, Check(C("SAME_STATE", ["OB1/1/7"], false, ("Q6", "Member"), ("I1", "Member"))).Proof);

    [Fact]
    public void ExpansionBeyondTheNodeLimitIsUndecidedNotRefuted()
    {
        // X0 := I1，Xk := Xk-1 ∧ Xk-1 ∧ Xk-1 ∧ Xk-1，Q7 := X7 ∧ (I2 ∨ ¬I2)：逻辑上 Q7 ≡ I1，与 I2 同步可以反驳，
        // 但代入展开超过节点数上限 → 判不了，不反驳也不升级（与上限以内的对照组比较）
        IrStatement[] Chain(int depth)
        {
            var list = new List<IrStatement> { A("OB7/1/0", "X0", V("I1")) };
            for (var k = 1; k <= depth; k++)
                list.Add(A($"OB7/1/{k}", $"X{k}", IrExpr.And(Enumerable.Repeat(V($"X{k - 1}"), 4))));
            list.Add(A("OB7/1/99", "Q7", IrExpr.And([V($"X{depth}"), IrExpr.Or([V("I2"), IrExpr.Not(V("I2"))])])));
            return list.ToArray();
        }
        PipelineTag[] tags = [.. Tags, new("Q7", "%Q2.0", "Bool")];
        ValidatedNetworkConstraint Run(int depth)
        {
            var unit = new IrUnit(7, "OB7", 1, "LAD", "FlgNet", Chain(depth), [], []);
            var q = new UnderstandingRuleSource.QWriteIndex(tags, [unit]);
            var writes = new ProjectWrites([unit], tags);
            var validator = new NetworkConstraintValidator(tags, new NetworkProofs(tags, q.Count, q.HasUnresolved, writes, _ => null, _ => false),
                new HashSet<string>(), NetworkConstraintValidator.UnknownConstants(tags, writes));
            return Assert.Single(validator.Validate(new AiNetworkReply("",
                [C("SAME_STATE", ["OB7/1/99"], false, ("Q7", "Member"), ("I2", "Member"))]), unit, IrBlock.FromUnits("OB7", [unit], [])));
        }
        Assert.Equal("RefutedByIr", Run(2).RejectReason);
        var big = Run(6);
        Assert.Null(big.RejectReason);
        Assert.Equal(UnderstandingProofs.AiNetwork, big.Proof);
    }

    [Fact]
    public void DelayedRelationsAreNotRefuted() =>
        Assert.Null(Check(C("IMPLIES", ["OB1/1/1"], true, ("Q1", "Source"), ("I1", "Target"))).RejectReason);

    [Fact]
    public void InstantRelationAmongSignalsThisNetworkDoesNotWriteIsRejected()
    {
        Assert.Equal("NoSignalWrittenHere", Check(C("SAME_STATE", ["OB1/1/1", "OB1/1/2"], false, ("I1", "Member"), ("I2", "Member"))).RejectReason);
        Assert.Equal("NoSignalWrittenHere", Check(C("AT_MOST_ONE", ["OB1/1/1", "OB1/1/2"], false, ("I1", "Member"), ("I2", "Member"))).RejectReason);
        // 必须为 false（屏蔽报警等）与延时类不受此限
        Assert.Null(Check(C("MUST_BE_FALSE", ["OB1/1/1"], false, ("I1", "Target"))).RejectReason);
        Assert.Null(Check(C("AT_MOST_ONE", ["OB1/1/1", "OB1/1/2"], true, ("I1", "Member"), ("I2", "Member"))).RejectReason);
    }

    [Fact]
    public void UnprovenSameStateIsAiAndCopyOfInputIsCode()
    {
        Assert.Equal(UnderstandingProofs.AiNetwork, Check(C("SAME_STATE", ["OB1/1/3"], false, ("Q3", "Member"), ("I1", "Member"))).Proof);
        Assert.Equal(UnderstandingProofs.CodeNetwork, Check(C("SAME_STATE", ["OB1/1/6"], false, ("Q4", "Member"), ("I2", "Member"))).Proof);
    }

    [Fact]
    public void SignalWrittenAsItsAddressIsResolvedToThePointName()
    {
        var v = Check(C("SAME_STATE", ["OB1/1/6"], false, ("%Q0.4", "Member"), ("I0.1", "Member")));
        Assert.Null(v.RejectReason);
        Assert.Equal(["Q4", "I2"], v.Constraint.Signals.Select(s => s.Name).ToArray());
        Assert.Equal("SignalNotIo:%I3.0", Check(C("SAME_STATE", ["OB1/1/6"], false, ("Q4", "Member"), ("%I3.0", "Member"))).RejectReason);   // 重名点的地址
    }

    [Fact]
    public void MemoryPointIsNotAnIoSignal() =>
        Assert.Equal("SignalNotIo:K", Check(C("SAME_STATE", ["OB1/1/3"], false, ("Q3", "Member"), ("K", "Member"))).RejectReason);

    [Fact]
    public void GateSourceMustBeAUniqueBoolIoPoint()
    {
        Assert.Equal("SignalNotIo:K", Check(C("REQUIRES_0_WHEN_FALSE", ["OB1/1/3"], false, ("K", "Source"), ("Q3", "Target"))).RejectReason);
        Assert.Equal("SignalNotIo:DB.M", Check(C("REQUIRES_0_WHEN_FALSE", ["OB1/1/1"], false, ("DB.M", "Source"), ("Q1", "Target"))).RejectReason);
        Assert.Null(Check(C("REQUIRES_0_WHEN_FALSE", ["OB1/1/1"], false, ("I1", "Source"), ("Q1", "Target"))).RejectReason);
    }

    [Theory]
    [InlineData("Dup")]     // 点表里重名
    [InlineData("IW")]      // 不是 Bool
    [InlineData("Nope")]    // 不在点表
    public void SignalsMustBeUniqueBoolInputsOrOutputs(string name) =>
        Assert.Equal("SignalNotIo:" + name, Check(C("SAME_STATE", ["OB1/1/3"], false, ("Q3", "Member"), (name, "Member"))).RejectReason);

    [Fact]
    public void EvidenceMustExistInThisNetworkAndMentionEverySignal()
    {
        Assert.Equal("EvidenceMissing", Check(C("SAME_STATE", [], false, ("Q4", "Member"), ("I2", "Member"))).RejectReason);
        Assert.Equal("EvidenceMissing", Check(C("SAME_STATE", ["[ ]"], false, ("Q4", "Member"), ("I2", "Member"))).RejectReason);
        Assert.Equal("EvidenceUnknown:OB1/9/9", Check(C("SAME_STATE", ["OB1/9/9"], false, ("Q4", "Member"), ("I2", "Member"))).RejectReason);
        Assert.Equal("EvidenceUnknown:FB_W/1/1", Check(C("SAME_STATE", ["FB_W/1/1"], false, ("Q4", "Member"), ("I2", "Member"))).RejectReason);
        Assert.Equal("EvidenceDoesNotMention:Q4", Check(C("SAME_STATE", ["OB1/1/3"], false, ("Q4", "Member"), ("I2", "Member"))).RejectReason);
    }

    [Fact]
    public void OutputsMustBeWrittenHere() =>
        Assert.Equal("OutputNotWrittenHere:Q5",
            Check(C("AT_MOST_ONE", ["OB1/1/1", "OB1/1/5"], false, ("Q1", "Member"), ("Q5", "Member"))).RejectReason);

    [Fact]
    public void EdgeOnlySignalIsRejected() =>
        Assert.Equal("EdgeSourceOnly:I3", Check(C("MUST_BE_FALSE", ["OB1/1/4"], true, ("I3", "Target"))).RejectReason);

    [Fact]
    public void ConstantOutputIsRejected() =>
        Assert.Equal("ConstantOutput:Q3", Check(C("SAME_STATE", ["OB1/1/3"], false, ("Q3", "Member"), ("I1", "Member")), "Q3").RejectReason);

    [Fact]
    public void InstantFeedbackIsRejectedAndDelayedFeedbackIsAi()
    {
        Assert.Equal("FeedbackMustBeDelayed", Check(C("IMPLIES", ["OB1/1/1"], false, ("Q1", "Source"), ("I1", "Target"))).RejectReason);
        var delayed = Check(C("IMPLIES", ["OB1/1/1"], true, ("Q1", "Source"), ("I1", "Target")));
        Assert.Null(delayed.RejectReason);
        var rule = NetworkConstraintValidator.ToRule(delayed, "m1", "ck");
        Assert.Equal("AfterSettling", rule.Applicability);
        Assert.True(rule.RequiresSettlingEvidence);
        Assert.Equal(UnderstandingProofs.AiNetwork, rule.Proof);
        Assert.Equal("ck", rule.Trace!.CacheKey);
        Assert.Equal("m1", rule.Trace.Model);
        Assert.Equal(["OB1/1/1"], rule.Trace.Evidence);
    }

    [Fact]
    public void InstantRuleHasNoSettlingAndKeepsTheGrade()
    {
        var rule = NetworkConstraintValidator.ToRule(
            Check(C("AT_MOST_ONE", ["OB1/1/1", "OB1/1/2"], false, ("Q1", "Member"), ("Q2", "Member"))), "m1", "ck");
        Assert.Null(rule.Applicability);
        Assert.False(rule.RequiresSettlingEvidence);
        Assert.Equal(UnderstandingProofs.CodeNetwork, rule.Proof);
        Assert.Equal(UnderstandingProofs.CodeNetwork, rule.Trace!.Origin);
        Assert.Equal(["Q1", "Q2"], rule.Signals.Select(s => s.Name).ToArray());
    }

    [Theory]
    [InlineData("AT_MOST_ONE", "Q2:Member")]
    [InlineData("AT_MOST_ONE", "Q1:Member", "Q2:Source")]
    [InlineData("IMPLIES", "Q1:Source")]
    [InlineData("IMPLIES", "Q1:Source", "I1:Source", "I2:Target")]
    [InlineData("REQUIRES_0_WHEN_FALSE", "I1:Target", "Q1:Target")]
    [InlineData("MUST_BE_FALSE", "I3:Member")]
    [InlineData("AT_MOST_ONE", "Q1:Member", "q1:Member")]   // 去重后少于原数
    [InlineData("OTHER", "Q1:Member", "Q2:Member")]
    public void ShapeIsChecked(string op, params string[] signals) =>
        Assert.Equal("Shape", Check(C(op, ["OB1/1/1"], false,
            signals.Select(s => (s.Split(':')[0], s.Split(':')[1])).ToArray())).RejectReason);

    [Fact]
    public void ValidateKeepsTheReplyOrder()
    {
        var result = Validator().Validate(new AiNetworkReply("",
        [
            C("SAME_STATE", ["OB1/1/3"], false, ("Q3", "Member"), ("K", "Member")),
            C("SAME_STATE", ["OB1/1/6"], false, ("Q4", "Member"), ("I2", "Member")),
        ]), Unit, IrBlock.FromUnits("OB1", [Unit], []));
        Assert.Equal(new string?[] { "SignalNotIo:K", null }, result.Select(r => r.RejectReason).ToArray());
    }
}
