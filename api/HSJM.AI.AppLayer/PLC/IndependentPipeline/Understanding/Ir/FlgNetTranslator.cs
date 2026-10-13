#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>把一个 FlgNet（LAD/FBD 网络）逐部件翻译成 IR。只依据部件类型、端口与连线，不看任何变量名。
/// 没有内置语义的部件都是博图指令：按原名、模板值、实例与已接线输入列出（Instr 表达式），名字记入 Instructions，绝不当成常真；
/// 认不出的访问（Unknown）记入 UnknownParts；未接线端口为 Unwired。</summary>
public sealed record FlgNetResult(IReadOnlyList<IrStatement> Statements, IReadOnlyList<IrNode> Nodes,
    IReadOnlyList<string> UnknownParts, IReadOnlyList<string> Instructions);

public sealed class FlgNetTranslator
{
    static readonly Dictionary<string, string> Comparators = new(StringComparer.Ordinal)
    { ["Eq"] = "==", ["Ne"] = "<>", ["Gt"] = ">", ["Ge"] = ">=", ["Lt"] = "<", ["Le"] = "<=" };
    static readonly Dictionary<string, string> Arithmetic = new(StringComparer.Ordinal)
    { ["Add"] = "+", ["Sub"] = "-", ["Mul"] = "*", ["Div"] = "/", ["Mod"] = "MOD" };
    static readonly HashSet<string> UnaryFunctions = new(StringComparer.Ordinal)
    { "Convert", "Round", "Trunc", "Ceil", "Floor", "Abs", "Neg", "Sqrt", "Sqr", "Exp", "Ln", "Sin", "Cos", "Tan" };
    /// <summary>自身生成语句的部件（线圈、置位复位、跳转、返回），不再生成「执行」语句。</summary>
    static readonly HashSet<string> StatementParts = new(StringComparer.Ordinal)
    { "Coil", "SCoil", "RCoil", "PCoil", "NCoil", "TrCoil", "IlCoil", "SvCoil", "Sr", "Rs", "Jump", "JumpNot", "Return", "Inc", "Dec" };
    static readonly HashSet<string> QOutputParts = new(StringComparer.Ordinal)
    { "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD" };

    readonly Dictionary<string, XElement> parts = new(StringComparer.Ordinal);
    readonly Dictionary<string, IrExpr> accesses = new(StringComparer.Ordinal);
    readonly Dictionary<(string Uid, string Port), XElement> sinkWire = new();
    readonly List<XElement> wires = [];
    readonly Dictionary<(string Uid, string Port), IrExpr> memo = new();
    readonly HashSet<(string Uid, string Port)> busy = [];
    readonly Dictionary<(string Uid, string Port), string> nodeNames = new();
    readonly List<IrNode> nodes = [];
    readonly SortedSet<string> unknownParts = new(StringComparer.Ordinal);
    readonly SortedSet<string> instructions = new(StringComparer.Ordinal);
    /// <summary>已记过顺带写入的部件；pendingWrites 是尚未挂到语句上的顺带写入（由下一条生成的语句带走）。</summary>
    readonly HashSet<string> sideWritten = new(StringComparer.Ordinal);
    readonly List<string> pendingWrites = [];
    readonly string idPrefix;
    readonly string transitionTarget;
    readonly string nodePrefix;
    int statementIndex;

    FlgNetTranslator(XElement flgNet, string idPrefix, string transitionTarget, string nodePrefix)
    {
        this.idPrefix = idPrefix;
        this.transitionTarget = transitionTarget;
        this.nodePrefix = nodePrefix;
        Load(flgNet);
    }

    /// <summary>transitionTarget：TrCoil 写入目标的显示名（顺控块里按转移编号区分）；
    /// nodePrefix：公共节点名前缀，同一程序段含多个 FlgNet 时用来避免节点重名。</summary>
    /// <remarks>返回的 UnknownParts 同时包含指令名与认不出的访问（供后续代码分析）；要区分请用 <see cref="TranslateFull"/>。</remarks>
    public static (IReadOnlyList<IrStatement> Statements, IReadOnlyList<IrNode> Nodes, IReadOnlyList<string> UnknownParts)
        Translate(XElement flgNet, string idPrefix, string transitionTarget = "〔转移条件〕", string nodePrefix = "")
    {
        var result = TranslateFull(flgNet, idPrefix, transitionTarget, nodePrefix);
        return (result.Statements, result.Nodes, result.UnknownParts);
    }

