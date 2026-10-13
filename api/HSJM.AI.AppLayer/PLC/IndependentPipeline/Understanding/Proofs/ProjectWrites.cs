#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>全项目（含 FC）程序写入索引：赋值/置位/复位目标、调用输出引脚目标、调用背景实例；文字段（源码、顺控动作、
/// 未识别元素）里以标识符提到的名字记为「可能被写」。只看程序本身，HMI/通讯/系统存储位等程序外写入不建模。
/// 重叠口径（只会多算写入）：
/// ①名字：与 IrBlock 同一套段比较——同一变量、父结构/子成员（确定写入），下标不是整数字面量（Arr[#i] 与 Arr[3]）为可能写入；
/// 写一段内存的指令（MOVE_BLK、FILL_BLK、FieldWrite、Serialize/Deserialize 等，按指令类型判定）与 ANY/VARIANT/POINTER 形参，
/// 输出接数组元素时可能写整个数组，接结构/根时可能写整个根；不认识的指令输出接数组元素时同样可能写整个数组；
/// ②地址：I/Q/M 区（含德文 E/A）的绝对地址、字节/字/双字标签与位切片按点表换算成位范围比较（确定写入）；
/// 指针常数 P#M0.0 BYTE 10、P#DB5.DBX0.0 WORD 2 按区与字节范围换算为确定写入（不按字节对齐时按整字节放大）；
/// 算不出范围的区写入（%M:类型:偏移、带区字母的间接寻址 QB[AR1,…]/DBX[…]/DIX[…]、没有类型长度的指针常数）只在该区内为可能写入；
/// DB 绝对地址（%DB5.DBX0.0、P#DB…）换算不到符号名，与所有符号名（不在点表、也不是绝对地址的名字）互相视为可能写入；
/// 局部区（L[AR1,…] 间接、%Local0.0 绝对地址）只对符号名（局部变量）为可能写入，索引不分块；
/// ③区未知的写入目标（跨区间接 B[AR1,…]、不带区的指针常数、翻译器标为〔…〕的认不出的访问）对任何名字都是可能写入；
/// 符号名里的空白是合法字符（博图允许，如 "DB".PB[1]."PB3_CYCLE START"），按普通符号处理。
/// 文字段里的寄存器/区间接寻址（QB[AR1,…]、B[AR1,…]）：源码（STL/SCL）里只有跟在写指令（T / = / S / R / FP / FN）之后才算提到，
/// L、A、O 等读指令不算；方括号里是整数字面量（如 SCL 数组元素 i[1]）的不是间接寻址。FC 块（isFcUnit，用户：FC 完全不管）
/// 文字里的间接寻址与指针常数不按区计（与 Q 写入索引不计 FC 同一裁定），绝对地址与名字照常计。
/// 目标「?」（连线指向不存在的访问，XML 损坏）不算写入，只计数；下标读不到（Arr[?]）按非字面量下标处理。
/// Writers 返回确定与可能两类；可能写入另由 UncertainWriters/HasUncertainWriter 给出：判唯一写入者、判稳定时
/// 应把它们当作「可能在任何时刻写」。按段建前缀树、按「区+字节」建地址索引；对所有名字（或整区）都一样的可能写入
/// 在首次用到时算成共享集合，查询结果只引用它、不复制。</summary>
public sealed class ProjectWrites
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    internal const string Register = @"(?:(?<![A-Za-z0-9_#])P#|(?<![A-Za-z0-9_])AR[12](?![A-Za-z0-9_]))";

    /// <summary>写一段内存的博图指令/系统块（指令集结构证据，不是用户命名）。</summary>
    static readonly HashSet<string> BlockWriteInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MOVE_BLK", "MOVE_BLK_VARIANT", "UMOVE_BLK", "FILL_BLK", "UFILL_BLK", "FieldWrite", "Serialize", "Deserialize",
        "SCATTER", "SCATTER_BLK", "GATHER_BLK", "BLKMOV", "UBLKMOV", "FILL"
    };

    /// <summary>指向一段内存的形参类型。</summary>
    static readonly HashSet<string> MemoryPointerTypes = new(StringComparer.OrdinalIgnoreCase) { "Any", "Variant", "Pointer" };

    internal enum Spread { None, ElementArray, BlockWrite }

    /// <summary>一条语句的写入目标（未清洗，可能为空/空白）：IsStatementTarget 为赋值/置位/复位的 Target。
    /// 与 IrBlock 共用：调用引脚目标、调用背景实例、SideWrites（指令盒顺带写入）；Spread 为块写放宽口径。</summary>
    internal readonly record struct WriteTarget(string? Target, Spread Spread, bool IsStatementTarget);

    internal static IEnumerable<WriteTarget> StatementWrites(IrStatement s)
    {
        if (s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset)
            yield return new(s.Target, s.Kind == IrStatementKinds.Assign ? SpreadOf(s.Value) : Spread.None, true);
        if (s.Call is { } call)
        {
            var callSpread = BlockWriteInstructions.Contains(call.Callee?.Trim() ?? "");
            foreach (var pin in call.Pins)
            {
                var spread = callSpread || MemoryPointerTypes.Contains(pin.Type?.Trim() ?? "") ? Spread.BlockWrite : Spread.None;
                foreach (var t in pin.Targets) yield return new(t, spread, false);
            }
            yield return new(call.Instance, Spread.None, false);
        }
        // 指令盒顺带改写的变量（背景实例、边沿存储位、未展开语义指令的输入侧端口）；数组元素放宽到整个数组
        foreach (var t in s.SideWrites ?? []) yield return new(t, Spread.ElementArray, false);
    }

    /// <summary>块写放宽：最后一个字面量下标 → 整个数组（「[*]」，非字面量下标）；写一段内存的指令接结构/根 → 整个根；无需放宽返回 null。</summary>
    internal static IReadOnlyList<string>? Widen(IReadOnlyList<string> segs, Spread spread)
    {
        if (spread == Spread.None) return null;
        var last = -1;
        for (var i = segs.Count - 1; i >= 0; i--)
            if (IsSubscript(segs[i]) && IrBlock.LiteralIndex.IsMatch(segs[i])) { last = i; break; }
        return last >= 0 ? [.. segs.Take(last), "[*]"]
            : spread == Spread.BlockWrite && segs.Count > 1 ? [segs[0]] : null;
    }

    sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>下标不是整数字面量的分支。</summary>
        public Node? Wild;
        public readonly List<int> Ends = [];
        List<int>? subtree;

        public IReadOnlyList<int> Subtree()
        {
            if (subtree is not null) return subtree;
            var list = new List<int>(Ends);
            foreach (var child in Children.Values) list.AddRange(child.Subtree());
            if (Wild is not null) list.AddRange(Wild.Subtree());
            return subtree = list;
        }
    }

    /// <summary>共享的可能写入集合（按语句去重）。</summary>
    sealed class Shared
    {
        public static readonly Shared Empty = new([]);
        public readonly (long UnitId, string StatementId)[] Items;
        public readonly HashSet<(long, string)> Set;
        public Shared((long, string)[] items) { Items = items; Set = [.. items]; }
    }

    readonly List<(string Target, long UnitId, string StatementId)> entries = [];
    /// <summary>只算可能写入的条目（块移动放宽出来的数组/根）。</summary>
    readonly HashSet<int> possibleOnly = [];
    readonly Node trie = new();
    /// <summary>落在 I/Q/M/DB 区的写入（含算不出范围的）。</summary>
    readonly PlcAddress.AreaIndex areaEntries = new();
    /// <summary>不在点表、也不是绝对地址的写入目标（可能是 DB 变量，与 DB 绝对地址互相重叠）。</summary>
    readonly List<int> symbolicEntries = [];
    /// <summary>区未知的写入目标：对任何名字都是可能写入。</summary>
    readonly List<int> unknownEntries = [];
    /// <summary>局部区写入：对符号名为可能写入。</summary>
    readonly List<int> localEntries = [];
    /// <summary>落在 DB 区的全部写入（对符号名为可能写入）。</summary>
    readonly List<int> dbEntries = [];
    /// <summary>各区范围未知的写入（区名 → 条目；DB 号未知的记在「DB」下）。</summary>
    readonly Dictionary<string, List<int>> wholeAreaEntries = new(StringComparer.Ordinal);
    readonly Dictionary<string, Shared> sharedCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> uncertainCounts = UncertainKinds.All.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);

    /// <summary>写入目标的分类计数键（UncertainWriteCounts）。</summary>
    public static class UncertainKinds
    {
        /// <summary>目标「?」（连线指向不存在的访问，XML 损坏），不算写入。</summary>
        public const string UnwiredOutput = "UnwiredOutput";
        /// <summary>按区与字节范围换算成确定写入的指针常数。</summary>
        public const string PointerResolved = "PointerResolved";
        /// <summary>I/Q/M 区内范围未知（间接寻址、%M:类型:偏移、没有类型长度的指针常数）：该区内可能写入。</summary>
        public const string AreaWide = "AreaWide";
        /// <summary>DB 区内范围未知（DBX[…]/DIX[…]、DB 号未知的指针常数）：DB 区与符号名可能写入。</summary>
        public const string DbWide = "DbWide";
        /// <summary>范围确定的 DB 绝对地址：对符号名为可能写入。</summary>
        public const string DbAbsolute = "DbAbsolute";
        /// <summary>局部区（间接或绝对地址）：对符号名为可能写入。</summary>
        public const string LocalIndirect = "LocalIndirect";
        /// <summary>区未知：对全部点可能写入。</summary>
        public const string AnyArea = "AnyArea";
        /// <summary>写一段内存的指令/形参，或不认识的指令写数组元素：放宽为可能写整个数组/根。</summary>
        public const string BlockWrite = "BlockWrite";

        public static readonly string[] All = [UnwiredOutput, PointerResolved, AreaWide, DbWide, DbAbsolute, LocalIndirect, AnyArea, BlockWrite];
    }

    /// <summary>项目里各类写入目标的条数（键见 UncertainKinds，全部键都在），供统计不确定写入的规模。</summary>
    public IReadOnlyDictionary<string, int> UncertainWriteCounts => uncertainCounts;
    readonly PlcAddress.Resolver resolver;
    readonly TextMentions texts;
    readonly Dictionary<string, (IReadOnlyList<(long UnitId, string StatementId)> All, IReadOnlyList<(long UnitId, string StatementId)> Uncertain)> writerCache = new(C);
    readonly Dictionary<string, bool> mentionCache = new(C);

    /// <param name="tags">点表：用于把标签名换算成地址（同址别名、字节/字标签、位切片）。传空则只认绝对地址写法。</param>
    /// <param name="isFcUnit">该程序段属于 FC 块（按块类型）：其文字里的间接寻址与指针常数不按区计提及（见类说明）。为 null 时都不是 FC。</param>
    public ProjectWrites(IEnumerable<IrUnit> units, IReadOnlyList<PipelineTag> tags, Func<IrUnit, bool>? isFcUnit = null)
    {
        resolver = new PlcAddress.Resolver(tags);
        var textList = new List<TextEntry>();
        var boxes = new HashSet<string>(C);
        foreach (var unit in units)
        {
            var fc = isFcUnit?.Invoke(unit) == true;
            indexedUnits.Add(unit.UnitId);
            if (!string.IsNullOrWhiteSpace(unit.BlockName)) blockNames.Add(unit.BlockName.Trim());
            foreach (var node in unit.Nodes) boxes.UnionWith(BoxInstancesIn(node.Value));
            foreach (var s in unit.Statements)
            {
                foreach (var e in Expressions(s)) boxes.UnionWith(BoxInstancesIn(e));
                if (s.Call is { } c && !string.IsNullOrWhiteSpace(c.Instance))
                    callInstances.Add(new(c.Callee?.Trim() ?? "", c.BlockType?.Trim() ?? "", c.Instance.Trim(), c.InstanceScope));
                if (s.Call is { } typed)
                    foreach (var pin in typed.Pins.Where(x => IsIecTimerOrCounterType(x.Type)))
                        timerActuals.UnionWith((pin.Value?.Vars() ?? []).Concat(pin.Targets).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
                foreach (var w in StatementWrites(s)) Add(w.Target, unit.UnitId, s.Id, w.Spread);
                // 不透明语句前的「（仅当 … 时执行）」说明行只读条件变量，不算文字里的写入（审查 M-4）
                if (s.Kind is IrStatementKinds.Source or IrStatementKinds.Action or IrStatementKinds.Unrecognized && !string.IsNullOrEmpty(s.Text))
                    textList.Add(new(SclTranslator.WrittenText(s.Text), s.Kind == IrStatementKinds.Source, fc));
            }
        }
        texts = new TextMentions(textList);
        boxInstances = boxes.ToArray();
    }

    readonly HashSet<long> indexedUnits = [];
    readonly HashSet<string> blockNames = new(C);
    readonly HashSet<string> timerActuals = new(C);
    readonly string[] boxInstances;
    readonly List<CallInstance> callInstances = [];

    /// <summary>项目里一次调用的背景实例（Callee/BlockType 原样去空白；Scope 为导出里的 GlobalVariable/LocalVariable，不明为 null）。</summary>
    internal readonly record struct CallInstance(string Callee, string BlockType, string Instance, string? Scope);

    /// <summary>该程序段在本索引里（语句写入已登记）。</summary>
    internal bool Indexes(long unitId) => indexedUnits.Contains(unitId);

    /// <summary>全项目指令盒（定时器/计数器/带实例的指令）的背景实例名（去重，忽略大小写）：与实例重叠的变量访问即更新，不建模。</summary>
    internal IReadOnlyList<string> BoxInstances => boxInstances;

    /// <summary>全项目块调用的背景实例。</summary>
    internal IReadOnlyList<CallInstance> CallInstances => callInstances;

    /// <summary>全项目程序段所属的块名（去重，忽略大小写）。</summary>
    internal IReadOnlyCollection<string> BlockNames => blockNames;

    /// <summary>全项目调用引脚里类型为 IEC 定时器/计数器的实参名（InOut 传入的定时器等）。</summary>
    internal IReadOnlyCollection<string> TimerOrCounterActuals => timerActuals;

    static readonly Regex IecTimerOrCounter = new(
        @"^(?:(?:TON|TOF|TP|TONR)(?:_L?TIME)?|IEC_L?TIMER|(?:CTU|CTD|CTUD)(?:_[A-Z]+)?|IEC_U?[SDL]?COUNTER)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>博图 IEC 定时器/计数器系统数据类型（指令集的类型名，不是用户命名）：TON/TOF/TP/TONR 及 _TIME/_LTIME 变体、
    /// IEC_TIMER/IEC_LTIMER、CTU/CTD/CTUD 及各整数变体、IEC_COUNTER/IEC_SCOUNTER/IEC_DCOUNTER/…；数组取元素类型，去引号。</summary>
    internal static bool IsIecTimerOrCounterType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var t = type.Trim();
        var of = t.LastIndexOf(" of ", StringComparison.OrdinalIgnoreCase);
        if (of >= 0) t = t[(of + 4)..];
        return IecTimerOrCounter.IsMatch(t.Trim().Trim('"'));
    }

    /// <summary>语句里会被求值的表达式：条件、值、调用 EN 与各引脚实参（未展开节点）。</summary>
    internal static IEnumerable<IrExpr> Expressions(IrStatement s)
    {
        yield return s.Guard;
        if (s.Value is not null) yield return s.Value;
        if (s.Call is null) yield break;
        yield return s.Call.En;
        foreach (var pin in s.Call.Pins)
            if (pin.Value is not null) yield return pin.Value;
    }

    /// <summary>表达式里指令盒的背景实例：Timer/Counter 名「部件|实例|端口」的实例段；Instr 名「指令[&lt;模板&gt;][[实例]]|端口」末尾方括号里的实例。</summary>
    internal static IEnumerable<string> BoxInstancesIn(IrExpr e)
    {
        if (e.Kind is IrKinds.Timer or IrKinds.Counter && e.Name is { } n)
        {
            var first = n.IndexOf('|'); var last = n.LastIndexOf('|');
            if (first >= 0 && last > first && n[(first + 1)..last].Trim() is { Length: > 0 } instance) yield return instance;
        }
        else if (e.Kind == IrKinds.Instr && e.Name is { } head && InstrInstance(head) is { } instance) yield return instance;
        foreach (var arg in e.Args ?? [])
            foreach (var x in BoxInstancesIn(arg)) yield return x;
    }

    static string? InstrInstance(string name)
    {
        var bar = name.LastIndexOf('|');
        var head = (bar < 0 ? name : name[..bar]).TrimEnd();
        if (!head.EndsWith(']')) return null;
        var depth = 0;
        for (var i = head.Length - 1; i >= 0; i--)
        {
            if (head[i] == ']') depth++;
            else if (head[i] == '[' && --depth == 0)
                return head[(i + 1)..^1].Trim() is { Length: > 0 } instance ? instance : null;
        }
        return null;
    }

    /// <summary>指令盒数据输出：已知写一段内存的指令 → BlockWrite；其它未展开语义的指令 → 只对数组元素放宽。</summary>
    internal static Spread SpreadOf(IrExpr? value)
    {
        if (value is not { Kind: IrKinds.Instr, Name: { } head }) return Spread.None;
        var cut = head.IndexOfAny(['|', '<', '[']);
        var name = (cut < 0 ? head : head[..cut]).Trim();
        return BlockWriteInstructions.Contains(name) ? Spread.BlockWrite : Spread.ElementArray;
    }

    void Add(string? target, long unitId, string statementId, Spread spread)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var t = target.Trim();
        if (t == "?") { uncertainCounts[UncertainKinds.UnwiredOutput]++; return; }
        var index = NewEntry(t, unitId, statementId);
        var areas = resolver.Resolve(t);
        if (areas.Count > 0)
        {
            foreach (var a in areas)
            {
                areaEntries.Add(a, index);
                if (a.IsDb) dbEntries.Add(index);
                if (a.Start is null) Put(wholeAreaEntries, a.Name, index);
            }
            var kind = areas.Any(a => !a.IsResolved)
                ? areas.Any(a => !a.IsResolved && a.IsDb) ? UncertainKinds.DbWide : UncertainKinds.AreaWide
                : PlcAddress.IsPointer(t) ? UncertainKinds.PointerResolved
                : areas.Any(a => a.IsDb) ? UncertainKinds.DbAbsolute : null;
            if (kind is not null) uncertainCounts[kind]++;
        }
        else if (PlcAddress.IsLocalArea(t)) { localEntries.Add(index); uncertainCounts[UncertainKinds.LocalIndirect]++; return; }
        else if (PlcAddress.IsUnknownTarget(t)) { unknownEntries.Add(index); uncertainCounts[UncertainKinds.AnyArea]++; return; }
        else symbolicEntries.Add(index);
        var segs = IrBlock.Segments(t);
        Insert(segs, index);
        if (spread == Spread.None || areas.Count > 0) return;
        if (Widen(segs, spread) is not { } widened) return;
        var extra = NewEntry(t, unitId, statementId);
        possibleOnly.Add(extra);
        Insert(widened, extra);
        uncertainCounts[UncertainKinds.BlockWrite]++;
    }

    int NewEntry(string target, long unitId, string statementId)
    {
        entries.Add((target, unitId, statementId));
        return entries.Count - 1;
    }

    static void Put(Dictionary<string, List<int>> map, string key, int item)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(item);
    }

    void Insert(IReadOnlyList<string> segs, int index)
    {
        var node = trie;
        foreach (var seg in segs)
        {
            if (IsSubscript(seg) && !IrBlock.LiteralIndex.IsMatch(seg)) node = node.Wild ??= new Node();
            else
            {
                var key = Key(seg);
                if (!node.Children.TryGetValue(key, out var next)) node.Children[key] = next = new Node();
                node = next;
            }
        }
        node.Ends.Add(index);
    }

    static bool IsSubscript(string seg) => seg.StartsWith('[');

    /// <summary>名字段原样（字典忽略大小写）；整数字面量下标去掉空白。</summary>
    static string Key(string seg) => IsSubscript(seg) ? Regex.Replace(seg, @"\s", "") : seg;

    /// <summary>写入该变量的语句（确定与可能两类，见类说明；忽略大小写）。</summary>
    public IReadOnlyList<(long UnitId, string StatementId)> Writers(string name) => Lookup(name).All;

    /// <summary>只可能写到该变量的语句（非字面量下标、块移动放宽、本区内算不出范围的写入、DB 绝对地址↔符号名、局部区↔符号名、
    /// 区未知的目标）；同一语句另有确定的重叠时不在此列。</summary>
    public IReadOnlyList<(long UnitId, string StatementId)> UncertainWriters(string name) => Lookup(name).Uncertain;

    /// <summary>有可能写到该变量、但不能确定写的语句：判唯一写入者与稳定时应视为「可能在任何时刻写入」。</summary>
    public bool HasUncertainWriter(string name) => UncertainWriters(name).Count > 0;

    (IReadOnlyList<(long UnitId, string StatementId)> All, IReadOnlyList<(long UnitId, string StatementId)> Uncertain) Lookup(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ([], []);
        var key = name.Trim();
        if (writerCache.TryGetValue(key, out var hit)) return hit;
        // 只对按名字/字节命中的少量条目逐条判断确定与否；对所有名字都一样的部分走共享集合
        var specific = new Dictionary<int, bool>();
        void Hit(int entry, bool certain)
        {
            certain &= !possibleOnly.Contains(entry);
            if (!specific.TryGetValue(entry, out var known) || certain && !known) specific[entry] = certain;
        }
        foreach (var (entry, certain) in TrieMatches(IrBlock.Segments(key))) Hit(entry, certain);
        var areas = resolver.Resolve(key);
        var parts = new SortedSet<string>(StringComparer.Ordinal) { "U" };
        if (areas.Count == 0) { parts.Add("DB"); parts.Add("L"); }
        foreach (var a in areas)
        {
            if (a.Start is null)
            {
                if (a.IsDb) parts.Add("DB");
                else foreach (var (id, _) in areaEntries.Overlapping(a)) Hit(id, false);   // 点表地址本身算不出范围（罕见）
                continue;
            }
            foreach (var (id, other) in areaEntries.Overlapping(a, includeWholeArea: false)) Hit(id, a.IsResolved && other.IsResolved);
            parts.Add("W:" + a.Name);
            if (a.IsDb) { parts.Add("W:DB"); parts.Add("S"); }
        }
        var shared = SharedFor(string.Join("|", parts));
        (long, string) Of(int i) => (entries[i].UnitId, entries[i].StatementId);
        var certainStmts = specific.Where(kv => kv.Value).Select(kv => Of(kv.Key)).ToHashSet();
        var specificAll = specific.Keys.OrderBy(i => i).Select(Of).Distinct().Where(x => !shared.Set.Contains(x)).ToArray();
        var specificUncertain = specificAll.Where(x => !certainStmts.Contains(x)).ToArray();
        var certainInShared = certainStmts.Where(shared.Set.Contains).ToHashSet();
        IReadOnlyList<(long, string)> all = shared.Items.Length == 0 ? specificAll : new StatementView(specificAll, shared.Items, null);
        IReadOnlyList<(long, string)> uncertain = shared.Items.Length == 0 ? specificUncertain
            : new StatementView(specificUncertain, shared.Items, certainInShared.Count == 0 ? null : certainInShared);
        return writerCache[key] = (all, uncertain);
    }

    /// <summary>共享集合：U=区未知、L=局部区、S=符号名、DB=DB 区全部写入、W:区=该区范围未知的写入。按组合缓存。</summary>
    Shared SharedFor(string key)
    {
        if (sharedCache.TryGetValue(key, out var hit)) return hit;
        var indices = new List<int>();
        foreach (var part in key.Split('|'))
            indices.AddRange(part switch
            {
                "U" => unknownEntries,
                "L" => localEntries,
                "S" => symbolicEntries,
                "DB" => dbEntries,
                _ when part.StartsWith("W:", StringComparison.Ordinal) && wholeAreaEntries.TryGetValue(part[2..], out var w) => w,
                _ => []
            });
        var items = indices.Distinct().OrderBy(i => i).Select(i => (entries[i].UnitId, entries[i].StatementId)).Distinct().ToArray();
        return sharedCache[key] = items.Length == 0 ? Shared.Empty : new Shared(items);
    }

    /// <summary>两段语句的只读拼接：first 与 second 不重叠；second 中 exclude 里的语句跳过。</summary>
    sealed class StatementView(IReadOnlyList<(long, string)> first, IReadOnlyList<(long, string)> second, HashSet<(long, string)>? exclude)
        : IReadOnlyList<(long UnitId, string StatementId)>
    {
        (long, string)[]? materialized;

        public int Count { get; } = first.Count + second.Count - (exclude?.Count ?? 0);

        public (long UnitId, string StatementId) this[int index] => (materialized ??= this.ToArray())[index];

        public IEnumerator<(long UnitId, string StatementId)> GetEnumerator()
        {
            foreach (var x in first) yield return x;
            foreach (var x in second) if (exclude is null || !exclude.Contains(x)) yield return x;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>沿名字的段走前缀树：途经的写入是祖先（整体写父结构）；终点子树是同一变量与子成员；
    /// 经过非字面量下标的匹配只算可能写入。</summary>
    IEnumerable<(int Entry, bool Certain)> TrieMatches(IReadOnlyList<string> segs)
    {
        var frontier = new List<(Node Node, bool Certain)> { (trie, true) };
        for (var i = 0; i < segs.Count && frontier.Count > 0; i++)
        {
            var next = new List<(Node, bool)>();
            foreach (var (node, certain) in frontier)
            {
                foreach (var e in node.Ends) yield return (e, certain);
                var seg = segs[i];
                if (!IsSubscript(seg))
                {
                    if (node.Children.TryGetValue(seg, out var child)) next.Add((child, certain));
                }
                else if (IrBlock.LiteralIndex.IsMatch(seg))
                {
                    if (node.Children.TryGetValue(Key(seg), out var child)) next.Add((child, certain));
                    if (node.Wild is not null) next.Add((node.Wild, false));
                }
                else
                {
                    foreach (var (k, child) in node.Children) if (IsSubscript(k)) next.Add((child, false));
                    if (node.Wild is not null) next.Add((node.Wild, false));
                }
            }
            frontier = next;
        }
        foreach (var (node, certain) in frontier)
            foreach (var e in node.Subtree()) yield return (e, certain);
    }

    /// <summary>文字段（源码、顺控动作、未识别元素）里提到该名字：①名字本身或其成员（前后不紧邻 [A-Za-z0-9_]，忽略大小写）；
    /// ②任一祖先前缀被整体提到（后面不跟成员 '.' 或下标 '['），如「DB.Cmd := X」算提到 DB.Cmd.Run；
    /// ③祖先前缀后跟非整数字面量下标，如「Arr[#i] := …」算提到 Arr[3]；
    /// ④名字自己的下标不是整数字面量时，文字里同一前缀后的任一下标代入后再按①~③判断，如「Arr[2] := …」算提到 Arr[#i]。
    /// 名字是 I/Q/M 点时，提到覆盖它的其它标签名、绝对地址（Q1.3、QB1、M 1.3 等，含德文 E/A）、该区或跨区的间接寻址、
    /// 指针常数 P#M… 也算。文字里的 DB 绝对地址不换算到符号名。</summary>
    public bool MentionedInText(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var key = TextMentions.Unquote(name.Trim());
        if (mentionCache.TryGetValue(key, out var hit)) return hit;
        mentionCache[key] = false;   // 代入下标时防环
        return mentionCache[key] = ComputeMention(key);
    }

    static string Join(IReadOnlyList<string> segs, int from, int to)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = from; i < to; i++)
        {
            if (i > from && !IsSubscript(segs[i])) sb.Append('.');
            sb.Append(segs[i]);
        }
        return sb.ToString();
    }

    bool ComputeMention(string key)
    {
        if (texts.Mentions(key)) return true;
        var segs = IrBlock.Segments(key);
        for (var k = 0; k < segs.Count - 1; k++)
        {
            var prefix = Join(segs, 0, k + 1);
            var next = segs[k + 1];
            if (texts.MentionsWhole(prefix, nonLiteralIndexCounts: IsSubscript(next))) return true;
            if (!IsSubscript(next) || IrBlock.LiteralIndex.IsMatch(next)) continue;
            var rest = Join(segs, k + 2, segs.Count);
            foreach (var index in texts.IndexesAfter(prefix))
            {
                var variant = prefix + index + (rest.Length == 0 ? "" : "." + rest);
                if (!C.Equals(variant, key) && MentionedInText(variant)) return true;
            }
        }
        var areas = resolver.Resolve(key).Where(a => !a.IsDb).ToArray();
        return areas.Length > 0
            && (texts.MentionsAddress(areas) || resolver.AliasNames(areas).Any(n => !C.Equals(n, key) && texts.Mentions(n)));
    }

    /// <summary>a 与 b 是同一变量、一方是另一方的父结构/成员、下标可能同址，或两个绝对地址的位范围重叠（不看点表）。忽略大小写。</summary>
    public static bool Overlaps(string a, string b)
    {
        a = a.Trim(); b = b.Trim();
        if (C.Equals(a, b) || IrBlock.MayOverlap(IrBlock.Segments(a), IrBlock.Segments(b))) return true;
        var ra = PlcAddress.Resolver.Empty.Resolve(a);
        if (ra.Count == 0) return false;
        var rb = PlcAddress.Resolver.Empty.Resolve(b);
        return ra.Any(x => rb.Any(y => x.Overlaps(y)));
    }

    /// <summary>名字的根段（第一个 '.' 或 '[' 之前）。</summary>
    public static string Root(string name) => IrBlock.RootOf(name);

    /// <summary>一段文字：IsSource 为源码语句（STL/SCL），否则为顺控动作、未识别元素；IsFc 为 FC 块里的文字。</summary>
    internal readonly record struct TextEntry(string Text, bool IsSource, bool IsFc);

    /// <summary>文字提及的一次性索引：按 [A-Za-z0-9_] 分词（另把每个非 ASCII 字符单独记一词）建倒排表，查询时只在含全部词的
    /// 文字里核对边界；绝对地址、间接寻址、指针常数的提及在建索引时一次扫出。</summary>
    sealed class TextMentions
    {
        readonly string[] texts;
        readonly Dictionary<string, HashSet<int>> postings = new(StringComparer.Ordinal);
        readonly PlcAddress.AreaIndex addresses = new();

        const RegexOptions Ri = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        static readonly Regex AddressMention = new(
            @"(?<![A-Za-z0-9_])%?([IEQAM])(?:X?[ \t]*(\d+)\.([0-7])(?![0-9])|([BWD])[ \t]*(\d+)(?![0-9.]))", Ri);

        /// <summary>方括号前的词：区字母（+宽度）→ 该区间接寻址；只有宽度或什么都没有、且括号里有地址寄存器写法 → 跨区间接寻址；
        /// 其它（符号数组、DB/DI/L 区）不算。紧跟在 . " # ' 之后的是成员访问。</summary>
        static readonly Regex BracketMention = new(@"(?<![A-Za-z0-9_.""#'])([A-Za-z]*)[ \t]*\[([^\]\r\n]*)", Ri);
        static readonly Regex AreaWord = new(@"^([IEQAM])[XBWD]?$", Ri);
        static readonly Regex WidthWord = new(@"^[XBWD]?$", Ri);
        static readonly Regex RegisterText = new(Register, Ri);
        /// <summary>指针常数 P#M 0.0 BYTE 10：按整区算。</summary>
        static readonly Regex PointerMention = new(@"(?<![A-Za-z0-9_#])P#[ \t]*([IEQAM])(?=[ \t]*\d)", Ri);
        /// <summary>方括号里只有整数字面量（数组元素，如 i[1]、Arr[2, 3]）：不是寄存器/内存间接寻址。</summary>
        static readonly Regex IntegerSubscript = new(@"^[ \t]*\d+(?:[ \t]*,[ \t]*\d+)*[ \t]*$", Ri);
        /// <summary>同一行里紧挨在操作数前面的写指令：STL 的 T / = / S / R / FP / FN（与 QWriteIndex 同口径；「:=」「==」不是）。</summary>
        static readonly Regex WriteInstructionBefore = new(@"(?:(?<![A-Za-z0-9_])(?:[TSR]|F[PN])[ \t]+|(?<![:<>=])=[ \t]*)$", Ri);

        public TextMentions(IReadOnlyList<TextEntry> list)
        {
            // 名字提及按去引号后的文字判断（"DB".成员 → DB.成员）；绝对地址、间接寻址、指针常数仍在原文上扫（只会多算）
            texts = list.Select(x => WidenSubscripts(Unquote(x.Text))).ToArray();
            for (var i = 0; i < texts.Length; i++)
            {
                foreach (var token in Tokens(texts[i]))
                {
                    if (!postings.TryGetValue(token, out var set)) postings[token] = set = [];
                    set.Add(i);
                }
                var text = list[i].Text;
                foreach (Match m in AddressMention.Matches(text))
                    if (PlcAddress.FromMatch(m) is { } a) addresses.Add(a, i);
                if (list[i].IsFc) continue;   // FC 里的间接寻址与指针常数不按区计（见 ProjectWrites 类说明）
                foreach (Match m in PointerMention.Matches(text))
                    addresses.Add(PlcAddress.WholeArea(m.Groups[1].Value), i);
                foreach (Match m in BracketMention.Matches(text))
                {
                    var word = m.Groups[1].Value;
                    var inside = m.Groups[2].Value;
                    if (IntegerSubscript.IsMatch(inside)) continue;
                    // 源码里只有写指令的操作数才可能被写；读指令（L/A/O 等）读到的间接地址不算提及
                    if (list[i].IsSource && !WrittenOperand(text, m.Index)) continue;
                    if (AreaWord.Match(word) is { Success: true } area) addresses.Add(PlcAddress.WholeArea(area.Groups[1].Value), i);
                    else if (WidthWord.IsMatch(word) && RegisterText.IsMatch(inside))
                        foreach (var letter in new[] { "I", "Q", "M" }) addresses.Add(PlcAddress.WholeArea(letter), i);
                }
            }
        }

        static readonly Regex Quoted = new("\"([^\"\\r\\n]+)\"", RegexOptions.CultureInvariant);

        /// <summary>去掉符号名的双引号（博图文字里全局名、含特殊字符的成员名带引号，IR 里的名字不带）：「"DB".Arr[3].On」→「DB.Arr[3].On」、
        /// 「A."B C"」→「A.B C」。引号紧贴字母数字时补一个空格，免得两个词粘成一个词而漏掉提及；只写「"DB"」时去引号后仍是整体提到 DB。</summary>
        internal static string Unquote(string text) =>
            text.IndexOf('"') < 0 ? text : Quoted.Replace(text, m =>
            {
                var before = m.Index > 0 && IsWord(text[m.Index - 1]) ? " " : "";
                var end = m.Index + m.Length;
                var after = end < text.Length && IsWord(text[end]) ? " " : "";
                return before + m.Groups[1].Value + after;
            });

        static readonly Regex Subscript = new(@"\[[^\[\]\r\n]*\]", RegexOptions.CultureInvariant);
        static readonly Regex StrictLiteralSubscript = new(@"^\[\d+(?:,\d+)*\]$", RegexOptions.CultureInvariant);
        /// <summary>带参数的调用：标识符（或下标、引号收尾）后紧跟的括号里有「:=」；以及 STL 的 CALL 与输出赋值「=>」。</summary>
        static readonly Regex ParameterCall = new(@"[A-Za-z0-9_\]]\s*\([^()]*:=|=>|(?<![A-Za-z0-9_])CALL(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>审查 I-2：下标放宽成「整个数组」（换成非字面量下标 [#*]，按规则③算提到整个数组）——
        /// 文字里有块移动/填充类指令（BlockWriteInstructions，与 IR 的 Widen 同一份名单）或带参数的调用（数组元素可能是 OUT/InOut 的起始地址）时，
        /// 所有下标都放宽；否则只放宽不是紧凑整数字面量的下标（带空格、表达式、变量）。</summary>
        internal static string WidenSubscripts(string text)
        {
            if (text.IndexOf('[') < 0) return text;
            var all = ParameterCall.IsMatch(text) || Tokens(text).Any(t => BlockWriteInstructions.Contains(t));
            return Subscript.Replace(text, m => all || !StrictLiteralSubscript.IsMatch(m.Value) ? "[#*]" : m.Value);
        }

        /// <summary>at 处的操作数前面（同一行）紧挨着写指令。</summary>
        static bool WrittenOperand(string text, int at)
        {
            var lineStart = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
            return WriteInstructionBefore.IsMatch(text[lineStart..at]);
        }

        static bool IsWord(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

        /// <summary>ASCII 词（小写）与每个非 ASCII 字符（大写，前缀 \u0001 以区别于词）。</summary>
        static IEnumerable<string> Tokens(string s)
        {
            var start = -1;
            for (var i = 0; i <= s.Length; i++)
            {
                if (i < s.Length && IsWord(s[i])) { if (start < 0) start = i; continue; }
                if (start >= 0) { yield return s[start..i].ToLowerInvariant(); start = -1; }
                if (i < s.Length && s[i] > 127) yield return "\u0001" + char.ToUpperInvariant(s[i]);
            }
        }

        /// <summary>含名字全部词的文字：名字里的每个 ASCII 词在命中处必是完整的词，取倒排交集不会漏。</summary>
        IEnumerable<string> Candidates(string name)
        {
            var keys = Tokens(name).Distinct(StringComparer.Ordinal).ToArray();
            if (keys.Length == 0) return texts;
            var sets = new HashSet<int>[keys.Length];
            for (var k = 0; k < keys.Length; k++)
            {
                if (!postings.TryGetValue(keys[k], out var set)) return [];
                sets[k] = set;
            }
            Array.Sort(sets, (x, y) => x.Count.CompareTo(y.Count));
            return sets[0].Where(i => sets.Skip(1).All(s => s.Contains(i))).Select(i => texts[i]);
        }

        /// <summary>名字在文字里出现、前面不紧邻 [A-Za-z0-9_] 的位置（返回名字之后的下标）。</summary>
        static IEnumerable<int> Ends(string text, string name)
        {
            for (var i = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = i + 1 < text.Length ? text.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase) : -1)
                if (i == 0 || !IsWord(text[i - 1])) yield return i + name.Length;
        }

        /// <summary>与正则 (?&lt;![A-Za-z0-9_])名字(?![A-Za-z0-9_])（忽略大小写）同口径。</summary>
        public bool Mentions(string name) =>
            Candidates(name).Any(text => Ends(text, name).Any(end => end >= text.Length || !IsWord(text[end])));

        /// <summary>前缀被整体提到：后面不跟 [A-Za-z0-9_]、成员 '.' 或下标 '['；nonLiteralIndexCounts 时，后跟非整数字面量下标也算。</summary>
        public bool MentionsWhole(string prefix, bool nonLiteralIndexCounts) =>
            Candidates(prefix).Any(text => Ends(text, prefix).Any(end =>
            {
                var j = end;
                if (j >= text.Length) return true;
                if (IsWord(text[j]) || text[j] == '.') return false;
                while (j < text.Length && text[j] is ' ' or '\t') j++;
                if (j >= text.Length || text[j] != '[') return true;
                if (!nonLiteralIndexCounts) return false;
                var close = text.IndexOf(']', j);
                return close < 0 || !IrBlock.LiteralIndex.IsMatch(text[j..(close + 1)]);
            }));

        /// <summary>文字里紧跟在前缀后面的下标（含方括号，去重）。</summary>
        public IEnumerable<string> IndexesAfter(string prefix) =>
            Candidates(prefix).SelectMany(text => Ends(text, prefix).Select(end =>
            {
                var j = end;
                while (j < text.Length && text[j] is ' ' or '\t') j++;
                if (j >= text.Length || text[j] != '[') return null;
                var close = text.IndexOf(']', j);
                return close < 0 ? null : text[j..(close + 1)];
            })).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        public bool MentionsAddress(IReadOnlyList<PlcAddress.Area> areas) => areas.Any(a => addresses.Overlapping(a).Any());
    }
}

