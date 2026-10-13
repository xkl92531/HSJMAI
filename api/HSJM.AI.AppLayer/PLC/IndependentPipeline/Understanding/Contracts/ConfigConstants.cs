#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>期望状态报警（K4）配置输入的常量解析，只用于该种类（用户 2026-10-02 对裁定 F8 的例外）。只会解析不出，不会解析错：
/// ① 全局变量在全项目（写入索引含 FC）的全部写入都是同一个常数 → 该常数。每条写入须是无条件赋值、目标就是它本身（不是父结构/成员），
///    所在块每个扫描都执行（程序循环组织块或从它逐层无条件调用到、无跳转/返回的块，与 ConstantBits 同口径，审查 M-2）；值是布尔常数，或是又能按本规则解析的全局变量（深度 ≤4、防环）；
///    有任何不确定写入、文字段提到它 → 解析失败；
/// ② 全项目从不写入（无写入、无不确定写入、文字段不提到）的全局 DB 成员 → 取 DB 起始值：成员自己的起始值，数组元素取对应下标的
///    元素起始值；导出里没写起始值的 Bool 取类型默认值 false；只支持一层数组下标，取不到（非全局 DB、成员找不到、多维、不是 Bool）就失败。
/// ③ 未接线的配置输入取接口初值，在 CallSiteSubstitution 里处理。
/// 另：常量位（ConstantBits，含系统存储位特例）直接取其值——上面两条对「:= 恒真位」这类写入就是靠它解析的。
/// 已知代价：程序外（HMI/通讯）改写不建模；文字段「提到」按 ProjectWrites.MentionedInText 口径（带引号的 DB 名按去引号后的成员路径判，
/// 块移动类指令与带参数的调用里的数组元素按整个数组算）。</summary>
public sealed class ConfigConstants
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    const int MaxDepth = 4;

    readonly ProjectWrites writes;
    readonly Dictionary<(long, string), (IrUnit Unit, IrStatement Statement)> statements = new();
    readonly HashSet<string> everyScan;
    readonly IReadOnlyDictionary<string, string> dbInterfaces;
    readonly Dictionary<string, bool?> cache = new(C);
    readonly IReadOnlyDictionary<string, bool> constantBits;

    /// <param name="units">全项目程序段（含 FC）。</param>
    /// <param name="isProgramCycleBlock">程序循环组织块（按导出事件类型 ProgramCycle 判断）。</param>
    /// <param name="dbInterfaces">全局 DB 名 → 导出里的接口 XML（含 Sections）；没有就传空。</param>
    /// <param name="constantBits">常量位（ConstantBits）。</param>
    public ConfigConstants(IEnumerable<IrUnit> units, ProjectWrites writes, Func<string, bool> isProgramCycleBlock,
        IReadOnlyDictionary<string, string>? dbInterfaces, IReadOnlyDictionary<string, bool>? constantBits = null)
    {
        this.constantBits = constantBits ?? new Dictionary<string, bool>(C);
        this.writes = writes;
        this.dbInterfaces = dbInterfaces ?? new Dictionary<string, string>(C);
        var all = units.ToArray();
        foreach (var u in all)
            foreach (var s in u.Statements)
                statements.TryAdd((u.UnitId, s.Id), (u, s));
        everyScan = ConstantBits.EveryScanBlocks(all, isProgramCycleBlock, this.constantBits);
    }

    /// <summary>全局变量在全项目恒为的布尔常数；解析不出为 null。</summary>
    public bool? Resolve(string name) => Resolve(name, 0, new HashSet<string>(C));

    bool? Resolve(string name, int depth, HashSet<string> visiting)
    {
        var key = name.Trim();
        if (key.Length == 0 || depth > MaxDepth || !visiting.Add(key)) return null;
        try
        {
            if (depth == 0 && cache.TryGetValue(key, out var hit)) return hit;
            var result = Compute(key, depth, visiting);
            if (depth == 0) cache[key] = result;
            return result;
        }
        finally { visiting.Remove(key); }
    }

    bool? Compute(string name, int depth, HashSet<string> visiting)
    {
        if (constantBits.TryGetValue(name, out var bit)) return bit;
        if (writes.HasUncertainWriter(name) || writes.MentionedInText(name)) return null;
        var ws = writes.Writers(name);
        if (ws.Count == 0) return DbStartValue(name);
        bool? value = null;
        foreach (var w in ws)
        {
            if (!statements.TryGetValue(w, out var hit)) return null;
            var (unit, s) = hit;
            if (s.Kind != IrStatementKinds.Assign || !CallSiteSubstitution.IsTrue(s.Guard) || s.Value is null || s.SideWrites is { Count: > 0 }) return null;
            if (!SameName(s.Target, name)) return null;
            var block = unit.BlockName.Trim();
            if (!everyScan.Contains(block)) return null;
            bool? v = IrBlock.ExpandNodesIn(unit, s.Value) switch
            {
                { Kind: IrKinds.Const, Name: var k } => BoolText(k),
                { Kind: IrKinds.Var, Name: { } other } => Resolve(other, depth + 1, visiting),
                _ => null
            };
            if (v is null || value is not null && value != v) return null;
            value = v;
        }
        return value;
    }

    static bool SameName(string? a, string b) =>
        a is not null && Norm(IrBlock.Segments(a.Trim())).SequenceEqual(Norm(IrBlock.Segments(b.Trim())), C);

    static IEnumerable<string> Norm(IEnumerable<string> segs) =>
        segs.Select(s => s.StartsWith('[') ? new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()) : s.Trim().Trim('"'));

    static bool? BoolText(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" => true, "FALSE" or "0" => false, _ => null
    };

    bool? DbStartValue(string name)
    {
        var segs = IrBlock.Segments(name).Select(s => s.Trim()).ToArray();
        if (segs.Length < 2 || segs[0].StartsWith('[')) return null;
        if (!dbInterfaces.TryGetValue(segs[0].Trim('"'), out var xml)) return null;
        return StartValue(xml, segs[1..]);
    }

    static bool Is(XElement e, string local) => e.Name.LocalName == local;

    static IEnumerable<XElement> ChildMembers(XElement e) =>
        e.Elements().Where(x => Is(x, "Member"))
            .Concat(e.Elements().Where(x => Is(x, "Sections")).Elements().Where(x => Is(x, "Section")).Elements().Where(x => Is(x, "Member")));

    /// <summary>接口 XML 里成员路径（不含 DB 名）的布尔起始值；见类说明 ②。</summary>
    internal static bool? StartValue(string interfaceXml, IReadOnlyList<string> path)
    {
        XElement root;
        try { root = XElement.Parse(interfaceXml); } catch (XmlException) { return null; }
        var sections = Is(root, "Sections") ? root : root.Elements().FirstOrDefault(e => Is(e, "Sections"));
        if (sections is null || path.Count == 0) return null;
        IEnumerable<XElement> level = sections.Elements().Where(e => Is(e, "Section")).Elements().Where(e => Is(e, "Member"));
        XElement? member = null, array = null;
        string? index = null;
        foreach (var seg in path)
        {
            if (seg.StartsWith('['))
            {
                if (member is null || index is not null || !((string?)member.Attribute("Datatype") ?? "").StartsWith("Array[", StringComparison.OrdinalIgnoreCase))
                    return null;
                var inner = new string(seg.Trim('[', ']').Where(ch => !char.IsWhiteSpace(ch)).ToArray());
                if (!int.TryParse(inner, out _)) return null;
                (array, index) = (member, inner);
                level = ChildMembers(member);
                continue;
            }
            var bare = seg.Trim('"');
            var found = level.Where(m => C.Equals((string?)m.Attribute("Name"), bare)).ToArray();
            if (found.Length != 1) return null;
            member = found[0];
            level = ChildMembers(member);
        }
        if (member is null) return null;
        static string? Own(XElement m) => m.Elements().FirstOrDefault(e => Is(e, "StartValue"))?.Value;
        static IEnumerable<XElement> Subs(XElement m) =>
            m.Elements().Where(e => Is(e, "Subelement") && e.Elements().Any(x => Is(x, "StartValue")));
        string? Sub(XElement m) => Subs(m).FirstOrDefault(e => (string?)e.Attribute("Path") == index)?.Elements().First(x => Is(x, "StartValue")).Value;
        bool leafIsElement = index is not null && path[^1].StartsWith('[');   // Array of Bool 的元素
        var type = (string?)member.Attribute("Datatype") ?? "";
        if (leafIsElement ? !type.EndsWith(" of Bool", StringComparison.OrdinalIgnoreCase) : !C.Equals(type, "Bool")) return null;
        string? text;
        if (index is null) text = Own(member);
        else if (leafIsElement) text = Sub(member) ?? (Subs(member).Any(e => ((string?)e.Attribute("Path"))?.Contains(',') == true) ? "?" : Own(member));
        else
        {
            // 结构数组的成员：元素起始值写在成员自己的 Subelement 上；数组本身带元素起始值的写法认不出 → 失败
            if (Subs(array!).Any()) return null;
            text = Sub(member) ?? Own(member);
        }
        if (text == "?") return null;
        return text is null ? false : BoolText(text);
    }
}
