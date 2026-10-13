#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using System.Xml.Linq;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 以泰星正式项目 FB3100_ValveControl 的真实结构为原型（2026-09-30 摸底）：输出是内部命令位的纯复制，
/// 两个命令位互串对方常闭且都串急停常开；「伸出到位 ∧ 缩回到位」经 TON 置位报警、由复位输入清除。
/// </summary>
public class FbContractInferenceTests
{
    internal const string Fb = "ValveFB";
    internal static readonly string[] Inputs = ["I_Estop", "I_FwdAuto", "I_BkdAuto", "I_FwdPos1", "I_FwdPos2", "I_BkdPos1", "I_Reset"];
    internal static readonly string[] Outputs = ["O_FwdOut", "O_BkdOut", "Alarm3"];

    internal static string Net(Action<FlgNetBuilder> build) { var b = new FlgNetBuilder(); build(b); return b.Build(); }

    /// <summary>标准夹具网络；variant 用于反例替换其中某个网络。</summary>
    internal static List<string> ValveBody(bool mutual = true, bool timer = true, bool ncGate = false,
        bool threeInputAlarm = false, bool duplicateQFwdWriter = false,
        bool separateBkdCopy = false, bool undebouncedAlarmBranch = false,
        bool srWritesQFwd = false, bool timerAndedContact = false, bool inRangeReadsQFwd = false,
        bool alwaysTrueCopy = false, bool negatedQFwdCoil = false, string? preset = null)
    {
        // alwaysTrueCopy：泰星 FB3100 的写法——输出复制前串一个体内无定义的常开触点（夹具里叫 AlwaysTRUE，按普通变量对待）
        (string, bool)[] Copy(string command) => alwaysTrueCopy ? [("AlwaysTRUE", false), (command, false)] : [(command, false)];
        var nets = new List<string>
        {
            Net(b => { b.Series("Coil", "O_FwdOut", Copy("Cmd.QFwd")); if (!separateBkdCopy) b.Series("Coil", "O_BkdOut", Copy("Cmd.QBkd")); }),
            Net(b => { var or = b.Or("FwdCmd", "Cmd.QFwd");
                       var last = b.Chain(Pin(or, "out"), ("Cmd.QBkd", true), (ncGate ? "I_Stop" : "I_Estop", ncGate));
                       var coil = b.Coil("Coil", "Cmd.QFwd", Pin(last, "out"));
                       if (negatedQFwdCoil) b.Negate(coil, "operand"); }),
            Net(b => { var or = b.Or("BkdCmd", "Cmd.QBkd");
                       var last = mutual ? b.Chain(Pin(or, "out"), ("Cmd.QFwd", true), ("I_Estop", false))
                                         : b.Chain(Pin(or, "out"), ("I_Estop", false));
                       b.Coil("Coil", "Cmd.QBkd", Pin(last, "out")); }),
            Net(b => { b.Series("Coil", "FwdCmd", ("I_FwdAuto", false)); b.Series("Coil", "BkdCmd", ("I_BkdAuto", false)); }),
            Net(b => { var or = b.Or("I_FwdPos1", "I_FwdPos2");
                       var last = threeInputAlarm ? b.Chain(Pin(or, "out"), ("I_BkdPos1", false), ("I_Reset", false))
                                                  : b.Chain(Pin(or, "out"), ("I_BkdPos1", false));
                       if (timer && undebouncedAlarmBranch)
                       {
                           // SCoil 的 in 是 O 块：TON.Q 与一条未消抖的「伸出到位 ∧ 缩回到位」支路并联
                           var ton = b.Timer("TON", Pin(last, "out"));
                           var o = b.Part("O", 2);
                           b.Wire(Pin(ton, "Q"), Pin(o, "in1"));
                           var raw = b.Chain(Rail(), ("I_FwdPos1", false), ("I_BkdPos1", false));
                           b.Wire(Pin(raw, "out"), Pin(o, "in2"));
                           b.Coil("SCoil", "Alarm3", Pin(o, "out"));
                       }
                       else if (timer && timerAndedContact)
                       {
                           // SCoil 的 in = TON.Q 再串一个常开触点：置位表达式不是单纯的 TON
                           var ton = b.Timer("TON", Pin(last, "out"));
                           var gated = b.Chain(Pin(ton, "Q"), ("I_FwdAuto", false));
                           b.Coil("SCoil", "Alarm3", Pin(gated, "out"));
                       }
                       else if (timer)
                       {
                           var ton = b.Timer("TON", Pin(last, "out"));
                           // 泰星 FB3100 的真实导出：T#2S 是 TypedConstant；L# 前缀用来测 LiteralConstant
                           if (preset != null)
                               b.Wire(Id(preset.StartsWith("T#") ? b.Constant(preset, "TypedConstant")
                                   : preset.StartsWith("L#") ? b.Constant(preset[2..]) : b.Access(preset)), Pin(ton, "PT"));
                           b.Coil("SCoil", "Alarm3", Pin(ton, "Q"));
                       }
                       else b.Coil("SCoil", "Alarm3", Pin(last, "out"));
                       b.Series("RCoil", "Alarm3", ("S_Reset", false)); }),
            Net(b => b.Series("Coil", "S_Reset", ("I_Reset", false))),
        };
        if (separateBkdCopy) nets.Insert(1, Net(b => b.Series("Coil", "O_BkdOut", Copy("Cmd.QBkd"))));
        if (inRangeReadsQFwd) nets.Add(Net(b => { var ir = b.Part("InRange"); b.Wire(Id(b.Access("Cmd.QFwd")), Pin(ir, "VAL")); }));
        if (srWritesQFwd) nets.Add(Net(b => { var sr = b.Part("Sr"); b.Wire(Id(b.Access("Cmd.QFwd")), Pin(sr, "operand")); }));
        if (duplicateQFwdWriter) nets.Add(Net(b => b.Series("Coil", "Cmd.QFwd", ("I_FwdAuto", false))));
        return nets;
    }