/// <summary>PLC 绝对地址：I/Q/M 区（德文 E→I、A→Q）位/字节/字/双字，DB 区 %DBn.DBX/B/W/D；位切片「名.%X3」按根段换算
/// （字节标签精确到位，其余按整个标签范围）。与 UnderstandingRuleSource.QWriteIndex 的 Q 区换算同法。</summary>
internal static class PlcAddress
{
    const RegexOptions Ri = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Reg = ProjectWrites.Register;

    /// <summary>区（I/Q/M/DBn；「DB」表示某个 DB、编号未知）与位范围（含两端）；Start/End 为 null 表示该区内范围未知（整区）。</summary>
    public readonly record struct Area(string Name, int? Start, int? End)
    {
        public bool IsDb => Name.StartsWith("DB", StringComparison.Ordinal);

        /// <summary>区与范围都确定。</summary>
        public bool IsResolved => Start is not null && Name != "DB";

        public bool Overlaps(Area o)
        {
            if (Name != o.Name) return IsDb && o.IsDb && (Name == "DB" || o.Name == "DB");
            if (Start is null || o.Start is null) return true;
            return Start <= o.End && o.Start <= End;
        }
    }

    /// <summary>「某个 DB，范围未知」：与所有 DB 区地址重叠。</summary>
    public static readonly Area AnyDb = new("DB", null, null);

