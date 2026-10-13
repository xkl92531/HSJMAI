#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record BlockWrite(IrStatement Statement, IrUnit Unit, IrExpr? Value);

/// <summary>一个块全部程序段的 IR 汇总。只描述程序结构，不做任何按名字的推断。变量名比较忽略大小写（博图符号不区分大小写）。</summary>
public sealed class IrBlock
{
    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    readonly Dictionary<string, List<BlockWrite>> writers = new(Cmp);
    readonly Dictionary<string, int> order = new(StringComparer.Ordinal);

    public string Name { get; }
    public IReadOnlyList<IrUnit> Units { get; }
    public IReadOnlyList<IfaceMember> Interface { get; }
    public IReadOnlyDictionary<string, IrStatement> StatementsById { get; }

    IrBlock(string name, IReadOnlyList<IrUnit> units, IReadOnlyList<IfaceMember> iface)
    {
        Name = name; Units = units; Interface = iface;
        var byId = new Dictionary<string, IrStatement>(StringComparer.Ordinal);
        foreach (var unit in units)
            foreach (var s in unit.Statements)
            {
                if (byId.TryAdd(s.Id, s)) order[s.Id] = order.Count;
                // 与 ProjectWrites 同一套写入目标：赋值/置位/复位、调用引脚、调用背景实例、SideWrites（指令盒顺带写入）
                foreach (var w in ProjectWrites.StatementWrites(s))
                {
                    if (string.IsNullOrWhiteSpace(w.Target)) continue;
                    var write = new BlockWrite(s, unit,
                        w.IsStatementTarget && s.Kind == IrStatementKinds.Assign && s.Value is not null ? ExpandNodes(unit, s.Value) : null);
                    Add(w.Target, write);
                    Classify(w.Target.Trim(), write, w.Spread);
                }
            }
        StatementsById = byId;
    }

    void Add(string variable, BlockWrite write)
    {
        if (!writers.TryGetValue(variable, out var list)) writers[variable] = list = [];
        list.Add(write);
    }

    /// <summary>可能写到某些名字、但按名字比较认不出的写入：块写放宽（以「[*]」/根的形式已进 writers）之外的
    /// 绝对地址与区写入（Areas）、局部区写入（Local）、区未知的目标与「?」（Any）。</summary>
    sealed record UncertainWrite(string Target, BlockWrite Write, IReadOnlyList<PlcAddress.Area>? Areas, bool Local);
    readonly List<UncertainWrite> uncertain = [];

    void Classify(string target, BlockWrite write, ProjectWrites.Spread spread)
    {
        if (target.Length == 0) return;
        // 「?」：连线指向不存在的访问（XML 损坏），不知道写到了哪里 → 本块可能写任何量（ProjectWrites 仍按旧裁定不算写入）
        if (target == "?") { uncertain.Add(new(target, write, null, false)); return; }
        var areas = PlcAddress.Resolver.Empty.Resolve(target);
        if (areas.Count > 0) { uncertain.Add(new(target, write, areas, false)); return; }
        if (PlcAddress.IsLocalArea(target)) { uncertain.Add(new(target, write, null, true)); return; }
        if (target.StartsWith('〔') || PlcAddress.IsUnknownTarget(WithoutQuotedOrIndexSpaces(target)))
        { uncertain.Add(new(target, write, null, false)); return; }
        if (ProjectWrites.Widen(Segments(target), spread) is { } widened)
            Add(Join(widened), new BlockWrite(write.Statement, write.Unit, null));
    }

    /// <summary>去掉引号内与方括号内的空白（「"My DB".x」「Arr[ 4 ]」是正常写法，不算认不出的目标）。</summary>
    static string WithoutQuotedOrIndexSpaces(string target)
    {
        var sb = new System.Text.StringBuilder(target.Length);
        var depth = 0; var quoted = false;
        foreach (var ch in target)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == '[') depth++;
            else if (!quoted && ch == ']' && depth > 0) depth--;
            if ((quoted || depth > 0) && char.IsWhiteSpace(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static string Join(IReadOnlyList<string> segs)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            if (i > 0 && !segs[i].StartsWith('[')) sb.Append('.');
            sb.Append(segs[i]);
        }
        return sb.ToString();
    }