    internal static PipelineSnapshot Snapshot(IEnumerable<string> bodyNets, IEnumerable<PipelineUnit>? extra = null,
        IEnumerable<PipelineTag>? tags = null, string[]? inputs = null, string[]? temps = null, string? iface = null)
    {
        iface ??= Interface(inputs ?? Inputs, Outputs, temps);
        var units = bodyNets.Select((xml, i) => new PipelineUnit(10, Fb, 100 + i, "LAD", xml, "", [],
            NetworkOrder: i + 1, InterfaceXml: iface, BlockKind: "FB")).ToList();
        if (extra != null) units.AddRange(extra);
        return new PipelineSnapshot(1, units, (tags ?? []).ToArray());
    }

    [Fact]
    public void MutuallyInterlockedOutputsAreMutex()
    {
        var contract = FbContractInference.Infer(Snapshot(ValveBody()), Fb);
        var mutex = Assert.Single(contract.Mutexes);
        Assert.Equal(("O_FwdOut", "O_BkdOut"), (mutex.OutputA, mutex.OutputB));
    }

    [Fact]
    public void OutputCopiesInDifferentNetworksAreNotMutex()
    {
        // 两个输出的复制线圈不在同一网络：可能把上一周期的值与本周期的值配成一对，互斥不可证
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(separateBkdCopy: true)), Fb).Mutexes);
    }

    [Fact]
    public void LatchWhoseSetIsNotPurelyTonIsNotAContradiction()
    {
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(undebouncedAlarmBranch: true)), Fb).Contradictions);
    }

    [Fact]
    public void MutexFromStaleCopiedReadIsNotEmitted()
    {
        // 审查反例：T := ¬Q；Q := ¬P ∧ C；P := T ∧ D；O_FwdOut := P，O_BkdOut := Q（同一网络复制）。
        // 闭包代换会得出 P ⇒ ¬Q、Q ⇒ ¬P，但 P 读的是上一周期的 ¬Q（经 T 转手），从全 false 起步第 1 周期两个输出同时为 1。
        var nets = new List<string>
        {
            Net(b => b.Series("Coil", "T", ("Q", true))),
            Net(b => b.Series("Coil", "Q", ("P", true), ("C", false))),
            Net(b => b.Series("Coil", "P", ("T", false), ("D", false))),
            Net(b => { b.Series("Coil", "O_FwdOut", ("P", false)); b.Series("Coil", "O_BkdOut", ("Q", false)); }),
        };
        Assert.Empty(FbContractInference.Infer(Snapshot(nets, inputs: ["C", "D"]), Fb).Mutexes);
    }

    [Fact]
    public void VariableAlsoWrittenBySrOperandIsNotSubstituted()
    {
        var contract = FbContractInference.Infer(Snapshot(ValveBody(srWritesQFwd: true)), Fb);
        Assert.Empty(contract.Mutexes);
        Assert.DoesNotContain(contract.Gates, g => g.Outputs.Contains("O_FwdOut"));
    }

    [Fact]
    public void LatchWhoseSetIsTonAndedWithContactIsNotAContradiction()
    {
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(timerAndedContact: true)), Fb).Contradictions);
    }

    [Fact]
    public void InputWrittenInBodyIsNotAGate()
    {
        // FB 程序体里自己写了 I_Estop：它不再是纯外部输入，不能作为门控
        var nets = ValveBody();
        nets.Add(Net(b => b.Series("Coil", "I_Estop", ("I_Reset", false))));
        var gates = FbContractInference.Infer(Snapshot(nets), Fb).Gates;
        Assert.DoesNotContain(gates, g => g.Input == "I_Estop");
        // 接口输入每次调用都由调用方重新赋值，体内那一处线圈不是它唯一的写入方，不能拿它代换（否则 I_Reset 会被误当门控）
        Assert.DoesNotContain(gates, g => g.Input == "I_Reset");
    }

    [Fact]
    public void GateOutputsAreSortedOrdinal()
    {
        var gate = Assert.Single(FbContractInference.Infer(Snapshot(ValveBody()), Fb).Gates);
        Assert.Equal(new[] { "O_BkdOut", "O_FwdOut" }, gate.Outputs);
    }

    [Fact]
    public void CommandDefinedInCopiesNetworkIsNotMutex()
    {
        // 审查反例（同一网络自上而下）：O_FwdOut := VA；VA := ¬VB ∧ X；VB := ¬VA ∧ Y；O_BkdOut := VB。
        // 从 VA=1、VB=0、X=0、Y=1 起一个周期：O_FwdOut 复制到旧 VA=1，O_BkdOut 复制到新 VB=1，两个输出同时为 1。
        var nets = new List<string>
        {
            Net(b =>
            {
                b.Series("Coil", "O_FwdOut", ("VA", false));
                b.Series("Coil", "VA", ("VB", true), ("X", false));
                b.Series("Coil", "VB", ("VA", true), ("Y", false));
                b.Series("Coil", "O_BkdOut", ("VB", false));
            }),
        };
        Assert.Empty(FbContractInference.Infer(Snapshot(nets, inputs: ["X", "Y"]), Fb).Mutexes);
    }

    [Fact]
    public void TempCommandVariablesAreNotSubstituted()
    {
        // 命令位声明为 Temp：复制发生在定义之前，读到的是未定义的局部栈数据
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(), temps: ["Cmd"]), Fb).Mutexes);
    }

    [Fact]
    public void CommandAlsoReadThroughInRangeValKeepsMutex()
    {
        Assert.Single(FbContractInference.Infer(Snapshot(ValveBody(inRangeReadsQFwd: true)), Fb).Mutexes);
    }

    [Fact]
    public void OneDirectionalInterlockIsNotMutex()
    {
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(mutual: false)), Fb).Mutexes);
    }

    [Fact]
    public void NormallyOpenInputInEveryOutputDriveIsAGate()
    {
        var gate = Assert.Single(FbContractInference.Infer(Snapshot(ValveBody()), Fb).Gates);
        Assert.Equal("I_Estop", gate.Input);
        Assert.Equal(new[] { "O_BkdOut", "O_FwdOut" }, gate.Outputs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NormallyClosedInputIsNotAGate()
    {
        var contract = FbContractInference.Infer(Snapshot(ValveBody(ncGate: true), inputs: [.. Inputs, "I_Stop"]), Fb);
        Assert.DoesNotContain(contract.Gates, g => g.Input == "I_Stop");
    }

    [Fact]
    public void DebouncedLatchedPairsAreContradictions()
    {
        var pairs = FbContractInference.Infer(Snapshot(ValveBody()), Fb).Contradictions
            .Select(c => (c.InputA, c.InputB)).Order().ToArray();
        Assert.Equal(new[] { ("I_BkdPos1", "I_FwdPos1"), ("I_BkdPos1", "I_FwdPos2") }, pairs);
    }

    [Fact]
    public void LatchWithoutTimerIsNotAContradiction()
    {
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(timer: false)), Fb).Contradictions);
    }

    [Fact]
    public void ThreeInputTermDoesNotYieldPairwiseContradictions()
    {
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(threeInputAlarm: true)), Fb).Contradictions);
    }

    [Fact]
    public void VariableWithTwoWritersIsNotSubstituted()
    {
        var contract = FbContractInference.Infer(Snapshot(ValveBody(duplicateQFwdWriter: true)), Fb);
        Assert.Empty(contract.Mutexes);
        Assert.DoesNotContain(contract.Gates, g => g.Outputs.Contains("O_FwdOut"));
    }

    [Fact]
    public void MissingBodyYieldsEmptyContract()
    {
        Assert.True(FbContractInference.Infer(Snapshot([]), Fb).IsEmpty);
    }

    // ---- 最终审查修复（F1~F5、F9）----

    /// <summary>F1：Static 区多重背景成员内嵌自己的 Sections（含与本 FB 同名的 Output、名为 Cmd 的 Input）。</summary>
    static string InterfaceWithNestedInstance() => Interface(Inputs, Outputs, statics:
    [
        new XElement("Member", new XAttribute("Name", "Inner"), new XAttribute("Datatype", "\"SubFB\""),
            new XElement("Sections",
                new XElement("Section", new XAttribute("Name", "Input"),
                    new XElement("Member", new XAttribute("Name", "Cmd"), new XAttribute("Datatype", "Bool"))),
                new XElement("Section", new XAttribute("Name", "Output"),
                    new XElement("Member", new XAttribute("Name", "O_FwdOut"), new XAttribute("Datatype", "Bool"))))),
    ]);

    [Fact]
    public void NestedInstanceSectionsAreNotPortsOfThisFb()
    {
        var contract = FbContractInference.Infer(Snapshot(ValveBody(), iface: InterfaceWithNestedInstance()), Fb);
        // 不崩；内嵌的 Input "Cmd" 不是本 FB 的输入（否则 Cmd.* 不能代换、互斥丢失），内嵌 Output 不重复计入
        Assert.Single(contract.Mutexes);
        Assert.Equal("I_Estop", Assert.Single(contract.Gates).Input);
    }

    [Fact]
    public void InferenceFailureIsCachedAsEmptyContract()
    {
        // 顶层 Output 段重复声明同名成员：推断内部抛出；ContractFor 必须缓存空契约，不重复推断、不抛出
        var iface = XElement.Parse(Interface(Inputs, Outputs));
        var output = iface.Descendants("Section").Single(s => (string?)s.Attribute("Name") == "Output");
        output.Add(new XElement(output.Elements("Member").First()));
        var units = new CountingList<PipelineUnit>(Snapshot(ValveBody(), iface: iface.ToString(SaveOptions.DisableFormatting)).Units);
        var rules = new Original353FbContractRules(new PipelineSnapshot(1, units, []));
        Assert.True(rules.ContractFor(Fb).IsEmpty);
        var enumerations = units.Enumerations;
        Assert.True(rules.ContractFor(Fb).IsEmpty);
        Assert.Equal(enumerations, units.Enumerations);
    }

    internal sealed class CountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int Enumerations { get; private set; }
        public T this[int index] => inner[index];
        public int Count => inner.Count;
        public IEnumerator<T> GetEnumerator() { Enumerations++; return inner.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void NegatedCoilIsNeverADefinition()
    {
        // F2：Cmd.QFwd 由取反线圈写（写入的是条件的反值）：不能当定义代换
        var contract = FbContractInference.Infer(Snapshot(ValveBody(negatedQFwdCoil: true)), Fb);
        Assert.Empty(contract.Mutexes);
        Assert.DoesNotContain(contract.Gates, g => g.Outputs.Contains("O_FwdOut"));
    }

    [Fact]
    public void OutputCopiesGatedByUndefinedContactAreMutex()
    {
        // F3：FB3100 形状——O_FwdOut ← AlwaysTRUE ∧ Cmd.QFwd（同一网络复制），命令位在后续网络直接互串
        var mutex = Assert.Single(FbContractInference.Infer(Snapshot(ValveBody(alwaysTrueCopy: true)), Fb).Mutexes);
        Assert.Equal(("O_FwdOut", "O_BkdOut"), (mutex.OutputA, mutex.OutputB));
        Assert.Contains(mutex.Evidence, e => e.Contains("Cmd.QFwd") && e.Contains("Cmd.QBkd"));
    }

    [Fact]
    public void GatedCopiesInDifferentNetworksAreNotMutex()
    {
        Assert.Empty(FbContractInference.Infer(Snapshot(ValveBody(alwaysTrueCopy: true, separateBkdCopy: true)), Fb).Mutexes);
    }

    [Fact]
    public void JumpInBodyRemovesGatesButKeepsMutex()
    {
        // F4：体内有跳转，后续网络可能不执行：门控不可证，互斥不受影响
        var nets = ValveBody();
        nets.Add(Net(b => { var jump = b.Part("JumpNot"); b.Wire(Rail(), Pin(jump, "in")); }));
        var contract = FbContractInference.Infer(Snapshot(nets), Fb);
        Assert.Empty(contract.Gates);
        Assert.Single(contract.Mutexes);
    }

    [Fact]
    public void ReturnInSclSegmentRemovesGates()
    {
        var nets = ValveBody();
        nets.Insert(0, "<StructuredText><Token Text=\"IF\" UId=\"1\" /><Token Text=\"return\" UId=\"2\" /></StructuredText>");
        Assert.Empty(FbContractInference.Infer(Snapshot(nets), Fb).Gates);
    }

    [Fact]
    public void MoveIntoParentStructureBlocksMemberSubstitution()
    {
        // F5：体内 MOVE 写整个 Cmd 结构：Cmd.QFwd / Cmd.QBkd 不再是唯一线圈写，不能代换
        var nets = ValveBody();
        nets.Add(Net(b => { var mv = b.Part("Move"); b.Wire(Rail(), Pin(mv, "en")); b.Wire(Pin(mv, "out1"), Id(b.Access("Cmd"))); }));
        var contract = FbContractInference.Infer(Snapshot(nets), Fb);
        Assert.Empty(contract.Mutexes);
        Assert.Empty(contract.Gates);
    }

    [Fact]
    public void BodyNetworkWithoutContentYieldsEmptyContract()
    {
        // F9：体内有 RawContent 为空的网络（内容未知）：整体空契约
        var nets = ValveBody();
        nets.Add("");
        Assert.True(FbContractInference.Infer(Snapshot(nets), Fb).IsEmpty);
    }

    // 整分支复审 N-1：矛盾记录被置位的目标与 TON 预设，供校验 AI 指认的报警位与延时

    [Theory]
    [InlineData("T#2S", "T#2S")]                       // TypedConstant
    [InlineData("L#2000", "2000")]                      // LiteralConstant
    [InlineData("C_ConflictTime", "C_ConflictTime")]    // 变量（代入时再解析）
    [InlineData(null, null)]
    public void ContradictionRecordsTheSetTargetAndTheTimerPreset(string? preset, string? expected)
    {
        var contract = FbContractInference.Infer(Snapshot(ValveBody(preset: preset), inputs: [.. Inputs, "C_ConflictTime"]), Fb);
        Assert.Equal(2, contract.Contradictions.Count);
        Assert.All(contract.Contradictions, c =>
        {
            Assert.Equal("Alarm3", c.SetTarget);
            Assert.Equal(expected, c.Preset);
        });
    }
}