    static readonly Regex Io = new(@"^%?([IEQAM])(?:X?\s*(\d+)\.([0-7])|([BWD])\s*(\d+))$", Ri);
    static readonly Regex Db = new(@"^%?DB\s*(\d+)\s*\.\s*DB(?:X\s*(\d+)\.([0-7])|([BWD])\s*(\d+))$", Ri);
    static readonly Regex Slice = new(@"^(.+)\.%?([XBWD])(\d+)$", Ri);
    /// <summary>算不出范围的区访问：% 开头的 I/Q/M 写法（%Q:类型:偏移 等）；带区字母的寄存器间接（MB[AR1,P#0.0]、
    /// M?LReal[AR1,P#0.0]，括号里须有作为独立记号的 P#/AR1/AR2，免得把名为 A、M 的符号数组当成绝对区）。</summary>
    static readonly Regex IoUnresolved = new(
        @"^(?:%([IEQAM])|([IEQAM])(?:[XBWD]|\?\w*)?\s*\[[^\]]*" + Reg + ")", Ri);
    static readonly Regex DbUnresolved = new(
        @"^(?:%D[BI]|D[BI][XBWD]?(?:\?\w*)?\s*\[[^\]]*" + Reg + @"|DB\s*\d+\s*\.\s*D[BI][XBWD]?\s*\[)", Ri);
    /// <summary>局部区：寄存器间接 L[AR1,P#0.0]、LB[…]、L?LReal[…]；绝对地址 %Local0.0、%LocalB3、%Local:LReal:64。</summary>
    static readonly Regex LocalArea = new(@"^(?:L(?:[XBWD]|\?\w*)?\s*\[[^\]]*" + Reg + @"|%Local(?![A-Za-z_])|%Local[XBWD:]|%L(?=[XBWD]?\s*\d))", Ri);
    /// <summary>区未知的写入目标：翻译器标为〔…〕的认不出的访问、换算不了的指针常数、方括号里有地址寄存器写法而前面没有区字母。
    /// 符号名里的空白是合法字符（博图允许），不据此判定。</summary>
    static readonly Regex Unknown = new(@"^\s*〔|^P#|\[[^\]]*" + Reg, Ri);
    /// <summary>指针常数：P#M0.0 BYTE 10、P#DB5.DBX0.0 WORD 2、P#DBX0.0 BYTE 4（DB 号未知）；类型与长度可省。</summary>
    static readonly Regex Pointer = new(
        @"^P#\s*(?:([IEQAM])\s*|DB\s*(\d+)\s*\.\s*DB[XBWD]?\s*|(D[BI])[XBWD]?\s*)(\d+)\.([0-7])(?:\s+([A-Za-z_]+)\s+(\d+))?\s*$", Ri);
    static readonly Regex PointerArea = new(@"^P#\s*(?:([IEQAM])(?=\s*\d)|(D[BI]))", Ri);