    /// <summary>按名字比较认不出、但可能写到 name 的写入（与 ProjectWrites 按区收窄同一口径，只是没有点表）：
    /// 区未知的目标与「?」（连线指向不存在的访问）→ 任何名字；I/Q/M/DB 区（含绝对地址、间接、指针常数）→ 绝对地址按区与范围比较，符号全局名一律算
    /// （没有点表、不知道它在哪个区）；本块 Static 只被 DB 区（背景 DB）命中；Input/Output 形参被 DB 区与局部区命中
    /// （FB 背景 DB；FC 形参经局部区指针传递）；InOut 被一切命中；Temp 只被局部区命中。
    /// 注意：区写入对 Input/Output 形参的收窄只对 FB 成立——FC 的形参按引用可能指向 I/Q/M 区，IrBlock 不认块类型，
    /// 把 IrBlock 用于 FC 时须由调用方兜底（如 CallOutputDefinition 对 FC 写任何真全局量即判无法解析）。
    /// 符号名之间的同址别名（没有点表）这里也认不出，需要的调用方自行保守处理。
    /// 目标与 name 文字相同的写入不在此列（已是 Writers(name)）。</summary>
    public IReadOnlyList<BlockWrite> UncertainWriters(string name)
    {
        if (uncertain.Count == 0 || string.IsNullOrWhiteSpace(name)) return [];
        var n = name.Trim();
        var areas = PlcAddress.Resolver.Empty.Resolve(n);
        var member = Interface.FirstOrDefault(m => Cmp.Equals(m.Name, RootOf(n)));
        var result = new List<BlockWrite>();
        foreach (var u in uncertain)
        {
            if (Cmp.Equals(u.Target, n)) continue;
            bool hit;
            if (u.Areas is null && !u.Local) hit = true;
            else if (areas.Count > 0) hit = u.Areas is not null && u.Areas.Any(a => areas.Any(b => a.Overlaps(b)));
            else if (member is null) hit = !u.Local;   // 符号全局名
            else hit = member.Section switch
            {
                "Temp" => u.Local,
                "Static" => u.Areas is not null && u.Areas.Any(a => a.IsDb),
                "Input" or "Output" => u.Local || u.Areas is not null && u.Areas.Any(a => a.IsDb),
                _ => true   // InOut（引用调用方变量）与其它接口成员
            };
            if (hit) result.Add(u.Write);
        }
        return result;
    }

    /// <summary>可能写到 name 的全部写入：同名、重叠目标（OverlappingTargets）、认不出的写入（UncertainWriters）。</summary>
    public IReadOnlyList<BlockWrite> WritesTouching(string name) =>
        Writers(name).Concat(OverlappingTargets(name).SelectMany(Writers)).Concat(UncertainWriters(name)).Distinct().ToArray();

    /// <summary>全部写入目标（含块写放宽出来的「[*]」/根）。</summary>
    public IEnumerable<string> WriteTargets => writers.Keys;

    public static IrBlock FromUnits(string name, IReadOnlyList<IrUnit> units, IReadOnlyList<IfaceMember> iface) => new(name, units, iface);

