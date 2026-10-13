#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public enum ProofVerdict { Proven, Counterexample, NotProven }

/// <summary>Signal 在 WriteAt（程序段内语句序号）被写为 Value；Value 里的变量名已用 ExclusionProof.Stamp 加上读取时刻。
/// 前提：Value 是该语句执行后 Signal 的完整取值（条件赋值须由调用方折算成完整表达式）；两个定义来自同一个程序段（时刻是段内序号）。
/// 「从这条写入到扫描结束没有别的写入」由 ExclusionProof 自己核对（顺序可信版问 stable(Signal, WriteAt+1, End)，
/// 不可信版问 WritesOf 与 StableAfter），不满足即 NotProven。</summary>
public sealed record TimedDef(string Signal, int WriteAt, IrExpr Value)
{
    /// <summary>Value 里有带扫描结束时刻（End）的读取：定义里的读取不可能发生在扫描结束，这样的定义不参与证明。</summary>
    internal bool ReadsAtEnd => Value.Vars().Any(v => ExclusionProof.Split(v).Time == ExclusionProof.End);
}

/// <summary>语句顺序不可信的程序段（FlgNet，含顺控块里的 FlgNet）。IR 语句按部件 UId 排、指令盒输出追加在后，不是执行顺序；
/// 而且一条线圈语句的表达式是从左母线到它的整条能流——分支前的公共触点、中间线圈上游的触点是同一次物理读取，
/// 却被复制进几条线圈语句。所以读取时刻只能说明「哪条语句的表达式里出现过」，说明不了它在本段哪次写入之前或之后。
/// WritesOf(name)：给定一个读到的变量名（不带时刻），返回本段内**可能写到它**的全部语句序号（少返回一条就可能误证，宁多勿少）。
/// 调用方必须按 ProjectWrites/IrBlock 的重叠口径（同名忽略大小写、父结构/子成员、非字面量下标、地址别名、与它重叠的
/// 不确定写入如间接寻址）作答，**并且还要把以下四类计为本段对该变量的写入**：
/// ①调用语句：被调块（FB/FC，含它再调用的块）块体内可能写到的一切——全局变量（FC 写全局也算）、InOut/Output 实参、背景实例；
///   被调块没有程序体或内容不完整/认不出时，视为可能写全部全局变量（以及它的全部实参）；
/// ②指令盒（定时器/计数器、带实例或未展开语义的指令等，非 FB/FC 调用的部件）：对背景实例（整个实例）及其输出/InOut 操作数的写入
///   （IrStatement.SideWrites 与赋值语句目标，ProjectWrites/IrBlock 已计入）；
/// ③边沿存储位（P/N 触点、P/N 线圈、P_TRIG/N_TRIG 的 bit 操作数，同样在 SideWrites 里）；
/// ④文字语句（源码、顺控动作、未识别元素）里提到的变量（与 ProjectWrites.MentionedInText 同一口径）。
/// 同一语句内（同一时刻）的两次读取直接视为同值、不看 Steady：凡是可能在一条语句内部被改值的变量
/// （如能流中间串着调用盒子或指令盒），必须出现在 WritesOf 里，只让 Steady 返回 false 挡不住。
/// 两个定义自身的写入序号只有在名字相同（忽略大小写）时可以省略；两个输出互为地址别名或重叠时，问其中一个必须返回另一个的
/// 写入序号（否则当成两个点）。
/// Steady(name)：本段内没有本段写入时变量值不变（程序外写入不建模；与不确定写入重叠的变量返回 false）。
/// StableAfter(name)：本段结束到扫描结束之间变量不被改写。</summary>
public sealed record UnorderedSegment(Func<string, IEnumerable<int>> WritesOf, Func<string, bool> Steady,
    Func<string, bool> StableAfter);