    /// <summary>指针常数元素类型的字节数；BOOL 按位计（返回 0）。不认识返回 null。</summary>
    static int? ElementBytes(string type) => type.ToUpperInvariant() switch
    {
        "BOOL" => 0,
        "BYTE" or "CHAR" or "SINT" or "USINT" => 1,
        "WORD" or "INT" or "UINT" or "S5TIME" or "DATE" or "WCHAR" => 2,
        "DWORD" or "DINT" or "UDINT" or "REAL" or "TIME" or "TOD" or "TIME_OF_DAY" => 4,
        "LWORD" or "LINT" or "ULINT" or "LREAL" or "LTIME" => 8,
        _ => null
    };

    public static bool IsPointer(string target) => target.StartsWith("P#", StringComparison.OrdinalIgnoreCase);

    /// <summary>指针常数（以 P# 开头）占的地址：见 ParsePointer；区未知或不是指针常数返回 null。</summary>
    public static Area? ParsePointerConstant(string target)
    {
        var t = target.Trim();
        return IsPointer(t) ? ParsePointer(t) : null;
    }

    public static bool IsLocalArea(string target) => LocalArea.IsMatch(target);

    /// <summary>指针常数 → 范围（有类型与长度时精确到字节/位；非 BOOL 且起始位不在字节边界时按整字节放大）、
    /// 该区整区（缺类型长度或类型不认识）、任意 DB（DB 号未知）；区未知返回 null。</summary>
    static Area? ParsePointer(string t)
    {
        var m = Pointer.Match(t);
        if (m.Success)
        {
            if (m.Groups[3].Success) return AnyDb;
            var area = m.Groups[1].Success ? Norm(m.Groups[1].Value)
                : int.TryParse(m.Groups[2].Value, out var db) ? "DB" + db : null;
            if (area is null) return AnyDb;
            if (m.Groups[6].Success && ElementBytes(m.Groups[6].Value) is { } size
                && int.TryParse(m.Groups[4].Value, out var b) && b <= 1_000_000
                && int.TryParse(m.Groups[7].Value, out var n) && n is > 0 and <= 1_000_000)
            {
                var start = b * 8 + (m.Groups[5].Value[0] - '0');
                if (size == 0) return new Area(area, start, start + n - 1);
                var last = start + n * size * 8 - 1;
                return new Area(area, start / 8 * 8, last / 8 * 8 + 7);
            }
            return new Area(area, null, null);
        }
        var a = PointerArea.Match(t);
        if (!a.Success) return null;
        return a.Groups[1].Success ? WholeArea(a.Groups[1].Value) : AnyDb;
    }