    public static IrBlock Load(PipelineSnapshot source, string blockName)
    {
        var units = source.Units.Where(u => string.Equals(u.BlockName, blockName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.NetworkOrder ?? int.MaxValue).ThenBy(u => u.Id).ToArray();
        var ifaceXml = units.Select(u => u.InterfaceXml).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        return new(blockName, units.Select(u => UnitTranslator.Translate(u, source.Tags)).ToArray(), InterfaceReader.Read(ifaceXml));
    }

    public bool HasBody => Units.Any(u => u.UnitKind != "MissingBody" && u.Statements.Count > 0);

    /// <summary>块体只有文字（源码、标签、说明）：AI 读不到可核对的逻辑。为不透明源码里的 RETURN/GOTO 合成的返回语句
    /// （SclTranslator.IsSourceControlMarker）不是翻译出来的逻辑，不算（裁定 F7）。</summary>
    public bool IsTextOnly => HasBody && Units.SelectMany(u => u.Statements)
        .All(s => s.Kind is IrStatementKinds.Source or IrStatementKinds.Label or IrStatementKinds.Note || SclTranslator.IsSourceControlMarker(s));

    public int TranslationLength => Units.Sum(u => IrText.Unit(u).Length);

    public IfaceMember? Formal(string name) => Interface.FirstOrDefault(m =>
        m.Section is "Input" or "Output" or "InOut" && Cmp.Equals(m.Name, name));

    /// <summary>名字的根段（第一个 '.' 或 '[' 之前）是本块 Static/Temp 接口成员：只有本块能写的变量。忽略大小写。</summary>
    public bool IsLocal(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var cut = name.IndexOfAny(['.', '[']);
        var root = cut < 0 ? name : name[..cut];
        return Interface.Any(m => m.Section is "Static" or "Temp" && Cmp.Equals(m.Name, root));
    }

    /// <summary>语句在块内的执行顺序：先按 Units 顺序，再按程序段内语句顺序。编号不存在返回 -1。</summary>
    public int OrderOf(string statementId) => order.TryGetValue(statementId, out var i) ? i : -1;

    public bool IsBoolFormal(string name, params string[] sections)
    {
        var f = Formal(name);
        return f is not null && (sections.Length == 0 || sections.Contains(f.Section, Cmp))
            && string.Equals(f.DataType, "Bool", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>写该名字的语句：已翻译的写入（同名目标），加上文字里可能写到它的文字语句（TextMentions 成立时，见 TextWriters；
    /// 这些写入的值未知）。</summary>
    public IReadOnlyList<BlockWrite> Writers(string variable)
    {
        IReadOnlyList<BlockWrite> list = writers.TryGetValue(variable, out var l) ? l : [];
        return TextMentions(variable) ? list.Concat(TextWriters(variable)).ToArray() : list;
    }

    Dictionary<string, List<string>>? targetsByRoot;

    /// <summary>与 variable 重叠、但不是同一目标的写入目标：祖先（整体写父结构，如调用输出/InOut 引脚接 Cmd、Move 写 Cmd）、
    /// 子成员（Cmd.FwdPos.x、Cmd.W.%X3），以及下标不是整数字面量、可能指向同一元素的写法（Arr[#i] 与 Arr[3]）。按根段分组查找。</summary>
    public IReadOnlyList<string> OverlappingTargets(string variable)
    {
        if (string.IsNullOrWhiteSpace(variable)) return [];
        var index = targetsByRoot ??= writers.Keys.GroupBy(k => RootOf(k.Trim()), Cmp).ToDictionary(g => g.Key, g => g.ToList(), Cmp);
        var name = variable.Trim();
        if (!index.TryGetValue(RootOf(name), out var candidates)) return [];
        var mine = Segments(name);
        return candidates.Where(t => !Cmp.Equals(t.Trim(), name) && MayOverlap(mine, Segments(t.Trim()))).ToArray();
    }

    /// <summary>本块对 variable 有写入：写它本身，或写与它重叠的目标（祖先、子成员、可能同址的下标写法、块写指令放宽），
    /// 或认不出但可能写到它的目标（UncertainWriters），或文字段（源码、顺控动作、未识别元素）里提到它（TextMentions：
    /// 本身/成员、整体提到祖先、祖先带非字面量下标等，与 ProjectWrites 同口径；文字里的写入不建模，一律算可能写入）。忽略大小写。</summary>
    public bool IsWritten(string variable) =>
        Writers(variable).Count > 0 || OverlappingTargets(variable).Count > 0 || UncertainWriters(variable).Count > 0;

    ProjectWrites? textIndex;

    /// <summary>文字段里提到该名字（ProjectWrites.MentionedInText 口径，不含翻译器加的条件说明行）。</summary>
    public bool TextMentions(string variable) =>
        !string.IsNullOrWhiteSpace(variable) && (textIndex ??= new ProjectWrites(Units, [])).MentionedInText(variable);

    readonly Dictionary<string, IReadOnlyList<BlockWrite>> textWriters = new(Cmp);

    /// <summary>可能在文字里写到该名字的文字语句：文字含其根段的那些；按地址等别名提到、根段不在文字里时取全部文字语句。</summary>
    IReadOnlyList<BlockWrite> TextWriters(string variable)
    {
        if (textWriters.TryGetValue(variable, out var cached)) return cached;
        var texts = Units.SelectMany(u => u.Statements.Where(IsTextStatement).Select(s => (Unit: u, Statement: s))).ToArray();
        var root = RootOf(variable.Trim()).Trim('"');
        var hit = texts.Where(x => SclTranslator.WrittenText(x.Statement.Text!).Contains(root, StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = (hit.Length > 0 ? hit : texts).Select(x => new BlockWrite(x.Statement, x.Unit, null)).ToArray();
        return textWriters[variable] = result;
    }

    static bool IsTextStatement(IrStatement s) =>
        s.Kind is IrStatementKinds.Source or IrStatementKinds.Action or IrStatementKinds.Unrecognized && !string.IsNullOrEmpty(s.Text);

    /// <summary>名字拆段：按顶层 '.' 分段，下标 [..] 单独成段；引号内的字符原样保留。</summary>
    internal static IReadOnlyList<string> Segments(string name)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0; var quoted = false;
        void Flush() { if (current.Length > 0) parts.Add(current.ToString()); current.Clear(); }
        foreach (var ch in name)
        {
            if (ch == '"') quoted = !quoted;
            if (!quoted && depth == 0 && ch == '.') { Flush(); continue; }
            if (!quoted && ch == '[') { if (depth++ == 0) Flush(); }
            current.Append(ch);
            if (!quoted && ch == ']' && depth > 0 && --depth == 0) Flush();
        }
        Flush();
        return parts;
    }

    internal static readonly Regex LiteralIndex = new(@"^\[\s*\d+(\s*,\s*\d+)*\s*\]$", RegexOptions.CultureInvariant);

    /// <summary>两组段在较短者的长度内逐段可能相等：名字段忽略大小写相同；下标段字面量相同，或任一方不是整数字面量。
    /// 两者完全相同（同一目标）不算重叠。</summary>
    internal static bool MayOverlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = Math.Min(a.Count, b.Count);
        var exact = a.Count == b.Count;
        for (var i = 0; i < n; i++)
        {
            if (Cmp.Equals(a[i], b[i])) continue;
            var ai = a[i].StartsWith('['); var bi = b[i].StartsWith('[');
            if (!ai || !bi) return false;
            var al = LiteralIndex.IsMatch(a[i]); var bl = LiteralIndex.IsMatch(b[i]);
            if (al && bl && !string.Equals(Regex.Replace(a[i], @"\s", ""), Regex.Replace(b[i], @"\s", ""), StringComparison.Ordinal)) return false;
            exact = false;
        }
        return !exact;
    }

    /// <summary>Bool 的 Input/InOut 形参，且本块对它没有任何写入：已翻译语句里没有以它（或其成员/下标/位切片，按根段）
    /// 为目标的写入，文字段（源码、顺控动作、未识别元素）里也没有提到它（可能在文字里被写）。这样的形参在块内读到的就是调用方的实参。</summary>
    public bool IsUnwrittenBoolInputFormal(string name)
    {
        if (!IsBoolFormal(name, "Input", "InOut")) return false;
        var root = RootOf(name);
        return !writers.Keys.Any(k => Cmp.Equals(RootOf(k.Trim()), root)) && UncertainWriters(name).Count == 0 && !MentionedInAnyText(root);
    }

    /// <summary>扫全部文字段：源码、顺控动作、未识别的顺控元素（这些文字里的写入都不进 writers）。口径同 TextMentions。</summary>
    bool MentionedInAnyText(string variable) => TextMentions(variable);

    const int MaxAliasDepth = 8;
    Dictionary<string, List<string>>? aliasEdges;

    /// <summary>纯拷贝别名（含自身，忽略大小写）：X 的唯一定义（Definition）化成 DNF 后只有一项，去掉常真文字后恰为一个正的
    /// 变量 Y（无取反、无比较等不透明条件），则 X 与 Y 互为别名；双向、可传递，最多 8 跳，防环。
    /// 常真文字：①本块未写入（按根段，含文字段里提到）的全局量——既不是本块 Static/Temp，也不是形参（与「未被写入的全局量如
    /// AlwaysTRUE 不代表输出」同一口径，不看名字）；②唯一定义只由常真文字合取而成（含 TRUE）的变量。Const TRUE 在化简时已折掉。
    /// 常真的拷贝仍是常真，不算别名（单独拷贝一个常真文字的变量不连边），避免经常量把无关变量连成一片。
    /// 文字段（源码、顺控动作、未识别元素）里提到的变量不当作有唯一定义。</summary>
    public IReadOnlySet<string> Aliases(string name)
    {
        var edges = aliasEdges ??= BuildAliasEdges();
        var result = new HashSet<string>(Cmp) { name };
        var frontier = new List<string> { name };
        for (var depth = 0; depth < MaxAliasDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var v in frontier)
                if (edges.TryGetValue(v, out var neighbours))
                    foreach (var n in neighbours)
                        if (result.Add(n)) next.Add(n);
            frontier = next;
        }
        return result;
    }

    Dictionary<string, List<string>> BuildAliasEdges()
    {
        var edges = new Dictionary<string, List<string>>(Cmp);
        if (HasControlFlow) return edges;   // 此时没有任何唯一定义
        var writtenRoots = writers.Keys.Select(k => RootOf(k.Trim())).ToHashSet(Cmp);
        void Link(string a, string b)
        {
            if (!edges.TryGetValue(a, out var la)) edges[a] = la = [];
            if (!edges.TryGetValue(b, out var lb)) edges[b] = lb = [];
            if (!la.Contains(b, Cmp)) la.Add(b);
            if (!lb.Contains(a, Cmp)) lb.Add(a);
        }
        foreach (var (x, list) in writers)
        {
            // 先做便宜的筛选，再取 Definition（它会扫源码文字）
            if (list.Count != 1 || list[0].Statement.Kind != IrStatementKinds.Assign || !list[0].Statement.Guard.IsTrue
                || list[0].Value is not { } value || value.Vars().Distinct(Cmp).Take(9).Count() > 8) continue;
            if (CopySource(x, constant) is { } y) Link(x, y);
        }
        return edges;

        bool constant(string name) => IsConstantTrue(name, writtenRoots);
    }

    readonly Dictionary<string, bool> constantMemo = new(Cmp);
    readonly HashSet<string> constantVisiting = new(Cmp);

    /// <summary>name 恒为真（见 Aliases 的「常真文字」）。按名字缓存；有环按非常真处理。
    /// 规则②（唯一定义全由常真构成）只用于 Static 成员与全局量：Temp 先读后写读到的是残留值，形参读到的可能是调用方实参。</summary>
    bool IsConstantTrue(string name, IReadOnlySet<string> writtenRoots)
    {
        if (constantMemo.TryGetValue(name, out var known)) return known;
        if (!constantVisiting.Add(name)) return false;
        var root = RootOf(name);
        bool result;
        if (Formal(root) is not null) result = false;
        else if (IsLocal(name)) result = IsStatic(root) && DefinedByConstants(name, writtenRoots);
        else result = !writtenRoots.Contains(root) && UncertainWriters(name).Count == 0 && !MentionedInAnyText(name) && !MentionedInAnyText(root)
            || DefinedByConstants(name, writtenRoots);
        constantVisiting.Remove(name);
        return constantMemo[name] = result;
    }

    bool DefinedByConstants(string name, IReadOnlySet<string> writtenRoots) =>
        !MentionedInAnyText(name) && Definition(name) is { } def && BoolSimplifier.ToDnf(def, 4) is { Count: 1 } dnf
        && dnf[0].All(l => l.Positive && !l.Opaque && !Cmp.Equals(l.Key, name) && IsConstantTrue(l.Key, writtenRoots));

    /// <summary>名字的根段是本块 Static 接口成员（忽略大小写）。</summary>
    public bool IsStatic(string name) =>
        Interface.Any(m => m.Section == "Static" && Cmp.Equals(m.Name, RootOf(name)));

    string? CopySource(string x, Func<string, bool> constantTrue)
    {
        if (MentionedInAnyText(x) || Definition(x) is not { } def || BoolSimplifier.ToDnf(def, 4) is not { Count: 1 } dnf) return null;
        var kept = dnf[0].Where(l => !(l.Positive && !l.Opaque && constantTrue(l.Key))).ToArray();
        if (kept.Length != 1 || !kept[0].Positive || kept[0].Opaque) return null;
        var y = kept[0].Key;
        return Cmp.Equals(y, x) ? null : y;
    }

    internal static string RootOf(string name)
    {
        var cut = name.IndexOfAny(['.', '[']);
        return cut < 0 ? name : name[..cut];
    }

    /// <summary>块内任一处有 Jump/JumpNot/Return：语句不一定按顺序全部执行。</summary>
    public bool HasControlFlow => Units.SelectMany(u => u.Statements)
        .Any(s => s.Kind is IrStatementKinds.Jump or IrStatementKinds.JumpNot or IrStatementKinds.Return);

    /// <summary>变量的唯一定义：恰好一个无条件赋值写入者时返回展开后的值。保守：块内有跳转/返回，
    /// 或任一 Source 语句（未翻译的源码文本）以完整名字提到该变量（可能在文本里被写），
    /// 或存在与它重叠的其它写入目标（整体写父结构、写子成员、可能同址的下标写法、块写指令放宽，见 OverlappingTargets），
    /// 或有认不出但可能写到它的写入（UncertainWriters），一律返回 null。</summary>
    public IrExpr? Definition(string variable)
    {
        if (HasControlFlow || MentionedInSourceText(variable) || OverlappingTargets(variable).Count > 0
            || UncertainWriters(variable).Count > 0) return null;
        var list = Writers(variable);
        return list.Count == 1 && list[0].Statement.Kind == IrStatementKinds.Assign && list[0].Statement.Guard.IsTrue
            ? list[0].Value : null;
    }

    /// <summary>审查 I-2：文字段（含顺控动作）里提到它即算可能被写——本身/成员、整体提到祖先（「Cmd := …」使 Cmd.Run 失效）、
    /// 祖先带非字面量下标等，与 ProjectWrites 同口径。</summary>
    bool MentionedInSourceText(string variable) => TextMentions(variable);

    /// <summary>写入者 Guard/Value 直接读到的变量。只基于已翻译的语句：Source 语句文本里的写入不建模。</summary>
    public IReadOnlySet<string> DirectSources(string variable)
    {
        var set = new HashSet<string>(Cmp);
        foreach (var w in Writers(variable)) Read(w, set);
        return set;
    }

    /// <summary>经写入者传递读到的全部变量（不含自身）。只基于已翻译的语句：Source 语句文本里的写入不建模。</summary>
    public IReadOnlySet<string> DependencyClosure(string variable)
    {
        var seen = new HashSet<string>(Cmp);
        var queue = new Queue<string>([variable]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var w in Writers(current))
            {
                var direct = new HashSet<string>(Cmp);
                Read(w, direct);
                foreach (var v in direct)
                    if (!Cmp.Equals(v, variable) && seen.Add(v)) queue.Enqueue(v);
            }
        }
        return seen;
    }

    /// <summary>一条语句提到的全部变量（忽略大小写）：写入目标、Guard/Value（节点已展开）、调用引脚的实参与输出目标。编号不存在返回空集。</summary>
    public IReadOnlySet<string> StatementVars(string id)
    {
        var set = new HashSet<string>(Cmp);
        foreach (var unit in Units)
            foreach (var s in unit.Statements)
            {
                if (!string.Equals(s.Id, id, StringComparison.Ordinal)) continue;
                if (s.Target is { Length: > 0 }) set.Add(s.Target);
                foreach (var v in ExpandNodes(unit, s.Guard).Vars()) set.Add(v);
                if (s.Value is not null) foreach (var v in ExpandNodes(unit, s.Value).Vars()) set.Add(v);
                if (s.Call is { } call)
                    foreach (var pin in call.Pins)
                    {
                        if (pin.Value is not null) foreach (var v in ExpandNodes(unit, pin.Value).Vars()) set.Add(v);
                        foreach (var t in pin.Targets) set.Add(t);
                    }
                return set;   // 与 StatementsById 一致：同编号只取第一条
            }
        return set;
    }

    void Read(BlockWrite w, HashSet<string> into)
    {
        foreach (var v in ExpandNodes(w.Unit, w.Statement.Guard).Vars()) into.Add(v);
        if (w.Value is not null) foreach (var v in w.Value.Vars()) into.Add(v);
        if (w.Statement.Call is { } call)
            foreach (var pin in call.Pins.Where(p => p.Value is not null))
                foreach (var v in ExpandNodes(w.Unit, pin.Value!).Vars()) into.Add(v);
    }

    public IrExpr ExpandNodes(IrUnit unit, IrExpr e) => ExpandNodesIn(unit, e);

    /// <summary>不依赖块实例的节点展开（只用程序段自己的节点表）。</summary>
    public static IrExpr ExpandNodesIn(IrUnit unit, IrExpr e) => Expand(unit, e, 0);

    static IrExpr Expand(IrUnit unit, IrExpr e, int depth)
    {
        if (depth > 32) return e;
        if (e.Kind == IrKinds.Node)
        {
            var node = unit.Nodes.FirstOrDefault(n => n.Name == e.Name);
            return node is null ? e : Expand(unit, node.Value, depth + 1);
        }
        return e.Args is null ? e : e with { Args = e.Args.Select(a => Expand(unit, a, depth + 1)).ToArray() };
    }
}
