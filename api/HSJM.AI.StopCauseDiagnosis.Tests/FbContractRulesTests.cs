#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class FbContractRulesTests
{
    static readonly PipelineTag[] Tags =
    [
        new("YV_W", "%Q1.0", "Bool"), new("YV_H", "%Q1.1", "Bool"),
        new("CYL_W", "%I2.1", "Bool"), new("CYL_W2", "%I2.3", "Bool"), new("CYL_H", "%I2.0", "Bool"),
        new("EStop_PB", "%I5.0", "Bool"),
    ];

    /// <summary>调用方网络：急停经触点接 DB 变量，到位直接接 %I，两个输出接 %Q。</summary>
    /// <summary>
    /// estopScope：急停触点变量的 Scope（null = GlobalVariable）；estopNegated：急停经常闭触点；
    /// estopAnd：急停与另一触点串联；en："" = 不接，"rail" = 接左母线，"contact" = 经触点；bindBkdPos：I_BkdPos1 是否接线。
    /// </summary>
    static string Caller(bool bindOutputs = true, bool extraWriteYvW = false, string? estopScope = null,
        bool estopNegated = false, bool estopAnd = false, string en = "", bool bindBkdPos = true, string? negatePin = null)
    {
        var b = new FlgNetBuilder("GlobalVariable");
        var call = b.Call(FbContractInferenceTests.Fb,
            ("I_Estop", "Input"), ("I_FwdPos1", "Input"), ("I_FwdPos2", "Input"), ("I_BkdPos1", "Input"),
            ("O_FwdOut", "Output"), ("O_BkdOut", "Output"));
        if (negatePin != null) b.Negate(call, negatePin);
        var estop = b.Contact("Mode.Estop", estopNegated, estopScope);
        b.Wire(Rail(), Pin(estop, "in"));
        var estopOut = Pin(estop, "out");
        if (estopAnd) estopOut = Pin(b.Chain(estopOut, ("Mode.Other", false)), "out");
        b.Wire(estopOut, Pin(call, "I_Estop"));
        if (en == "rail") b.Wire(Rail(), Pin(call, "en"));
        if (en == "contact") b.Wire(Pin(b.Chain(Rail(), ("Mode.Enable", false)), "out"), Pin(call, "en"));
        b.Wire(Id(b.Access("CYL_W")), Pin(call, "I_FwdPos1"));
        b.Wire(Id(b.Access("CYL_W2")), Pin(call, "I_FwdPos2"));
        if (bindBkdPos) b.Wire(Id(b.Access("CYL_H")), Pin(call, "I_BkdPos1"));
        if (bindOutputs)
        {
            b.Wire(Pin(call, "O_FwdOut"), Id(b.Access("YV_W")));
            b.Wire(Pin(call, "O_BkdOut"), Id(b.Access("YV_H")));
        }
        if (extraWriteYvW) b.Series("Coil", "YV_W", ("CYL_H", false));
        return b.Build();
    }

    static PipelineUnit CallerUnit(string xml) => new(20, "Caller", 500, "LAD", xml, "", [], NetworkOrder: 1, BlockKind: "FC");

    /// <summary>withEstopWriter：别处网络里急停 DB 位由物理急停按钮唯一驱动（上游追溯用）。</summary>
    static PipelineSnapshot Snapshot(string callerXml, bool withEstopWriter = true, params PipelineUnit[] others) =>
        SnapshotCore(callerXml, withEstopWriter, false, [], others);

    /// <summary>negatedEstopWriter：急停 DB 位的唯一写入线圈是取反线圈；extraTags：追加的变量表点。</summary>
    static PipelineSnapshot SnapshotCore(string callerXml, bool withEstopWriter, bool negatedEstopWriter,
        PipelineTag[] extraTags, params PipelineUnit[] others)
    {
        var extra = new List<PipelineUnit> { CallerUnit(callerXml) };
        extra.AddRange(others);
        if (withEstopWriter)
        {
            var w = new FlgNetBuilder("GlobalVariable");
            var coil = w.Series("Coil", "Mode.Estop", ("EStop_PB", false));
            if (negatedEstopWriter) w.Negate(coil, "operand");
            extra.Add(new PipelineUnit(30, "Safety", 600, "LAD", w.Build(), "", [], NetworkOrder: 1, BlockKind: "FC"));
        }
        return FbContractInferenceTests.Snapshot(FbContractInferenceTests.ValveBody(), extra, [.. Tags, .. extraTags]);
    }

    static IReadOnlyList<PipelineRule> Extract(PipelineSnapshot snapshot) =>
        new Original353FbContractRules(snapshot).Extract(snapshot.Units.Single(u => u.Id == 500));

    [Fact]
    public void BoundOutputsYieldProvenMutex()
    {
        var rule = Assert.Single(Extract(Snapshot(Caller())), r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.Equal("AT_MOST_ONE", rule.Operator);
        Assert.Equal(new[] { "YV_H", "YV_W" }, rule.Signals.Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GateUsesTheDbSourceAndTracesThePhysicalButton()
    {
        var rule = Assert.Single(Extract(Snapshot(Caller())), r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Equal("REQUIRES_0_WHEN_FALSE", rule.Operator);
        Assert.Equal("Mode.Estop", rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal(new[] { "YV_H", "YV_W" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "EStop_PB" }, rule.UpstreamSignals);
    }

    [Fact]
    public void GateWithoutUniqueUpstreamWriterHasNoUpstreamSignals()
    {
        var rule = Assert.Single(Extract(Snapshot(Caller(), withEstopWriter: false)), r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Empty(rule.UpstreamSignals);
    }

    [Fact]
    public void ContradictionPairsNeedSettlingEvidence()
    {
        var rules = Extract(Snapshot(Caller())).Where(r => r.Proof == Original353FbContractRules.ContradictionProof).ToArray();
        Assert.Equal(2, rules.Length);
        Assert.All(rules, r =>
        {
            Assert.Equal("AT_MOST_ONE", r.Operator);
            Assert.True(r.RequiresSettlingEvidence);
            Assert.Equal("AfterSettling", r.Applicability);
            Assert.Contains(r.Signals, s => s.Name == "CYL_H");
        });
    }

    [Fact]
    public void UnboundOutputsYieldNoMutexOrGate()
    {
        var rules = Extract(Snapshot(Caller(bindOutputs: false)));
        Assert.DoesNotContain(rules, r => r.Proof is Original353FbContractRules.MutexProof or Original353FbContractRules.GateProof);
    }

    [Fact]
    public void OutputWrittenElsewhereInCallerIsNotUsed()
    {
        var rules = Extract(Snapshot(Caller(extraWriteYvW: true)));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.DoesNotContain(rules, r => r.Signals.Any(s => s.Name == "YV_W"));
    }

    [Fact]
    public void ProofAndUpstreamDoNotChangeTheRuleKey()
    {
        var plain = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "x");
        Assert.Equal(plain.Key, (plain with { Proof = "p", UpstreamSignals = ["U"] }).Key);
    }

    static bool IsContract(PipelineRule r) => r.Proof is Original353FbContractRules.MutexProof
        or Original353FbContractRules.GateProof or Original353FbContractRules.ContradictionProof;

    [Fact]
    public void CallWithConditionalEnYieldsNoContractRules()
    {
        Assert.DoesNotContain(Extract(Snapshot(Caller(en: "contact"))), IsContract);
    }

    [Fact]
    public void CallWithEnOnPowerRailStillYieldsContractRules()
    {
        var rules = Extract(Snapshot(Caller(en: "rail")));
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.GateProof);
    }

    [Fact]
    public void LocalScopeGateSourceYieldsNoGateButKeepsMutexAndContradictions()
    {
        var rules = Extract(Snapshot(Caller(estopScope: "LocalVariable")));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.Equal(2, rules.Count(r => r.Proof == Original353FbContractRules.ContradictionProof));
    }

    [Fact]
    public void LocalScopeUpstreamCoilIsNotTraced()
    {
        var w = new FlgNetBuilder("LocalVariable");
        w.Series("Coil", "Mode.Estop", ("EStop_PB", false));
        var localWriter = new PipelineUnit(30, "Safety", 600, "LAD", w.Build(), "", [], NetworkOrder: 1, BlockKind: "FC");
        var rule = Assert.Single(Extract(Snapshot(Caller(), false, localWriter)), r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Empty(rule.UpstreamSignals);
    }

    [Fact]
    public void OutputWrittenByAnotherBlockIsNotUsed()
    {
        var w = new FlgNetBuilder("GlobalVariable");
        w.Series("Coil", "YV_W", ("CYL_H", false));
        var other = new PipelineUnit(40, "Other", 700, "LAD", w.Build(), "", [], NetworkOrder: 1, BlockKind: "FC");
        var rules = Extract(Snapshot(Caller(), true, other));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        var gate = Assert.Single(rules, r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Equal(new[] { "YV_H" }, gate.Signals.Where(s => s.Role == "Target").Select(s => s.Name));
    }

    [Fact]
    public void OutputMentionedInStructuredTextIsNotUsed()
    {
        const string scl = "<StructuredText><Access Scope=\"GlobalVariable\" UId=\"1\"><Symbol><Component Name=\"YV_W\" /></Symbol></Access></StructuredText>";
        var other = new PipelineUnit(40, "Other", 700, "SCL", scl, "", [], NetworkOrder: 1, BlockKind: "FC");
        var rules = Extract(Snapshot(Caller(), true, other));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.DoesNotContain(rules, r => r.Signals.Any(s => s.Name == "YV_W"));
    }

    [Fact]
    public void NormallyClosedGateContactYieldsNoGate()
    {
        Assert.DoesNotContain(Extract(Snapshot(Caller(estopNegated: true))), r => r.Proof == Original353FbContractRules.GateProof);
    }

    [Fact]
    public void AndOfTwoContactsOnGateYieldsNoGate()
    {
        Assert.DoesNotContain(Extract(Snapshot(Caller(estopAnd: true))), r => r.Proof == Original353FbContractRules.GateProof);
    }

    [Fact]
    public void UnwiredPositionInputYieldsNoContradictionForIt()
    {
        Assert.DoesNotContain(Extract(Snapshot(Caller(bindBkdPos: false))), r => r.Proof == Original353FbContractRules.ContradictionProof);
    }

    [Fact]
    public void MalformedCallIsSkippedWithoutThrowing()
    {
        var xml = System.Xml.Linq.XElement.Parse(Caller());
        var info = xml.Descendants("CallInfo").Single();
        info.AddAfterSelf(new System.Xml.Linq.XElement(info));
        var rules = Extract(Snapshot(xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));
        Assert.DoesNotContain(rules, IsContract);
    }

    [Fact]
    public void TwoDimensionalArrayAccessElsewhereDoesNotSuppressContractRules()
    {
        var net = new System.Xml.Linq.XElement("FlgNet",
            new System.Xml.Linq.XElement("Parts", Original353FlgNetTests.ArrayAccess(1, "1", "2"),
                new System.Xml.Linq.XElement("Part", new System.Xml.Linq.XAttribute("Name", "Coil"), new System.Xml.Linq.XAttribute("UId", 2))),
            new System.Xml.Linq.XElement("Wires",
                new System.Xml.Linq.XElement("Wire", new System.Xml.Linq.XAttribute("UId", 3), Id(1), Pin(2, "operand")),
                new System.Xml.Linq.XElement("Wire", new System.Xml.Linq.XAttribute("UId", 4), Rail(), Pin(2, "in"))));
        var other = new PipelineUnit(40, "Other", 700, "LAD", net.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), "", [],
            NetworkOrder: 1, BlockKind: "FC");
        var rules = Extract(Snapshot(Caller(), true, other));
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.GateProof);
    }

    [Fact]
    public void GeneratorEmitsContractRulesForTheCallerUnit()
    {
        var snapshot = Snapshot(Caller());
        var result = new Original353Generator(snapshot).Generate(snapshot.Units.Single(u => u.Id == 500));
        Assert.Contains(result.Rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.Contains(result.Rules, r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Contains(result.Rules, r => r.Proof == Original353FbContractRules.ContradictionProof);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistedGeneratorFieldCarriesTheNameRuleSwitchMarker(bool familyOff)
    {
        // 关掉一个名字规则族发布：入库每条规则 ValidStates.Generator 带 |namerules-off:<族>；全开时与常量版本相同
        var snapshot = Snapshot(Caller());
        var switches = familyOff ? NameRuleSwitches.AllOn.Without(NameRuleFamily.CoilSyncState) : NameRuleSwitches.AllOn;
        var generated = OriginalConstraintWorkflow.Generate(snapshot, default, switches);
        var rows = Original353TestPublisher.MapGenerated(1, generated, snapshot.Tags, true,
            new Dictionary<(long UnitId, string RuleKey), Original353RuleWording>(),
            new Dictionary<string, string?>(), []);
        Assert.NotEmpty(rows);
        var expected = familyOff ? OriginalConstraintWorkflow.Version + "|namerules-off:CoilSyncState" : OriginalConstraintWorkflow.Version;
        Assert.All(rows, r => Assert.Equal(expected,
            System.Text.Json.JsonDocument.Parse(r.Rule.ValidStates!).RootElement.GetProperty("Generator").GetString()));
    }

    [Fact]
    public void ContractRulesAreNotAffectedByNameRuleSwitches()
    {
        // 名字规则全关：第一期 FB 程序体代码证明照常产出，且只剩它们
        var snapshot = Snapshot(Caller());
        var result = new Original353Generator(snapshot, NameRuleSwitches.AllOff).Generate(snapshot.Units.Single(u => u.Id == 500));
        Assert.Contains(result.Rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.Contains(result.Rules, r => r.Proof == Original353FbContractRules.GateProof);
        Assert.All(result.Rules, r => Assert.NotNull(r.Proof));
        Assert.Empty(result.SafetyRules);
        Assert.Empty(result.ConditionalFeedbackRules);
    }

    // ---- 最终审查修复（F2、F4、F5）----

    [Fact]
    public void NegatedCallOutputPinIsTreatedAsUnwired()
    {
        // F2：调用点 O_FwdOut 引脚取反：YV_W 拿到的是输出的反值，不能代入契约
        var rules = Extract(Snapshot(Caller(negatePin: "O_FwdOut")));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.MutexProof);
        Assert.DoesNotContain(rules, r => r.Signals.Any(s => s.Name == "YV_W"));
    }

    [Fact]
    public void NegatedCallInputPinYieldsNoGate()
    {
        Assert.DoesNotContain(Extract(Snapshot(Caller(negatePin: "I_Estop"))), r => r.Proof == Original353FbContractRules.GateProof);
    }

    [Fact]
    public void NegatedUpstreamCoilIsNotTraced()
    {
        var rule = Assert.Single(Extract(SnapshotCore(Caller(), true, true, [])), r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Empty(rule.UpstreamSignals);
    }

    [Fact]
    public void ReturnElsewhereInCallerBlockYieldsNoGate()
    {
        // F4：调用所在块里有 Return：调用可能不执行，门控不可证；互斥不受影响
        var b = new FlgNetBuilder("GlobalVariable");
        var ret = b.Part("Return");
        b.Wire(Pin(b.Chain(Rail(), ("CYL_W", false)), "out"), Pin(ret, "in"));
        var sibling = new PipelineUnit(20, "Caller", 499, "LAD", b.Build(), "", [], NetworkOrder: 0, BlockKind: "FC");
        var rules = Extract(Snapshot(Caller(), true, sibling));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.GateProof);
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.MutexProof);
    }

    static PipelineUnit OtherWriter(Action<FlgNetBuilder> build)
    {
        var b = new FlgNetBuilder("GlobalVariable");
        build(b);
        return new PipelineUnit(40, "Other", 700, "LAD", b.Build(), "", [], NetworkOrder: 1, BlockKind: "FC");
    }

    [Fact]
    public void ByteTagWrittenElsewhereCoveringTheOutputBitYieldsNoMutex()
    {
        // F5：别处 MOVE 写 QB_Out（%QB1）覆盖 YV_W(%Q1.0)/YV_H(%Q1.1)：两个输出都不是全项目唯一写
        var other = OtherWriter(b => { var mv = b.Part("Move"); b.Wire(Rail(), Pin(mv, "en")); b.Wire(Pin(mv, "out1"), Id(b.Access("QB_Out"))); });
        var rules = Extract(SnapshotCore(Caller(), true, false, [new("QB_Out", "%QB1", "Byte")], other));
        Assert.DoesNotContain(rules, r => r.Proof is Original353FbContractRules.MutexProof or Original353FbContractRules.GateProof);
    }

    [Fact]
    public void ByteTagWrittenElsewhereOnAnotherByteKeepsMutex()
    {
        var other = OtherWriter(b => { var mv = b.Part("Move"); b.Wire(Rail(), Pin(mv, "en")); b.Wire(Pin(mv, "out1"), Id(b.Access("QB_Out"))); });
        var rules = Extract(SnapshotCore(Caller(), true, false, [new("QB_Out", "%QB2", "Byte")], other));
        Assert.Contains(rules, r => r.Proof == Original353FbContractRules.MutexProof);
    }

    [Fact]
    public void AbsoluteOutputByteWrittenElsewhereYieldsNoMutex()
    {
        // F5：无符号绝对地址 %QB1 的写入同样覆盖这两个位
        var other = OtherWriter(b => { var mv = b.Part("Move"); b.Wire(Rail(), Pin(mv, "en")); b.Wire(Pin(mv, "out1"), Id(b.AbsoluteAccess("Output", "Byte", 8))); });
        var rules = Extract(Snapshot(Caller(), true, other));
        Assert.DoesNotContain(rules, r => r.Proof == Original353FbContractRules.MutexProof);
    }
}