    static string Norm(string letter) => char.ToUpperInvariant(letter[0]) switch { 'E' => "I", 'A' => "Q", var c => c.ToString() };

    public static Area WholeArea(string letter) => new(Norm(letter), null, null);

    /// <summary>不是点表名、不是可换算的地址、不是局部区时，判断写入目标是否区未知（对任何名字都可能写入）。
    /// 认不出的访问由翻译器写成〔…〕（SymbolReader/FlgNetTranslator），名字里的空白不算。</summary>
    public static bool IsUnknownTarget(string target) => Unknown.IsMatch(target);

    static Area Range(string area, Group bitByte, Group bit, Group width, Group start)
    {
        if (bitByte.Success)
        {
            if (!int.TryParse(bitByte.Value, out var b) || b > 1_000_000) return new(area, null, null);
            var x = b * 8 + (bit.Value[0] - '0');
            return new(area, x, x);
        }
        if (!int.TryParse(start.Value, out var s) || s > 1_000_000) return new(area, null, null);
        var w = char.ToUpperInvariant(width.Value[0]) switch { 'B' => 1, 'W' => 2, _ => 4 };
        return new(area, s * 8, (s + w) * 8 - 1);
    }

    /// <summary>绝对地址本身（不看点表）；不是绝对地址返回 null。</summary>
    public static Area? Parse(string text)
    {
        var t = text.Trim();
        var m = Io.Match(t);
        if (m.Success) return Range(Norm(m.Groups[1].Value), m.Groups[2], m.Groups[3], m.Groups[4], m.Groups[5]);
        m = Db.Match(t);
        if (m.Success)
            return int.TryParse(m.Groups[1].Value, out var db) ? Range("DB" + db, m.Groups[2], m.Groups[3], m.Groups[4], m.Groups[5]) : AnyDb;
        return null;
    }

