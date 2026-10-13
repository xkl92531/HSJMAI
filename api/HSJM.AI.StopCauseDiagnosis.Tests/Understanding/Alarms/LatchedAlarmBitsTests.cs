#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Alarms;

/// <summary>已知问题第三批：全局 DB 里被置位、也被复位（FC 也算）的位，且同一 DB 里 ≥3 位共用同一复位/确认条件 = 锁存报警位。只按结构判定，不看名字。</summary>
public class LatchedAlarmBitsTests
{
    const string AlarmDb = """
        <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static">
          <Member Name="Err1" Datatype="Bool" />
          <Member Name="Err2" Datatype="Bool" />
          <Member Name="P1" Datatype="Bool" />
          <Member Name="P2" Datatype="Bool" />
          <Member Name="Errs" Datatype="Array[0..15] of Bool" />
          <Member Name="Word1" Datatype="Word" />
          <Member Name="Byte1" Datatype="Byte" />
          <Member Name="Count" Datatype="Int" />
          <Member Name="St" Datatype="Struct"><Member Name="Bit" Datatype="Bool" /></Member>
          <Member Name="Udt" Datatype="&quot;UDT_A&quot;"><Sections><Section Name="None"><Member Name="Flag" Datatype="Bool" /></Section></Sections></Member>
        </Section></Sections></Interface>
        """;

    const string OtherDb = """
        <Interface><Sections><Section Name="Static"><Member Name="Q1" Datatype="Bool" /><Member Name="Q2" Datatype="Bool" /></Section></Sections></Interface>
        """;

