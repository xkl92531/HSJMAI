#nullable enable
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class UnitTranslatorTests
{
    static PipelineUnit Unit(string raw, string language = "LAD", int network = 3) =>
        new(1, "Blk", 7, language, raw, "", [], network);

    [Fact]
    public void CallListsEveryPinWithConstantsAndUnwired()
    {
        var b = new FlgNetBuilder();
        var call = b.CallWithInstance("FbX", "Inst", ("In1", "Input", "Bool"), ("In2", "Input", "Bool"),
            ("N", "Input", "Int"), ("Q1", "Output", "Bool"));
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in")); b.Wire(Pin(a, "out"), Pin(call, "In1"));
        b.Wire(Id(b.Constant("2")), Pin(call, "N"));
        b.Wire(Pin(call, "Q1"), Id(b.Access("Y", "GlobalVariable")));
        var text = IrText.Unit(UnitTranslator.Translate(Unit(b.Build())));
        Assert.Contains("[Blk/3/1] 调用 FB FbX，实例 Inst，EN 未接线（每次扫描都执行）", text);
        Assert.Contains("  - 输入 In1 (Bool) ← A", text);
        Assert.Contains("  - 输入 In2 (Bool) ← 未接线（取接口默认值）", text);
        Assert.Contains("  - 输入 N (Int) ← 2", text);
        Assert.Contains("  - 输出 Q1 (Bool) → Y", text);
    }

    [Fact]
    public void JumpIsRenderedWithItsLabel()
    {
        var b = new FlgNetBuilder();
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in"));
        var jump = b.Part("Jump");
        var root = XElement.Parse(b.Build());
        root.Descendants("Part").Single(e => (string?)e.Attribute("UId") == jump.ToString()).Add(new XElement("Label", new XAttribute("Name", "L1")));
        root.Element("Wires")!.Add(new XElement("Wire", new XAttribute("UId", 999), Pin(a, "out"), Pin(jump, "in")));
        var ir = UnitTranslator.Translate(Unit(root.ToString()));
        Assert.Contains(ir.Statements, s => s.Kind == IrStatementKinds.Jump && s.Target == "L1");
        Assert.Contains("若 A 则 跳转到标签 L1", IrText.Unit(ir));
    }

    [Fact]
    public void LongExpressionFanningOutBecomesOneNode()
    {
        var b = new FlgNetBuilder();
        var c1 = b.Contact("A"); b.Wire(Rail(), Pin(c1, "in"));
        var c2 = b.Contact("B"); b.Wire(Pin(c1, "out"), Pin(c2, "in"));
        var c3 = b.Contact("C"); b.Wire(Pin(c2, "out"), Pin(c3, "in"));
        var c4 = b.Contact("D"); b.Wire(Pin(c3, "out"), Pin(c4, "in"));
        var y1 = b.Part("Coil"); b.Wire(Id(b.Access("Y1")), Pin(y1, "operand"));
        var y2 = b.Part("Coil"); b.Wire(Id(b.Access("Y2")), Pin(y2, "operand"));
        b.Wire(Pin(c4, "out"), Pin(y1, "in"), Pin(y2, "in"));
        var ir = UnitTranslator.Translate(Unit(b.Build()));
        var node = Assert.Single(ir.Nodes);
        Assert.Equal("(A AND B AND C AND D)", IrText.Expr(node.Value));
        var text = IrText.Unit(ir);
        Assert.Contains("Y1 := 〔节点 n1〕", text);
        Assert.Contains("Y2 := 〔节点 n1〕", text);
        Assert.Contains("〔节点 n1〕 = (A AND B AND C AND D)", text);
    }

    [Fact]
    public void StructuredTextIfIsTranslatedToGuardedSet()
    {
        var raw = """<StructuredText><Token Text="IF" /><Blank /><Access Scope="LocalVariable" UId="1"><Symbol><Component Name="A" /></Symbol></Access><Blank /><Token Text="THEN" /><NewLine /><Access Scope="LocalVariable" UId="2"><Symbol><Component Name="Y" /></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Token Text="TRUE" /><Token Text=";" /><NewLine /><Token Text="END_IF" /><Token Text=";" /></StructuredText>""";
        var ir = UnitTranslator.Translate(Unit(raw, "SCL"));
        var s = Assert.Single(ir.Statements);
        Assert.Equal(IrStatementKinds.Set, s.Kind);
        Assert.Equal("[Blk/3/1] 若 A 则 置位 Y（保持为 true）", IrText.Statement(s));
    }

    [Fact]
    public void SclIfUsesInterfaceBoolMembersWhenMerging()
    {
        // IF A THEN Y := B; ELSE Y := C; END_IF —— Y 在接口里是 Bool → 合并成逻辑式，而不是 SEL
        var raw = SclXml.Build("IF", "$A", "THEN", "$Y", ":=", "$B", ";", "ELSE", "$Y", ":=", "$C", ";", "END_IF", ";");
        var iface = Interface(["A", "B", "C"], ["Y"]);
        var ir = UnitTranslator.Translate(new PipelineUnit(1, "Blk", 7, "SCL", raw, "", [], 3, iface));
        Assert.Equal(["[Blk/3/1] Y := ((A AND B) OR (NOT(A) AND C))"], ir.Statements.Select(IrText.Statement).ToArray());
    }

    [Fact]
    public void GuardedExecuteStatementShowsItsCondition()
    {
        var raw = SclXml.Build("IF", "$A", "THEN", SclXml.Timer("T1", "TON", "B", "T#2s"), ";", "END_IF", ";");
        var ir = UnitTranslator.Translate(Unit(raw, "SCL"));
        Assert.Equal(["[Blk/3/1] 若 A 则 执行 TON[T1](IN=B, PT=T#2s).Q（输出未直接接线，结果可能在别处读取）"],
            ir.Statements.Select(IrText.Statement).ToArray());
    }

    [Fact]
    public void GraphActionsAreListedWithQualifier()
    {
        var raw = """<Actions><Title /><Action Qualifier="N"><Token Text="X" /><Token Text=":=" /><Token Text="1" /><Token Text="&#xA;" /></Action><Action /></Actions>""";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        var s = Assert.Single(ir.Statements);
        Assert.Equal("[Blk/3/1] 动作（限定符 N）：X:=1", IrText.Statement(s));
        Assert.Equal("Actions", ir.RootKind);
    }

    [Fact]
    public void TransitionWrapperTranslatesInnerFlgNet()
    {
        var b = new FlgNetBuilder();
        var tr = b.Part("TrCoil");
        var chain = b.Chain(Rail(), ("A", false), ("B", false));
        b.Wire(Pin(chain, "out"), Pin(tr, "in"));
        var raw = "<Transition Name=\"T1\">" + b.Build() + "</Transition>";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        Assert.Equal("Transition", ir.RootKind);
        Assert.Contains("〔转移条件〕 := (A AND B)", IrText.Unit(ir));
    }

    [Fact]
    public void PlainTextCodeIsKeptAsSource()
    {
        var ir = UnitTranslator.Translate(Unit("L #X;\nT #Y;", "STL"));
        Assert.Equal("L #X;\nT #Y;", Assert.Single(ir.Statements).Text);
    }

    [Fact]
    public void RenderedTextNeverSaysAlwaysTrue()
    {
        var b = new FlgNetBuilder();
        var coil = b.Part("Coil"); b.Wire(Id(b.Access("Y")), Pin(coil, "operand"));
        var text = IrText.Unit(UnitTranslator.Translate(Unit(b.Build())));
        Assert.DoesNotContain("始终成立", text);
        Assert.DoesNotContain("无输入连接", text);
        Assert.StartsWith("**Blk** 网络 3（LAD）", text);
    }

    // ---- fix round 1 ----

    static string Net(Action<FlgNetBuilder> build)
    {
        var b = new FlgNetBuilder();
        build(b);
        return b.Build();
    }

    static string GraphSample() =>
        "<Graph xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v5\">"
        + "<PreOperations><PermanentOperation ProgrammingLanguage=\"LAD\">"
        + Net(b => b.Series("Coil", "P", ("Q", false))) + "</PermanentOperation></PreOperations>"
        + "<Sequence><Title />"
        + "<Steps>"
        + "<Step Number=\"1\" Init=\"true\" Name=\"Init\" MaximumStepTime=\"T#10S\" WarningTime=\"T#5S\">"
        + "<Actions><Title /><Action Qualifier=\"N\"><Token Text=\"Y1\" /><Token Text=\":=\" /><Token Text=\"TRUE\" /><Token Text=\";\" /></Action>"
        + "<Action Qualifier=\"S\" Event=\"S1\" Interlock=\"true\" /><Action /></Actions>"
        + "<Supervisions><Supervision ProgrammingLanguage=\"LAD\">"
        + Net(b => { var sv = b.Part("SvCoil"); var c = b.Chain(Rail(), ("E", false)); b.Wire(Pin(c, "out"), Pin(sv, "in")); })
        + "</Supervision></Supervisions>"
        + "<Interlocks><Interlock ProgrammingLanguage=\"LAD\">"
        + Net(b => { var il = b.Part("IlCoil"); var c = b.Chain(Rail(), ("D", false)); b.Wire(Pin(c, "out"), Pin(il, "in")); })
        + "</Interlock></Interlocks>"
        + "</Step>"
        + "<Step Number=\"2\" Name=\"Run\" />"
        + "</Steps>"
        + "<Transitions>"
        + "<Transition Number=\"1\" Name=\"T1\" ProgrammingLanguage=\"LAD\">"
        + Net(b => { var tr = b.Part("TrCoil"); var c = b.Chain(Rail(), ("A", false), ("B", false)); b.Wire(Pin(c, "out"), Pin(tr, "in")); })
        + "</Transition>"
        + "<Transition Number=\"2\" Name=\"T2\" ProgrammingLanguage=\"LAD\">"
        + Net(b => { var tr = b.Part("TrCoil"); var c = b.Chain(Rail(), ("C", false)); b.Wire(Pin(c, "out"), Pin(tr, "in")); })
        + "</Transition>"
        + "</Transitions>"
        + "<Branches><Branch Number=\"1\" Type=\"AltBegin\" Cardinality=\"2\" /></Branches>"
        + "<Connections>"
        + "<Connection><NodeFrom><StepRef Number=\"1\" /></NodeFrom><NodeTo><TransitionRef Number=\"1\" /></NodeTo><LinkType>Direct</LinkType></Connection>"
        + "<Connection><NodeFrom><TransitionRef Number=\"1\" /></NodeFrom><NodeTo><BranchRef Number=\"1\" In=\"0\" /></NodeTo><LinkType>Direct</LinkType></Connection>"
        + "<Connection><NodeFrom><TransitionRef Number=\"2\" /></NodeFrom><NodeTo><EndConnection /></NodeTo><LinkType>Direct</LinkType></Connection>"
        + "</Connections>"
        + "<Mystery><Token Text=\"x\" /></Mystery>"
        + "</Sequence>"
        + "<PostOperations />"
        + "<AlarmsSettings><AlarmSupervisionCategories /></AlarmsSettings>"
        + "</Graph>";

    [Fact]
    public void GraphRootTranslatesEverySequenceElementInOrder()
    {
        var ir = UnitTranslator.Translate(Unit(GraphSample(), "GRAPH"));
        Assert.Equal("Graph", ir.RootKind);
        var lines = ir.Statements.Select(IrText.Statement).ToArray();
        string[] expected =
        [
            "[Blk/3/PreOperations1/1] P := Q",
            "[Blk/3/S1] 步 1 Init（初始步，最长步时间 T#10S，警告时间 T#5S）",
            "[Blk/3/S1A1] 动作（限定符 N）：Y1:=TRUE;",
            "[Blk/3/S1A2] 动作（限定符 S，事件 S1，受互锁 true）：（无语句）",
            "[Blk/3/S1I1/1] 〔互锁条件〕 := D",
            "[Blk/3/S1V1/1] 〔监控条件〕 := E",
            "[Blk/3/S2] 步 2 Run",
            "[Blk/3/T1] 转移 1 T1",
            "[Blk/3/T1/1] 〔转移 T1 条件〕 := (A AND B)",
            "[Blk/3/T2] 转移 2 T2",
            "[Blk/3/T2/1] 〔转移 T2 条件〕 := C",
            "[Blk/3/B1] 分支 1（AltBegin）",
            "[Blk/3/C1] 连接：步 1 → 转移 1",
            "[Blk/3/C2] 连接：转移 1 → 分支 1",
            "[Blk/3/C3] 连接：转移 2 → 序列结束",
            "[Blk/3/Mystery1] 〔未识别的顺控元素 Mystery〕",
            "[Blk/3/AlarmsSettings1] 报警设置（配置项，非程序逻辑）",
        ];
        Assert.Equal(expected, lines);
        Assert.Equal(lines.Length, ir.Statements.Select(s => s.Id).Distinct().Count());
        Assert.Equal(["Mystery"], ir.UnknownParts);
    }

    [Fact]
    public void GraphNetworksGetDistinctNodeNames()
    {
        static string FanOut(string coilKind) => Net(b =>
        {
            var c = b.Chain(Rail(), ("A", false), ("B", false), ("C", false), ("D", false));
            var y1 = b.Part(coilKind); var y2 = b.Part("Coil");
            b.Wire(Id(b.Access("Y")), Pin(y2, "operand"));
            b.Wire(Pin(c, "out"), Pin(y1, "in"), Pin(y2, "in"));
        });
        var raw = "<Graph><Sequence><Steps><Step Number=\"1\" Name=\"S\">"
            + "<Interlocks><Interlock>" + FanOut("IlCoil") + "</Interlock></Interlocks>"
            + "<Supervisions><Supervision>" + FanOut("SvCoil") + "</Supervision></Supervisions>"
            + "</Step></Steps></Sequence></Graph>";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        Assert.Equal(2, ir.Nodes.Count);
        Assert.Equal(2, ir.Nodes.Select(n => n.Name).Distinct().Count());
    }

    [Fact]
    public void StlXmlIsRenderedOneLinePerStatement()
    {
        const string access = "<Access Scope=\"GlobalVariable\" UId=\"{0}\"><Symbol><Component Name=\"{1}\" /></Symbol></Access>";
        var raw = "<StatementList xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StatementList/v4\">"
            + "<StlStatement><LabelDeclaration><Label Name=\"M1\" /></LabelDeclaration><StlToken Text=\"A\" />" + string.Format(access, 1, "X") + "</StlStatement>"
            + "<StlStatement><StlToken Text=\"=\" />" + string.Format(access, 2, "Y") + "<LineComment><Text>set y</Text></LineComment></StlStatement>"
            + "<StlStatement><StlToken><Token Text=\"JC\" /></StlToken><Label Name=\"M1\" /><Weird /></StlStatement>"
            + "</StatementList>";
        var ir = UnitTranslator.Translate(Unit(raw, "STL"));
        Assert.Equal("StatementList", ir.RootKind);
        var s = Assert.Single(ir.Statements);
        Assert.Equal(IrStatementKinds.Source, s.Kind);
        Assert.Equal("M1: A X\n= Y // set y\nJC M1 〔Weird〕", s.Text);
    }

    [Fact]
    public void SclCallIsRenderedWithInstanceAndParameters()
    {
        var raw = "<StructuredText xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3\">"
            + "<Access Scope=\"Call\" UId=\"1\"><CallInfo Name=\"FbValve\" BlockType=\"FB\">"
            + "<Instance Scope=\"GlobalVariable\" UId=\"2\"><Component Name=\"Valve\" /><Component Name=\"V1\" /></Instance>"
            + "<Token Text=\"(\" />"
            + "<Parameter Name=\"In1\" Section=\"Input\" Type=\"Bool\"><Token Text=\":=\" /><Blank /><Access Scope=\"LocalVariable\" UId=\"3\"><Symbol><Component Name=\"A\" /></Symbol></Access></Parameter>"
            + "<Token Text=\",\" /><Blank />"
            + "<Parameter Name=\"Out1\" Section=\"Output\" Type=\"Bool\"><Access Scope=\"LocalVariable\" UId=\"4\"><Symbol><Component Name=\"Y\" /></Symbol></Access></Parameter>"
            + "<Token Text=\")\" /></CallInfo></Access><Token Text=\";\" /><NewLine />"
            + "<Access Scope=\"Call\" UId=\"5\"><CallInfo Name=\"FcDo\" BlockType=\"FC\">"
            + "<Parameter Name=\"N\" Section=\"Input\" Type=\"Int\"><Access Scope=\"LiteralConstant\" UId=\"6\"><Constant><ConstantValue>5</ConstantValue></Constant></Access></Parameter>"
            + "</CallInfo></Access><Token Text=\";\" /><NewLine />"
            + "<Access Scope=\"Weird\" UId=\"7\" /><Token Text=\";\" />"
            + "</StructuredText>";
        var ir = UnitTranslator.Translate(Unit(raw, "SCL"));
        Assert.Equal(["FbValve[Valve.V1](In1 := A, Out1 => Y);", "FcDo(N := 5);", "〔未识别的访问 Weird〕;"],
            ir.Statements.Select(s => s.Text).ToArray());
        Assert.All(ir.Statements, s => Assert.Equal(IrStatementKinds.Source, s.Kind));
        Assert.Contains("未识别的访问 Weird", ir.UnknownParts);
    }

    [Fact]
    public void ActionWithAttributesButNoTokensIsKept()
    {
        var raw = """<Actions><Action Qualifier="R" Event="S0" /><Action Interlock="false"><Token Text="Z" /></Action><Action /></Actions>""";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        Assert.Equal(["[Blk/3/1] 动作（限定符 R，事件 S0）：（无语句）", "[Blk/3/2] 动作（受互锁 false）：Z"],
            ir.Statements.Select(IrText.Statement).ToArray());
    }

    const string NoSourceText = "（本程序段没有程序源码：可能是加密/受保护块或缺失，不要推断其功能）";

    [Fact]
    public void EmptySourceIsNotDescribedAsWritingNothing()
    {
        var text = IrText.Unit(UnitTranslator.Translate(Unit("")));
        Assert.Contains(NoSourceText, text);
        Assert.DoesNotContain("（本网络没有写入任何变量）", text);

        var missing = new PipelineUnit(1, "Blk", 7, "LAD", "", "", [], 0, UnitKind: "MissingBody");
        var missingText = IrText.Unit(UnitTranslator.Translate(missing));
        Assert.Contains(NoSourceText, missingText);
        Assert.DoesNotContain("（本网络没有写入任何变量）", missingText);

        var emptyNet = IrText.Unit(UnitTranslator.Translate(Unit("<FlgNet><Parts /><Wires /></FlgNet>")));
        Assert.Contains("（本网络没有写入任何变量）", emptyNet);
        Assert.DoesNotContain(NoSourceText, emptyNet);
    }

    // ---- fix round 2 ----

    const string SclNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3";
    const string StlNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StatementList/v4";
    static string Var(string path, int uid = 90, string scope = "LocalVariable") =>
        $"<Access Scope=\"{scope}\" UId=\"{uid}\"><Symbol>{string.Concat(path.Split('.').Select(c => $"<Component Name=\"{c}\" />"))}</Symbol></Access>";
    static string Lit(string value) =>
        $"<Access Scope=\"LiteralConstant\"><Constant><ConstantValue>{value}</ConstantValue></Constant></Access>";
    static string Instruction(string name, string content) =>
        $"<Access Scope=\"Call\"><Instruction Name=\"{name}\"><Token Text=\"(\" />{content}<Token Text=\")\" /></Instruction></Access>";
    static string Nameless(string content) => $"<NamelessParameter>{content}</NamelessParameter>";

    [Fact]
    public void SclBuiltInInstructionCallsAreRenderedRecursively()
    {
        var raw = $"<StructuredText xmlns=\"{SclNs}\">"
            + Var("X") + "<Blank /><Token Text=\":=\" /><Blank />"
            + Instruction("DINT_TO_REAL", Nameless(Instruction("DWORD_TO_DINT", Nameless(Instruction("SWAP", Nameless(Var("RobToPLC.Robt_X", 91, "GlobalVariable")))))))
            + "<Token Text=\";\" /><NewLine />"
            + Var("Y") + "<Blank /><Token Text=\":=\" /><Blank />"
            + Instruction("LIMIT", "<Parameter Name=\"MN\">" + Lit("0") + "</Parameter><Token Text=\",\" /><Blank />"
                + "<Parameter Name=\"IN\"><Token Text=\":=\" />" + Var("X") + "</Parameter>")
            + "<Token Text=\";\" />"
            + "</StructuredText>";
        var ir = UnitTranslator.Translate(Unit(raw, "SCL"));
        Assert.Equal(["[Blk/3/1] X := DINT_TO_REAL(DWORD_TO_DINT(SWAP(RobToPLC.Robt_X)))", "[Blk/3/2] Y := LIMIT(MN=0, IN=X)"],
            ir.Statements.Select(IrText.Statement).ToArray());
        Assert.Empty(ir.UnknownParts);
    }

    [Fact]
    public void SclCallKeepsTokensAndCommentsBetweenParameters()
    {
        var raw = $"<StructuredText xmlns=\"{SclNs}\"><Access Scope=\"Call\"><CallInfo Name=\"FcDo\" BlockType=\"FC\">"
            + "<Token Text=\"(\" /><Parameter Name=\"A\" Section=\"Input\">" + Lit("1") + "</Parameter><Token Text=\",\" /><Blank />"
            + "<Comment><Text>second</Text></Comment><Blank />"
            + "<Parameter Name=\"B\" Section=\"Input\">" + Lit("2") + "</Parameter><Token Text=\")\" />"
            + "</CallInfo></Access><Token Text=\";\" /><Oddity Kind=\"x\" /></StructuredText>";
        var ir = UnitTranslator.Translate(Unit(raw, "SCL"));
        Assert.Equal(["FcDo(A := 1, (* second *) B := 2);", "〔Oddity Kind=x〕"], ir.Statements.Select(s => s.Text).ToArray());
        Assert.Contains("未识别的 SCL 元素 Oddity", ir.UnknownParts);
    }

    [Fact]
    public void IndirectAndDbAddressesAreRenderedInPlcNotation()
    {
        var raw = $"<StatementList xmlns=\"{StlNs}\">"
            + "<StlStatement><StlToken Text=\"L\" /><Access Scope=\"Address\"><Indirect Width=\"Word\" Area=\"DI\" Register=\"AR1\" BitOffset=\"112\" /></Access></StlStatement>"
            + "<StlStatement><StlToken Text=\"A\" /><Access Scope=\"Address\"><Indirect Width=\"Bool\" Area=\"DB\" Register=\"AR2\" BitOffset=\"9\" /></Access></StlStatement>"
            + "<StlStatement><StlToken Text=\"T\" /><Access Scope=\"Address\"><Address Area=\"DB\" Type=\"Word\" BlockNumber=\"5\" BitOffset=\"16\" /></Access></StlStatement>"
            + "<StlStatement><StlToken Text=\"A\" /><Access Scope=\"Address\"><Address Area=\"DB\" Type=\"Bool\" BlockNumber=\"5\" BitOffset=\"19\" /></Access></StlStatement>"
            + "<StlStatement><StlToken Text=\"L\" /><Access Scope=\"Address\"><Strange Foo=\"1\" /></Access></StlStatement>"
            + "</StatementList>";
        var ir = UnitTranslator.Translate(Unit(raw, "STL"));
        Assert.Equal("L DIW[AR1,P#14.0]\nA DBX[AR2,P#1.1]\nT %DB5.DBW2\nA %DB5.DBX2.3\nL 〔未识别的地址 Strange(Foo=1)〕", Assert.Single(ir.Statements).Text);
        Assert.Equal(["未识别的地址 Strange(Foo=1)"], ir.UnknownParts);

        var flg = "<FlgNet><Parts><Access Scope=\"Address\" UId=\"1\"><Strange Foo=\"2\" /></Access><Part Name=\"Coil\" UId=\"2\" /></Parts>"
            + "<Wires><Wire UId=\"3\"><IdentCon UId=\"1\" /><NameCon UId=\"2\" Name=\"operand\" /></Wire></Wires></FlgNet>";
        Assert.Contains("未识别的地址 Strange(Foo=2)", UnitTranslator.Translate(Unit(flg)).UnknownParts);
    }

    [Fact]
    public void LabelAndPredefinedAccessesAreReadable()
    {
        var stl = $"<StatementList xmlns=\"{StlNs}\"><StlStatement><StlToken Text=\"JU\" />"
            + "<Access Scope=\"Label\"><Label Name=\"M0\" /><LineComment Inserted=\"false\"><Text>循环;</Text></LineComment></Access>"
            + "</StlStatement></StatementList>";
        Assert.Equal("JU M0 // 循环;", Assert.Single(UnitTranslator.Translate(Unit(stl, "STL")).Statements).Text);

        var scl = $"<StructuredText xmlns=\"{SclNs}\">"
            + "<Access Scope=\"PredefinedVariable\"><Predefined Name=\"ENO\" /></Access><Blank /><Token Text=\":=\" /><Blank /><Token Text=\"FALSE\" /><Token Text=\";\" /><NewLine />"
            + "<Access Scope=\"PredefinedVariable\"><Symbol><Component Name=\"Sys\" /><Component Name=\"Flag\" /></Symbol></Access><Token Text=\";\" />"
            + "</StructuredText>";
        var ir = UnitTranslator.Translate(Unit(scl, "SCL"));
        Assert.Equal(["[Blk/3/1] #ENO := FALSE", "[Blk/3/2] 源码：\n```\nSys.Flag;\n```"], ir.Statements.Select(IrText.Statement).ToArray());
        Assert.Empty(ir.UnknownParts);
    }

    const string InstructionNote = "注：以下为博图指令，翻译器按原名与引脚列出、未展开其语义，可按博图指令手册理解：";

    [Fact]
    public void GenericInstructionBoxesAreListedAsInstructionsNotUnrecognised()
    {
        var b = new FlgNetBuilder();
        var power = b.InstancePart("MC_POWER", "Axis1Power");
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in")); b.Wire(Pin(a, "out"), Pin(power, "Enable"));
        b.Wire(Id(b.Access("Ax")), Pin(power, "Axis"));
        b.Wire(Pin(power, "Status"), Id(b.Access("Y")));
        var conv = b.Part("T_CONV");
        b.Wire(Id(b.Access("N")), Pin(conv, "IN"));
        var root = XElement.Parse(b.Build());
        root.Descendants("Part").Single(p => (string?)p.Attribute("UId") == conv.ToString())
            .Add(new XElement("TemplateValue", new XAttribute("Name", "value_type"), "DInt"));
        var ir = UnitTranslator.Translate(Unit(root.ToString()));
        var text = IrText.Unit(ir);
        Assert.Contains("Y := 〔指令 MC_POWER[Axis1Power](Axis=Ax, Enable=A)〕.Status", text);
        Assert.Contains("执行 〔指令 T_CONV<value_type=DInt>(IN=N)〕.out", text);
        Assert.Contains(InstructionNote + "MC_POWER、T_CONV", text);
        Assert.DoesNotContain("未识别", text);
        Assert.DoesNotContain("其含义不要推断", text);
        Assert.Equal(["MC_POWER", "T_CONV"], ir.UnknownParts);
        Assert.Equal(["MC_POWER", "T_CONV"], ir.Instructions);
    }

    /// <summary>构造一个带公式的 Calc 部件（与博图导出一致：Equation 用大写 IN1，端口为小写 in1）。</summary>
    [Fact]
    public void CalcSubstitutesInputsIntoItsEquation()
    {
        var raw = "<FlgNet><Parts>"
            + "<Access Scope=\"LocalVariable\" UId=\"1\"><Symbol><Component Name=\"A\" /></Symbol></Access>"
            + "<Access Scope=\"LocalVariable\" UId=\"2\"><Symbol><Component Name=\"C\" /></Symbol></Access>"
            + "<Access Scope=\"LocalVariable\" UId=\"3\"><Symbol><Component Name=\"D\" /></Symbol></Access>"
            + "<Access Scope=\"LiteralConstant\" UId=\"4\"><Constant><ConstantValue>10</ConstantValue></Constant></Access>"
            + "<Access Scope=\"LocalVariable\" UId=\"5\"><Symbol><Component Name=\"R\" /></Symbol></Access>"
            + "<Part Name=\"Calc\" UId=\"6\" DisabledENO=\"true\"><Equation>IN3-(IN1*IN2)</Equation>"
            + "<TemplateValue Name=\"Card\" Type=\"Cardinality\">3</TemplateValue><TemplateValue Name=\"SrcType\" Type=\"Type\">Real</TemplateValue></Part>"
            + "<Part Name=\"Add\" UId=\"7\"><TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue></Part>"
            + "</Parts><Wires>"
            + "<Wire UId=\"10\"><Powerrail /><NameCon UId=\"6\" Name=\"en\" /></Wire>"
            + "<Wire UId=\"11\"><IdentCon UId=\"1\" /><NameCon UId=\"6\" Name=\"in1\" /></Wire>"
            + "<Wire UId=\"12\"><NameCon UId=\"7\" Name=\"out\" /><NameCon UId=\"6\" Name=\"in2\" /></Wire>"
            + "<Wire UId=\"13\"><IdentCon UId=\"4\" /><NameCon UId=\"6\" Name=\"in3\" /></Wire>"
            + "<Wire UId=\"14\"><NameCon UId=\"6\" Name=\"out\" /><IdentCon UId=\"5\" /></Wire>"
            + "<Wire UId=\"15\"><IdentCon UId=\"2\" /><NameCon UId=\"7\" Name=\"in1\" /></Wire>"
            + "<Wire UId=\"16\"><IdentCon UId=\"3\" /><NameCon UId=\"7\" Name=\"in2\" /></Wire>"
            + "</Wires></FlgNet>";
        var ir = UnitTranslator.Translate(Unit(raw));
        Assert.Equal(["[Blk/3/1] R := Calc(10-(A*(C + D)))"], ir.Statements.Select(IrText.Statement).ToArray());
        Assert.Empty(ir.UnknownParts);
    }

    [Fact]
    public void SelMaxMinLimitAndSMoveHaveReadableSemantics()
    {
        var b = new FlgNetBuilder();
        var sel = b.Part("SEL");
        var g = b.Contact("G1"); b.Wire(Rail(), Pin(g, "in")); b.Wire(Pin(g, "out"), Pin(sel, "G"));
        b.Wire(Id(b.Constant("1")), Pin(sel, "IN0")); b.Wire(Id(b.Constant("2")), Pin(sel, "IN1"));
        b.Wire(Pin(sel, "out"), Id(b.Access("S")));
        var max = b.Part("MAX", 3);
        b.Wire(Id(b.Access("X")), Pin(max, "in1")); b.Wire(Id(b.Access("X2")), Pin(max, "in2")); b.Wire(Id(b.Constant("3")), Pin(max, "in3"));
        b.Wire(Pin(max, "out"), Id(b.Access("M")));
        var min = b.Part("MIN", 2);
        b.Wire(Id(b.Access("X")), Pin(min, "in1")); b.Wire(Id(b.Access("X2")), Pin(min, "in2"));
        b.Wire(Pin(min, "out"), Id(b.Access("Mn")));
        var limit = b.Part("LIMIT");
        b.Wire(Id(b.Constant("0")), Pin(limit, "MN")); b.Wire(Id(b.Access("X")), Pin(limit, "IN")); b.Wire(Id(b.Constant("10")), Pin(limit, "MX"));
        b.Wire(Pin(limit, "out"), Id(b.Access("L")));
        var move = b.Part("S_Move");
        b.Wire(Id(b.Access("Src")), Pin(move, "IN")); b.Wire(Pin(move, "OUT1"), Id(b.Access("Dst")));
        var ir = UnitTranslator.Translate(Unit(b.Build()));
        var lines = ir.Statements.Select(IrText.Statement).ToArray();
        Assert.Contains("[Blk/3/1] S := SEL(G=G1, 真=2, 假=1)", lines);
        Assert.Contains("[Blk/3/2] M := MAX(X, X2, 3)", lines);
        Assert.Contains("[Blk/3/3] Mn := MIN(X, X2)", lines);
        Assert.Contains("[Blk/3/4] L := LIMIT(MN=0, IN=X, MX=10)", lines);
        Assert.Contains("[Blk/3/5] Dst := Src", lines);
        Assert.Equal(5, lines.Length);
        Assert.Empty(ir.UnknownParts);
    }

    [Fact]
    public void IncAndDecBecomeAssignments()
    {
        var b = new FlgNetBuilder();
        var inc = b.Part("Inc");
        var a = b.Contact("A"); b.Wire(Rail(), Pin(a, "in")); b.Wire(Pin(a, "out"), Pin(inc, "en"));
        b.Wire(Id(b.Access("Cnt")), Pin(inc, "operand"));
        var dec = b.Part("Dec");
        b.Wire(Id(b.Access("Cnt2")), Pin(dec, "operand"));
        var ir = UnitTranslator.Translate(Unit(b.Build()));
        Assert.Equal(["[Blk/3/1] 若 A 则 Cnt := (Cnt + 1)", "[Blk/3/2] Cnt2 := (Cnt2 - 1)"],
            ir.Statements.Select(IrText.Statement).ToArray());
        Assert.Empty(ir.UnknownParts);
    }

    [Fact]
    public void GraphHousekeepingAndInnerContainersNeverDropContent()
    {
        static string FanOutInterlock() => Net(b =>
        {
            var c = b.Chain(Rail(), ("A", false), ("B", false), ("C", false), ("D", false));
            var il = b.Part("IlCoil"); var y = b.Part("Coil");
            b.Wire(Id(b.Access("Y")), Pin(y, "operand"));
            b.Wire(Pin(c, "out"), Pin(il, "in"), Pin(y, "in"));
        });
        static string Sequence(string extra) =>
            "<Sequence><Steps><Step Number=\"1\" Name=\"S\">"
            + "<Actions><Title /><Action Qualifier=\"N\"><Token Text=\"Z\" /></Action><ActionOdd><Q /></ActionOdd></Actions>"
            + "<Interlocks><Interlock>" + FanOutInterlock() + "</Interlock><InterlockOdd><Q /></InterlockOdd></Interlocks>"
            + "<StepExtra Foo=\"1\"><X /></StepExtra>"
            + "</Step>" + extra + "</Steps>"
            + "<Transitions><TransitionOdd><Q /></TransitionOdd></Transitions>"
            + "</Sequence>";
        var raw = "<Graph><PreOperations /><PostOperations />" + Sequence("<StepsOdd><Q /></StepsOdd>") + Sequence("") + "</Graph>";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        var lines = ir.Statements.Select(IrText.Statement).ToArray();
        Assert.Contains("[Blk/3/Q1/S1A2] 〔未识别的顺控元素 ActionOdd〕", lines);
        Assert.Contains("[Blk/3/Q1/S1/InterlockOdd1] 〔未识别的顺控元素 InterlockOdd〕", lines);
        Assert.Contains("[Blk/3/Q1/S1/StepExtra1] 〔未识别的顺控元素 StepExtra〕", lines);
        Assert.Contains("[Blk/3/Q1/StepsOdd1] 〔未识别的顺控元素 StepsOdd〕", lines);
        Assert.Contains("[Blk/3/Q1/TransitionOdd1] 〔未识别的顺控元素 TransitionOdd〕", lines);
        Assert.DoesNotContain(lines, l => l.Contains("PreOperations") || l.Contains("PostOperations"));
        Assert.Equal(lines.Length, ir.Statements.Select(s => s.Id).Distinct().Count());
        Assert.Equal(2, ir.Nodes.Count);
        Assert.Equal(2, ir.Nodes.Select(n => n.Name).Distinct().Count());
        Assert.Equal(["ActionOdd", "InterlockOdd", "StepExtra", "StepsOdd", "TransitionOdd"], ir.UnknownParts);
    }

    [Fact]
    public void StatementListChildrenOtherThanStatementsAreKept()
    {
        var raw = $"<StatementList xmlns=\"{StlNs}\"><StlStatement><StlToken Text=\"NOP\" /></StlStatement>"
            + "<LineComment><Text>hdr</Text></LineComment><Foo><Bar /></Foo><Empty /></StatementList>";
        var ir = UnitTranslator.Translate(Unit(raw, "STL"));
        Assert.Equal("NOP\n// hdr\n〔Foo〕", Assert.Single(ir.Statements).Text);
        Assert.Equal(["未识别的 STL 元素 Foo"], ir.UnknownParts);
    }

    // ---- fix round 3 ----

    const string GraphNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v5";
    const string OneStepSequence = "<Sequence><Steps><Step Number=\"1\" Name=\"S\" /></Steps></Sequence>";

    [Fact]
    public void EmptyPermanentOperationsAreSkippedSilently()
    {
        var raw = $"<Graph xmlns=\"{GraphNs}\">"
            + "<PreOperations><PermanentOperation ProgrammingLanguage=\"LAD\" /></PreOperations>"
            + OneStepSequence
            + "<PostOperations><PermanentOperation ProgrammingLanguage=\"LAD\" /></PostOperations>"
            + "</Graph>";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        Assert.Equal(["[Blk/3/S1] 步 1 S"], ir.Statements.Select(IrText.Statement).ToArray());
        Assert.Empty(ir.UnknownParts);
        Assert.DoesNotContain("未识别", IrText.Unit(ir));
    }

    [Fact]
    public void PermanentOperationsWithLogicAreTranslated()
    {
        var st = "<StructuredText><Access Scope=\"GlobalVariable\"><Symbol><Component Name=\"Z\" /></Symbol></Access>"
            + "<Blank /><Token Text=\":=\" /><Blank /><Token Text=\"0\" /><Token Text=\";\" /></StructuredText>";
        var raw = $"<Graph xmlns=\"{GraphNs}\">"
            + "<PreOperations><PermanentOperation ProgrammingLanguage=\"SCL\">" + st + "</PermanentOperation></PreOperations>"
            + OneStepSequence
            + "<PostOperations><PermanentOperation ProgrammingLanguage=\"LAD\">" + Net(b => b.Series("Coil", "P", ("Q", false)))
            + "</PermanentOperation></PostOperations>"
            + "</Graph>";
        var ir = UnitTranslator.Translate(Unit(raw, "GRAPH"));
        var statements = ir.Statements;
        Assert.Equal(3, statements.Count);
        Assert.Equal("[Blk/3/PreOperations1] 源码：\n```\nZ := 0;\n```", IrText.Statement(statements[0]));
        Assert.Equal("[Blk/3/S1] 步 1 S", IrText.Statement(statements[1]));
        Assert.Equal("[Blk/3/PostOperations1/1] P := Q", IrText.Statement(statements[2]));
        Assert.Empty(ir.UnknownParts);
    }

    [Fact]
    public void AccessWithDataTypeRendersTheTypeName()
    {
        var raw = $"<StructuredText xmlns=\"{SclNs}\"><Token Text=\"CASE\" /><Blank />"
            + Instruction("TypeOf", Nameless(Var("Axis")))
            + "<Blank /><Token Text=\"OF\" /><NewLine />"
            + "<Access Scope=\"SystemType\"><DataType>TO_PositioningAxis</DataType></Access><Token Text=\":\" /><Blank />"
            + "<Access Scope=\"TypeName\"><DataType> Int </DataType></Access>"
            + "</StructuredText>";
        var ir = UnitTranslator.Translate(Unit(raw, "SCL"));
        Assert.Equal("CASE TypeOf(Axis) OF\nTO_PositioningAxis: Int", Assert.Single(ir.Statements).Text);
        Assert.Empty(ir.UnknownParts);
    }
}
