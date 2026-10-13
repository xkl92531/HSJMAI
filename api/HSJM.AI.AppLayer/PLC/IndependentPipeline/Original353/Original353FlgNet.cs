#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>
/// 严格的 FlgNet 网络读取器（反馈组与 FB 契约推断共用）：只沿真实驱动端口反向追溯，
/// 遇到不认识的部件返回 null，绝不猜测；不会把同一根线上的其它输入端点当作来源。
/// </summary>
internal sealed class Original353FlgNet
{
    /// <summary>定时器透传节点：Kind="Timer"，Name=部件名（TON/TOF/TP/TONR），Children=[IN 表达式]。</summary>
    public const string TimerKind = "Timer";
    static readonly HashSet<string> TimerParts = new(StringComparer.OrdinalIgnoreCase) { "TON", "TOF", "TP", "TONR" };

    internal static string A(XElement e, string name) => (string?)e.Attribute(name) ?? "";
    internal static bool Is(XElement e, string name) => e.Name.LocalName == name;
    internal static LogicTerm Not(LogicTerm value) => new("Not", null, [value]);
    internal static LogicTerm Join(string kind, IEnumerable<LogicTerm> values)
    {
        var items = values.ToArray(); return items.Length == 1 ? items[0] : new(kind, null, items);
    }

    internal static string? Symbol(XElement access)
    {
        if (A(access, "Scope") == "LiteralConstant") return null;
        var symbols = access.Elements().Where(e => Is(e, "Symbol")).Take(2).ToArray();
        if (symbols.Length != 1) return null;
        var components = symbols[0].Elements().Where(e => Is(e, "Component")).ToArray();
        if (components is not { Length: > 0 }) return null;
        var names = new List<string>();
        foreach (var part in components)
        {
            var name = A(part, "Name"); if (name.Length == 0) return null;
            var index = ExactIndex(part);
            if (index == null) return null;
            names.Add(name + index);
        }
        return string.Join('.', names);
    }

    /// <summary>
    /// 组件能精确表示时返回其下标后缀（无下标为 ""）；位切片、引用解引用等修饰、多维 / 变量 / 嵌套下标返回 null。
    /// 数组下标必须是组件的唯一直接子 Access 且为整数字面量（DB.Arr[Idx[2]] 不能读成 DB.Arr[2]）。
    /// </summary>
    static string? ExactIndex(XElement component)
    {
        if (component.Attributes().Any(a => a.Name.LocalName.Contains("Slice", StringComparison.OrdinalIgnoreCase))) return null;
        var modifier = A(component, "AccessModifier");
        if (modifier == "Array")
        {
            // 必须是全函数：多维 / 表达式下标返回 null，绝不抛出
            var children = component.Elements().Take(2).ToArray();
            if (children.Length != 1 || !Is(children[0], "Access") || A(children[0], "Scope") != "LiteralConstant") return null;
            var values = children[0].Descendants().Where(e => Is(e, "ConstantValue")).Take(2).ToArray();
            return values.Length == 1 && int.TryParse(values[0].Value, out _) ? "[" + values[0].Value + "]" : null;
        }
        return modifier is "" or "None" && !component.HasElements ? "" : null;
    }

    /// <summary>写入位置无法确定（引用解引用、无法换算的绝对地址等）：与任何变量都可能重叠。</summary>
    public const string UnknownLocation = "\u0000UnknownLocation";