    static readonly IReadOnlyDictionary<string, string> Dbs =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) { ["AlarmDB"] = AlarmDb, ["OtherDB"] = OtherDb };

    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement Set(string id, string target, IrExpr guard) => new(id, IrStatementKinds.Set, target, guard, null);
    static IrStatement Reset(string id, string target, IrExpr guard) => new(id, IrStatementKinds.Reset, target, guard, null);
    static IrStatement Assign(string id, string target, IrExpr value) => new(id, IrStatementKinds.Assign, target, IrExpr.True, value);
    static IrUnit U(long id, string block, int network, params IrStatement[] s) => new(id, block, network, "LAD", "FlgNet", s, [], []);

    /// <summary>同 DB 里另外两位：各自的触发条件置位、共用确认信号 Ack 复位（报警组的另两位）。</summary>
    static IrUnit Peers(string ack = "Ack", string db = "AlarmDB", string p1 = "P1", string p2 = "P2") => U(900, "FB_Peer", 1,
        Set("p1s", $"{db}.{p1}", V("Trig1")), Reset("p1r", $"{db}.{p1}", V(ack)),
        Set("p2s", $"{db}.{p2}", V("Trig2")), Reset("p2r", $"{db}.{p2}", V(ack)));

    /// <summary>members：块 → 静态局部成员名；iface：块 → 完整接口（优先）；fbs：FB 块名；ifaceXml：块接口 XML（取形参注释）。</summary>
    static IReadOnlyList<LatchedAlarmBit> Find(IReadOnlyList<IrUnit> units, bool peers = true,
        IReadOnlyDictionary<string, string[]>? members = null, System.Func<string, bool>? mentioned = null, Dictionary<string, int>? notes = null,
        IReadOnlyDictionary<string, IfaceMember[]>? iface = null, string[]? fbs = null, IReadOnlyDictionary<string, string>? ifaceXml = null)
        => LatchedAlarmBits.Find(new LatchedAlarmProject(peers ? [.. units, Peers()] : units, Dbs,
            b => iface != null && iface.TryGetValue(b, out var full) ? full
                : members != null && members.TryGetValue(b, out var m) ? m.Select(n => M("Static", n)).ToArray() : [],
            mentioned ?? (_ => false))
        {
            FunctionBlocks = (fbs ?? []).ToHashSet(System.StringComparer.OrdinalIgnoreCase),
            BlockInterfaceXml = b => ifaceXml != null && ifaceXml.TryGetValue(b, out var x) ? x : null,
        }, n => { if (notes != null) notes[n] = notes.GetValueOrDefault(n) + 1; });

    static IfaceMember M(string section, string name, string type = "Bool") => new(section, name, type, null, null, []);

    static bool Has(IReadOnlyList<LatchedAlarmBit> bits, string symbol) => bits.Any(b => b.Symbol == symbol);

    [Fact]
    public void SetAndResetSharingTheAckOfTheGroupIsAnAlarm()
    {
        var bits = Find([U(1, "FB_Alarm", 3, Set("s1", "AlarmDB.Err1", V("Sensor")), Reset("r1", "AlarmDB.Err1", V("Ack")))]);
        Assert.Equal(new[] { "AlarmDB.Err1", "AlarmDB.P1", "AlarmDB.P2" }, bits.Select(b => b.Symbol).ToArray());
        var bit = bits[0];
        Assert.Equal(1, bit.SetUnitId);
        Assert.Contains(bit.Evidence, e => e.StartsWith("置位") && e.Contains("Sensor"));
        Assert.Contains(bit.Evidence, e => e.StartsWith("复位") && e.Contains("Ack"));
        Assert.Contains(bit.Evidence, e => e.StartsWith("确认条件") && e.Contains("Ack"));
    }

    [Fact]
    public void FewerThanThreeBitsSharingTheResetIsNotAGroup()
    {
        var notes = new Dictionary<string, int>();
        // 只有两位共用 Ack：不成组
        Assert.Empty(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack")),
            Set("s2", "AlarmDB.Err2", V("Y")), Reset("r2", "AlarmDB.Err2", V("Ack")))], peers: false, notes: notes));
        Assert.Equal(2, notes["LatchedNotInResetGroup"]);
    }

    [Fact]
    public void LocalAcknowledgeGroupsOnlyWithinTheSameDb()
    {
        // 确认条件含块局部量 Mode.Reset：Err1 与 OtherDB 的两位不在同一 DB，不成组
        var members = new Dictionary<string, string[]> { ["FB_A"] = ["Mode"], ["FB_Peer"] = ["Mode"] };
        var peers = U(900, "FB_A", 2,
            Set("p1s", "OtherDB.Q1", V("Trig1")), Reset("p1r", "OtherDB.Q1", V("Mode.Reset")),
            Set("p2s", "OtherDB.Q2", V("Trig2")), Reset("p2r", "OtherDB.Q2", V("Mode.Reset")));
        Assert.Empty(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Mode.Reset"))), peers],
            peers: false, members: members));
    }

    [Fact]
    public void GlobalAcknowledgeGroupsAcrossDbs()
    {
        // 确认条件全是全局量（测试线 DB3600_HMIDrive_All.Sync.AllSyncReset）：跨 DB 成组
        var bits = Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack"))),
            Peers(db: "OtherDB", p1: "Q1", p2: "Q2")], peers: false);
        Assert.Equal(new[] { "AlarmDB.Err1", "OtherDB.Q1", "OtherDB.Q2" }, bits.Select(b => b.Symbol).ToArray());
        Assert.Contains(bits[0].Evidence, e => e.Contains("全项目 3 位共用"));
    }

    [Fact]
    public void ResultEncodingBitsAreExcluded()
    {
        // PLC3 DB57_UserDefine.Bit[257~259]：OK 位在「质量 > 1」时复位（= NG 位的置位条件），NG 位反之；两位的置位都要求 ¬完成标志
        var notes = new Dictionary<string, int>();
        var ok = IrExpr.And([V("Start"), IrExpr.Not(V("AlarmDB.Errs[1]")), V("QualityOk")]);
        var ng = IrExpr.And([V("Start"), IrExpr.Not(V("AlarmDB.Errs[1]")), V("QualityNg")]);
        var bits = Find([U(1, "FC280", 18,
            Set("d", "AlarmDB.Errs[1]", IrExpr.And([V("Start"), IrExpr.Not(V("AlarmDB.Errs[1]"))])), Reset("dr", "AlarmDB.Errs[1]", V("NewCycle")),
            Set("o", "AlarmDB.Errs[2]", ok), Reset("or1", "AlarmDB.Errs[2]", V("NewCycle")), Reset("or2", "AlarmDB.Errs[2]", ng),
            Set("n", "AlarmDB.Errs[3]", ng), Reset("nr1", "AlarmDB.Errs[3]", V("NewCycle")), Reset("nr2", "AlarmDB.Errs[3]", ok))],
            peers: false, notes: notes);
        Assert.Empty(bits);
        Assert.Equal(3, notes["LatchedStateEncoding"]);
    }

    // ---- 经 FB 输出引脚的锁存报警（6.2：FB3100 的 A_Word 接 M010_0_Alarm.Type.1.Cylinder[n]） ----

    const string CylDb = """
        <Interface><Sections><Section Name="Static">
          <Member Name="1.Cylinder" Datatype="Array[1..3] of &quot;UDT_Cyl&quot;"><Sections><Section Name="None">
            <Member Name="Alarm1" Datatype="Bool" /><Member Name="Alarm3" Datatype="Bool" /><Member Name="Alarm7" Datatype="Bool" />
          </Section></Sections></Member>
          <Member Name="W" Datatype="Word" />
        </Section></Sections></Interface>
        """;

    static readonly IfaceMember[] ValveIface =
    [
        M("Input", "S_Reset"), M("Input", "I_Pos"), M("Output", "A_Word", "\"UDT_Cyl\""), M("Output", "A_Bits", "Word"), M("Static", "Stat"),
    ];

    const string ValveIfaceXml = """
        <Interface><Sections><Section Name="Output">
          <Member Name="A_Word" Datatype="&quot;UDT_Cyl&quot;"><Sections><Section Name="None">
            <Member Name="Alarm3" Datatype="Bool"><Comment><MultiLanguageText Lang="zh-CN">缩回到位超时</MultiLanguageText></Comment></Member>
          </Section></Sections></Member>
        </Section></Sections></Interface>
        """;

    /// <summary>FB 内两位报警（Alarm3、Alarm1）共用确认信号 ack。</summary>
    static IrUnit ValveBody(IrExpr ack, params IrStatement[] extra) => U(50, "FB_Valve", 15,
    [
        Set("v1", "A_Word.Alarm3", V("I_Pos")), Reset("v2", "A_Word.Alarm3", ack),
        Set("v3", "A_Word.Alarm1", V("I_A")), Reset("v4", "A_Word.Alarm1", ack),
        .. extra
    ]);

    static IrStatement CallValve(string id, string target, bool negated = false) => new(id, IrStatementKinds.Call, null, IrExpr.True, null,
        new IrCall("FB_Valve", "FB", "Valve[3]", IrExpr.Unwired(), [new IrPin("A_Word", "Output", "\"UDT_Cyl\"", null, [target], negated)]));

    static IReadOnlyList<LatchedAlarmBit> FindFb(IReadOnlyList<IrUnit> units, Dictionary<string, int>? notes = null, IReadOnlyDictionary<string, string>? dbs = null)
    {
        var all = new Dictionary<string, string>(Dbs, System.StringComparer.OrdinalIgnoreCase) { ["CylDB"] = CylDb };
        foreach (var (k, v) in dbs ?? new Dictionary<string, string>()) all[k] = v;
        return LatchedAlarmBits.Find(new LatchedAlarmProject(units, all,
            b => b == "FB_Valve" ? ValveIface : b == "FB_Caller" ? [M("Static", "Valve")] : [], _ => false)
        {
            FunctionBlocks = new HashSet<string> { "FB_Valve" },
            BlockInterfaceXml = b => b == "FB_Valve" ? ValveIfaceXml : null,
        }, n => { if (notes != null) notes[n] = notes.GetValueOrDefault(n) + 1; });
    }

    [Fact]
    public void FbOutputLatchedWithAnInputAcknowledgeBecomesTheWiredGlobalBit()
    {
        var bits = FindFb([ValveBody(new IrExpr(IrKinds.Rise, null, [V("S_Reset")])), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))]);
        Assert.Equal(new[] { "CylDB.1.Cylinder[3].Alarm1", "CylDB.1.Cylinder[3].Alarm3" }, bits.Select(b => b.Symbol).Order().ToArray());
        var bit = bits.Single(b => b.Symbol.EndsWith("Alarm3"));
        Assert.Equal(60, bit.SetUnitId);                     // 工位按调用所在程序段
        Assert.Equal(("Valve[3]", "FB_Valve"), (bit.Instance, bit.Callee));
        Assert.Equal("缩回到位超时", bit.Text);               // 全局 DB 成员没有注释时用 FB 形参注释
        Assert.Contains(bit.Evidence, e => e.Contains("输入形参/全局量"));
    }

    [Fact]
    public void FbOutputResetOnlyByAStaticIsNotAnAlarmUnlessThreeShareIt()
    {
        var notes = new Dictionary<string, int>();
        // 两位共用 FB 内部量：不够 3 位
        Assert.Empty(FindFb([ValveBody(V("Stat")), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))], notes));
        Assert.Equal(2, notes["FbLatchedNotAlarm"]);
        // FB 内 3 位共用同一静态确认量：成组
        var body = ValveBody(V("Stat"),
            Set("v5", "A_Word.Alarm7", V("I_B")), Reset("v6", "A_Word.Alarm7", V("Stat")));
        Assert.Equal(3, FindFb([body, U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))]).Count);
    }

    [Fact]
    public void FbOutputAssignedPlainlyInsideTheFbIsNotLatched()
    {
        Assert.Empty(FindFb([ValveBody(V("S_Reset"), Assign("v9", "A_Word.Alarm3", V("I_Pos"))), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))]));
    }

    [Fact]
    public void WiredGlobalBitMustBeWrittenOnlyByThatCall()
    {
        var notes = new Dictionary<string, int>();
        Assert.Equal("CylDB.1.Cylinder[3].Alarm1", Assert.Single(FindFb([ValveBody(V("S_Reset")), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]")),
            U(61, "FB_Other", 1, Assign("o1", "CylDB.1.Cylinder[3].Alarm3", V("Z")))], notes)).Symbol);
        Assert.Equal(1, notes["FbLatchedCallNotUniqueWriter"]);
        // 同一目标被两次调用写
        Assert.Empty(FindFb([ValveBody(V("S_Reset")), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"), CallValve("c2", "CylDB.1.Cylinder[3]"))]));
    }

    [Fact]
    public void NegatedPinCallerLocalOrNonGlobalTargetIsNotUsed()
    {
        Assert.Empty(FindFb([ValveBody(V("S_Reset")), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]", negated: true))]));
        Assert.Empty(FindFb([ValveBody(V("S_Reset")), U(60, "FB_Caller", 7, CallValve("c1", "Valve.Out"))]));
        Assert.Empty(FindFb([ValveBody(V("S_Reset")), U(60, "FC_Caller", 7, CallValve("c1", "IDB_X.Alarm"))]));
    }

    [Fact]
    public void WordOutputAssembledBitwiseKeepsOnlyTheLatchedBits()
    {
        // 输出引脚接整个字：FB 内按位切片置位/复位的位入清单，按位赋值的位不入
        var body = U(50, "FB_Valve", 15,
            Set("w1", "A_Bits.%X3", V("I_Pos")), Reset("w2", "A_Bits.%X3", V("S_Reset")),
            Set("w5", "A_Bits.%X5", V("I_A")), Reset("w6", "A_Bits.%X5", V("S_Reset")),
            Assign("w3", "A_Bits.%X4", V("I_Pos")));
        var call = new IrStatement("c1", IrStatementKinds.Call, null, IrExpr.True, null,
            new IrCall("FB_Valve", "FB", "Valve[3]", IrExpr.Unwired(), [new IrPin("A_Bits", "Output", "Word", null, ["CylDB.W"])]));
        Assert.Equal(new[] { "CylDB.W.%X3", "CylDB.W.%X5" }, FindFb([body, U(60, "FC_Caller", 7, call)]).Select(b => b.Symbol).Order().ToArray());
    }

    [Fact]
    public void FbOutputResetByAProcessConditionIsAProcessState()
    {
        // 测量完成/结果、MES 握手：复位是「¬启动请求」或「步号 == 5」这类流程判断，不是确认信号
        var notes = new Dictionary<string, int>();
        Assert.Empty(FindFb([ValveBody(IrExpr.Not(V("I_Start"))), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))], notes));
        Assert.Equal(2, notes["FbLatchedProcessReset"]);
        var step = new IrExpr(IrKinds.Cmp, "==", [V("Step"), IrExpr.Const("5")]);
        Assert.DoesNotContain(FindFb([ValveBody(V("S_Reset"), Reset("v7", "A_Word.Alarm3", step)), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))]),
            b => b.Symbol.EndsWith("Alarm3"));
    }

    [Fact]
    public void FbOutputWithAPrivateResetSignalIsAProcessMemory()
    {
        // 电机正转记忆：复位 = 正转到位（只复位这一位）∨ 记忆复位按钮（两位共用）——有一条确认路径不成组 → 不算
        var notes = new Dictionary<string, int>();
        var bits = FindFb([ValveBody(V("S_Reset"), Reset("v8", "A_Word.Alarm3", V("I_Arrived"))), U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))], notes);
        Assert.Equal("CylDB.1.Cylinder[3].Alarm1", Assert.Single(bits).Symbol);
        Assert.Equal(1, notes["FbLatchedNotAlarm"]);
    }

    [Fact]
    public void ResetThatOnlyUndoesTheSetConditionIsStrippedBeforeComparing()
    {
        // 泰星 FC101：置位 ¬OKn，复位 OKn ∧ Mode.Reset —— 去掉只重述置位条件的项后，确认条件都是 Mode.Reset
        var bits = Find([U(1, "FC101", 13,
            Set("s1", "AlarmDB.Err1", IrExpr.Not(V("Safety.OK1"))), Reset("r1", "AlarmDB.Err1", IrExpr.And([V("Safety.OK1"), V("Mode.Reset")])),
            Set("s2", "AlarmDB.Err2", IrExpr.Not(V("Safety.OK2"))), Reset("r2", "AlarmDB.Err2", IrExpr.And([V("Safety.OK2"), V("Mode.Reset")])),
            Set("s3", "AlarmDB.Errs[3]", IrExpr.Not(V("Safety.OK3"))),
            Reset("r3", "AlarmDB.Errs[3]", IrExpr.And([IrExpr.Not(IrExpr.Not(V("Safety.OK3"))), V("Mode.Reset")])))], peers: false);
        Assert.Equal(new[] { "AlarmDB.Err1", "AlarmDB.Err2", "AlarmDB.Errs[3]" }, bits.Select(b => b.Symbol).ToArray());
    }

    [Fact]
    public void ResetWithoutAnAcknowledgeSignalNeverGroups()
    {
        // 复位只是置位条件消失 / 自身定时 / 指令 ENO（握手、弹窗放行、调试位）：去掉后没有确认条件
        var notes = new Dictionary<string, int>();
        IrStatement[] Bit(string n, string sym) =>
        [
            Set("s" + n, sym, IrExpr.And([V("Auto"), V("Button" + n)])),
            Reset("r" + n, sym, IrExpr.Or([IrExpr.Not(V("Auto")), V(sym)])),
        ];
        Assert.Empty(Find([U(1, "FB_A", 1, [.. Bit("1", "AlarmDB.Err1"), .. Bit("2", "AlarmDB.Err2"), .. Bit("3", "AlarmDB.Errs[1]")])],
            peers: false, notes: notes));
        Assert.Equal(3, notes["LatchedNoAcknowledge"]);
    }

    [Fact]
    public void LocalAcknowledgeNamesAreQualifiedByTheirBlock()
    {
        // 两个块各有局部 Mode.Reset：不是同一个信号
        var members = new Dictionary<string, string[]> { ["FB_A"] = ["Mode"], ["FB_B"] = ["Mode"] };
        var bits = Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Mode.Reset")),
                Set("s2", "AlarmDB.Err2", V("Y")), Reset("r2", "AlarmDB.Err2", V("Mode.Reset"))),
            U(2, "FB_B", 1, Set("s3", "AlarmDB.Errs[1]", V("Z")), Reset("r3", "AlarmDB.Errs[1]", V("Mode.Reset")))], peers: false, members: members);
        Assert.Empty(bits);
    }

    [Fact]
    public void ResetInAnotherUnitStillGroupsAndStationUnitIsTheSetUnit()
    {
        var bits = Find([U(1, "FB_Alarm", 3, Set("s1", "AlarmDB.Err1", V("Sensor"))),
            U(2, "FB_Reset", 1, Reset("r1", "AlarmDB.Err1", V("Ack")))]);
        Assert.Equal(1, bits.Single(b => b.Symbol == "AlarmDB.Err1").SetUnitId);
    }

    [Fact]
    public void OnlySetOrOnlyResetIsNotLatched()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")))]), "AlarmDB.Err1"));
        Assert.False(Has(Find([U(1, "FB_A", 1, Reset("r1", "AlarmDB.Err1", V("Ack")))]), "AlarmDB.Err1"));
    }

    [Fact]
    public void PlainCoilAssignmentIsNotLatched()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Assign("a1", "AlarmDB.Err1", V("X")))]), "AlarmDB.Err1"));
    }

    [Fact]
    public void AdditionalPlainAssignmentDisqualifies()
    {
        var notes = new Dictionary<string, int>();
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack"))),
            U(2, "FB_B", 1, Assign("a1", "AlarmDB.Err1", V("Z")))], notes: notes), "AlarmDB.Err1"));
        Assert.Equal(1, notes["LatchedOtherWriter"]);
    }

    [Fact]
    public void SetAndResetInFcCount()
    {
        // 用户 2026-10-02「看 FC」：认报警位时 FC 里的置位/复位也算（约束生成仍不管 FC）
        Assert.True(Has(Find([U(1, "FC_Alarm", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack")))]), "AlarmDB.Err1"));
        Assert.True(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X"))), U(2, "FC_Alarm", 1, Reset("r1", "AlarmDB.Err1", V("Ack")))]), "AlarmDB.Err1"));
    }

    [Fact]
    public void ReadsByProgramLogicDoNotDisqualify()
    {
        // 报警汇总（任一报警 → 停机）读报警位是常态，不再排除
        Assert.True(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack"))),
            U(2, "FC_Sum", 1, Assign("a1", "AnyAlarm", IrExpr.Or([V("AlarmDB.Err1"), V("AlarmDB.P1")])))]), "AlarmDB.Err1"));
    }

    [Fact]
    public void NotAGlobalDbMemberIsNotLatched()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "Stat.Err", V("X")), Reset("r1", "Stat.Err", V("Ack")))]), "Stat.Err"));
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "IDB_Valve.Err", V("X")), Reset("r1", "IDB_Valve.Err", V("Ack")))]), "IDB_Valve.Err"));
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "%M10.0", V("X")), Reset("r1", "%M10.0", V("Ack")))]), "%M10.0"));
    }

    [Fact]
    public void LocalVariableShadowingTheDbNameIsNotLatched()
    {
        var members = new Dictionary<string, string[]> { ["FB_A"] = ["AlarmDB"] };
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack")))], members: members), "AlarmDB.Err1"));
    }

    [Fact]
    public void ArrayElementWithLiteralIndexIsLatchedAndVariableIndexIsNot()
    {
        Assert.True(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Errs[3]", V("X")), Reset("r1", "AlarmDB.Errs[3]", V("Ack")))]), "AlarmDB.Errs[3]"));
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Errs[#i]", V("X")), Reset("r1", "AlarmDB.Errs[#i]", V("Ack")))]), "AlarmDB.Errs[#i]"));
    }

    [Fact]
    public void WordBitSliceIsLatched()
    {
        Assert.True(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Word1.%X12", V("X")), Reset("r1", "AlarmDB.Word1.%X12", V("Ack")))]), "AlarmDB.Word1.%X12"));
    }

    [Fact]
    public void SliceOutsideTheWidthOrOfANonBitStringTypeIsNotLatched()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Byte1.%X8", V("X")), Reset("r1", "AlarmDB.Byte1.%X8", V("Ack")))]), "AlarmDB.Byte1.%X8"));
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Count.%X1", V("X")), Reset("r1", "AlarmDB.Count.%X1", V("Ack")))]), "AlarmDB.Count.%X1"));
    }

    [Fact]
    public void NonBoolMemberOrUnknownPathIsNotLatched()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Word1", V("X")), Reset("r1", "AlarmDB.Word1", V("Ack")))]), "AlarmDB.Word1"));
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Nope", V("X")), Reset("r1", "AlarmDB.Nope", V("Ack")))]), "AlarmDB.Nope"));
    }

    [Fact]
    public void StructAndUdtMembersResolve()
    {
        var bits = Find([U(1, "FB_A", 1,
            Set("s1", "AlarmDB.St.Bit", V("X")), Reset("r1", "AlarmDB.St.Bit", V("Ack")),
            Set("s2", "AlarmDB.Udt.Flag", V("X")), Reset("r2", "AlarmDB.Udt.Flag", V("Ack")))]);
        Assert.True(Has(bits, "AlarmDB.St.Bit"));
        Assert.True(Has(bits, "AlarmDB.Udt.Flag"));
    }

    [Fact]
    public void ClearingTheParentWithFalseIsAllowedButOtherParentWritesDisqualify()
    {
        var latched = new[] { Set("s1", "AlarmDB.Word1.%X1", V("X")), Reset("r1", "AlarmDB.Word1.%X1", V("Ack")) };
        Assert.True(Has(Find([U(1, "FB_A", 1, latched), U(2, "FB_B", 1, Assign("a1", "AlarmDB.Word1", IrExpr.Const("0")))]), "AlarmDB.Word1.%X1"));
        Assert.False(Has(Find([U(1, "FB_A", 1, latched), U(2, "FB_B", 1, Assign("a1", "AlarmDB.Word1", V("Other.W")))]), "AlarmDB.Word1.%X1"));
    }

    [Fact]
    public void SiblingBitWritesDoNotDisqualify()
    {
        Assert.True(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack")),
            Assign("a1", "AlarmDB.Err2", V("Z")))]), "AlarmDB.Err1"));
    }

    [Fact]
    public void MentionInTextDisqualifies()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack")))],
            mentioned: n => n == "AlarmDB.Err1"), "AlarmDB.Err1"));
    }

    [Fact]
    public void SetFromSeveralUnitsIsSkipped()
    {
        var notes = new Dictionary<string, int>();
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "AlarmDB.Err1", V("Ack"))),
            U(2, "FB_A", 2, Set("s2", "AlarmDB.Err1", V("Z")))], notes: notes), "AlarmDB.Err1"));
        Assert.Equal(1, notes["LatchedSetInSeveralUnits"]);
    }

    [Fact]
    public void AlwaysTrueSetIsNotAnAlarm()
    {
        Assert.False(Has(Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", IrExpr.True), Reset("r1", "AlarmDB.Err1", V("Ack")))]), "AlarmDB.Err1"));
    }

    [Fact]
    public void CaseInsensitiveTargetsMatch()
    {
        var bits = Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", V("X")), Reset("r1", "alarmdb.ERR1", V("ack")))]);
        Assert.True(Has(bits, "AlarmDB.Err1"));
    }

    [Fact]
    public void MemberNamesContainingDotsOrBracketsResolve()
    {
        // 泰星 M010_0_Alarm：成员名 "0.System"、"0.1Estop"，结构成员名就叫 "[1]"；程序里拼成 M010_0_Alarm.Type.0.System.0.1Estop.[1]
        const string xml = """
            <Interface><Sections><Section Name="Static"><Member Name="Type" Datatype="Struct">
              <Member Name="0.System" Datatype="&quot;UDT_Sys&quot;"><Sections><Section Name="None">
                <Member Name="0.1Estop" Datatype="Struct"><Member Name="[1]" Datatype="Bool" /><Member Name="[2]" Datatype="Int" /></Member>
              </Section></Sections></Member>
            </Member></Section></Sections></Interface>
            """;
        Assert.True(LatchedAlarmBits.IsBit(xml, ["Type", "0", "System", "0", "1Estop", "[1]"]));
        Assert.False(LatchedAlarmBits.IsBit(xml, ["Type", "0", "System", "0", "1Estop", "[2]"]));
        Assert.False(LatchedAlarmBits.IsBit(xml, ["Type", "0", "System", "0", "1Estop", "[3]"]));
    }

    [Fact]
    public void AmbiguousDottedResolutionIsNotABit()
    {
        const string xml = """
            <Interface><Sections><Section Name="Static">
              <Member Name="A.B" Datatype="Bool" />
              <Member Name="A" Datatype="Struct"><Member Name="B" Datatype="Bool" /></Member>
            </Section></Sections></Interface>
            """;
        Assert.False(LatchedAlarmBits.IsBit(xml, ["A", "B"]));
    }

    [Fact]
    public void EachOrPathOfTheResetIsItsOwnAcknowledgeAndTheBitItselfCountsAsTrue()
    {
        // 测试线 FC190：复位 (自己 ∧ ResetStatus) ∨ (自己 ∧ AllSyncReset)——「自己」在复位里恒按 true 看（为 false 时复位无效果），
        // 「或」的每一支是一条复位路径；Err1 只走 ResetStatus、P1/P2 只走 AllSyncReset，Err2 两条都有 → 两组各 3 位
        var bits = Find([U(1, "FC190", 4,
                Set("s1", "AlarmDB.Err1", V("A")), Reset("r1", "AlarmDB.Err1", V("ResetStatus")),
                Set("s2", "AlarmDB.Err2", V("B")),
                Reset("r2", "AlarmDB.Err2", IrExpr.Or([IrExpr.And([V("AlarmDB.Err2"), V("ResetStatus")]), IrExpr.And([V("AlarmDB.Err2"), V("AllSyncReset")])])),
                Set("s3", "AlarmDB.Errs[1]", V("C")), Reset("r3", "AlarmDB.Errs[1]", V("ResetStatus"))),
            Peers(ack: "AllSyncReset")], peers: false);
        Assert.Equal(new[] { "AlarmDB.Err1", "AlarmDB.Err2", "AlarmDB.Errs[1]", "AlarmDB.P1", "AlarmDB.P2" }, bits.Select(b => b.Symbol).ToArray());
        Assert.Equal(2, bits.Single(b => b.Symbol == "AlarmDB.Err2").Evidence.Count(e => e.StartsWith("确认条件")));
    }

    [Fact]
    public void AcknowledgeReadNegatedInsideTheSetConditionIsStillTheAcknowledge()
    {
        // PLC2 版 FB3007：置位 TON(… ∧ ¬Reset …)、复位 上升沿(Reset) ∧ 自己——Reset 出现在置位条件里不等于「复位只是条件消失」
        var ton = new IrExpr(IrKinds.Timer, "TON|T1|Q", [new IrExpr(IrKinds.Port, "IN", [IrExpr.And([V("I_Pos"), IrExpr.Not(V("S_Reset"))])]),
            new IrExpr(IrKinds.Port, "PT", [IrExpr.Const("T#2S")])]);
        var ack = IrExpr.And([new IrExpr(IrKinds.Rise, null, [V("S_Reset")]), V("A_Word.Alarm3")]);
        var body = U(50, "FB_Valve", 15,
            Set("v1", "A_Word.Alarm3", ton), Reset("v2", "A_Word.Alarm3", ack),
            Set("v3", "A_Word.Alarm1", V("I_A")), Reset("v4", "A_Word.Alarm1", new IrExpr(IrKinds.Rise, null, [V("S_Reset")])));
        Assert.Equal(2, FindFb([body, U(60, "FC_Caller", 7, CallValve("c1", "CylDB.1.Cylinder[3]"))]).Count);
    }

    [Fact]
    public void ResetLiteralThatRepeatsOrNegatesANecessarySetLiteralIsNotAnAcknowledge()
    {
        // 弹窗放行：置位 (自动 ∧ 按钮1) ∨ (自动 ∧ 按钮2)，复位 ¬自动——「自动」是每个置位分支都要的条件，¬自动 只是条件消失
        var notes = new Dictionary<string, int>();
        IrStatement[] Bit(string n, string sym) =>
        [
            Set("s" + n, sym, IrExpr.Or([IrExpr.And([V("Auto"), V("Btn" + n)]), IrExpr.And([V("Auto"), V("Other" + n)])])),
            Reset("r" + n, sym, IrExpr.Not(V("Auto"))),
        ];
        Assert.Empty(Find([U(1, "FB_A", 1, [.. Bit("1", "AlarmDB.Err1"), .. Bit("2", "AlarmDB.Err2"), .. Bit("3", "AlarmDB.Errs[1]")])],
            peers: false, notes: notes));
        Assert.Equal(3, notes["LatchedNoAcknowledge"]);
    }

    [Fact]
    public void EdgeOfASetLiteralAndAConstantMaskedSetAreConditionGone()
    {
        var notes = new Dictionary<string, int>();
        // 测试线 FC9000：置位 同步中 ∧ 需要同步、复位 下降沿(同步中)——同步命令位，不是报警
        IrStatement[] Sync(string n, string sym) =>
        [
            Set("s" + n, sym, IrExpr.And([V("AllInSync"), V("Need" + n)])),
            Reset("r" + n, sym, new IrExpr(IrKinds.Fall, null, [V("AllInSync")])),
        ];
        Assert.Empty(Find([U(1, "FC9000", 12, [.. Sync("1", "AlarmDB.Err1"), .. Sync("2", "AlarmDB.Err2"), .. Sync("3", "AlarmDB.Errs[1]")])],
            peers: false, notes: notes));
        Assert.Equal(3, notes["LatchedNoAcknowledge"]);
        // PLC2 FB130 X_2：置位被常数屏蔽（… ∧ ¬1 化简为 FALSE），复位 ¬(压力超限) ∧ Reset 仍按原式认出确认条件 Reset
        var masked = IrExpr.And([new IrExpr(IrKinds.Cmp, ">=", [V("P1"), V("Lim")]), IrExpr.Not(IrExpr.Const("1"))]);
        var bits = Find([U(1, "FB_A", 1, Set("s1", "AlarmDB.Err1", masked),
            Reset("r1", "AlarmDB.Err1", IrExpr.And([IrExpr.Not(new IrExpr(IrKinds.Cmp, ">=", [V("P1"), V("Lim")])), V("Ack")])))]);
        Assert.True(Has(bits, "AlarmDB.Err1"));
    }
}
