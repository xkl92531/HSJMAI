#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>代码证明出的关系。Members 为点表规范名（序数排序）；Proof 为 UnderstandingProofs 的 Code:* 或 Review:*。</summary>
public sealed record ProvenRelation(string Operator, IReadOnlyList<string> Members, string Proof, IReadOnlyList<string> Evidence);

/// <summary>一个程序段里 %Q 输出之间的互斥/同步证明（spec §7.1「瞬时断言须代码证明」）。只看结构：地址区、写入位置与次数、表达式，
/// 不看名字。判故障级要求 100% 准确，凡拿不准一律不证明（只会漏）：
/// · 输出定义：本段唯一的无条件赋值，或 EN 恒真的 FB 调用输出引脚（单目标、不取反，经 CallOutputDefinition 代入），
///   写入唯一 Bool %Q；该 %Q 全项目只有这一处写入（Q 区索引计 1、无算不出范围的 Q 区写入、ProjectWrites 只有这一条且不是
///   不确定写入、文字段不提到）。
/// · 块有跳转/返回（裁定 F2）、FC 块（用户：FC 完全不管）、程序段不在写入索引里、段内语句编号重复 → 一律不证明。
/// · 语句顺序：只有 SCL/STL 源码根的段（ExclusionProof.StatementOrderTrusted）按语句序号推断先后；FlgNet、顺控等段（裁定 F1）
///   里本段可能写到的变量每次读取都是独立的自由值，「后一条拷贝前一条」「实例输出读取展开」都不做；唯一的例外是结构证据：
///   值以合取方式含某个 EN 恒真调用盒的 ENO 原子（能流经过该盒子，必在该调用执行之后求值），且本段只有这次调用写该实例时，
///   对该实例 Bool 输出的读取按本次调用的定义展开（每次读取各用独立标签），同步判定也把两条拷贝里同一调用的 ENO 原子、
///   该调用实例输出的读取视为同一次执行的同值量。
/// · 段内写入（WritesOf / 区间稳定共用 MayWrite）：ProjectWrites 的写入（赋值/置位/复位、调用引脚与背景实例、指令盒顺带写入
///   SideWrites——背景实例、边沿存储位、InOut 端口；父子/下标/地址别名重叠、不确定写入）落在该语句上；表达式里定时器/计数器的
///   背景实例；调用 InOut 与指针类形参的实参；FB 调用的块体（递归）写到的一切（ProjectWrites 里被调块各段的写入）；
///   本块 InOut 形参（实参可能是任何变量）与本块背景 DB 的全局名（可能与 Static/形参同址）两类别名；
///   顺控结构（步/转移/分支/连接）与转移/互锁/监控条件（Condition）只改顺控背景实例内部状态，不计用户变量写入；顺控动作只写
///   其文字提到的名字（ProjectWrites.MentionedInText 同口径，含点表地址别名），另：动作提到本块 InOut 形参、或问的就是 InOut 形参时
///   视为可能写到（引用调用方变量）；被调块体里有顺控动作时整个调用按「可能写任何量」处理；
///   以下语句视为可能写任何量：文字段（源码、未识别元素）、目标为「?」或认不出的〔…〕、
///   调用 FC/没有程序体/不在索引/块体含上述语句的块、DB_ANY 等引用类型实参、含边沿/指令/调用输出/非纯函数却没有 SideWrites
///   记录的语句（顺序不可信的段里只要本段别的语句记了 SideWrites 或有调用就按翻译器已记录处理）。
/// · 稳定：IEC 定时器/计数器（及没有程序体的系统块）背景实例下的成员访问即更新，任何段内都不稳定（每次读取独立）；
///   与不确定写入重叠、被文字段提到的量不稳定；%I 与「程序里有确定写入者」的量在段内没有写入时视为稳定。
///   已知限制（裁定 F8，只在此处成立）：段内相邻语句之间的程序外写入（HMI/通讯）、中断、系统/时钟存储位不建模——
///   程序里有写入者的量假定不被程序外同时改写；程序从不写入的非 %I 量视为可能随时变化。「有写入者」按名字判断，
///   不同块里同名的 Static 会互相作证（同属 F8 放宽）。
/// · 名字里有非整数字面量下标的读取（Arr[Idx]、Arr[#i]）每次都是独立的自由值，不参与任何同值推理（审查 C-1）。
///   另：「扫描结束前不再被改写」只看 IR 可见的写入，项目里别处调用的 FC 若因导出不完整漏掉写入则看不到。
/// · 同一调用的各输出引脚来自同一次执行，共用原子标签；之后对实例输出的每一次读取各用独立标签（同一语句里读两次也算两次）。</summary>
public sealed class NetworkProofs
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    /// <summary>自由读取原子的标签（不得含「|」）。</summary>
    const string FreeTag = "〔独立读取〕";
    const int MaxCalleeDepth = 8;
    /// <summary>无状态、不写任何变量的函数（纯函数白名单之外，翻译器按只读部件处理的范围判断）。</summary>
    static readonly HashSet<string> NonWritingFunctions = new(ExclusionProof.PureFunctions, StringComparer.Ordinal) { "InRange", "OutRange" };
    static readonly HashSet<string> MemoryPointerTypes = new(StringComparer.OrdinalIgnoreCase) { "Any", "Variant", "Pointer" };

    sealed record OutputDef(string Signal, int WriteAt, string StatementId, IrExpr WithDefaults, IrExpr WithoutDefaults,
        bool FromCall, IrExpr? Plain, bool NonConstant);

    /// <summary>一条语句可能写到的东西。Anything：可能写任何量。Extra：按名字/地址重叠比较的额外目标；
    /// Targets：调用方名字下的全部写入目标（含 Extra 与被调块写的全局量），用于别名分类；CalleeUnits：FB 调用块体（递归）的程序段。</summary>
    sealed class Info
    {
        public bool Anything;
        /// <summary>顺控动作的文字（已去注释）：只写其提到的名字。</summary>
        public string? ActionText;
        public readonly List<string> Extra = [];
        public readonly List<string> Targets = [];
        public HashSet<long>? CalleeUnits;
    }

    sealed record CalleeEffect(HashSet<long> Units, List<string> Globals);

    sealed class Analysis(IrUnit unit, IrBlock block)
    {
        public IrUnit Unit { get; } = unit;
        public IrBlock Block { get; } = block;
        public bool Ordered;
        public bool Refused;
        public Info[] Infos = [];
        public Dictionary<string, int> IndexById = new(StringComparer.Ordinal);
        public readonly Dictionary<string, IReadOnlyList<int>> WritesOf = new(C);
        public IReadOnlyList<OutputDef>? Defs;
        public int FreeCounter, ReadCounter;
    }

    enum Kind { BlockOwned, InOut, Temp, Io, Global }

    readonly IReadOnlyDictionary<string, PipelineTag> unique;
    readonly IReadOnlyList<PipelineTag> tags;
    readonly Dictionary<(long Unit, int Index), ProjectWrites> actionMentions = new();
    readonly Func<string, int> qWriteCount;
    readonly bool qUnresolved;
    readonly ProjectWrites writes;
    readonly Func<string, IrBlock?> blockOf;
    readonly Func<string, bool> isFcBlock;
    readonly IReadOnlyDictionary<string, bool> constantBits;
    bool ConstantBit(string name) => constantBits.ContainsKey(name.Trim());
    readonly PlcAddress.Resolver resolver;
    readonly Dictionary<long, Analysis> cache = new();
    readonly Dictionary<string, CalleeEffect?> calleeCache = new(C);
    readonly HashSet<string> calleeVisiting = new(C);
    readonly Dictionary<string, Dictionary<long, HashSet<string>>> writersByUnit = new(C);
    readonly Dictionary<string, bool> volatileCache = new(C);
    readonly Dictionary<string, HashSet<string>?> ownInstances = new(C);
    /// <summary>访问即更新的背景实例（按实例名首段分组）。</summary>
    Dictionary<string, List<IReadOnlyList<string>>>? volatileInstances;

    /// <param name="isFcBlock">按块类型判断 FC（不看块名）；FC 块里的程序段不证明（FC 形参按引用、与实参同址，本类不建模）。必填。</param>
    /// <param name="constantBits">常量位（ConstantBits，点表规范名 → 恒定值）：读取时直接当常数代入，段内视为稳定。</param>
    public NetworkProofs(IReadOnlyList<PipelineTag> tags, Func<string, int> qWriteCount, bool qUnresolved, ProjectWrites writes,
        Func<string, IrBlock?> blockOf, Func<string, bool> isFcBlock, IReadOnlyDictionary<string, bool>? constantBits = null)
    {
        this.constantBits = constantBits ?? new Dictionary<string, bool>(C);
        ArgumentNullException.ThrowIfNull(isFcBlock);
        unique = ConstantOutputs.UniqueTags(tags);
        this.tags = tags;
        resolver = new PlcAddress.Resolver(tags);
        this.qWriteCount = qWriteCount; this.qUnresolved = qUnresolved; this.writes = writes; this.blockOf = blockOf;
        this.isFcBlock = isFcBlock;
    }

    // ───────────── 点表 ─────────────

    bool IsBoolArea(string n, string prefix) => unique.TryGetValue(n.Trim(), out var t)
        && string.Equals(t.DataType, "Bool", StringComparison.OrdinalIgnoreCase) && t.Address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    bool IsQ(string n) => IsBoolArea(n, "%Q");
    bool IsI(string n) => IsBoolArea(n, "%I");
    string Canonical(string n) => unique.TryGetValue(n.Trim(), out var t) ? t.Name : n.Trim();

    // ───────────── 分析入口 ─────────────

    Analysis Analyze(IrUnit unit, IrBlock block)
    {
        if (cache.TryGetValue(unit.UnitId, out var hit) && ReferenceEquals(hit.Unit, unit) && ReferenceEquals(hit.Block, block)) return hit;
        var a = new Analysis(unit, block);
        cache[unit.UnitId] = a;
        var statements = unit.Statements;
        a.Refused = block.HasControlFlow
            || statements.Any(s => s.Kind is IrStatementKinds.Jump or IrStatementKinds.JumpNot or IrStatementKinds.Return)
            || isFcBlock(unit.BlockName)
            || !writes.Indexes(unit.UnitId);
        for (var i = 0; i < statements.Count && !a.Refused; i++)
            if (!a.IndexById.TryAdd(statements[i].Id, i)) a.Refused = true;   // 编号重复：写入归属与定位都不可靠
        if (a.Refused) return a;
        a.Ordered = ExclusionProof.StatementOrderTrusted(unit);
        var covers = UnitCovers(unit);
        a.Infos = statements.Select(s => Build(s, unit, a.Ordered, covers, depth: 0)).ToArray();
        return a;
    }

    /// <summary>顺序不可信的段里，别的语句记了 SideWrites 或有调用：翻译器把共享部件的写入记在第一次求值它的语句上。</summary>
    static bool UnitCovers(IrUnit unit) => unit.Statements.Any(s => s.SideWrites is { Count: > 0 } || s.Call is not null);

    // ───────────── 写入 ─────────────

    Info Build(IrStatement s, IrUnit unit, bool ordered, bool covers, int depth)
    {
        var info = new Info();
        switch (s.Kind)
        {
            case IrStatementKinds.Assign: case IrStatementKinds.Set: case IrStatementKinds.Reset:
                if (string.IsNullOrWhiteSpace(s.Target)) info.Anything = true;
                break;
            case IrStatementKinds.Call:
                if (s.Call is null) info.Anything = true;
                break;
            case IrStatementKinds.Box: case IrStatementKinds.Label: case IrStatementKinds.Note:
            case IrStatementKinds.Jump: case IrStatementKinds.JumpNot: case IrStatementKinds.Return:
            // 顺控结构与转移/互锁/监控条件：只改顺控背景实例内部状态（步激活、转移、互锁/监控结果），不写用户变量
            case IrStatementKinds.Condition: case IrStatementKinds.Step: case IrStatementKinds.Transition:
            case IrStatementKinds.Branch: case IrStatementKinds.Connection:
                break;
            case IrStatementKinds.Action:
                // 动作里调用块/指令：被调方可能写文字里没提到的变量
                if (s.TextCallsBlock) info.Anything = true;
                else info.ActionText = SclTranslator.WrittenText(s.Text ?? "");
                break;
            default:   // 源码、未识别元素、未知种类
                info.Anything = true;
                break;
        }
        if (info.Anything) return info;
        foreach (var w in ProjectWrites.StatementWrites(s))
        {
            if (string.IsNullOrWhiteSpace(w.Target)) continue;
            var t = w.Target.Trim();
            if (t == "?" || t.StartsWith('〔')) { info.Anything = true; return info; }
            info.Targets.Add(t);
        }
        foreach (var e in ProjectWrites.Expressions(s).Select(x => IrBlock.ExpandNodesIn(unit, x)))
        {
            foreach (var instance in ProjectWrites.BoxInstancesIn(e)) { info.Extra.Add(instance); info.Targets.Add(instance); }
            if (HasWritingPart(e) && s.SideWrites is not { Count: > 0 } && (ordered || !covers)) { info.Anything = true; return info; }
        }
        if (s.Call is { } call)
        {
            foreach (var pin in call.Pins)
            {
                var type = pin.Type?.Trim() ?? "";
                if (IsReferenceType(type)) { info.Anything = true; return info; }
                if (pin.Section == "InOut" || MemoryPointerTypes.Contains(type))
                    foreach (var v in pin.Value?.Vars() ?? []) { info.Extra.Add(v.Trim()); info.Targets.Add(v.Trim()); }
            }
            if (Effect(call, depth) is not { } effect) { info.Anything = true; return info; }
            info.CalleeUnits = effect.Units;
            info.Extra.AddRange(effect.Globals);
            info.Targets.AddRange(effect.Globals);
        }
        return info;
    }

    /// <summary>引用一个块/一段内存、由被调块解引用后可写任何地方的形参类型。</summary>
    static bool IsReferenceType(string type) =>
        type.StartsWith("DB_ANY", StringComparison.OrdinalIgnoreCase) || type.StartsWith("REF_TO", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("Block_", StringComparison.OrdinalIgnoreCase);

    /// <summary>求值时可能写变量、但翻译器以 SideWrites 记录写入的部件：边沿、未展开的指令、调用输出、非纯函数。</summary>
    static bool HasWritingPart(IrExpr e) =>
        e.Kind is IrKinds.Rise or IrKinds.Fall or IrKinds.Instr or IrKinds.CallOut
        || e.Kind == IrKinds.Func && !(e.Name is { } f && NonWritingFunctions.Contains(f))
        || e.Args is not null && e.Args.Any(HasWritingPart);

    /// <summary>FB 调用块体（递归）的写入：可精确分析时返回块体程序段与写到的全局量；FC、没有程序体、不在写入索引、
    /// 块体有可能写任何量的语句、递归过深或成环 → null（可能写任何量）。</summary>
    CalleeEffect? Effect(IrCall call, int depth)
    {
        if (!C.Equals(call.BlockType?.Trim(), "FB")) return null;
        var name = call.Callee?.Trim();
        if (string.IsNullOrEmpty(name) || depth >= MaxCalleeDepth || calleeVisiting.Contains(name)) return null;
        if (calleeCache.TryGetValue(name, out var known)) return known;
        calleeVisiting.Add(name);
        CalleeEffect? result = null;
        try { result = ComputeEffect(name, depth); }
        finally { calleeVisiting.Remove(name); }
        return calleeCache[name] = result;
    }

    CalleeEffect? ComputeEffect(string name, int depth)
    {
        if (blockOf(name) is not { HasBody: true } callee) return null;
        var units = new HashSet<long>();
        var globals = new List<string>();
        foreach (var u in callee.Units)
        {
            if (u.Statements.Count == 0) continue;
            if (!writes.Indexes(u.UnitId)) return null;
            units.Add(u.UnitId);
            var ordered = ExclusionProof.StatementOrderTrusted(u);
            var covers = UnitCovers(u);
            foreach (var s in u.Statements)
            {
                var info = Build(s, u, ordered, covers, depth + 1);
                // 顺控动作按文字写入，调用方按名字问不全被调块动作写到的全局量：整个调用按可能写任何量处理
                if (info.Anything || info.ActionText is not null) return null;
                globals.AddRange(info.Targets.Where(t => !OwnedBy(callee, t)));
                if (info.CalleeUnits is { } nested) units.UnionWith(nested);
            }
        }
        return new CalleeEffect(units, globals.Distinct(C).ToList());
    }

    /// <summary>名字的根段是块的接口成员（Static/Temp/形参：写到背景实例里，或 InOut 由调用点实参另计）。</summary>
    static bool OwnedBy(IrBlock block, string name) => block.Interface.Any(m => C.Equals(m.Name, Bare(FirstSegment(name))));

    static string FirstSegment(string name) => IrBlock.Segments(name.Trim()) is { Count: > 0 } segs ? segs[0] : name.Trim();
    static string Bare(string segment) => segment.Trim().TrimStart('#').Trim('"');

    Dictionary<long, HashSet<string>> WritersByUnit(string name)
    {
        if (writersByUnit.TryGetValue(name, out var hit)) return hit;
        var map = new Dictionary<long, HashSet<string>>();
        foreach (var (unitId, statementId) in writes.Writers(name))
        {
            if (!map.TryGetValue(unitId, out var set)) map[unitId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(statementId);
        }
        return writersByUnit[name] = map;
    }

    /// <summary>语句 k 可能写到 name（写入回调与区间稳定共用，宁多勿少）。</summary>
    bool MayWrite(Analysis a, int k, string name)
    {
        var info = a.Infos[k];
        if (info.Anything) return true;
        if (info.ActionText is { } text && ActionMayWrite(a, k, text, name)) return true;
        var byUnit = WritersByUnit(name.Trim());
        if (byUnit.TryGetValue(a.Unit.UnitId, out var ids) && ids.Contains(a.Unit.Statements[k].Id)) return true;
        if (info.CalleeUnits is { } callee && callee.Any(byUnit.ContainsKey)) return true;
        if (info.Extra.Any(t => Overlap(t, name))) return true;
        return AliasWrite(a, info, name);
    }

    /// <summary>顺控动作 k 可能写到 name：文字提到它（本身/成员/祖先/下标、点表地址别名，ProjectWrites 同口径）；
    /// 问的是本块 InOut 形参，或动作提到了本块任一 InOut 形参（经引用可写到调用方的任何变量）。</summary>
    bool ActionMayWrite(Analysis a, int k, string text, string name)
    {
        if (!actionMentions.TryGetValue((a.Unit.UnitId, k), out var index))
            actionMentions[(a.Unit.UnitId, k)] = index = new ProjectWrites(
                [new IrUnit(a.Unit.UnitId, a.Unit.BlockName, a.Unit.Network, a.Unit.Language, a.Unit.RootKind,
                    [new IrStatement("action", IrStatementKinds.Action, null, IrExpr.True, null, null, text)], [], [])], tags);
        if (index.MentionedInText(name)) return true;
        if (KindOf(a.Block, name) == Kind.InOut) return true;
        return a.Block.Interface.Any(m => m.Section == "InOut" && index.MentionedInText(m.Name));
    }

    /// <summary>按名字比较认不出的同址写入：本块 InOut 形参（引用调用方实参，任何写入都可能是它）；
    /// 本块 Static/输入/输出形参与本块背景 DB 的全局名（"Inst_DB".X）。</summary>
    bool AliasWrite(Analysis a, Info info, string name)
    {
        if (info.Targets.Count == 0) return false;
        var kind = KindOf(a.Block, name);
        if (kind == Kind.InOut) return info.Targets.Any(t => KindOf(a.Block, t) != Kind.Temp);
        if (kind is Kind.Global or Kind.Io && info.Targets.Any(t => KindOf(a.Block, t) == Kind.InOut)) return true;
        if (kind == Kind.BlockOwned)
            return info.Targets.Any(t => KindOf(a.Block, t) == Kind.Global && MaybeOwnInstance(a.Block, t));
        if (kind == Kind.Global && MaybeOwnInstance(a.Block, name))
            return info.Targets.Any(t => KindOf(a.Block, t) == Kind.BlockOwned);
        return false;
    }

    Kind KindOf(IrBlock block, string name)
    {
        var bare = Bare(FirstSegment(name));
        var member = block.Interface.FirstOrDefault(m => C.Equals(m.Name, bare));
        if (member is not null)
            return member.Section switch { "InOut" => Kind.InOut, "Temp" => Kind.Temp, _ => Kind.BlockOwned };
        var areas = resolver.Resolve(name);
        return areas.Count > 0 && areas.All(x => !x.IsDb) ? Kind.Io : Kind.Global;
    }

    /// <summary>全局名可能是本块自己的背景 DB：DB 绝对地址；或项目里本块的调用实例不全是已知的全局背景 DB（没有调用、
    /// 有多重背景）；否则根段等于其中某个背景 DB 名。</summary>
    bool MaybeOwnInstance(IrBlock block, string globalName)
    {
        if (resolver.Resolve(globalName).Any(x => x.IsDb)) return true;
        if (!ownInstances.TryGetValue(block.Name, out var roots))
        {
            var calls = writes.CallInstances.Where(c => C.Equals(c.Callee, block.Name)).ToArray();
            roots = calls.Length == 0 || calls.Any(c => !string.Equals(c.Scope, "GlobalVariable", StringComparison.OrdinalIgnoreCase))
                ? null : calls.Select(c => Bare(FirstSegment(c.Instance))).ToHashSet(C);
            ownInstances[block.Name] = roots;
        }
        return roots is null || roots.Contains(Bare(FirstSegment(globalName)));
    }

    static IReadOnlyList<string> Norm(string name) =>
        IrBlock.Segments(name.Trim()).Select(s => s.StartsWith('[') ? s : Bare(s)).ToArray();

    static bool SameOrOverlap(IReadOnlyList<string> x, IReadOnlyList<string> y) =>
        x.Count == y.Count && x.Zip(y).All(p => C.Equals(p.First, p.Second)) || IrBlock.MayOverlap(x, y);

    /// <summary>两个名字可能同址：同一变量、父子、下标可能相同（去掉引号与 #），或按点表/绝对地址换算后范围相交；
    /// 一方是 DB 绝对地址、另一方是符号名时视为可能同址。</summary>
    bool Overlap(string a, string b)
    {
        if (SameOrOverlap(Norm(a), Norm(b))) return true;
        var ra = resolver.Resolve(a);
        var rb = resolver.Resolve(b);
        if (ra.Count > 0 && rb.Count > 0) return ra.Any(x => rb.Any(y => x.Overlaps(y)));
        return ra.Any(x => x.IsDb) || rb.Any(x => x.IsDb);
    }

    IReadOnlyList<int> WritesOf(Analysis a, string name)
    {
        var key = name.Trim();
        if (a.WritesOf.TryGetValue(key, out var hit)) return hit;
        var list = new List<int>();
        for (var k = 0; k < a.Unit.Statements.Count; k++)
            if (MayWrite(a, k, key)) list.Add(k);
        return a.WritesOf[key] = list;
    }

    // ───────────── 稳定 ─────────────

    /// <summary>访问即更新、或里面的内容本类看不全的实例，其成员一律易变（每次读取独立）。实例来源（全项目，按结构）：
    /// ①IR 里指令盒（定时器/计数器/带实例指令）的背景实例；②块接口里（任何 Section，含结构成员）类型为 IEC 定时器/计数器
    /// 系统数据类型的量（SCL/STL/顺控文字体里的定时器在 IR 看不到盒子，复审 R-1）；③调用引脚类型为这些类型的实参
    /// （经 InOut 传给 FB/FC 当实例用，复审 R-2）；④被调块程序体不能完全分析的调用实例（FC/系统块、没有程序体、不在索引、
    /// 块体含文字/顺控/未识别语句等，即 Effect 为 null）。
    /// 任意外层路径（审查 I-1）：读取名从任一段起与实例名相同或重叠即易变——「FB_X_DB.T1.Q」「X.T1.Q」「Yi.Pwr.Status」。
    /// 只按实例名的段比较，不同块里同名的成员也算（只会漏）。</summary>
    bool Volatile(string name)
    {
        var key = name.Trim();
        if (volatileCache.TryGetValue(key, out var hit)) return hit;
        volatileInstances ??= writes.BoxInstances
            .Concat(writes.CallInstances.Where(c => Effect(new IrCall(c.Callee, c.BlockType, c.Instance, IrExpr.True, []), 0) is null)
                .Select(c => c.Instance))
            .Concat(writes.TimerOrCounterActuals)
            .Concat(TimerTypedMembers())
            .Distinct(C).Select(Norm).Where(x => x.Count > 0).GroupBy(x => x[0], C).ToDictionary(g => g.Key, g => g.ToList(), C);
        var segs = Norm(key);
        var result = false;
        for (var p = 0; p < segs.Count && !result; p++)
        {
            if (segs[p].StartsWith('[') || !volatileInstances.TryGetValue(segs[p], out var candidates)) continue;
            var tail = segs.Skip(p).ToArray();
            result = candidates.Any(i => SameOrOverlap(tail, i));
        }
        return volatileCache[key] = result;
    }

    /// <summary>全项目各块接口里类型为 IEC 定时器/计数器的量（从接口成员起的路径，结构成员带外层名）。</summary>
    IEnumerable<string> TimerTypedMembers()
    {
        IEnumerable<string> Walk(IfaceMember m, string prefix)
        {
            var path = prefix.Length == 0 ? m.Name : prefix + "." + m.Name;
            if (ProjectWrites.IsIecTimerOrCounterType(m.DataType)) yield return path;
            foreach (var child in m.Children ?? [])
                foreach (var x in Walk(child, path)) yield return x;
        }
        foreach (var name in writes.BlockNames)
            if (blockOf(name) is { } block)
                foreach (var member in block.Interface)
                    foreach (var path in Walk(member, "")) yield return path;
    }

    /// <summary>不能视为在段内稳定的量：访问即更新的实例成员、与不确定写入重叠（Task 3 裁定）、文字段提到。</summary>
    bool Unsettled(string name) => Volatile(name) || writes.HasUncertainWriter(name) || writes.MentionedInText(name);

    bool HasProgramWriter(string name) => writes.Writers(name).Count > 0;

    static bool IsBarrier(IrStatement s) => s.Call is not null || s.Kind is IrStatementKinds.Source or IrStatementKinds.Action
        or IrStatementKinds.Unrecognized or IrStatementKinds.Jump or IrStatementKinds.JumpNot or IrStatementKinds.Return
        or IrStatementKinds.Label or IrStatementKinds.Step or IrStatementKinds.Transition or IrStatementKinds.Branch
        or IrStatementKinds.Connection;

    /// <summary>顺序可信段：变量在时刻 t1 与 t2 读到的值相同（t1 &lt; t2，t2 可为 End；时刻 t 的读取在语句 t 写入之前）。
    /// t2 = End：全项目唯一写入者是本段 w 处、t1 &gt; w，且本段 t1 起没有语句可能写它（%Q 另须 Q 区索引计 1）。
    /// t2 &lt; End：%I 或程序里有写入者（裁定 F8），[t1, t2) 没有语句可能写它，(t1, t2) 没有屏障，t1 本身不是调用。</summary>
    bool Stable(Analysis a, string name, int t1, int t2)
    {
        if (t1 >= 0 && t2 > t1 && ConstantBit(name)) return true;
        if (t1 < 0 || t2 <= t1 || Unsettled(name)) return false;
        var n = a.Unit.Statements.Count;
        if (t2 == ExclusionProof.End)
        {
            var ws = writes.Writers(name);
            if (ws.Count != 1 || ws[0].UnitId != a.Unit.UnitId || !a.IndexById.TryGetValue(ws[0].StatementId, out var w) || t1 <= w) return false;
            if (IsQ(name) && (qUnresolved || qWriteCount(Canonical(name)) != 1)) return false;
            for (var k = t1; k < n; k++)
                if (MayWrite(a, k, name)) return false;
            return true;
        }
        if (t2 > n || !IsI(name) && !HasProgramWriter(name)) return false;
        for (var k = t1; k < t2; k++)
        {
            var s = a.Unit.Statements[k];
            if (MayWrite(a, k, name) || k > t1 && IsBarrier(s) || k == t1 && s.Call is not null) return false;
        }
        return true;
    }

    /// <summary>顺序不可信段：本段没有写入时两次读取同值（%I 或程序里有写入者，裁定 F8）。</summary>
    bool Steady(string name) => ConstantBit(name) || !Unsettled(name) && (IsI(name) || HasProgramWriter(name));

    /// <summary>本段结束到扫描结束之间不被改写：全部写入者都在本段（%Q 另须 Q 区索引计数一致）。</summary>
    bool StableAfter(Analysis a, string name)
    {
        if (Unsettled(name)) return false;
        var ws = writes.Writers(name);
        if (ws.Count == 0 || ws.Any(x => x.UnitId != a.Unit.UnitId)) return false;
        return !IsQ(name) || !qUnresolved && qWriteCount(Canonical(name)) == ws.Count;
    }

    bool SingleWriter(string q, IrUnit unit, string statementId) =>
        !qUnresolved && qWriteCount(Canonical(q)) == 1 && !Unsettled(q)
        && writes.Writers(q) is { Count: 1 } ws && ws[0].UnitId == unit.UnitId && ws[0].StatementId == statementId;

    /// <summary>输出在本段写入后到扫描结束不再改变。</summary>
    bool OutputFinal(Analysis a, OutputDef d) => a.Ordered
        ? Stable(a, d.Signal, d.WriteAt + 1, ExclusionProof.End)
        : WritesOf(a, d.Signal).All(k => k == d.WriteAt) && StableAfter(a, d.Signal);

    // ───────────── 定义 ─────────────

    /// <summary>读取是独立的自由值：名字里有非整数字面量下标（读的是哪个元素取决于下标变量，下标可能在两次读取之间被改写，
    /// 审查 C-1 裁定一律独立）、访问即更新的实例成员，或读取它的语句本身可能写它（调用实参被调用改写、中途写入）。</summary>
    bool Free(Analysis a, int t, string name) => !ConstantBit(name) && (HasVariableIndex(name) || Volatile(name) || MayWrite(a, t, name));

    static bool HasVariableIndex(string name) =>
        IrBlock.Segments(name.Trim()).Any(seg => seg.StartsWith('[') && !IrBlock.LiteralIndex.IsMatch(seg));

    /// <summary>给变量加读取时刻；自由读取换成各自独立的带标签原子（文字带出现序号，永不相同）。</summary>
    IrExpr Stamp(Analysis a, IrExpr e, int t)
    {
        if (e.Kind == IrKinds.Var && constantBits.TryGetValue(e.Name!.Trim(), out var bit)) return bit ? IrExpr.True : IrExpr.Const("FALSE");
        if (e.Kind == IrKinds.Var)
            return Free(a, t, e.Name!)
                ? ExclusionProof.TaggedAtom(FreeTag, t, $"{e.Name} 第 {++a.FreeCounter} 次读取")
                : IrExpr.Var(ExclusionProof.At(e.Name!, t));
        return e.Args is null ? e : e with { Args = e.Args.Select(x => Stamp(a, x, t)).ToArray() };
    }

    /// <summary>TON 的 Q 为真时 IN 必为真（同一次求值）：TON.Q 加强为 IN ∧ TON.Q。</summary>
    static IrExpr Strengthen(IrExpr e)
    {
        if (e.Kind == IrKinds.Timer && e.Name is { } n && n.StartsWith("TON|", StringComparison.Ordinal) && n.EndsWith("|Q", StringComparison.Ordinal)
            && e.Args?.FirstOrDefault(x => x.Kind == IrKinds.Port && x.Name == "IN")?.Args is [var input])
            return IrExpr.And([Strengthen(input), e]);
        return e.Args is null ? e : e with { Args = e.Args.Select(Strengthen).ToArray() };
    }

    /// <summary>值里有非常量输入：读到非常量样变量，或有比较之外的不透明部件（被调块内部状态、定时器等）。</summary>
    bool NonConstantInput(IrExpr e) => e.Kind switch
    {
        IrKinds.Var => !ConstantOutputs.IsConstantLike(e.Name!.Trim(), unique, writes, constantBits),
        IrKinds.Const => false,
        IrKinds.Not or IrKinds.And or IrKinds.Or or IrKinds.Xor or IrKinds.Cmp or IrKinds.Arith => e.Args?.Any(NonConstantInput) == true,
        IrKinds.Func when e.Name is { } f && ExclusionProof.PureFunctions.Contains(f) => e.Args?.Any(NonConstantInput) == true,
        _ => true
    };

    static string Tag(string instance, int at) => $"{instance.Trim().Replace('|', '/')}@{at}";

    IReadOnlyList<OutputDef> Defs(Analysis a)
    {
        if (a.Defs is not null) return a.Defs;
        var list = new List<OutputDef>();
        var unit = a.Unit;
        IrExpr Caller(IrExpr e) => IrBlock.ExpandNodesIn(unit, e);
        for (var i = 0; i < unit.Statements.Count; i++)
        {
            var s = unit.Statements[i];
            if (s.Call is { } call)
            {
                if (!CallSiteSubstitution.IsTrue(call.En) || !CallSiteSubstitution.IsTrue(s.Guard) || !C.Equals(call.BlockType?.Trim(), "FB")
                    || blockOf(call.Callee) is not { HasBody: true } callee) continue;
                foreach (var pin in call.Pins.Where(p => p.Section == "Output" && !p.Negated && p.Targets.Count == 1))
                {
                    var q = pin.Targets[0].Trim();
                    if (!IsQ(q) || !SingleWriter(q, unit, s.Id)) continue;
                    // 同一调用的各输出来自同一次执行：共用标签
                    var tag = Tag(call.Instance, i);
                    var with = CallOutputDefinition.Resolve(callee, call, pin.Name, Caller, true, tag, out _);
                    var without = CallOutputDefinition.Resolve(callee, call, pin.Name, Caller, false, tag, out _);
                    if (with is null || without is null) continue;
                    list.Add(new(Canonical(q), i, s.Id, Strengthen(Stamp(a, with, i)), Strengthen(Stamp(a, without, i)), true, null,
                        NonConstantInput(with)));
                }
                continue;
            }
            if (s.Kind != IrStatementKinds.Assign || !s.Guard.IsTrue || s.Value is null || string.IsNullOrWhiteSpace(s.Target)) continue;
            var target = s.Target.Trim();
            if (!IsQ(target) || !SingleWriter(target, unit, s.Id)) continue;
            var plain = IrBlock.ExpandNodesIn(unit, s.Value);
            IrExpr w, wo;
            if (a.Ordered) (w, wo) = ExpandInstanceReads(a, plain, i);
            else if (CallsBefore(a, plain, i) is { Count: > 0 } after) (w, wo) = ExpandReadsAfterCalls(a, plain, i, after);
            else w = wo = Stamp(a, plain, i);
            list.Add(new(Canonical(target), i, s.Id, Strengthen(w), Strengthen(wo), false, plain, NonConstantInput(plain)));
        }
        return a.Defs = list;
    }

    /// <summary>只用于顺序可信的段：「实例.Bool 输出」读的是本段之前最近一次该实例调用的结果（其间没有语句可能写这个实例、
    /// 没有屏障），展开成调用时刻的定义；每一次读取各用独立的原子标签。其它变量按本语句时刻加戳。</summary>
    (IrExpr With, IrExpr Without) ExpandInstanceReads(Analysis a, IrExpr e, int at)
    {
        if (e.Kind == IrKinds.Var)
        {
            if (InstanceRead(a, e.Name!, at) is { } expanded) return expanded;
            var stamped = Stamp(a, e, at);
            return (stamped, stamped);
        }
        if (e.Args is null) return (e, e);
        var parts = e.Args.Select(x => ExpandInstanceReads(a, x, at)).ToArray();
        return (e with { Args = parts.Select(p => p.With).ToArray() }, e with { Args = parts.Select(p => p.Without).ToArray() });
    }

    (IrExpr With, IrExpr Without)? InstanceRead(Analysis a, string name, int at)
    {
        if (Free(a, at, name)) return null;
        var segs = Norm(name);
        for (var c = at - 1; c >= 0; c--)
        {
            var s = a.Unit.Statements[c];
            if (s.Call is not { Instance: { Length: > 0 } instance } call) continue;
            var inst = Norm(instance);
            if (segs.Count != inst.Count + 1 || !inst.Zip(segs).All(p => SameSegment(p.First, p.Second))) continue;
            var member = segs[^1];
            if (member.StartsWith('[') || !CallSiteSubstitution.IsTrue(call.En) || !CallSiteSubstitution.IsTrue(s.Guard)
                || !C.Equals(call.BlockType?.Trim(), "FB") || blockOf(call.Callee) is not { HasBody: true } callee
                || !callee.IsBoolFormal(member, "Output") || Volatile(instance)) return null;
            for (var k = c + 1; k < at; k++)
                if (MayWrite(a, k, instance) || IsBarrier(a.Unit.Statements[k])) return null;
            var tag = $"{Tag(instance, c)}#r{++a.ReadCounter}";
            IrExpr Caller(IrExpr x) => IrBlock.ExpandNodesIn(a.Unit, x);
            var w = CallOutputDefinition.Resolve(callee, call, member, Caller, true, tag, out _);
            var wo = CallOutputDefinition.Resolve(callee, call, member, Caller, false, tag, out _);
            if (w is null || wo is null) return null;
            return (Stamp(a, w, c), Stamp(a, wo, c));
        }
        return null;
    }

    /// <summary>顺序不可信段里结构上先于本语句执行的调用：值（节点已展开）本身是、或以合取项含有某调用盒的 ENO 原子，
    /// 该调用 EN 与条件恒真（EN 未接线时盒子直接接左母线，合取在一起的其它触点只能在盒子之后），是 FB 调用、有部件 UId，
    /// 且本段里该（块名，UId）的调用只有一个（顺控段可能有多个 FlgNet，UId 会重复）。返回调用语句序号。</summary>
    List<int> CallsBefore(Analysis a, IrExpr plain, int at)
    {
        var atoms = (plain.Kind == IrKinds.And ? plain.Args! : [plain])
            .Select(FlgNetTranslator.EnoPartUid).OfType<(string Name, string Uid)>().ToArray();
        var result = new List<int>();
        foreach (var (name, uid) in atoms)
        {
            var matches = Enumerable.Range(0, a.Unit.Statements.Count).Where(k =>
                a.Unit.Statements[k].Call is { PartUid: { } u } call && u == uid && string.Equals(call.Callee, name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0] == at) continue;
            var s = a.Unit.Statements[matches[0]];
            if (!CallSiteSubstitution.IsTrue(s.Call!.En) || !CallSiteSubstitution.IsTrue(s.Guard) || !C.Equals(s.Call.BlockType?.Trim(), "FB")) continue;
            result.Add(matches[0]);
        }
        return result;
    }

    /// <summary>同 ExpandInstanceReads，但实例输出读取只按 calls（结构上先于本语句执行的调用）展开。</summary>
    (IrExpr With, IrExpr Without) ExpandReadsAfterCalls(Analysis a, IrExpr e, int at, IReadOnlyList<int> calls)
    {
        if (e.Kind == IrKinds.Var)
        {
            if (InstanceReadAfter(a, e.Name!, at, calls) is { } expanded) return expanded;
            var stamped = Stamp(a, e, at);
            return (stamped, stamped);
        }
        if (e.Args is null) return (e, e);
        var parts = e.Args.Select(x => ExpandReadsAfterCalls(a, x, at, calls)).ToArray();
        return (e with { Args = parts.Select(p => p.With).ToArray() }, e with { Args = parts.Select(p => p.Without).ToArray() });
    }

    /// <summary>name 是 calls 中某次调用的「实例.Bool 输出」，且本段除这次调用外没有语句可能写这个实例：展开为调用时刻的定义（独立标签）。</summary>
    (IrExpr With, IrExpr Without)? InstanceReadAfter(Analysis a, string name, int at, IReadOnlyList<int> calls)
    {
        if (Free(a, at, name)) return null;
        if (CallOfInstanceOutput(a, name, calls) is not { } c) return null;
        var call = a.Unit.Statements[c].Call!;
        var callee = blockOf(call.Callee)!;
        var member = Norm(name)[^1];
        var tag = $"{Tag(call.Instance, c)}#r{++a.ReadCounter}";
        IrExpr Caller(IrExpr x) => IrBlock.ExpandNodesIn(a.Unit, x);
        var w = CallOutputDefinition.Resolve(callee, call, member, Caller, true, tag, out _);
        var wo = CallOutputDefinition.Resolve(callee, call, member, Caller, false, tag, out _);
        if (w is null || wo is null) return null;
        return (Stamp(a, w, c), Stamp(a, wo, c));
    }

    /// <summary>name 是 calls 中某次调用（有程序体的 FB）的「实例.Bool 输出形参」、实例不易变、本段除这次调用外没有语句可能写这个实例时，
    /// 返回该调用的语句序号。</summary>
    int? CallOfInstanceOutput(Analysis a, string name, IReadOnlyList<int> calls)
    {
        var segs = Norm(name);
        foreach (var c in calls)
        {
            var call = a.Unit.Statements[c].Call!;
            if (call.Instance is not { Length: > 0 } instance) continue;
            var inst = Norm(instance);
            if (segs.Count != inst.Count + 1 || !inst.Zip(segs).All(p => SameSegment(p.First, p.Second))) continue;
            var member = segs[^1];
            if (member.StartsWith('[') || blockOf(call.Callee) is not { HasBody: true } callee
                || !callee.IsBoolFormal(member, "Output") || Volatile(instance)) return null;
            for (var k = 0; k < a.Unit.Statements.Count; k++)
                if (k != c && MayWrite(a, k, instance)) return null;
            return c;
        }
        return null;
    }

    /// <summary>名字段忽略大小写相同；下标段须是整数字面量且相同（非字面量下标不确定指向同一元素）。</summary>
    static bool SameSegment(string x, string y)
    {
        if (!x.StartsWith('[') || !y.StartsWith('[')) return !x.StartsWith('[') && !y.StartsWith('[') && C.Equals(x, y);
        return IrBlock.LiteralIndex.IsMatch(x) && IrBlock.LiteralIndex.IsMatch(y)
            && string.Equals(new string(x.Where(ch => !char.IsWhiteSpace(ch)).ToArray()),
                new string(y.Where(ch => !char.IsWhiteSpace(ch)).ToArray()), StringComparison.Ordinal);
    }

    // ───────────── 互斥 ─────────────

    ProofVerdict Verdict(Analysis a, OutputDef x, OutputDef y, out bool needsDefaults)
    {
        needsDefaults = false;
        if (C.Equals(x.Signal, y.Signal) || !x.NonConstant || !y.NonConstant) return ProofVerdict.NotProven;
        var block = a.Block;
        bool Certain(string n) => !block.IsLocal(n.Trim()) && block.Formal(Bare(FirstSegment(n))) is null;
        // 程序里没有任何写入者的非 %I 量（HMI 位等）：两次读取不能证同值（F8），但反例也不能只靠它两次读到不同值成立
        bool ExternalOnly(string n) => !IsI(n) && !HasProgramWriter(n);
        ProofVerdict Judge(IrExpr ex, IrExpr ey) => a.Ordered
            ? ExclusionProof.Mutex(new(x.Signal, x.WriteAt, ex), new(y.Signal, y.WriteAt, ey), (n, t1, t2) => Stable(a, n, t1, t2), Certain,
                externalOnly: ExternalOnly)
            : ExclusionProof.Mutex(new(x.Signal, x.WriteAt, ex), new(y.Signal, y.WriteAt, ey),
                new UnorderedSegment(n => WritesOf(a, n), Steady, n => StableAfter(a, n)), Certain, externalOnly: ExternalOnly);
        // 先按「未接线输入取任意值」判定：成立即与初值无关（代入初值后定义恒假时也成立，审查 M-1）
        var without = Judge(x.WithoutDefaults, y.WithoutDefaults);
        if (without == ProofVerdict.Proven) return ProofVerdict.Proven;
        var with = Judge(x.WithDefaults, y.WithDefaults);
        if (with == ProofVerdict.Proven) { needsDefaults = true; return ProofVerdict.Proven; }
        return with == ProofVerdict.Counterexample || without == ProofVerdict.Counterexample ? ProofVerdict.Counterexample : ProofVerdict.NotProven;
    }

    /// <summary>a、b 是本段两个输出定义（点名，忽略大小写）时的互斥判定；needsDefaults：只有把未接线输入代入接口初值才成立。
    /// 不是本段输出定义、或本段不证明（控制流、FC 等）→ NotProven。</summary>
    public ProofVerdict MutexVerdict(IrUnit unit, IrBlock block, string a, string b, out bool needsDefaults)
    {
        needsDefaults = false;
        var an = Analyze(unit, block);
        if (an.Refused) return ProofVerdict.NotProven;
        var defs = Defs(an);
        var da = defs.FirstOrDefault(d => C.Equals(d.Signal, a?.Trim()));
        var db = defs.FirstOrDefault(d => C.Equals(d.Signal, b?.Trim()));
        return da is null || db is null ? ProofVerdict.NotProven : Verdict(an, da, db, out needsDefaults);
    }

    // ───────────── 同步 ─────────────

    /// <summary>两个 Bool 点在扫描结束时同值：①两条赋值的值文字相同（区分大小写：字符常量 'a' 与 'A' 不同）、只由纯逻辑/比较/运算构成、不读这两个目标、至少读一个
    /// 非常量样变量，且每个读到的变量在两次读取之间稳定（顺序可信段按区间、不可信段须本段不写）；②（只在顺序可信段）后一条直接
    /// 拷贝前一个输出；③输出直接拷贝一个程序从不写入的唯一 Bool %I（输入映像整个扫描不变）。两个输出写入后都不再改变。</summary>
    public bool SameStateProven(IrUnit unit, IrBlock block, string a, string b)
    {
        var an = Analyze(unit, block);
        return !an.Refused && SameState(an, a?.Trim() ?? "", b?.Trim() ?? "");
    }

    bool SameState(Analysis an, string a, string b)
    {
        var defs = Defs(an).Where(d => !d.FromCall).ToArray();
        var da = defs.FirstOrDefault(d => C.Equals(d.Signal, a));
        var db = defs.FirstOrDefault(d => C.Equals(d.Signal, b));
        if (da is not null && db is null) return CopiesInput(an, da, b);
        if (db is not null && da is null) return CopiesInput(an, db, a);
        if (da is null || db is null || C.Equals(da.Signal, db.Signal)) return false;
        var (x, y) = da.WriteAt < db.WriteAt ? (da, db) : (db, da);
        if (!x.NonConstant || !y.NonConstant || x.Plain is null || y.Plain is null || !OutputFinal(an, x) || !OutputFinal(an, y)) return false;
        if (y.Plain is { Kind: IrKinds.Var } copy && C.Equals(copy.Name!.Trim(), x.Signal))
            return an.Ordered && !Free(an, y.WriteAt, x.Signal) && Stable(an, x.Signal, y.WriteAt, ExclusionProof.End);
        // 顺序不可信段：两条拷贝都接在同一调用盒的 ENO 之后时，该 ENO 原子与该调用实例输出的读取是同一次执行的值
        var after = an.Ordered ? [] : CallsBefore(an, x.Plain, x.WriteAt).Intersect(CallsBefore(an, y.Plain, y.WriteAt)).ToList();
        var allowedAtoms = after.Select(c => an.Unit.Statements[c].Call!).Select(c => FlgNetTranslator.EnoAtom(c.Callee, c.PartUid!).Name!)
            .ToHashSet(StringComparer.Ordinal);
        if (!IsPureLogic(x.Plain, allowedAtoms) || !string.Equals(IrText.Expr(x.Plain), IrText.Expr(y.Plain), StringComparison.Ordinal)) return false;
        var reads = x.Plain.Vars().Select(v => v.Trim()).Distinct(C).ToArray();
        if (reads.Length == 0) return false;
        foreach (var v in reads)
        {
            if (Free(an, x.WriteAt, v) || Free(an, y.WriteAt, v)) return false;
            if (after.Count > 0 && CallOfInstanceOutput(an, v, after) is { } c)
            {
                // 本段只有这次调用写它（CallOfInstanceOutput 已核对）；两条拷贝都在调用之后，读到的是同一次执行的输出
                if (Unsettled(v) || WritesOf(an, v).Any(k => k != c)) return false;
                continue;
            }
            if (an.Ordered ? !Stable(an, v, x.WriteAt, y.WriteAt) : WritesOf(an, v).Count > 0 || !Steady(v)) return false;
        }
        return true;
    }

    static bool IsPureLogic(IrExpr e, IReadOnlySet<string>? allowedAtoms = null) => e.Kind switch
    {
        IrKinds.Var or IrKinds.Const => true,
        IrKinds.Unknown => allowedAtoms is not null && e.Name is { } n && allowedAtoms.Contains(n),
        IrKinds.Not or IrKinds.And or IrKinds.Or or IrKinds.Xor or IrKinds.Cmp or IrKinds.Arith => e.Args?.All(x => IsPureLogic(x, allowedAtoms)) == true,
        IrKinds.Func when e.Name is { } f && ExclusionProof.PureFunctions.Contains(f) => e.Args?.All(x => IsPureLogic(x, allowedAtoms)) == true,
        _ => false
    };

    bool CopiesInput(Analysis an, OutputDef d, string input) =>
        d.Plain is { Kind: IrKinds.Var } v && C.Equals(v.Name!.Trim(), input) && IsI(input)
        && !Unsettled(input) && !HasProgramWriter(input) && !MayWrite(an, d.WriteAt, input) && OutputFinal(an, d);

    // ───────────── 汇总 ─────────────

    /// <param name="ct">取消（如发布期限到）：每判定一对输出前检查。</param>
    public IReadOnlyList<ProvenRelation> Prove(IrUnit unit, IrBlock block, CancellationToken ct = default)
    {
        var an = Analyze(unit, block);
        if (an.Refused) return [];
        var result = new List<ProvenRelation>();
        var defs = Defs(an);
        static IReadOnlyList<string> Pair(string a, string b) => new[] { a, b }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < defs.Count; i++)
            for (var j = i + 1; j < defs.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                var (a, b) = (defs[i], defs[j]);
                if (C.Equals(a.Signal, b.Signal)) continue;
                if (Verdict(an, a, b, out var needsDefaults) == ProofVerdict.Proven)
                {
                    var proof = needsDefaults ? UnderstandingProofs.ReviewCallSiteDefaults
                        : a.FromCall || b.FromCall ? UnderstandingProofs.CodeCallSite : UnderstandingProofs.CodeNetwork;
                    result.Add(new("AT_MOST_ONE", Pair(a.Signal, b.Signal), proof, new[] { a.StatementId, b.StatementId }.Distinct().ToArray()));
                }
                else if (!a.FromCall && !b.FromCall && SameState(an, a.Signal, b.Signal))
                    result.Add(new("SAME_STATE", Pair(a.Signal, b.Signal), UnderstandingProofs.CodeNetwork, [a.StatementId, b.StatementId]));
            }
        foreach (var d in defs.Where(d => !d.FromCall))
            if (d.Plain is { Kind: IrKinds.Var } v && IsI(v.Name!) && CopiesInput(an, d, v.Name!.Trim()))
                result.Add(new("SAME_STATE", Pair(d.Signal, Canonical(v.Name!)), UnderstandingProofs.CodeNetwork, [d.StatementId]));
        return result.DistinctBy(r => r.Operator + "|" + string.Join("&", r.Members).ToUpperInvariant()).ToArray();
    }

    public static PipelineRule ToRule(ProvenRelation p, string model) =>
        new(p.Operator, p.Members.Select(m => new PipelineSignal(m, "Member")).ToArray(),
            p.Operator == "AT_MOST_ONE" ? $"{p.Members[0]} 与 {p.Members[1]} 互斥（程序证明）" : $"{string.Join("、", p.Members)} 同步（程序证明）")
        { Proof = p.Proof, Trace = new PipelineRuleTrace(p.Proof, model, p.Evidence, null) };
}