    /// <summary>
    /// 访问所覆盖的存储位置（判定「谁写了什么」用，保守取大）：可精确表示的符号 → 符号本身；
    /// 带位切片 / 非字面量下标的符号 → 能确定的父级前缀（覆盖实际被访问的部分）；
    /// 无符号的绝对地址 → 等价的 %I/%Q/%M 地址；其余 → <see cref="UnknownLocation"/>。
    /// </summary>
    internal static string Location(XElement access)
    {
        if (Symbol(access) is { } exact) return exact;
        var symbols = access.Elements().Where(e => Is(e, "Symbol")).Take(2).ToArray();
        if (symbols.Length > 1) return UnknownLocation;
        if (symbols.Length == 1)
        {
            var names = new List<string>();
            foreach (var part in symbols[0].Elements().Where(e => Is(e, "Component")))
            {
                var name = A(part, "Name");
                if (name.Length == 0) return UnknownLocation;
                if (ExactIndex(part) is { } index) { names.Add(name + index); continue; }
                // 引用解引用等：实际位置取决于运行时指针
                if (A(part, "AccessModifier") is not ("" or "None" or "Array")) return UnknownLocation;
                names.Add(name);
                break;
            }
            return names.Count == 0 ? UnknownLocation : string.Join('.', names);
        }
        var addresses = access.Elements().Where(e => Is(e, "Address")).Take(2).ToArray();
        if (addresses.Length != 1) return UnknownLocation;
        var area = A(addresses[0], "Area") switch { "Input" => "I", "Output" => "Q", "Memory" => "M", _ => null };
        if (area == null || !int.TryParse(A(addresses[0], "BitOffset"), out var offset) || offset < 0) return UnknownLocation;
        var width = A(addresses[0], "Type").ToUpperInvariant() switch
        {
            "BOOL" => "X", "BYTE" or "SINT" or "USINT" or "CHAR" => "B", "WORD" or "INT" or "UINT" => "W",
            "DWORD" or "DINT" or "UDINT" or "REAL" => "D", _ => null
        };
        if (width == null) return UnknownLocation;
        if (width == "X") return $"%{area}{offset / 8}.{offset % 8}";
        return offset % 8 == 0 ? $"%{area}{width}{offset / 8}" : UnknownLocation;
    }