    public static FlgNetResult TranslateFull(XElement flgNet, string idPrefix, string transitionTarget = "〔转移条件〕", string nodePrefix = "")
    {
        var translator = new FlgNetTranslator(flgNet, idPrefix, transitionTarget, nodePrefix);
        var statements = translator.Statements();
        var all = new SortedSet<string>(translator.unknownParts, StringComparer.Ordinal);
        all.UnionWith(translator.instructions);
        return new FlgNetResult(statements, translator.nodes, all.ToArray(), translator.instructions.ToArray());
    }

    static bool Is(XElement e, string name) => e.Name.LocalName == name;
    static string A(XElement e, string name) => (string?)e.Attribute(name) ?? "";
    static IrExpr Port(string name, IrExpr value) => new(IrKinds.Port, name, [value]);
    static int Size(IrExpr e) => 1 + (e.Args?.Sum(Size) ?? 0);
    static string TextOf(IrExpr e) => e.Kind is IrKinds.Var or IrKinds.Const ? e.Name! : IrText.Expr(e);

    void Load(XElement flg)
    {
        var partsElement = flg.Elements().FirstOrDefault(e => Is(e, "Parts"));
        if (partsElement != null)
            foreach (var e in partsElement.Elements())
            {
                var uid = A(e, "UId");
                if (uid.Length == 0) continue;
                if (Is(e, "Access"))
                {
                    var access = SymbolReader.Access(e);
                    if (access.Kind == IrKinds.Unknown) unknownParts.Add(access.Name ?? "未识别的访问");
                    accesses[uid] = access;
                }
                else if (Is(e, "Part") || Is(e, "Call")) parts[uid] = e;
            }
        var wiresElement = flg.Elements().FirstOrDefault(e => Is(e, "Wires"));
        if (wiresElement == null) return;
        foreach (var wire in wiresElement.Elements().Where(e => Is(e, "Wire")))
        {
            wires.Add(wire);
            foreach (var sink in wire.Elements().Skip(1).Where(c => Is(c, "NameCon")))
                sinkWire[(A(sink, "UId"), A(sink, "Name"))] = wire;
        }
    }

    static bool Negated(XElement part, string port) =>
        part.Elements().Any(e => Is(e, "Negated") && A(e, "Name") == port);

    bool HasPort(string uid, string port) => sinkWire.ContainsKey((uid, port));

    /// <summary>读输入端口的驱动值；applyNegation=false 时忽略该端口的取反标记（线圈的写入目标用）。</summary>
    IrExpr? In(string uid, string port, bool applyNegation = true)
    {
        if (!sinkWire.TryGetValue((uid, port), out var wire)) return null;
        var driver = wire.Elements().First();
        IrExpr value;
        if (Is(driver, "Powerrail")) value = IrExpr.True;
        else if (Is(driver, "IdentCon"))
            value = accesses.TryGetValue(A(driver, "UId"), out var access) ? access : new IrExpr(IrKinds.Unknown, "未知访问");
        else if (Is(driver, "NameCon")) value = Shared(wire, A(driver, "UId"), A(driver, "Name"));
        else value = IrExpr.Unwired();
        if (applyNegation && parts.TryGetValue(uid, out var part) && Negated(part, port)) value = IrExpr.Not(value);
        return value;
    }

    IrExpr InOrUnwired(string uid, string port) => In(uid, port) ?? IrExpr.Unwired();

    /// <summary>一个输出端口驱动多个输入、且表达式较长时，提取为节点，避免文字按扇出成倍膨胀。</summary>
    IrExpr Shared(XElement wire, string uid, string port)
    {
        var value = PartOut(uid, port);
        var fanOut = wire.Elements().Skip(1).Count(c => Is(c, "NameCon"));
        if (fanOut < 2 || Size(value) <= 4) return value;
        if (!nodeNames.TryGetValue((uid, port), out var name))
        {
            name = nodePrefix + "n" + (nodes.Count + 1);
            nodeNames[(uid, port)] = name;
            nodes.Add(new IrNode(name, value));
        }
        return new IrExpr(IrKinds.Node, name);
    }

