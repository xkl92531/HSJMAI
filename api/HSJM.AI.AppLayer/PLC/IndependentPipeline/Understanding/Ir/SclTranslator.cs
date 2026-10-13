#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>SCL（StructuredText）→ IR 的子集翻译：赋值、IF/ELSIF/ELSE、REGION、IEC 计时器实例调用、RETURN。
/// 翻不了的语句（CASE/FOR/WHILE/REPEAT、块调用、可能写入实参的指令、GOTO、标签……）逐条原样保留为源码语句，绝不编造；
/// 含 RETURN/GOTO 的不透明语句另记一条返回语句（条件为〔源码中的 RETURN/GOTO〕），使块被视为有控制流。
/// 任何意外异常都退回为整段源码（与旧行为相同）。</summary>
public static class SclTranslator
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    static readonly Regex TimerName = new("^(TONR|TON|TOF|TP)(_L?TIME)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> EndWords = new(StringComparer.Ordinal)
        { "END_IF", "ELSIF", "ELSE", "END_REGION", "END_CASE", "END_FOR", "END_WHILE", "END_REPEAT", "UNTIL" };

    /// <summary>合成返回语句的条件文字：不透明源码里有 RETURN/GOTO。</summary>
    public const string SourceControlMarker = "源码中的 RETURN/GOTO";

    /// <summary>是不是为不透明源码里的 RETURN/GOTO 合成的返回语句（不是翻译出来的程序逻辑）。</summary>
    public static bool IsSourceControlMarker(IrStatement s) =>
        s.Kind == IrStatementKinds.Return && s.Guard is { Kind: IrKinds.Unknown, Name: SourceControlMarker };

    /// <summary>可以当无副作用函数翻译的博图指令（指令集结构证据，不是用户命名）：数据类型转换（X_TO_Y，两端都是博图基本数据类型）、
    /// 移位/循环、选择/限值、数学与字符串查询函数。不在表里的指令（如 Strg_TO_Chars 的 Chars、VariantPut、POKE、FILL_BLK
    /// 等可能写实参的，以及按地址读内存、值不只取决于参数的 PEEK）整条保留为源码。</summary>
    static readonly HashSet<string> PureInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHL", "SHR", "ROL", "ROR", "LIMIT", "MIN", "MAX", "SEL", "MUX", "ABS", "SQR", "SQRT", "LN", "EXP", "EXPT",
        "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "FRAC", "TRUNC", "ROUND", "CEIL", "FLOOR", "NORM_X", "SCALE_X",
        "SWAP", "LEN", "FIND", "LEFT", "RIGHT", "MID", "CONCAT", "DELETE", "INSERT", "REPLACE", "CONVERT", "TypeOf"
    };

    /// <summary>博图基本数据类型（转换指令 X_TO_Y 的两端）。</summary>
    const string ElementaryType = "BOOL|BYTE|WORD|DWORD|LWORD|SINT|USINT|INT|UINT|DINT|UDINT|LINT|ULINT|REAL|LREAL|TIME|LTIME|S5TIME"
        + "|DATE|TOD|TIME_OF_DAY|LTOD|DT|LDT|DTL|DATE_AND_TIME|CHAR|WCHAR|STRING|WSTRING|BCD16|BCD32|BCD64";
    static readonly Regex Conversion = new($"^(?:{ElementaryType})_TO_(?:{ElementaryType})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool IsPureInstruction(string name) => PureInstructions.Contains(name) || Conversion.IsMatch(name);

    /// <summary>IF 合并时单个值表达式（按树计）的节点数上限：超出则整段保留为源码（审查 I-5，防止嵌套 IF 指数增长）。</summary>
    internal const int MaxMergedNodes = 2000;

    /// <summary>不透明语句前面加的条件说明行「（仅当 … 时执行）」：只读条件里的变量，不是写入。</summary>
    const string GuardNotePrefix = "（仅当 ";
    const string GuardNoteSuffix = " 时执行）\n";

    /// <summary>文字语句里可能含写入的部分：去掉翻译器加的条件说明行（审查 M-4，说明行只读不写）与注释（「// …」到行尾、
    /// 「(* … *)」，单引号字符串里的不算注释）。注释里提到的名字不是写入（如只有说明注释的 SCL 程序段）。</summary>
    public static string WrittenText(string text)
    {
        if (text.StartsWith(GuardNotePrefix, StringComparison.Ordinal) && text.IndexOf(GuardNoteSuffix, StringComparison.Ordinal) is var end and > 0)
            text = text[(end + GuardNoteSuffix.Length)..];
        if (!text.Contains("//", StringComparison.Ordinal) && !text.Contains("(*", StringComparison.Ordinal)) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'') quoted = !quoted;
            else if (!quoted && ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                if (i < text.Length) sb.Append('\n');
                continue;
            }
            else if (!quoted && ch == '(' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*)", i + 2, StringComparison.Ordinal);
                if (close < 0) break;
                sb.Append(' ');
                i = close + 1;
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    enum LexKind { Token, Value, Call, Text, Other }

    /// <summary>Text：记号大写化后的文字（Original 为原样）；Raw：该词元在 StructuredText 子元素中的序号（截取源码用）。</summary>
    sealed record Lex(LexKind Kind, string Text, IrExpr? Value, XElement Element, int Raw, string Original = "");

    abstract record Node(int From, int To);
    sealed record AssignNode(string Target, IrExpr Value, int From, int To) : Node(From, To);
    sealed record IfNode(IReadOnlyList<(IrExpr Cond, IReadOnlyList<Node> Body)> Branches, IReadOnlyList<Node>? Else, int From, int To) : Node(From, To);
    sealed record TimerNode(string Kind, string Instance, IrExpr In, IrExpr Pt, int From, int To) : Node(From, To);
    sealed record ReturnNode(int From, int To) : Node(From, To);
    /// <summary>HasHiddenWrite：范围内有块调用（CallInfo）或可能按地址写内存的非纯指令（POKE 等），可能改写任何变量。</summary>
    sealed record OpaqueNode(bool HasControlFlow, bool HasHiddenWrite, int From, int To) : Node(From, To);
    sealed record RegionNode(IReadOnlyList<Node> Body, int From, int To) : Node(From, To);

    sealed class Fail : Exception { }

    public static IReadOnlyList<IrStatement> Translate(XElement structuredText, string prefix, ISet<string>? unknown = null,
        Func<string, bool>? knownBool = null)
    {
        IrStatement Whole() => new($"{prefix}/1", IrStatementKinds.Source, null, IrExpr.True, null, null, SclText.Render(structuredText, unknown));
        try
        {
            var raw = structuredText.Elements().ToList();
            var lexes = Tokenize(raw, unknown);
            if (lexes.Count == 0) return [Whole()];
            var parsed = new Parser(lexes, unknown).Program();
            var typer = new Typer(knownBool ?? (_ => false));
            typer.Infer(parsed);
            var nodes = typer.Rewrite(parsed);
            string Source(int from, int to)
            {
                var first = lexes[from].Raw;
                var last = lexes[to].Raw;
                return SclText.Render(new XElement("S", raw.Skip(first).Take(last - first + 1).Select(x => new XElement(x))), unknown);
            }
            var emitter = new Emitter(prefix, Source, typer.IsBoolVar);
            emitter.Emit(nodes, IrExpr.True);
            return emitter.Statements.Count == 0 ? [Whole()] : emitter.Statements;
        }
        catch (Exception) { return [Whole()]; }
    }

    static bool IsCall(XElement e) => e.Name.LocalName is "CallInfo" or "Instruction";

    /// <summary>调用（含嵌套在参数里的）可能改写任何变量：块调用（CallInfo），或不带背景实例、不是纯函数也不是计时器的有名指令
    /// （POKE、POKE_BLK、FILL_BLK……按地址/区写）。带实例或无名（实例调用）的指令只写其实例，实例名出现在源码文字里。</summary>
    static bool HidesWrites(XElement access) =>
        access.DescendantsAndSelf().Any(d => d.Name.LocalName == "CallInfo")
        || access.DescendantsAndSelf().Where(d => d.Name.LocalName == "Instruction").Any(i =>
            !i.Elements().Any(e => e.Name.LocalName == "Instance") && ((string?)i.Attribute("Name") ?? "").Trim() is { Length: > 0 } n
            && !IsPureInstruction(n) && !TimerName.IsMatch(n));

    static List<Lex> Tokenize(IReadOnlyList<XElement> raw, ISet<string>? unknown)
    {
        var lexes = new List<Lex>();
        for (var i = 0; i < raw.Count; i++)
        {
            var e = raw[i];
            switch (e.Name.LocalName)
            {
                case "Blank": case "NewLine": case "LineComment": case "Comment": break;
                case "Token":
                {
                    var text = ((string?)e.Attribute("Text") ?? "").Trim();
                    lexes.Add(new(LexKind.Token, text.ToUpperInvariant(), null, e, i, text));
                    break;
                }
                case "Access":
                    if (e.Elements().Any(IsCall)) lexes.Add(new(LexKind.Call, "", null, e, i));
                    else
                    {
                        var value = SymbolReader.Access(e);
                        if (value.Kind == IrKinds.Unknown) unknown?.Add(value.Name ?? "未识别的访问");
                        lexes.Add(new(LexKind.Value, "", value, e, i));
                    }
                    break;
                case "Text": lexes.Add(new(LexKind.Text, e.Value, null, e, i)); break;
                default: lexes.Add(new(LexKind.Other, e.Name.LocalName, null, e, i)); break;
            }
        }
        return lexes;
    }

    /// <summary>字面量记号：数字开头（含 16#FF 等）、带类型前缀（T#2s）或字符串（'…'）。关键字与运算符都不含这些。</summary>
    static bool IsLiteralToken(string text) =>
        text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '\'' || text.Contains('#'));

    sealed class Parser(IReadOnlyList<Lex> lexes, ISet<string>? unknown)
    {
        static readonly Lex Eof = new(LexKind.Token, "<EOF>", null, new XElement("EOF"), -1);
        static readonly (string Token, string Op)[] Comparators = [("=", "=="), ("<>", "<>"), ("<", "<"), (">", ">"), ("<=", "<="), (">=", ">=")];
        int pos;

        Lex Peek => pos < lexes.Count ? lexes[pos] : Eof;
        public bool AtEnd => pos >= lexes.Count;
        bool TakeTok(string text)
        {
            if (Peek.Kind != LexKind.Token || Peek.Text != text) return false;
            pos++;
            return true;
        }
        void Expect(string text) { if (!TakeTok(text)) throw new Fail(); }

        public IReadOnlyList<Node> Program() => Statements([]);

        IReadOnlyList<Node> Statements(IReadOnlyCollection<string> stop)
        {
            var list = new List<Node>();
            while (pos < lexes.Count && !(Peek.Kind == LexKind.Token && stop.Contains(Peek.Text)))
            {
                if (TakeTok(";")) continue;
                var start = pos;
                Node? node;
                try { node = Statement(); }
                catch (Fail) { node = null; }
                if (node is null) node = Opaque(start, stop);
                if (node is RegionNode region) list.AddRange(region.Body);
                else list.Add(node);
            }
            return list;
        }

        Node? Statement()
        {
            var start = pos;
            var t = Peek;
            if (t.Kind == LexKind.Token)
                return t.Text switch { "IF" => If(), "REGION" => Region(), "RETURN" => Return(), _ => null };
            if (t.Kind == LexKind.Value && t.Value is { Kind: IrKinds.Var } target)
            {
                if (pos + 2 < lexes.Count && lexes[pos + 1] is { Kind: LexKind.Token, Text: "." } && lexes[pos + 2].Kind == LexKind.Call)
                {
                    var timer = TimerCall(lexes[pos + 2], target.Name!);
                    if (timer is null) return null;
                    pos += 3;
                    TakeTok(";");
                    return timer with { From = start, To = pos - 1 };
                }
                pos++;
                if (!TakeTok(":=")) return null;
                var value = Expr();
                if (!TakeTok(";")) return null;
                return new AssignNode(target.Name!, value, start, pos - 1);
            }
            if (t.Kind == LexKind.Call)
            {
                var timer = TimerCall(t, null);
                if (timer is null) return null;
                pos++;
                TakeTok(";");
                return timer with { From = start, To = pos - 1 };
            }
            return null;
        }

        Node Return()
        {
            var start = pos++;
            TakeTok(";");
            return new ReturnNode(start, pos - 1);
        }

        Node If()
        {
            var start = pos++;
            var branches = new List<(IrExpr, IReadOnlyList<Node>)>();
            var cond = Expr();
            Expect("THEN");
            branches.Add((cond, Statements(["ELSIF", "ELSE", "END_IF"])));
            IReadOnlyList<Node>? otherwise = null;
            while (true)
            {
                if (TakeTok("ELSIF"))
                {
                    var c = Expr();
                    Expect("THEN");
                    branches.Add((c, Statements(["ELSIF", "ELSE", "END_IF"])));
                    continue;
                }
                if (TakeTok("ELSE")) otherwise = Statements(["END_IF"]);
                Expect("END_IF");
                break;
            }
            TakeTok(";");
            return new IfNode(branches, otherwise, start, pos - 1);
        }

        Node Region()
        {
            var start = pos++;
            if (Peek.Kind == LexKind.Text) pos++;
            var body = Statements(["END_REGION"]);
            Expect("END_REGION");
            TakeTok(";");
            return new RegionNode(body, start, pos - 1);
        }

        /// <summary>从 start 起跳过一条翻不了的语句：结构语句跳到配对的 END_xxx（计嵌套），其它跳到括号外的分号或上层的结束词。</summary>
        Node Opaque(int start, IReadOnlyCollection<string> stop)
        {
            pos = start;
            var control = false;
            var first = Peek;
            var ender = first.Kind == LexKind.Token ? first.Text switch
            {
                "IF" => "END_IF", "CASE" => "END_CASE", "FOR" => "END_FOR", "WHILE" => "END_WHILE",
                "REPEAT" => "END_REPEAT", "REGION" => "END_REGION", _ => null
            } : null;
            if (ender is not null)
            {
                var depth = 0;
                while (pos < lexes.Count)
                {
                    var x = lexes[pos++];
                    if (x.Kind != LexKind.Token) continue;
                    if (x.Text is "RETURN" or "GOTO") control = true;
                    if (x.Text == first.Text) depth++;
                    else if (x.Text == ender && --depth == 0) break;
                }
                TakeTok(";");
            }
            else
            {
                var paren = 0;
                while (pos < lexes.Count)
                {
                    var x = lexes[pos];
                    if (x.Kind == LexKind.Token)
                    {
                        if (paren == 0 && pos > start && (stop.Contains(x.Text) || EndWords.Contains(x.Text))) break;
                        if (x.Text is "RETURN" or "GOTO") control = true;
                        if (x.Text == "(") paren++;
                        else if (x.Text == ")") paren--;
                        pos++;
                        if (paren <= 0 && x.Text == ";") break;
                    }
                    else pos++;
                }
            }
            if (pos == start) pos++;
            var end = Math.Min(pos, lexes.Count) - 1;
            var hidden = false;
            for (var i = start; i <= end; i++)
                if (lexes[i].Kind == LexKind.Call && HidesWrites(lexes[i].Element)) hidden = true;
            return new OpaqueNode(control, hidden, start, end);
        }

        TimerNode? TimerCall(Lex call, string? instance)
        {
            var info = call.Element.Elements().FirstOrDefault(IsCall);
            if (info is null || info.Name.LocalName != "Instruction") return null;
            var m = TimerName.Match(((string?)info.Attribute("Name") ?? "").Trim());
            if (!m.Success) return null;
            instance ??= InstanceOf(info);
            if (string.IsNullOrEmpty(instance)) return null;
            IrExpr? input = null, preset = null;
            foreach (var p in info.Elements().Where(e => e.Name.LocalName is "Parameter" or "NamelessParameter"))
            {
                if (WritesThrough(p)) return null;   // Q => x / ET => x / 输入输出：写入不建模，整条不透明
                var value = Parameter(p);
                if (value is null) return null;
                switch (((string?)p.Attribute("Name") ?? "").ToUpperInvariant())
                {
                    case "IN": input = value; break;
                    case "PT": preset = value; break;
                    default: return null;            // TONR 的 R 等其它参数：IR 的计时器只表达 IN/PT，不丢语义
                }
            }
            return input is null || preset is null ? null : new TimerNode(m.Groups[1].Value.ToUpperInvariant(), instance, input, preset, 0, 0);
        }

        static string InstanceOf(XElement info)
        {
            var instance = info.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
            if (instance is null) return "";
            var symbol = instance.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
            return symbol != null ? SymbolReader.Symbol(symbol) : SymbolReader.Components(instance);
        }

        /// <summary>经该参数写入：Section 为 Output/InOut，或实参前是「=>」。</summary>
        static bool WritesThrough(XElement p) =>
            (string?)p.Attribute("Section") is "Output" or "InOut"
            || p.Elements().FirstOrDefault(e => e.Name.LocalName == "Token") is { } first && ((string?)first.Attribute("Text"))?.Trim() == "=>";

        IrExpr? Parameter(XElement p)
        {
            var inner = Tokenize(p.Elements().ToList(), unknown);
            if (inner.Count > 0 && inner[0].Kind == LexKind.Token && inner[0].Text is ":=" or "=>")
            {
                if (inner[0].Text == "=>") return null;
                inner.RemoveAt(0);
            }
            if (inner.Count == 0) return null;
            var parser = new Parser(inner, unknown);
            try
            {
                var e = parser.Expr();
                return parser.AtEnd ? e : null;
            }
            catch (Fail) { return null; }
        }

        public IrExpr Expr() => Or();

        IrExpr Or()
        {
            var x = Xor();
            while (TakeTok("OR")) x = IrExpr.Or([x, Xor()]);
            return x;
        }

        IrExpr Xor()
        {
            var x = And();
            while (TakeTok("XOR")) x = new IrExpr(IrKinds.Xor, null, [x, And()]);
            return x;
        }

        IrExpr And()
        {
            var x = Compare();
            while (TakeTok("AND") || TakeTok("&")) x = IrExpr.And([x, Compare()]);
            return x;
        }

        IrExpr Compare()
        {
            var x = Add();
            foreach (var (token, op) in Comparators)
                if (TakeTok(token)) return new IrExpr(IrKinds.Cmp, op, [x, Add()]);
            return x;
        }

        IrExpr Add()
        {
            var x = Mul();
            while (true)
            {
                if (TakeTok("+")) x = new IrExpr(IrKinds.Arith, "+", [x, Mul()]);
                else if (TakeTok("-")) x = new IrExpr(IrKinds.Arith, "-", [x, Mul()]);
                else return x;
            }
        }

        IrExpr Mul()
        {
            var x = Unary();
            while (true)
            {
                if (TakeTok("*")) x = new IrExpr(IrKinds.Arith, "*", [x, Unary()]);
                else if (TakeTok("/")) x = new IrExpr(IrKinds.Arith, "/", [x, Unary()]);
                else if (TakeTok("MOD")) x = new IrExpr(IrKinds.Arith, "MOD", [x, Unary()]);
                else return x;
            }
        }

        IrExpr Unary()
        {
            if (TakeTok("NOT")) return IrExpr.Not(Unary());
            if (TakeTok("-")) return new IrExpr(IrKinds.Func, "Neg", [Unary()]);
            if (TakeTok("+")) return Unary();
            var x = Primary();
            return TakeTok("**") ? new IrExpr(IrKinds.Func, "EXPT", [x, Unary()]) : x;
        }

        IrExpr Primary()
        {
            if (TakeTok("("))
            {
                var x = Expr();
                Expect(")");
                return x;
            }
            if (TakeTok("TRUE")) return IrExpr.True;
            if (TakeTok("FALSE")) return IrExpr.Const("FALSE");
            var t = Peek;
            if (t.Kind == LexKind.Token && IsLiteralToken(t.Original)) { pos++; return IrExpr.Const(t.Original); }
            if (t.Kind == LexKind.Value && t.Value is not null) { pos++; return t.Value; }
            if (t.Kind == LexKind.Call) { pos++; return CallValue(t); }
            throw new Fail();
        }

        /// <summary>表达式里的内置指令/函数调用：Func(名字, 参数…)，具名参数为 Port。裁定 F5：块调用（CallInfo）、带背景实例或
        /// 无名（实例调用）的指令、不在纯函数指令表里的指令、任一参数可能被写入（Output/InOut、=>）——都有隐藏写入，整条不透明。</summary>
        IrExpr CallValue(Lex call)
        {
            var info = call.Element.Elements().FirstOrDefault(IsCall) ?? throw new Fail();
            if (info.Name.LocalName != "Instruction" || info.Elements().Any(e => e.Name.LocalName == "Instance")) throw new Fail();
            var name = ((string?)info.Attribute("Name") ?? "").Trim();
            if (name.Length == 0 || !IsPureInstruction(name)) throw new Fail();
            var args = new List<IrExpr>();
            var n = 0;
            foreach (var p in info.Elements().Where(e => e.Name.LocalName is "Parameter" or "NamelessParameter"))
            {
                n++;
                if (WritesThrough(p)) throw new Fail();
                var v = Parameter(p) ?? throw new Fail();   // 审查 I-4：参数里嵌套块调用/非纯指令等解析不了 → 整条不透明
                args.Add(p.Name.LocalName == "Parameter" ? new IrExpr(IrKinds.Port, (string?)p.Attribute("Name") ?? $"IN{n}", [v]) : v);
            }
            return new IrExpr(IrKinds.Func, name, args);
        }
    }

    /// <summary>审查 I-1：AND/OR/XOR/NOT 只有在操作数都确定是 Bool 时才按逻辑运算翻译，否则写成不透明函数 AND(…)/OR(…)/XOR(…)/NOT(…)
    /// （可能是字的按位运算；博图不开 IEC 检查时 Bool 可隐式转成字，一个操作数是 Bool 不能断定另一个也是）。
    /// 确定是 Bool：调用方给的 Bool 名单（块接口、点表）、比较结果、TRUE/FALSE，以及从 Bool 语境向下推出的变量——
    /// IF/ELSIF 条件、Bool 目标的赋值值、计时器 IN 本身一定是 Bool，其中直接参与逻辑运算的变量也是 Bool（字不能隐式转成 Bool），
    /// 推出的变量在整个程序段内都按 Bool。</summary>
    sealed class Typer(Func<string, bool> knownBool)
    {
        readonly HashSet<string> inferred = new(C);

        public bool IsBoolVar(string name) => knownBool(name) || inferred.Contains(name);

        void MarkBool(IrExpr e)
        {
            switch (e.Kind)
            {
                case IrKinds.Var: inferred.Add(e.Name!); break;
                case IrKinds.And or IrKinds.Or or IrKinds.Xor or IrKinds.Not:
                    foreach (var a in e.Args!) MarkBool(a);
                    break;
            }
        }

        public void Infer(IReadOnlyList<Node> nodes)
        {
            for (var round = 0; round < 4; round++)
            {
                var before = inferred.Count;
                Visit(nodes);
                if (inferred.Count == before) break;
            }
        }

        void Visit(IReadOnlyList<Node> nodes)
        {
            foreach (var node in nodes)
                switch (node)
                {
                    case AssignNode a when IsBoolVar(a.Target): MarkBool(a.Value); break;
                    case TimerNode t: MarkBool(t.In); break;
                    case IfNode i:
                        foreach (var (cond, body) in i.Branches) { MarkBool(cond); Visit(body); }
                        if (i.Else is { } e) Visit(e);
                        break;
                }
        }

        /// <summary>改写后的表达式里，逻辑运算只在操作数都确定是 Bool 时保留，所以「是逻辑运算」本身就说明结果是 Bool。</summary>
        public bool IsBool(IrExpr e) => e.Kind switch
        {
            IrKinds.Cmp => true,
            IrKinds.Const => e.Name?.Trim().ToUpperInvariant() is "TRUE" or "FALSE",
            IrKinds.Var => IsBoolVar(e.Name!),
            IrKinds.And or IrKinds.Or or IrKinds.Xor or IrKinds.Not => true,
            _ => false
        };

        public IrExpr Rewrite(IrExpr e)
        {
            if (e.Args is null) return e;
            var args = e.Args.Select(Rewrite).ToArray();
            if (e.Kind is IrKinds.And or IrKinds.Or or IrKinds.Xor or IrKinds.Not && !args.All(IsBool))
                return new IrExpr(IrKinds.Func, e.Kind.ToUpperInvariant(), args);
            return e with { Args = args };
        }

        public IReadOnlyList<Node> Rewrite(IReadOnlyList<Node> nodes) => nodes.Select(Rewrite).ToArray();

        Node Rewrite(Node node) => node switch
        {
            AssignNode a => a with { Value = Rewrite(a.Value) },
            TimerNode t => t with { In = Rewrite(t.In), Pt = Rewrite(t.Pt) },
            IfNode i => i with
            {
                Branches = i.Branches.Select(b => (Rewrite(b.Cond), Rewrite(b.Body))).ToArray(),
                Else = i.Else is null ? null : Rewrite(i.Else)
            },
            _ => node
        };
    }

    sealed class Emitter(string prefix, Func<int, int, string> source, Func<string, bool> knownBool)
    {
        public readonly List<IrStatement> Statements = [];

        void Add(string kind, string? target, IrExpr guard, IrExpr? value, string? text = null, IReadOnlyList<string>? sideWrites = null) =>
            Statements.Add(new($"{prefix}/{Statements.Count + 1}", kind, target, guard, value, null, text) { SideWrites = sideWrites });

        public void Emit(IReadOnlyList<Node> nodes, IrExpr guard)
        {
            foreach (var node in nodes)
                switch (node)
                {
                    case AssignNode a: Add(IrStatementKinds.Assign, a.Target, guard, a.Value); break;
                    // 计时器实例调用改写整个背景实例（与 FlgNet 指令盒的 SideWrites 同口径）
                    case TimerNode t: Add(IrStatementKinds.Box, null, guard, Timer(t), sideWrites: [t.Instance]); break;
                    case ReturnNode: Add(IrStatementKinds.Return, null, guard, null); break;
                    case OpaqueNode o: Opaque(o, guard); break;
                    case IfNode i: EmitIf(i, guard); break;
                }
        }

        static IrExpr Timer(TimerNode t) => new(IrKinds.Timer, $"{t.Kind}|{t.Instance}|Q",
            [new IrExpr(IrKinds.Port, "IN", [t.In]), new IrExpr(IrKinds.Port, "PT", [t.Pt])]);

        void Opaque(OpaqueNode o, IrExpr guard)
        {
            var text = source(o.From, o.To);
            Add(IrStatementKinds.Source, null, IrExpr.True, null, guard.IsTrue ? text : $"（仅当 {IrText.Expr(guard)} 时执行）\n{text}");
            if (o.HasControlFlow) Add(IrStatementKinds.Return, null, new IrExpr(IrKinds.Unknown, SourceControlMarker), null);
        }

        void EmitIf(IfNode node, IrExpr guard)
        {
            var opaque = RewritesWhatItReads(node);
            if (!opaque)
            {
                var merged = TryMerge(node, guard);
                if (merged == MergeResult.Merged) return;
                opaque = merged == MergeResult.TooLarge;
            }
            if (!opaque && GuardedSafe(node)) { EmitGuarded(node, guard); return; }
            Opaque(new OpaqueNode(ContainsControl(node), Flatten(node).Any(n => n is OpaqueNode { HasHiddenWrite: true }), node.From, node.To), guard);
        }

        void EmitGuarded(IfNode node, IrExpr guard)
        {
            var guards = BranchGuards(node.Branches.Select(b => b.Cond).ToList());
            for (var i = 0; i < node.Branches.Count; i++) Emit(node.Branches[i].Body, IrExpr.And([guard, guards[i]]));
            if (node.Else is { Count: > 0 } otherwise) Emit(otherwise, IrExpr.And([guard, guards[^1]]));
        }

        /// <summary>第 i 分支：前面各分支条件都不成立且本分支条件成立；最后一项为 ELSE（全部不成立）。</summary>
        static IReadOnlyList<IrExpr> BranchGuards(IReadOnlyList<IrExpr> conds)
        {
            var result = new List<IrExpr>();
            for (var i = 0; i < conds.Count; i++) result.Add(IrExpr.And(conds.Take(i).Select(IrExpr.Not).Append(conds[i])));
            result.Add(IrExpr.And(conds.Select(IrExpr.Not)));
            return result;
        }

        /// <summary>名字下标里的变量（Buf[Idx] 的 Idx、Valve[2+PBValve] 的 PBValve；去引号，数字与类型常量不算）。</summary>
        internal static IEnumerable<string> IndexVars(string name)
        {
            foreach (var seg in IrBlock.Segments(name))
            {
                if (!seg.StartsWith('[')) continue;
                foreach (var part in IndexSplit.Split(seg))
                {
                    var v = part.Trim().Trim('"');
                    if (v.Length == 0 || char.IsDigit(v[0]) || v.Contains('#')) continue;
                    yield return v;
                    foreach (var inner in IndexVars(v)) yield return inner;
                }
            }
        }

        static readonly Regex IndexSplit = new(@"[\[\],+\-*/()\s]+", RegexOptions.CultureInvariant);

        static IEnumerable<string> NamesIn(IrExpr e) => e.Vars();

        /// <summary>IF 读到的条件变量（含嵌套 IF 的条件）与所有名字下标里的变量。</summary>
        static IReadOnlyList<string> ConditionAndIndexVars(IfNode node)
        {
            var conds = node.Branches.Select(b => b.Cond)
                .Concat(Flatten(node).OfType<IfNode>().SelectMany(i => i.Branches.Select(b => b.Cond))).ToArray();
            var names = conds.SelectMany(NamesIn)
                .Concat(Flatten(node).SelectMany(n => n switch
                {
                    AssignNode a => NamesIn(a.Value).Append(a.Target),
                    TimerNode t => NamesIn(t.In).Concat(NamesIn(t.Pt)).Append(t.Instance),
                    _ => Enumerable.Empty<string>()
                })).ToArray();
            return conds.SelectMany(NamesIn).Concat(names.SelectMany(IndexVars)).Distinct(C).ToArray();
        }

        /// <summary>审查 I-3：IF 内被写的变量（赋值目标、计时器实例）出现在条件（含嵌套条件）或任何名字的下标里 →
        /// 合并与带条件逐条写出都可能读错（条件按新值重算、下标指向别的元素），整段保留为源码。</summary>
        static bool RewritesWhatItReads(IfNode node)
        {
            var reads = ConditionAndIndexVars(node);
            if (reads.Count == 0) return false;
            foreach (var n in Flatten(node))
                switch (n)
                {
                    case AssignNode a when reads.Any(r => ProjectWrites.Overlaps(a.Target, r)): return true;
                    case TimerNode t when reads.Any(r => ProjectWrites.Overlaps(t.Instance, r)): return true;
                }
            return false;
        }

        /// <summary>分支条件读到的变量在 IF 内都没被写，逐条带条件写出才与原程序一致：赋值/计时器实例与条件变量、下标变量不重叠
        /// （ProjectWrites.Overlaps：同名、父子、可能同址的下标、绝对地址）；不透明语句的文字里没提到它们的根段；
        /// IF 内没有块调用或按地址写的指令（可能经全局量/地址改写条件变量）。</summary>
        bool GuardedSafe(IfNode node)
        {
            var condVars = ConditionAndIndexVars(node);
            foreach (var n in Flatten(node))
                switch (n)
                {
                    case AssignNode a when condVars.Any(v => ProjectWrites.Overlaps(a.Target, v)): return false;
                    case TimerNode t when condVars.Any(v => ProjectWrites.Overlaps(t.Instance, v)): return false;
                    case OpaqueNode { HasHiddenWrite: true }: return false;
                    case OpaqueNode o when condVars.Any(v => Mentions(source(o.From, o.To), IrBlock.RootOf(v.Trim()))): return false;
                }
            return true;
        }

        static bool Mentions(string text, string name) =>
            name.Length == 0
            || Regex.IsMatch(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static IEnumerable<Node> Flatten(IfNode node)
        {
            foreach (var body in node.Branches.Select(b => b.Body).Append(node.Else ?? Array.Empty<Node>()))
                foreach (var n in body)
                {
                    yield return n;
                    if (n is IfNode inner)
                        foreach (var x in Flatten(inner)) yield return x;
                }
        }

        static bool ContainsControl(IfNode node) => Flatten(node).Any(n => n is ReturnNode || n is OpaqueNode { HasControlFlow: true });

        // ---- IF 合并：按顺序执行语义求每个目标的最终值 ----

        sealed record Run(Dictionary<string, IrExpr> Env, HashSet<string> Assigned);

        static bool Mergeable(IfNode node) => Flatten(node).All(n => n is AssignNode or IfNode);

        static IrExpr Subst(IrExpr e, IReadOnlyDictionary<string, IrExpr> env) =>
            BoolSimplifier.Substitute(e, n => env.TryGetValue(n, out var v) ? v : null);

        static bool? BoolConstOf(IrExpr v) => v.Kind != IrKinds.Const ? null
            : v.Name?.Trim().ToUpperInvariant() switch { "TRUE" => true, "FALSE" => false, _ => null };

        /// <summary>值一定是 Bool：比较、TRUE/FALSE、确定为 Bool 的变量、逻辑运算（Typer 只在操作数都确定是 Bool 时保留逻辑运算，
        /// 否则已改写为不透明函数）。</summary>
        bool IsBoolish(IrExpr v) => v.Kind switch
        {
            IrKinds.Cmp => true,
            IrKinds.Const => BoolConstOf(v) is not null,
            IrKinds.Var => knownBool(v.Name!),
            IrKinds.And or IrKinds.Or or IrKinds.Not or IrKinds.Xor => true,
            _ => false
        };

        /// <summary>guards 比 conds 多一项（ELSE），values 与 guards 一一对应。Bool 目标为 ∨(分支条件 ∧ 值)，否则为嵌套 SEL。</summary>
        IrExpr Choose(string target, IReadOnlyList<IrExpr> conds, IReadOnlyList<IrExpr> guards, IReadOnlyList<IrExpr> values)
        {
            if (knownBool(target) || values.Any(IsBoolish))
                return BoolSimplifier.Fold(IrExpr.Or(guards.Zip(values, (g, v) => IrExpr.And([g, v]))));
            var result = values[^1];
            for (var i = conds.Count - 1; i >= 0; i--)
                result = new IrExpr(IrKinds.Func, "SEL",
                [
                    new IrExpr(IrKinds.Port, "G", [conds[i]]), new IrExpr(IrKinds.Port, "真", [values[i]]), new IrExpr(IrKinds.Port, "假", [result])
                ]);
            return result;
        }

        /// <summary>按顺序执行语义求一段语句后的变量值（env 为进入时的值，键忽略大小写）；order 记录目标第一次被赋值的先后。</summary>
        Run Exec(IReadOnlyList<Node> body, Dictionary<string, IrExpr> env, List<string> order)
        {
            var current = new Dictionary<string, IrExpr>(env, C);
            var assigned = new HashSet<string>(C);
            foreach (var node in body)
            {
                if (node is AssignNode a)
                {
                    current[a.Target] = Bounded(Subst(a.Value, current));
                    assigned.Add(a.Target);
                    if (!order.Contains(a.Target, C)) order.Add(a.Target);
                }
                else if (node is IfNode inner)
                {
                    var conds = inner.Branches.Select(b => Subst(b.Cond, current)).ToList();
                    var guards = BranchGuards(conds);
                    var runs = inner.Branches.Select(b => b.Body).Append(inner.Else ?? Array.Empty<Node>())
                        .Select(b => Exec(b, current, order)).ToList();
                    var changed = new HashSet<string>(runs.SelectMany(r => r.Assigned), C);
                    var next = new Dictionary<string, IrExpr>(current, C);
                    foreach (var t in changed)
                    {
                        var before = current.TryGetValue(t, out var v0) ? v0 : IrExpr.Var(t);
                        next[t] = Bounded(Choose(t, conds, guards, runs.Select(r => r.Assigned.Contains(t) ? r.Env[t] : before).ToList()));
                    }
                    current = next;
                    assigned.UnionWith(changed);
                }
            }
            return new(current, assigned);
        }

        /// <summary>按名字代入只认得完全同名的目标：IF 内两个不同的目标互相重叠（整体写父结构与写成员、可能同址的下标），
        /// 或读到与某个目标重叠但不同名的变量，合并都会丢失先后 → 不合并。</summary>
        static bool NameSubstitutionIsExact(IfNode node)
        {
            var targets = Flatten(node).OfType<AssignNode>().Select(a => a.Target.Trim()).Distinct(C).ToArray();
            for (var i = 0; i < targets.Length; i++)
                for (var j = i + 1; j < targets.Length; j++)
                    if (ProjectWrites.Overlaps(targets[i], targets[j])) return false;
            var reads = node.Branches.Select(b => b.Cond)
                .Concat(Flatten(node).SelectMany(n => n switch
                {
                    AssignNode a => [a.Value],
                    IfNode i => i.Branches.Select(b => b.Cond),
                    _ => Enumerable.Empty<IrExpr>()
                }))
                .SelectMany(e => e.Vars()).Distinct(C).ToArray();
            return !reads.Any(r => targets.Any(t => !C.Equals(r.Trim(), t) && ProjectWrites.Overlaps(r, t)));
        }

        /// <summary>IF 内只有赋值与嵌套 IF 时合并：每个分支都赋值 → 一条无条件赋值；只在部分分支赋 TRUE/FALSE → 各分支一条置位/复位；
        /// 其它部分赋值 → 一条无条件赋值（未赋值分支取目标自身）。按目标第一次被赋值的先后写出；写出的语句若读到前面语句的目标
        /// （会误读新值）则放弃合并。</summary>
        enum MergeResult { NotMergeable, Merged, TooLarge }

        sealed class TooLarge : Exception { }

        /// <summary>按树计的节点数（共享子树重复计），超过上限即抛 TooLarge；计数到上限即停，开销有界。</summary>
        static IrExpr Bounded(IrExpr e)
        {
            var budget = MaxMergedNodes;
            void Walk(IrExpr x)
            {
                if (--budget < 0) throw new TooLarge();
                if (x.Args is null) return;
                foreach (var a in x.Args) Walk(a);
            }
            Walk(e);
            return e;
        }

        MergeResult TryMerge(IfNode node, IrExpr guard)
        {
            try { return TryMergeCore(node, guard) ? MergeResult.Merged : MergeResult.NotMergeable; }
            catch (TooLarge) { return MergeResult.TooLarge; }
        }

        bool TryMergeCore(IfNode node, IrExpr guard)
        {
            if (!Mergeable(node) || !NameSubstitutionIsExact(node)) return false;
            var order = new List<string>();
            var conds = node.Branches.Select(b => b.Cond).ToList();
            var guards = BranchGuards(conds);
            var runs = node.Branches.Select(b => b.Body).Append(node.Else ?? Array.Empty<Node>())
                .Select(b => Exec(b, new Dictionary<string, IrExpr>(C), order)).ToList();
            var items = new List<(string Kind, string Target, IrExpr Guard, IrExpr? Value)>();
            foreach (var t in order)
            {
                var values = runs.Select(r => r.Assigned.Contains(t) ? r.Env[t] : null).ToList();
                if (values.All(v => v is not null))
                    items.Add((IrStatementKinds.Assign, t, guard, Bounded(Choose(t, conds, guards, values.Select(v => v!).ToList()))));
                else if (values.Where(v => v is not null).All(v => BoolConstOf(v!) is not null))
                {
                    for (var i = 0; i < values.Count; i++)
                        if (values[i] is { } v)
                            items.Add((BoolConstOf(v) == true ? IrStatementKinds.Set : IrStatementKinds.Reset, t, IrExpr.And([guard, guards[i]]), null));
                }
                else items.Add((IrStatementKinds.Assign, t, guard, Bounded(Choose(t, conds, guards, values.Select(v => v ?? IrExpr.Var(t)).ToList()))));
            }
            // 合并后的值都以进入 IF 时的值表示；写出的语句若读到前面语句刚写的目标，就会误读新值 → 放弃合并
            for (var i = 0; i < items.Count; i++)
            {
                var reads = items[i].Guard.Vars().Concat(items[i].Value?.Vars() ?? Enumerable.Empty<string>()).ToArray();
                if (items.Take(i).Any(prev => reads.Any(r => ProjectWrites.Overlaps(prev.Target, r)))) return false;
            }
            foreach (var item in items) Add(item.Kind, item.Target, item.Guard, item.Value);
            return true;
        }
    }
}