    /// <summary>
    /// 网络 / 程序段是否含改变执行顺序的控制流：跳转 / 返回 / 标号部件，或 SCL/STL 中的 RETURN / GOTO / EXIT / 跳转指令。
    /// 有控制流时「网络依次、每周期都执行」的前提不成立。注释里的文字不算。
    /// </summary>
    internal static bool HasControlFlow(XElement root)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            var local = e.Name.LocalName;
            if (local == "Part" && JumpParts.Contains(A(e, "Name"))) return true;
            if (local is "Label" or "LabelDeclaration" || (local == "Access" && A(e, "Scope") == "Label")) return true;
            if (local is "Token" or "Text" || local.EndsWith("Token", StringComparison.Ordinal))
            {
                if (e.Ancestors().Any(x => x.Name.LocalName.Contains("Comment", StringComparison.OrdinalIgnoreCase))) continue;
                var text = A(e, "Text") + " " + (e.HasElements ? "" : e.Value);
                if (text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Any(ControlKeywords.Contains)) return true;
            }
        }
        return false;
    }

    static readonly HashSet<string> JumpParts = new(StringComparer.OrdinalIgnoreCase)
        { "Jump", "JumpNot", "Return", "Label", "JumpList", "SwitchJump" };
    static readonly HashSet<string> ControlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETURN", "GOTO", "EXIT", "JMP", "JMPN", "JMP_LIST",
        "JU", "JC", "JCN", "JCB", "JNB", "JBI", "JNBI", "JO", "JOS", "JZ", "JN", "JP", "JM", "JPZ", "JMZ", "JUO", "JL",
        "LOOP", "BE", "BEC", "BEU",
    };
    static readonly char[] WordSeparators = " \t\r\n;:,.()[]=<>+-*/&|!\"'#%{}".ToCharArray();

    /// <summary>部件的该端口带取反标记（&lt;Negated Name="port"/&gt;，Part 在直接子，Call 可能在任意层）。</summary>
    static bool IsNegated(XElement part, string port) =>
        part.Descendants().Any(e => Is(e, "Negated") && A(e, "Name") == port);

    internal static bool BoolTag(PipelineSnapshot source, string name, string area)
    {
        var tags = source.Tags.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tags.Length != 1 || !tags[0].DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase)) return false;
        var physical = BusinessMemoryAliases.ParseAddress(tags[0].Address, tags[0].DataType);
        return physical.Status == "Known" && physical.Range?.Area == area.TrimStart('%') && physical.Range.BitLength == 1;
    }

    static readonly HashSet<string> ReadPorts = new(StringComparer.Ordinal)
        { "in", "IN", "PT", "PV", "CU", "CD", "LD", "R", "S", "R1", "S1", "pre", "en", "G", "MIN", "VAL", "MAX" };

    /// <summary>
    /// 读端口白名单（反向写成黑名单会漏掉 Sr/Rs 的 operand、PCoil/NCoil、边沿存储位 bit、CTUD 的 QU/QD 等写端口）：
    /// 触点类 operand 与白名单端口为读，其余一律视为写（保守）。Call 按接口 Section 判定。
    /// </summary>
    internal static bool IsReadPort(XElement part, string port)
    {
        if (Is(part, "Part"))
        {
            if (A(part, "Name") is "Contact" or "PContact" or "NContact" && port == "operand") return true;
            // in<数字> / IN<数字>（含 IN0）：O/A 块、SEL、MUX、CONCAT 等的输入
            return ReadPorts.Contains(port) || (port.Length > 2 && (port.StartsWith("in", StringComparison.Ordinal)
                || port.StartsWith("IN", StringComparison.Ordinal)) && port[2..].All(char.IsAsciiDigit));
        }
        if (Is(part, "Call"))
            return !part.Descendants().Any(e => Is(e, "Parameter") && A(e, "Name") == port && A(e, "Section") is "Output" or "InOut");
        return false;
    }

    readonly Dictionary<string, XElement> parts;
    readonly XElement[] wires;
    readonly bool timerPassthrough;
    readonly HashSet<(string, string)> visiting = [];

    public Original353FlgNet(XElement xml, bool timerPassthrough = false)
    {
        parts = xml.Elements().Single(e => Is(e, "Parts")).Elements().Where(e => A(e, "UId") != "").ToDictionary(e => A(e, "UId"));
        wires = xml.Elements().Single(e => Is(e, "Wires")).Elements().Where(e => Is(e, "Wire")).ToArray();
        this.timerPassthrough = timerPassthrough;
    }

    public IReadOnlyList<XElement> Calls => parts.Values.Where(e => Is(e, "Call")).ToArray();
    public IReadOnlyList<XElement> Parts => parts.Values.Where(e => Is(e, "Part")).ToArray();
    public XElement? PartById(string uid) => parts.GetValueOrDefault(uid);

    public LogicTerm? Pin(string id, string port)
    {
        // 取反的端口收到的是驱动的反值：不认（触点 operand 的取反由 Output 显式处理）
        if (parts.TryGetValue(id, out var self) && IsNegated(self, port)
            && !(Is(self, "Part") && A(self, "Name") == "Contact" && port == "operand")) return null;
        if (!visiting.Add((id, port))) return null;
        try
        {
            var matches = wires.Where(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == id && A(e, "Name") == port)).ToArray();
            if (matches.Length != 1) return null;
            var drivers = new List<LogicTerm>();
            foreach (var connector in matches[0].Elements())
            {
                if (Is(connector, "Powerrail")) drivers.Add(new("Constant", "true"));
                else if (Is(connector, "IdentCon") && parts.TryGetValue(A(connector, "UId"), out var access) && Is(access, "Access"))
                {
                    if (A(access, "Scope") == "LiteralConstant")
                    {
                        var literals = access.Descendants().Where(e => Is(e, "ConstantValue")).Take(2).ToArray();
                        var literal = literals.Length == 1 ? literals[0].Value : null;
                        if (literal is "TRUE" or "true") drivers.Add(new("Constant", "true"));
                        else if (literal is "FALSE" or "false") drivers.Add(new("Constant", "false"));
                        else return null;
                    }
                    else if (Symbol(access) is { } name) drivers.Add(LogicTerm.Signal(name)); else return null;
                }
                else if (Is(connector, "NameCon") && (A(connector, "UId") != id || A(connector, "Name") != port))
                {
                    var driver = A(connector, "UId");
                    if (timerPassthrough && parts.TryGetValue(driver, out var timer) && Is(timer, "Part")
                        && TimerParts.Contains(A(timer, "Name")) && A(connector, "Name") == "Q")
                    {
                        var input = Pin(driver, "IN");
                        if (input == null) return null;
                        drivers.Add(new(TimerKind, A(timer, "Name"), [input]));
                        continue;
                    }
                    if (!parts.TryGetValue(driver, out var part) || !Is(part, "Part") || A(connector, "Name") != "out") continue;
                    var value = Output(driver, part);
                    if (value == null) return null; drivers.Add(value);
                }
            }
            return drivers.Count == 1 ? drivers[0] : null;
        }
        finally { visiting.Remove((id, port)); }
    }

    LogicTerm? Output(string id, XElement part)
    {
        var kind = A(part, "Name");
        if (kind == "Contact")
        {
            var incoming = Pin(id, "in"); var operand = Pin(id, "operand"); if (incoming == null || operand == null) return null;
            if (part.Elements().Any(e => Is(e, "Negated") && A(e, "Name") == "operand")) operand = Not(operand);
            return Join("And", [incoming, operand]);
        }
        if (kind is "A" or "O")
        {
            if (part.Elements().Any(e => Is(e, "Negated") && A(e, "Name").StartsWith("in", StringComparison.OrdinalIgnoreCase))) return null;
            var cards = part.Elements().Where(e => Is(e, "TemplateValue") && A(e, "Name") == "Card").Take(2).ToArray();
            var card = cards.Length == 1 ? cards[0].Value : null;
            if (!int.TryParse(card, out var count) || count is < 2 or > 16) return null;
            var values = new List<LogicTerm>(); for (var i = 1; i <= count; i++)
            {
                var value = Pin(id, "in" + i); if (value == null) return null; values.Add(value);
            }
            return Join(kind == "A" ? "And" : "Or", values);
        }
        if (kind == "Not" && Pin(id, "in") is { } negated) return Not(negated);
        return null;
    }

    public string? BoundOutput(string callId, string outputPin)
    {
        // 取反的输出引脚：接线变量拿到的是输出的反值，视为未接线
        if (parts.TryGetValue(callId, out var call) && IsNegated(call, outputPin)) return null;
        var matches = wires.Where(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == callId && A(e, "Name") == outputPin)).ToArray();
        if (matches.Length != 1) return null;
        var wire = matches[0];
        var drivers = wire.Elements().Where(e => Is(e, "NameCon") || Is(e, "Powerrail")).ToArray();
        if (drivers.Length != 1) return null;
        var targets = wire.Elements().Where(e => Is(e, "IdentCon")).Select(e => parts.GetValueOrDefault(A(e, "UId")))
            .Where(e => e != null && Is(e, "Access") && A(e, "Scope") == "GlobalVariable").Select(e => Symbol(e!)).Distinct().ToArray();
        return targets.Length == 1 ? targets[0] : null;
    }

    public bool HasOtherWrite(string target, string callId, string outputPin)
    {
        var accesses = parts.Where(p => Is(p.Value, "Access") && A(p.Value, "Scope") == "GlobalVariable" && Symbol(p.Value) == target).Select(p => p.Key).ToHashSet();
        foreach (var wire in wires.Where(w => w.Elements().Any(e => Is(e, "IdentCon") && accesses.Contains(A(e, "UId")))))
        {
            foreach (var connector in wire.Elements().Where(e => Is(e, "NameCon")))
            {
                var uid = A(connector, "UId"); var pin = A(connector, "Name");
                if (uid == callId && pin == outputPin) continue;
                if (!parts.TryGetValue(uid, out var part)) return true;
                if (Is(part, "Part") && A(part, "Name") is "Coil" or "SCoil" or "RCoil" && pin == "operand") return true;
                if (Is(part, "Call") && part.Descendants().Any(e => Is(e, "Parameter") && A(e, "Name") == pin && A(e, "Section") == "Output")) return true;
            }
        }
        return false;
    }

    /// <summary>线圈 in 端口唯一的驱动是某个 TON 的 Q 时，返回该 TON 的 PT 接线：字面量/带类型常量返回其原文（如 T#2S），
    /// 变量返回符号；其它（不是 TON、PT 未接或不唯一、表达式）返回 null。</summary>
    public string? SetTimerPreset(string coilId)
    {
        var into = wires.Where(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == coilId && A(e, "Name") == "in")).ToArray();
        if (into.Length != 1) return null;
        var drivers = into[0].Elements().Where(e => !(Is(e, "NameCon") && A(e, "UId") == coilId)).ToArray();
        if (drivers is not [var q] || !Is(q, "NameCon") || A(q, "Name") != "Q"
            || !parts.TryGetValue(A(q, "UId"), out var timer) || !Is(timer, "Part") || A(timer, "Name") != "TON") return null;
        var timerId = A(timer, "UId");
        var pt = wires.Where(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == timerId && A(e, "Name") == "PT")).ToArray();
        if (pt.Length != 1) return null;
        var sources = pt[0].Elements().Where(e => !(Is(e, "NameCon") && A(e, "UId") == timerId)).ToArray();
        if (sources is not [var source] || !Is(source, "IdentCon") || !parts.TryGetValue(A(source, "UId"), out var access)
            || !Is(access, "Access")) return null;
        // 博图导出里带类型的常量（T#2S）是 TypedConstant，不带类型的是 LiteralConstant
        if (A(access, "Scope") is "LiteralConstant" or "TypedConstant")
        {
            var values = access.Descendants().Where(e => Is(e, "ConstantValue")).Take(2).ToArray();
            return values.Length == 1 && values[0].Value.Trim().Length > 0 ? values[0].Value.Trim() : null;
        }
        return Symbol(access);
    }

    /// <summary>部件 operand 端口所接的唯一变量符号（线圈目标）；不唯一或未接返回 null。</summary>
    public string? OperandSymbol(string partId)
    {
        var matches = wires.Where(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == partId && A(e, "Name") == "operand")).ToArray();
        if (matches.Length != 1) return null;
        var symbols = matches[0].Elements().Where(e => Is(e, "IdentCon")).Select(e => parts.GetValueOrDefault(A(e, "UId")))
            .Where(e => e != null && Is(e, "Access")).Select(e => Symbol(e!)).OfType<string>().Distinct().ToArray();
        return symbols.Length == 1 ? symbols[0] : null;
    }

    /// <summary>部件 operand 端口所接唯一变量的 Scope；不唯一或未接返回 null。</summary>
    public string? OperandScope(string partId)
    {
        var matches = wires.Where(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == partId && A(e, "Name") == "operand")).ToArray();
        if (matches.Length != 1) return null;
        var scopes = matches[0].Elements().Where(e => Is(e, "IdentCon")).Select(e => parts.GetValueOrDefault(A(e, "UId")))
            .Where(e => e != null && Is(e, "Access")).Select(e => A(e!, "Scope")).Distinct(StringComparer.Ordinal).ToArray();
        return scopes.Length == 1 ? scopes[0] : null;
    }

    /// <summary>是否有连线接到该部件端口。</summary>
    public bool IsWired(string id, string port) =>
        wires.Any(w => w.Elements().Any(e => Is(e, "NameCon") && A(e, "UId") == id && A(e, "Name") == port));

    /// <summary>本网络至少有一个该符号的 Access，且全部是 GlobalVariable。</summary>
    public bool IsGlobalSymbol(string symbol)
    {
        var scopes = parts.Values.Where(e => Is(e, "Access") && string.Equals(Symbol(e), symbol, StringComparison.OrdinalIgnoreCase))
            .Select(e => A(e, "Scope")).ToArray();
        return scopes.Length > 0 && scopes.All(s => s == "GlobalVariable");
    }

    /// <summary>
    /// 本网络对全局变量的写：GlobalVariable Access 与同线非读端口的组合（线圈 operand、Call 的 Output/InOut、
    /// 其它部件的非读端口；不认识的部件也算写，保守）。写入位置按 <see cref="Location"/> 取（切片 / 变量下标取父级，
    /// 绝对地址换算成 %地址，无法确定的记 <see cref="UnknownLocation"/>）。
    /// </summary>
    public IEnumerable<(string Symbol, string PartId, string Port)> GlobalWrites()
    {
        foreach (var wire in wires)
        {
            var symbols = wire.Elements().Where(e => Is(e, "IdentCon")).Select(e => parts.GetValueOrDefault(A(e, "UId")))
                .Where(e => e != null && Is(e, "Access") && A(e, "Scope") == "GlobalVariable").Select(e => Location(e!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (symbols.Length == 0) continue;
            foreach (var connector in wire.Elements().Where(e => Is(e, "NameCon")))
            {
                var uid = A(connector, "UId"); var port = A(connector, "Name");
                if (parts.TryGetValue(uid, out var part) && IsReadPort(part, port)) continue;
                foreach (var symbol in symbols) yield return (symbol, uid, port);
            }
        }
    }

    /// <summary>
    /// 每根连线上「变量 × 同线的 NameCon 端口」组合，用于判定变量被哪些端口写/读。常量与跳转标号不算变量；
    /// 不能精确表示的访问按 <see cref="Location"/> 给出覆盖它的位置（父级前缀或 <see cref="UnknownLocation"/>）。
    /// </summary>
    public IEnumerable<(string Symbol, XElement? Part, string Port)> AccessPins()
    {
        foreach (var wire in wires)
        {
            var symbols = wire.Elements().Where(e => Is(e, "IdentCon")).Select(e => parts.GetValueOrDefault(A(e, "UId")))
                .Where(e => e != null && Is(e, "Access") && !A(e, "Scope").EndsWith("Constant", StringComparison.Ordinal)
                    && A(e, "Scope") != "Label")
                .Select(e => Location(e!)).ToArray();
            if (symbols.Length == 0) continue;
            foreach (var connector in wire.Elements().Where(e => Is(e, "NameCon")))
                foreach (var symbol in symbols)
                    yield return (symbol, parts.GetValueOrDefault(A(connector, "UId")), A(connector, "Name"));
        }
    }
}