    IrExpr PartOut(string uid, string port)
    {
        if (memo.TryGetValue((uid, port), out var cached)) return cached;
        if (!parts.TryGetValue(uid, out var part)) return new IrExpr(IrKinds.Unknown, "未知部件");
        if (!busy.Add((uid, port))) return new IrExpr(IrKinds.Unknown, "环路");
        if (!Is(part, "Call")) NoteSideWrites(uid, part);
        var result = Is(part, "Call") ? CallOut(uid, part, port) : PartValue(uid, part, port);
        if (Negated(part, port)) result = IrExpr.Not(result); // 输出端口的取反标记（如 A 盒 <Negated Name="out"/>）
        busy.Remove((uid, port));
        memo[(uid, port)] = result;
        return result;
    }

    IEnumerable<IrExpr> NumberedInputs(string uid, string prefix) =>
        sinkWire.Keys.Where(k => k.Uid == uid && Regex.IsMatch(k.Port, "^" + prefix + @"\d+$", RegexOptions.IgnoreCase))
            .OrderBy(k => int.Parse(k.Port[prefix.Length..]))
            .Select(k => InOrUnwired(uid, k.Port));

    /// <summary>按端口名（不区分大小写）读输入；博图不同指令的端口大小写不一（如 Calc 的 in1 与公式里的 IN1）。</summary>
    IrExpr InAnyCase(string uid, string port)
    {
        if (HasPort(uid, port)) return InOrUnwired(uid, port);
        var actual = sinkWire.Keys.FirstOrDefault(k => k.Uid == uid && string.Equals(k.Port, port, StringComparison.OrdinalIgnoreCase));
        return actual.Port == null ? IrExpr.Unwired() : InOrUnwired(uid, actual.Port);
    }

    /// <summary>通用指令：原名 + 模板值 + 实例 + 已接线的输入端口。</summary>
    IrExpr Instruction(string uid, XElement part, string port)
    {
        var name = A(part, "Name");
        instructions.Add(name);
        var template = string.Join(",", part.Elements().Where(e => Is(e, "TemplateValue")).Select(e => A(e, "Name") + "=" + e.Value.Trim()));
        var instance = InstanceName(part);
        var head = (template.Length == 0 ? name : $"{name}<{template}>") + (instance.Length == 0 ? "" : $"[{instance}]");
        return new IrExpr(IrKinds.Instr, head + "|" + port, InputPorts(uid));
    }