    /// <summary>文字里的绝对地址匹配（组号与 Io 相同）。</summary>
    public static Area? FromMatch(Match m) =>
        m.Success ? Range(Norm(m.Groups[1].Value), m.Groups[2], m.Groups[3], m.Groups[4], m.Groups[5]) : null;

    static Area Narrow(Area a, char kind, int n)
    {
        if (a.Start is { } s && a.End is { } e && e - s == 7 && s % 8 == 0)
        {
            if (kind == 'X' && n is >= 0 and < 8) return a with { Start = s + n, End = s + n };
            if (kind == 'B' && n == 0) return a;
        }
        return a;
    }

    static Area? Unresolved(string t)
    {
        if (IsPointer(t)) return ParsePointer(t);
        if (DbUnresolved.IsMatch(t)) return AnyDb;
        var m = IoUnresolved.Match(t);
        if (!m.Success) return null;
        return WholeArea(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    /// <summary>名字 → 它占的地址范围（按点表与绝对地址）。空 = 符号名（不在点表、也不是绝对地址）或认不出的目标。</summary>
    public sealed class Resolver
    {
        public static readonly Resolver Empty = new([]);
        readonly Dictionary<string, List<Area>> tagAreas = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> tagNames = [];
        readonly AreaIndex tagIndex = new();

        public Resolver(IReadOnlyList<PipelineTag> tags)
        {
            foreach (var t in tags)
            {
                if (string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Address)) continue;
                if ((Parse(t.Address) ?? Unresolved(t.Address.Trim())) is not { } a) continue;
                var name = t.Name.Trim();
                if (!tagAreas.TryGetValue(name, out var list)) tagAreas[name] = list = [];
                list.Add(a);
                tagIndex.Add(a, tagNames.Count);
                tagNames.Add(name);
            }
        }

        public IReadOnlyList<Area> Resolve(string name) => Resolve(name.Trim(), 0);

        IReadOnlyList<Area> Resolve(string t, int depth)
        {
            var result = new List<Area>();
            if (Parse(t) is { } direct) result.Add(direct);
            if (tagAreas.TryGetValue(t, out var own)) result.AddRange(own);
            if (depth == 0 && Slice.Match(t) is { Success: true } m)
            {
                var kind = char.ToUpperInvariant(m.Groups[2].Value[0]);
                var n = int.TryParse(m.Groups[3].Value, out var v) ? v : -1;
                result.AddRange(Resolve(m.Groups[1].Value.Trim(), 1).Select(a => Narrow(a, kind, n)));
            }
            if (result.Count == 0 && Unresolved(t) is { } u) result.Add(u);
            return result;
        }

        /// <summary>点表里地址与这些范围重叠的标签名。</summary>
        public IEnumerable<string> AliasNames(IReadOnlyList<Area> areas) =>
            areas.SelectMany(a => tagIndex.Overlapping(a)).Select(x => x.Id).Distinct().Select(i => tagNames[i]).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>按区与字节索引的地址集合（编号由调用方给）：Overlapping 只核对同区相关字节、跨很多字节的少量宽条目、
    /// 整区未知的条目，不全表扫描。</summary>
    public sealed class AreaIndex
    {
        const int MaxIndexedBytes = 64;
        readonly List<(Area Area, int Id)> items = [];
        readonly Dictionary<(string Area, int Byte), List<int>> byByte = [];
        readonly Dictionary<string, List<int>> wide = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<int>> wholeArea = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<int>> byArea = new(StringComparer.Ordinal);
        readonly List<int> db = [];

        static void Put<TKey>(Dictionary<TKey, List<int>> map, TKey key, int item) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(item);
        }

        public void Add(Area area, int id)
        {
            var item = items.Count;
            items.Add((area, id));
            Put(byArea, area.Name, item);
            if (area.IsDb) db.Add(item);
            if (area.Start is { } s && area.End is { } e)
            {
                if (e / 8 - s / 8 >= MaxIndexedBytes) Put(wide, area.Name, item);
                else for (var b = s / 8; b <= e / 8; b++) Put(byByte, (area.Name, b), item);
            }
            else Put(wholeArea, area.Name, item);
        }

        /// <summary>与 q 重叠的条目（编号与登记时的地址）。includeWholeArea=false 时不含范围未知的条目（调用方另行处理）。</summary>
        public IEnumerable<(int Id, Area Area)> Overlapping(Area q, bool includeWholeArea = true)
        {
            IEnumerable<int> candidates;
            if (q.Name == "DB") candidates = db;
            else if (q.Start is { } s && q.End is { } e)
            {
                candidates = (e / 8 - s / 8 >= MaxIndexedBytes
                        ? byArea.TryGetValue(q.Name, out var all0) ? all0 : []
                        : Enumerable.Range(s / 8, e / 8 - s / 8 + 1).SelectMany(b => byByte.TryGetValue((q.Name, b), out var l) ? l : (IEnumerable<int>)[]))
                    .Concat(wide.TryGetValue(q.Name, out var wl) ? wl : []);
                if (includeWholeArea)
                {
                    candidates = candidates.Concat(wholeArea.TryGetValue(q.Name, out var w) ? w : []);
                    if (q.IsDb) candidates = candidates.Concat(wholeArea.TryGetValue("DB", out var anyDb) ? anyDb : []);
                }
            }
            else candidates = byArea.TryGetValue(q.Name, out var all) ? all : [];
            return candidates.Distinct().Where(i => items[i].Area.Overlaps(q) && (includeWholeArea || items[i].Area.Start is not null))
                .Select(i => (items[i].Id, items[i].Area));
        }
    }
}