/// <summary>两个 Bool 输出在扫描结束时不可能同为 true 的证明。纯函数，不看名字。</summary>
public static class ExclusionProof
{
    public const int End = int.MaxValue;
    const char Sep = '\u0001';
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    static readonly Regex TimeMark = new("\u0001-?[0-9]+", RegexOptions.CultureInvariant);
    static readonly Regex TaggedAtomName = new(@"^[^|]+\|-?[0-9]+\|", RegexOptions.CultureInvariant);
    /// <summary>无状态、只由参数决定结果的函数（博图转换/取整/数学函数与 SCL 运算符）。</summary>
    internal static readonly HashSet<string> PureFunctions = new(StringComparer.Ordinal)
    {
        "Convert", "Round", "Trunc", "Ceil", "Floor", "Abs", "Neg", "Sqrt", "Sqr", "Exp", "Ln", "Sin", "Cos", "Tan",
        "MIN", "MAX", "LIMIT", "SEL", "EXPT"
    };

    public static string At(string name, int time) => name + Sep + time.ToString(CultureInfo.InvariantCulture);

    public static (string Name, int Time) Split(string key)
    {
        var i = key.LastIndexOf(Sep);
        return i >= 0 && int.TryParse(key[(i + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
            ? (key[..i], t) : (key, -1);
    }

    /// <summary>给还没带读取时刻的变量名加上时刻（已带的保持不变）。</summary>
    public static IrExpr Stamp(IrExpr e, int time) =>
        BoolSimplifier.Substitute(e, n => n.Contains(Sep) ? null : IrExpr.Var(At(n, time)));

    public static string Unstamped(string text) => TimeMark.Replace(text, "");

    /// <summary>调用点代入时自造的不透明原子（文字「标签|读者序号|说明」）：标签区分实例与时刻，文字相同即同一个值。</summary>
    public static IrExpr TaggedAtom(string tag, int reader, string text) =>
        new(IrKinds.Unknown, $"{tag}|{reader.ToString(CultureInfo.InvariantCulture)}|{text}");

    public static bool IsTaggedAtom(IrExpr e) => e.Kind == IrKinds.Unknown && e.Name is { } n && TaggedAtomName.IsMatch(n);

    /// <summary>程序段内语句序号是否就是执行顺序：按程序段自身的根类型判定（块语言是块级的，LAD 块里也可以有 SCL 程序段）——
    /// 根为 StructuredText（SCL）或 StatementList（STL）且没有 FlgNet 痕迹（指令盒语句、节点）的可信；
    /// FlgNet、顺控块（Graph，含其中的 FlgNet）、纯文本及其它一律不可信。</summary>
    public static bool StatementOrderTrusted(IrUnit unit) =>
        unit.RootKind is "StructuredText" or "StatementList"
        && unit.Nodes.Count == 0
        && unit.Statements.All(s => s.Kind != IrStatementKinds.Box);

    /// <summary>语句顺序可信的程序段。stable(name, t1, t2)（t1 &lt; t2，t2 可为 End）：变量在两个时刻读到的值相同
    /// （时刻 t 的读取看到的是语句 t 写入之前的值）；两个时刻相同时不调用、直接视为相同；不带时刻的变量（Split 得 -1）永不视为相同。
    /// 先核对两个输出写入之后到扫描结束不再被改写：stable(Signal, WriteAt + 1, End)，否则 NotProven。
    /// externalOnly(name)：程序里没有任何写入者的量（HMI/通讯等程序外写入）——只靠它在两次读取之间取不同值才成立的反例
    /// 不算反例（判 NotProven，见 Judge）。maxTerms 已不用（判定改为可满足性检查，不再逐项枚举），保留参数以兼容调用方。</summary>
    public static ProofVerdict Mutex(TimedDef a, TimedDef b, Func<string, int, int, bool> stable, Func<string, bool> certain, int maxTerms = 64,
        Func<string, bool>? externalOnly = null)
    {
        if (!DistinctOutputs(a, b) || a.ReadsAtEnd || b.ReadsAtEnd) return ProofVerdict.NotProven;
        foreach (var d in new[] { a, b })
            if (d.WriteAt < 0 || d.WriteAt >= End - 1 || !stable(d.Signal, d.WriteAt + 1, End)) return ProofVerdict.NotProven;
        bool Same(string name, int t1, int t2) =>
            t1 >= 0 && t2 >= 0 && (t1 == t2 || stable(name, Math.Min(t1, t2), Math.Max(t1, t2)));
        return Judge(a, b, Same, (name, _) => certain(name), externalOnly);
    }

    static bool DistinctOutputs(TimedDef a, TimedDef b) => !C.Equals(a.Signal, b.Signal);

    /// <summary>语句顺序不可信的程序段（裁定 F1）：本段内有写入（WritesOf 非空，含可能写入与重叠）的变量，
    /// 它的每一次读取都是独立的自由值——不与任何其它读取、也不与任何写入后的值（含输出的 @End 断言）视为同值，不参与先后推理。
    /// 只有本段内没有写入的变量按 Steady/StableAfter 参与证明。名字不同但互相重叠的读取（父结构/子成员、地址别名）
    /// 靠 WritesOf 的重叠口径归为同一变量：其中任一被本段写到，就都是自由值；都没被写到时冲突规则只比较同名，
    /// 不会因名字不同而多证明。
    /// 自由值不是真正任意取值，所以含自由值的无矛盾项不算反例（只算证明不了）。
    /// 两个输出在本段只能由各自的定义语句写入（WritesOf 不得返回别的序号，互为别名时也会因此拦下）、且 StableAfter 成立，否则 NotProven。
    /// 代价：同一 FlgNet 网络内两个输出靠互串对方常闭触点的互锁不再能代码证明。externalOnly 同顺序可信版。</summary>
    public static ProofVerdict Mutex(TimedDef a, TimedDef b, UnorderedSegment segment, Func<string, bool> certain, int maxTerms = 64,
        Func<string, bool>? externalOnly = null)
    {
        if (!DistinctOutputs(a, b) || a.ReadsAtEnd || b.ReadsAtEnd) return ProofVerdict.NotProven;
        var written = new Dictionary<string, HashSet<int>>(C);
        HashSet<int> WritesOf(string name)
        {
            if (written.TryGetValue(name, out var set)) return set;
            set = [.. segment.WritesOf(name) ?? []];
            if (C.Equals(name, a.Signal)) set.Add(a.WriteAt);
            if (C.Equals(name, b.Signal)) set.Add(b.WriteAt);
            return written[name] = set;
        }
        foreach (var d in new[] { a, b })
            if (d.WriteAt < 0 || d.WriteAt == End || WritesOf(d.Signal).Any(w => w != d.WriteAt) || !segment.StableAfter(d.Signal))
                return ProofVerdict.NotProven;

        bool Free(string name, int t) => t != End && WritesOf(name).Count > 0;
        bool Same(string name, int t1, int t2)
        {
            if (t1 < 0 || t2 < 0 || Free(name, t1) || Free(name, t2)) return false;
            if (t1 == t2) return true;
            return segment.Steady(name) && (Math.Max(t1, t2) != End || segment.StableAfter(name));
        }
        return Judge(a, b, Same, (name, t) => certain(name) && !Free(name, t), externalOnly);
    }

    /// <summary>可满足性判定（取代逐项枚举析取范式，项数再多也能判）。把两个定义（否定推到文字上）与两个输出 @End 为真的断言
    /// 合取成一个命题公式，文字按下面的同值口径映射为命题变量（与原先逐项找矛盾等价）：
    /// ①同一变量（忽略大小写）两次读取同值（same）→ 同一命题变量（传递闭包：值相同可传递）；同一时刻也须 same 成立
    ///   （顺序不可信段里本段写到的变量每次读取各自独立）；
    /// ②同一无状态纯函数原子（去掉时刻后文字逐字相同，区分大小写）且原子里每个变量两时刻同值 → 同一命题变量；
    /// ③同一变量分别「== 两个不同数值常数」且两时刻同值 → 不能同时为真（附加子句）；其余原子（有状态部件等）每次出现各自独立。
    /// 结论：任一定义自身不可满足（恒假，互斥是空洞的）→ NotProven；整个公式不可满足 → Proven；
    /// 把不确定的文字（不透明原子、certain 为假的读取）一律置假后仍可满足 → Counterexample（存在只由确定文字构成、
    /// 且彼此无矛盾的成立方式，与原先「有一项全由确定文字构成且无矛盾」同义）；否则 NotProven。
    /// externalOnly 的量在找反例时各次读取强制同值：反例若只靠「程序从不写入的量在两次读取之间被程序外改变」才成立，判 NotProven
    /// （不反驳、也不证明）。超出求解上限一律 NotProven。</summary>
    /// <param name="certain">certain(name, time)：该次读取可作为反例里自由取值的变量。</param>
    static ProofVerdict Judge(TimedDef a, TimedDef b, Func<string, int, int, bool> same, Func<string, int, bool> certain,
        Func<string, bool>? externalOnly)
    {
        var sat = new SatBuilder(same);
        var defA = sat.Add(BoolSimplifier.ToNnf(a.Value));
        var defB = sat.Add(BoolSimplifier.ToNnf(b.Value));
        var assertA = sat.Add(IrExpr.Var(At(a.Signal, End)));
        var assertB = sat.Add(IrExpr.Var(At(b.Signal, End)));
        sat.Resolve(externalOnly);
        if (sat.Solve(defA, forCounterexample: false, certain) is not true) return ProofVerdict.NotProven;
        if (sat.Solve(defB, forCounterexample: false, certain) is not true) return ProofVerdict.NotProven;
        var both = SatBuilder.And(defA, defB, assertA, assertB);
        var any = sat.Solve(both, forCounterexample: false, certain);
        if (any is false) return ProofVerdict.Proven;
        if (any is null) return ProofVerdict.NotProven;
        return sat.Solve(both, forCounterexample: true, certain) is true ? ProofVerdict.Counterexample : ProofVerdict.NotProven;
    }

    /// <summary>把否定已推到文字上的表达式映射为命题公式（见 Judge 的同值口径）。先 Add 全部表达式（得到骨架），再 Resolve 分配命题变量。</summary>
    sealed class SatBuilder(Func<string, int, int, bool> same)
    {
        internal abstract record Skeleton;
        sealed record LeafRef(int Occurrence) : Skeleton;
        sealed record GateRef(bool IsAnd, Skeleton[] Children) : Skeleton;
        sealed record ConstRef(bool Value) : Skeleton;

        enum AtomKind { Variable, PureAtom, OpaqueAtom }
        sealed record Occurrence(AtomKind Kind, string Key, string Name, int Time, bool Positive, IrExpr Atom);

        readonly List<Occurrence> occurrences = [];
        int[] varOf = [], counterVarOf = [];
        int varCount, counterVarCount;
        readonly List<int[]> clauses = [], counterClauses = [];

        public static Skeleton And(params Skeleton[] items) => new GateRef(true, items);

        public Skeleton Add(IrExpr e)
        {
            switch (e.Kind)
            {
                case IrKinds.And: return new GateRef(true, e.Args!.Select(Add).ToArray());
                case IrKinds.Or: return new GateRef(false, e.Args!.Select(Add).ToArray());
                case IrKinds.Const when BoolConst(e) is { } c: return new ConstRef(c);
                case IrKinds.Var: return Leaf(e, true);
                case IrKinds.Not: return Leaf(e.Args![0], false);
                default: return Leaf(e, true);
            }
        }

        Skeleton Leaf(IrExpr atom, bool positive)
        {
            if (atom.Kind == IrKinds.Const && BoolConst(atom) is { } c) return new ConstRef(c == positive);
            Occurrence o;
            if (atom.Kind == IrKinds.Var)
            {
                var (name, time) = Split(atom.Name!);
                o = new(AtomKind.Variable, atom.Name!, name, time, positive, atom);
            }
            else
            {
                var key = IrText.Expr(atom);
                o = new(IsPure(atom) ? AtomKind.PureAtom : AtomKind.OpaqueAtom, key, "", -1, positive, atom);
            }
            occurrences.Add(o);
            return new LeafRef(occurrences.Count - 1);
        }

        static bool? BoolConst(IrExpr e) => e.Name?.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "1" => true, "FALSE" or "0" => false, _ => null
        };

        /// <summary>分配命题变量：证明用一套（同值口径），找反例用另一套（另把 externalOnly 的量各次读取并为同值）。</summary>
        public void Resolve(Func<string, bool>? externalOnly)
        {
            var count = occurrences.Count;
            var parent = Enumerable.Range(0, count).ToArray();
            int Find(int[] p, int x) { while (p[x] != x) x = p[x] = p[p[x]]; return x; }
            void Union(int[] p, int x, int y) { x = Find(p, x); y = Find(p, y); if (x != y) p[x] = y; }

            // ①变量：按（名字，时刻）分节点；同一节点自身同值则合并其各次出现；节点之间两两 same 则合并
            var variables = Enumerable.Range(0, count).Where(i => occurrences[i].Kind == AtomKind.Variable).ToArray();
            foreach (var byName in variables.GroupBy(i => occurrences[i].Name, C))
            {
                var nodes = byName.GroupBy(i => occurrences[i].Time).Select(g => (Time: g.Key, Items: g.ToArray()))
                    .Where(n => same(occurrences[n.Items[0]].Name, n.Time, n.Time)).ToArray();
                foreach (var node in nodes)
                    foreach (var i in node.Items.Skip(1)) Union(parent, node.Items[0], i);
                for (var x = 0; x < nodes.Length; x++)
                    for (var y = x + 1; y < nodes.Length; y++)
                        if (same(occurrences[nodes[x].Items[0]].Name, nodes[x].Time, nodes[y].Time)) Union(parent, nodes[x].Items[0], nodes[y].Items[0]);
            }
            // ②纯原子：按去时刻文字分组，组内按带时刻文字分节点，同理合并
            var pure = Enumerable.Range(0, count).Where(i => occurrences[i].Kind == AtomKind.PureAtom).ToArray();
            foreach (var group in pure.GroupBy(i => Unstamped(occurrences[i].Key), StringComparer.Ordinal))
            {
                var nodes = group.GroupBy(i => occurrences[i].Key, StringComparer.Ordinal).Select(g => g.ToArray())
                    .Where(n => AtomsSame(occurrences[n[0]].Atom, occurrences[n[0]].Atom, same)).ToArray();
                foreach (var node in nodes)
                    foreach (var i in node.Skip(1)) Union(parent, node[0], i);
                for (var x = 0; x < nodes.Length; x++)
                    for (var y = x + 1; y < nodes.Length; y++)
                        if (AtomsSame(occurrences[nodes[x][0]].Atom, occurrences[nodes[y][0]].Atom, same)) Union(parent, nodes[x][0], nodes[y][0]);
            }
            var counterParent = parent.ToArray();
            if (externalOnly is not null)
                foreach (var byName in variables.GroupBy(i => occurrences[i].Name, C).Where(g => externalOnly(g.Key)))
                {
                    var items = byName.ToArray();
                    foreach (var i in items.Skip(1)) Union(counterParent, items[0], i);
                }
            (varOf, varCount) = Number(parent, Find);
            (counterVarOf, counterVarCount) = Number(counterParent, Find);
            // ③同一变量「== 两个不同数值常数」不能同时为真
            var equalities = pure.Select(i => (Index: i, Eq: EqualsConstant(occurrences[i].Atom))).Where(x => x.Eq is not null).ToArray();
            for (var x = 0; x < equalities.Length; x++)
                for (var y = x + 1; y < equalities.Length; y++)
                {
                    var (ex, ey) = (equalities[x].Eq!.Value, equalities[y].Eq!.Value);
                    var (nx, tx) = Split(ex.Var);
                    var (ny, ty) = Split(ey.Var);
                    if (!C.Equals(nx, ny) || ex.Value == ey.Value || !same(nx, tx, ty)) continue;
                    clauses.Add([2 * varOf[equalities[x].Index] + 1, 2 * varOf[equalities[y].Index] + 1]);
                    counterClauses.Add([2 * counterVarOf[equalities[x].Index] + 1, 2 * counterVarOf[equalities[y].Index] + 1]);
                }
        }

        static (int[] Map, int Count) Number(int[] parent, Func<int[], int, int> find)
        {
            var ids = new Dictionary<int, int>();
            var map = new int[parent.Length];
            for (var i = 0; i < parent.Length; i++)
            {
                var root = find(parent, i);
                if (!ids.TryGetValue(root, out var id)) ids[root] = id = ids.Count;
                map[i] = id;
            }
            return (map, ids.Count);
        }

        /// <summary>forCounterexample：只保留确定的文字（变量读取且 certain 成立），其余文字置假（公式单调，置假即「不依赖它们」）。</summary>
        public bool? Solve(Skeleton root, bool forCounterexample, Func<string, int, bool> certain)
        {
            var map = forCounterexample ? counterVarOf : varOf;
            PropositionalSat.Formula Build(Skeleton s) => s switch
            {
                ConstRef c => c.Value ? PropositionalSat.True : PropositionalSat.False,
                GateRef g => g.IsAnd ? PropositionalSat.And(g.Children.Select(Build)) : PropositionalSat.Or(g.Children.Select(Build)),
                LeafRef l => Literal(l.Occurrence),
                _ => throw new InvalidOperationException()
            };
            PropositionalSat.Formula Literal(int i)
            {
                var o = occurrences[i];
                if (forCounterexample && (o.Kind != AtomKind.Variable || !certain(o.Name, o.Time))) return PropositionalSat.False;
                return new PropositionalSat.Lit(map[i], o.Positive);
            }
            return PropositionalSat.Satisfiable(Build(root), forCounterexample ? counterVarCount : varCount,
                forCounterexample ? counterClauses : clauses);
        }
    }

    /// <summary>裁定 F3：只有无状态纯函数原子（比较、算术、布尔组合、纯函数，且每个变量都带读取时刻）文字相同才是同一个值；
    /// 未接线/未识别（Unknown）、边沿（不带边沿存储位）、计时器/计数器/指令盒/Calc（有内部状态）、调用输出、节点一律不是。
    /// 调用点代入自造的带标签原子（TaggedAtom）例外：标签已区分实例与时刻。</summary>
    static bool IsPure(IrExpr e)
    {
        switch (e.Kind)
        {
            case IrKinds.Var: return Split(e.Name!).Time >= 0;
            case IrKinds.Const: return true;
            case IrKinds.Unknown: return IsTaggedAtom(e);
            case IrKinds.Not: case IrKinds.And: case IrKinds.Or: case IrKinds.Xor: case IrKinds.Cmp: case IrKinds.Arith:
                return e.Args is not null && e.Args.All(IsPure);
            case IrKinds.Func:
                return e.Name is { } n && PureFunctions.Contains(n) && e.Args is not null && e.Args.All(IsPure);
            default: return false;
        }
    }

    /// <summary>两个文字相同的原子：按出现顺序一一对应的变量都要在各自时刻之间同值。</summary>
    static bool AtomsSame(IrExpr a, IrExpr b, Func<string, int, int, bool> same)
    {
        var va = a.Vars().Select(Split).ToArray();
        var vb = b.Vars().Select(Split).ToArray();
        return va.Length == vb.Length && va.Zip(vb).All(p => C.Equals(p.First.Name, p.Second.Name) && same(p.First.Name, p.First.Time, p.Second.Time));
    }

    /// <summary>「变量 == 数值常数」（任一侧为常数）。</summary>
    static (string Var, double Value)? EqualsConstant(IrExpr atom)
    {
        if (atom.Kind != IrKinds.Cmp || atom.Name != "==" || atom.Args is not { Count: 2 } args) return null;
        foreach (var (v, c) in new[] { (args[0], args[1]), (args[1], args[0]) })
            if (v.Kind == IrKinds.Var && c.Kind == IrKinds.Const
                && double.TryParse(c.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                return (v.Name!, d);
        return null;
    }
}