    /// <summary>Calc：公式里的 INn 按端口 inn 的输入代入（渲染时替换）。</summary>
    IrExpr Calc(string uid, XElement part)
    {
        var equation = part.Elements().FirstOrDefault(e => Is(e, "Equation"))?.Value.Trim() ?? "";
        if (equation.Length == 0) return Instruction(uid, part, "out");
        var numbers = Regex.Matches(equation, @"\bIN(\d+)\b", RegexOptions.IgnoreCase)
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n);
        return new IrExpr(IrKinds.Calc, equation, numbers.Select(n => Port("IN" + n, InAnyCase(uid, "in" + n))).ToArray());
    }

    List<IrExpr> InputPorts(string uid) =>
        sinkWire.Keys.Where(k => k.Uid == uid).OrderBy(k => k.Port, StringComparer.Ordinal)
            .Select(k => Port(k.Port, InOrUnwired(uid, k.Port))).ToList();

    /// <summary>导出里 Instance 的 Scope 属性（GlobalVariable = 全局背景 DB，LocalVariable = 多重背景）；没有返回 null。</summary>
    static string? InstanceScope(XElement owner) =>
        owner.Elements().FirstOrDefault(e => Is(e, "Instance")) is { } instance && A(instance, "Scope") is { Length: > 0 } scope ? scope : null;

    static string InstanceName(XElement owner)
    {
        var instance = owner.Elements().FirstOrDefault(e => Is(e, "Instance"));
        if (instance == null) return "";
        var symbol = instance.Elements().FirstOrDefault(e => Is(e, "Symbol"));
        return symbol != null ? SymbolReader.Symbol(symbol) : SymbolReader.Components(instance);
    }

    IrExpr PartValue(string uid, XElement part, string port)
    {
        var name = A(part, "Name");
        switch (name)
        {
            case "Contact": return IrExpr.And([InOrUnwired(uid, "in"), InOrUnwired(uid, "operand")]);
            case "PContact": return IrExpr.And([In(uid, "in"), In(uid, "pre"), new IrExpr(IrKinds.Rise, null, [InOrUnwired(uid, "operand")])]);
            case "NContact": return IrExpr.And([In(uid, "in"), In(uid, "pre"), new IrExpr(IrKinds.Fall, null, [InOrUnwired(uid, "operand")])]);
            case "Coil": case "SCoil": case "RCoil": case "PCoil": case "NCoil": case "TrCoil": case "IlCoil": case "SvCoil":
                return InOrUnwired(uid, "in");
            case "A": return IrExpr.And(NumberedInputs(uid, "in"));
            case "O": return IrExpr.Or(NumberedInputs(uid, "in"));
            case "X": return new IrExpr(IrKinds.Xor, null, NumberedInputs(uid, "in").ToArray());
            case "Not": return IrExpr.Not(InOrUnwired(uid, "in"));
            case "PBox": case "P_TRIG": return new IrExpr(IrKinds.Rise, null, [In(uid, "in") ?? InOrUnwired(uid, "CLK")]);
            case "NBox": case "N_TRIG": return new IrExpr(IrKinds.Fall, null, [In(uid, "in") ?? InOrUnwired(uid, "CLK")]);
            case "Sr": case "Rs": return InOrUnwired(uid, "operand");
            case "TON": case "TOF": case "TP": case "TONR":
                return new IrExpr(IrKinds.Timer, $"{name}|{InstanceName(part)}|{port}",
                    [Port("IN", InOrUnwired(uid, "IN")), Port("PT", InOrUnwired(uid, "PT"))]);
            case "CTU": case "CTD": case "CTUD":
                return new IrExpr(IrKinds.Counter, $"{name}|{InstanceName(part)}|{port}", InputPorts(uid));
        }
        if (Comparators.TryGetValue(name, out var op))
            return IrExpr.And([In(uid, "pre"), new IrExpr(IrKinds.Cmp, op, [InOrUnwired(uid, "in1"), InOrUnwired(uid, "in2")])]);
        if (name is "InRange" or "OutRange")
            return IrExpr.And([In(uid, "pre"),
                new IrExpr(IrKinds.Func, name, [InOrUnwired(uid, "MIN"), InOrUnwired(uid, "VAL"), InOrUnwired(uid, "MAX")])]);
        if (port == "eno") return Eno(uid, name);
        if (Arithmetic.TryGetValue(name, out var arithmetic))
            return new IrExpr(IrKinds.Arith, arithmetic, NumberedInputs(uid, "in").ToArray());
        if (name is "Move" or "S_Move") return InAnyCase(uid, "in");
        if (UnaryFunctions.Contains(name)) return new IrExpr(IrKinds.Func, name, [InOrUnwired(uid, "in")]);
        switch (name)
        {
            case "Calc": return Calc(uid, part);
            case "SEL":
                return new IrExpr(IrKinds.Func, "SEL",
                    [Port("G", InAnyCase(uid, "G")), Port("真", InAnyCase(uid, "IN1")), Port("假", InAnyCase(uid, "IN0"))]);
            case "MAX": case "MIN": return new IrExpr(IrKinds.Func, name, NumberedInputs(uid, "in").ToArray());
            case "LIMIT":
                return new IrExpr(IrKinds.Func, "LIMIT",
                    [Port("MN", InAnyCase(uid, "MN")), Port("IN", InAnyCase(uid, "IN")), Port("MX", InAnyCase(uid, "MX"))]);
        }
        return Instruction(uid, part, port);
    }

    IrExpr CallOut(string uid, XElement call, string port)
    {
        var info = call.Elements().First(e => Is(e, "CallInfo"));
        if (port == "eno") return Eno(uid, A(info, "Name"));
        return new IrExpr(IrKinds.CallOut, $"{A(info, "Name")}|{InstanceName(info)}|{port}");
    }

    /// <summary>eno = en 且本次执行未出错：ADD/SUB/MUL 溢出、DIV 除零、CONV 转换出错、块内 ENO := FALSE 等情况下 en=1 而 eno=0。
    /// 「未出错」是不透明原子（文字带部件名与 UId，不同部件互不相同），只保留 eno → en 这一确定的蕴含。</summary>
    IrExpr Eno(string uid, string name) => IrExpr.And([In(uid, "en") ?? IrExpr.True, EnoAtom(name, uid)]);

    /// <summary>部件 uid 的「执行未出错」原子（文字带部件名与 UId）。</summary>
    internal static IrExpr EnoAtom(string name, string uid) => new(IrKinds.Unknown, $"{name}（部件 {uid}）的 ENO：执行未出错");

    static readonly System.Text.RegularExpressions.Regex EnoText =
        new(@"^(?<name>.*)（部件 (?<uid>[^（）]+)）的 ENO：执行未出错$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>e 是 EnoAtom 造的原子时返回（部件名，UId），否则 null。</summary>
    internal static (string Name, string Uid)? EnoPartUid(IrExpr e) =>
        e.Kind == IrKinds.Unknown && e.Name is { } n && EnoText.Match(n) is { Success: true } m ? (m.Groups["name"].Value, m.Groups["uid"].Value) : null;

    IrStatement Stmt(string kind, string? target, IrExpr guard, IrExpr? value, IrCall? call = null, string? text = null)
    {
        var statement = new IrStatement($"{idPrefix}/{++statementIndex}", kind, target, guard, value, call, text);
        if (pendingWrites.Count == 0) return statement;
        statement = statement with { SideWrites = pendingWrites.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        pendingWrites.Clear();
        return statement;
    }

    /// <summary>边沿指令：bit 端口接的是边沿存储位，每次执行都会改写。</summary>
    static readonly HashSet<string> EdgeParts = new(StringComparer.Ordinal)
    { "PContact", "NContact", "PCoil", "NCoil", "PBox", "NBox", "P_TRIG", "N_TRIG" };

    /// <summary>已按语义翻译、输入端口只读的部件（输出接变量的写入另由赋值语句表示）。</summary>
    static readonly HashSet<string> ReadOnlyInputParts = new(StringComparer.Ordinal)
    {
        "Contact", "A", "O", "X", "Not", "Coil", "SCoil", "RCoil", "TrCoil", "IlCoil", "SvCoil", "Sr", "Rs",
        "Jump", "JumpNot", "Return", "Inc", "Dec", "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD",
        "InRange", "OutRange", "Move", "S_Move", "Calc", "SEL", "MAX", "MIN", "LIMIT"
    };

    /// <summary>部件执行时顺带改写的变量：背景实例（整个实例）；边沿指令的存储位；未展开语义的指令（含边沿指令以外的
    /// 其它带实例指令）接在输入侧端口上的全部变量（端口方向不明，可能是 InOut，只会多算写入）。每个部件只记一次，
    /// 挂到接下来生成的那条语句上。</summary>
    void NoteSideWrites(string uid, XElement part)
    {
        if (!sideWritten.Add(uid)) return;
        var name = A(part, "Name");
        if (InstanceName(part) is { Length: > 0 } instance) pendingWrites.Add(instance);
        IEnumerable<string> ports;
        if (EdgeParts.Contains(name)) ports = ["bit"];
        else if (ReadOnlyInputParts.Contains(name) || Comparators.ContainsKey(name) || Arithmetic.ContainsKey(name)
                 || UnaryFunctions.Contains(name)) ports = [];
        else ports = sinkWire.Keys.Where(k => k.Uid == uid).Select(k => k.Port).ToArray();
        foreach (var port in ports)
            if (sinkWire.TryGetValue((uid, port), out var wire) && wire.Elements().First() is var driver && Is(driver, "IdentCon")
                && accesses.TryGetValue(A(driver, "UId"), out var access) && access.Kind == IrKinds.Var)
                pendingWrites.Add(access.Name!);
    }

    /// <summary>线圈写入的变量本身；operand 上的取反标记表示取反写入，由调用方作用到值上，不进目标名。
    /// operand 接的不是访问（变量/常数）而是别的部件输出时（XML 结构异常），写成〔…〕认不出的目标——写入判定据此按「可能写任何量」处理，
    /// 不会把表达式文字当成变量名。</summary>
    string Target(string uid)
    {
        var operand = In(uid, "operand", applyNegation: false) ?? IrExpr.Unwired();
        return operand.Kind is IrKinds.Var or IrKinds.Const or IrKinds.Unknown ? TextOf(operand) : $"〔线圈目标不是变量：{IrText.Expr(operand)}〕";
    }

    IrExpr CoilValue(string uid, XElement part)
    {
        var value = InOrUnwired(uid, "in");
        return Negated(part, "operand") ? IrExpr.Not(value) : value;
    }

    bool Evaluated(string uid) => memo.Keys.Any(k => k.Uid == uid);

    bool DrivesAnything(string uid) =>
        wires.Any(w => w.Elements().First() is var d && Is(d, "NameCon") && A(d, "UId") == uid && w.Elements().Skip(1).Any());

    static string LabelOf(XElement part)
    {
        var label = part.Descendants().FirstOrDefault(e => Is(e, "Label"));
        var name = label == null ? A(part, "Label") : A(label, "Name");
        return name.Length == 0 ? "?" : name;
    }

    List<IrStatement> Statements()
    {
        var list = new List<IrStatement>();
        foreach (var (uid, part) in parts.OrderBy(p => int.TryParse(p.Key, out var n) ? n : int.MaxValue))
        {
            if (Is(part, "Call")) { list.Add(CallStatement(uid, part)); continue; }
            if (StatementParts.Contains(A(part, "Name"))) NoteSideWrites(uid, part);
            switch (A(part, "Name"))
            {
                case "Coil": list.Add(Stmt(IrStatementKinds.Assign, Target(uid), IrExpr.True, CoilValue(uid, part))); break;
                case "SCoil": list.Add(Stmt(IrStatementKinds.Set, Target(uid), InOrUnwired(uid, "in"), null)); break;
                case "RCoil": list.Add(Stmt(IrStatementKinds.Reset, Target(uid), InOrUnwired(uid, "in"), null)); break;
                case "PCoil": list.Add(Stmt(IrStatementKinds.Assign, Target(uid), IrExpr.True, new IrExpr(IrKinds.Rise, null, [InOrUnwired(uid, "in")]))); break;
                case "NCoil": list.Add(Stmt(IrStatementKinds.Assign, Target(uid), IrExpr.True, new IrExpr(IrKinds.Fall, null, [InOrUnwired(uid, "in")]))); break;
                // 顺控条件线圈：不写任何用户变量（Condition 语句，文字与赋值相同）
                case "TrCoil": list.Add(Stmt(IrStatementKinds.Condition, transitionTarget, IrExpr.True, InOrUnwired(uid, "in"))); break;
                case "IlCoil": list.Add(Stmt(IrStatementKinds.Condition, "〔互锁条件〕", IrExpr.True, InOrUnwired(uid, "in"))); break;
                case "SvCoil": list.Add(Stmt(IrStatementKinds.Condition, "〔监控条件〕", IrExpr.True, InOrUnwired(uid, "in"))); break;
                case "Sr":
                    list.Add(Stmt(IrStatementKinds.Set, Target(uid), InOrUnwired(uid, "s"), null));
                    list.Add(Stmt(IrStatementKinds.Reset, Target(uid), InOrUnwired(uid, "r1"), null));
                    break;
                case "Rs":
                    list.Add(Stmt(IrStatementKinds.Reset, Target(uid), InOrUnwired(uid, "r"), null));
                    list.Add(Stmt(IrStatementKinds.Set, Target(uid), InOrUnwired(uid, "s1"), null));
                    break;
                case "Jump": list.Add(Stmt(IrStatementKinds.Jump, LabelOf(part), InOrUnwired(uid, "in"), null)); break;
                case "JumpNot": list.Add(Stmt(IrStatementKinds.JumpNot, LabelOf(part), InOrUnwired(uid, "in"), null)); break;
                case "Return": list.Add(Stmt(IrStatementKinds.Return, null, InOrUnwired(uid, "in"), null)); break;
                case "Inc": case "Dec": list.Add(IncDec(uid, part)); break;
            }
        }
        // 指令盒的数据输出写入变量（如 Add.out → T、TON.Q → Y）
        foreach (var wire in wires)
        {
            var driver = wire.Elements().First();
            if (!Is(driver, "NameCon")) continue;
            var puid = A(driver, "UId");
            if (!parts.TryGetValue(puid, out var part) || Is(part, "Call")) continue;
            var targets = wire.Elements().Skip(1).Where(c => Is(c, "IdentCon"))
                .Select(c => accesses.TryGetValue(A(c, "UId"), out var a) ? TextOf(a) : "?").ToArray();
            if (targets.Length == 0) continue;
            var guard = HasPort(puid, "en") ? InOrUnwired(puid, "en") : IrExpr.True;
            var value = PartOut(puid, A(driver, "Name"));
            foreach (var target in targets) list.Add(Stmt(IrStatementKinds.Assign, target, guard, value));
        }
        // 输出没有被任何语句读到的部件（输出悬空，如 TON 的 Q 不接线而在别处读 Tmr.Q）：作为「执行」语句保留，不静默丢弃。
        // 先处理输出完全不接线的部件，这样它们的上游会被一并求值，不会各自再生成一条；再兜底处理只接到未被读取端口的部件。
        var remaining = parts.Where(p => !Is(p.Value, "Call") && !StatementParts.Contains(A(p.Value, "Name")))
            .OrderBy(p => int.TryParse(p.Key, out var n) ? n : int.MaxValue).Select(p => p.Key).ToList();
        foreach (var drivesOthers in new[] { false, true })
            foreach (var uid in remaining)
            {
                if (Evaluated(uid) || DrivesAnything(uid) != drivesOthers) continue;
                var port = QOutputParts.Contains(A(parts[uid], "Name")) ? "Q" : "out";
                list.Add(Stmt(IrStatementKinds.Box, null, IrExpr.True, PartOut(uid, port)));
            }
        // 兜底：求值过、却没有语句可挂的顺带写入，挂到最后一条语句上（不丢）
        if (pendingWrites.Count > 0 && list.Count > 0)
        {
            list[^1] = list[^1] with { SideWrites = (list[^1].SideWrites ?? []).Concat(pendingWrites).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            pendingWrites.Clear();
        }
        return list;
    }

    /// <summary>Inc/Dec：由变量直接驱动的输入输出端口（通常为 operand）就是被加减 1 的变量；受 en 控制。</summary>
    IrStatement IncDec(string uid, XElement part)
    {
        var operandPort = sinkWire.Where(kv => kv.Key.Uid == uid && Is(kv.Value.Elements().First(), "IdentCon"))
            .Select(kv => kv.Key.Port)
            .OrderBy(p => string.Equals(p, "operand", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (operandPort == null) return Stmt(IrStatementKinds.Box, null, IrExpr.True, Instruction(uid, part, "out"));
        var operand = In(uid, operandPort, applyNegation: false)!;
        var op = A(part, "Name") == "Inc" ? "+" : "-";
        return Stmt(IrStatementKinds.Assign, TextOf(operand), In(uid, "en") ?? IrExpr.True,
            new IrExpr(IrKinds.Arith, op, [operand, IrExpr.Const("1")]));
    }

    IrStatement CallStatement(string uid, XElement call)
    {
        var info = call.Elements().First(e => Is(e, "CallInfo"));
        var pins = new List<IrPin>();
        foreach (var parameter in info.Elements().Where(e => Is(e, "Parameter")))
        {
            var pin = A(parameter, "Name");
            var section = A(parameter, "Section");
            IrExpr? value = section is "Input" or "InOut" ? In(uid, pin) : null;
            List<XElement> outWires = section == "Output"
                ? wires.Where(w => w.Elements().First() is var d && Is(d, "NameCon") && A(d, "UId") == uid && A(d, "Name") == pin).ToList()
                : [];
            IReadOnlyList<string> targets = section switch
            {
                "Output" => outWires.SelectMany(w => w.Elements().Skip(1).Where(c => Is(c, "IdentCon")))
                    .Select(c => accesses.TryGetValue(A(c, "UId"), out var a) ? TextOf(a) : "?").ToArray(),
                "InOut" when value is { Kind: IrKinds.Var } => [value.Name!],
                _ => []
            };
            var negated = section == "Output" && Negated(call, pin);
            var feedsLogic = outWires.Any(w => w.Elements().Skip(1).Any(c => Is(c, "NameCon")));
            pins.Add(new IrPin(pin, section, A(parameter, "Type"), value, targets, negated, feedsLogic));
        }
        var en = In(uid, "en") ?? IrExpr.True;
        return Stmt(IrStatementKinds.Call, null, en, null,
            new IrCall(A(info, "Name"), A(info, "BlockType"), InstanceName(info), en, pins) { InstanceScope = InstanceScope(info), PartUid = uid });
    }
}
