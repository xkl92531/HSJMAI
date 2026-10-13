#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>被调 FB 某个 Bool 输出在调用点的取值，用调用方的名字表示（spec §5 的代入，供代码证明用）。
/// 拿不准的量一律换成不透明原子（ExclusionProof.TaggedAtom，文字「atomTag|读者序号|说明 名字」），只会让证明更难成立，
/// 不会让它错误成立。写入一律按 IrBlock 的口径判断（WritesTouching / IsWritten / Definition：赋值/置位/复位、调用引脚与
/// 背景实例、SideWrites（指令盒顺带写入、边沿存储位）、块写指令放宽、认不出的写入目标按区收窄）：
/// · 被调块不是 FB（FC 等，call.BlockType 不为 "FB"）→ null：FC 形参按引用传递，输入与输出实参、块内全局写入都可能与
///   调用方变量同址，符号对符号的同址别名没有点表判不出（用户已定 FC 不管）；
/// · 输出不是 Bool 输出形参、块有跳转/返回、输出没有唯一定义、任何文字段（源码、顺控动作、未识别元素）提到它 → null；
///   被调块以全局名写（或在文字段里提到）本次调用的背景实例（"Inst".S）→ null：那是对本块 Static/形参的别名写入，
///   它们的唯一定义都不再可信；
/// · 名字里有非整数字面量的下标（Arr[Idx]、Arr[#i]）→ 不透明原子：下标变量属于被调块作用域，带进调用方会被重新解释；
/// · 输入形参：接线 → expandCaller(实参)（Bool 形参的常数实参规范化为 TRUE/FALSE；带成员后缀时实参须是变量，
///   成员名接在实参后再交 expandCaller——expandCaller 因此须能处理「实参.成员」这种带后缀的名字）；未接线 → usedDefaults=true，
///   useDefaults 时取接口初值（Bool 无初值为 FALSE，非 Bool 为 "0"），否则不透明原子；块内可能被改写（IrBlock.IsWritten）
///   或文字段提到的输入、InOut → 不透明原子；
/// · 本块 Static/Temp 与其它输出形参：唯一定义的写入者在读者之前执行时展开（深度 ≤6、防环），否则不透明原子。
///   「之前」：写入者在更早的程序段；或同一程序段且该段语句顺序可信（ExclusionProof.StatementOrderTrusted，SCL/STL 源码根）
///   且序号更小。FlgNet 等顺序不可信的段里靠「段内先写后读」才得到的定义一律不展开；
/// · 全局量：块内没有可能写任何量的语句、不写任何全局量（含 InOut、绝对地址、认不出的目标）、IrBlock 也不认为它被写、
///   且根段不是本次调用的背景实例（被调块以全局名读自己的背景 DB）→ 原样（调用方同一时刻读到的值）；否则不透明原子；
/// · 被调块内部的有状态/不认识的部件（定时器、计数器、边沿、指令盒、Calc、调用输出、非纯函数、未识别内容，
///   如 SEL/LIMIT 带 Port 参数的整体）→ 不透明原子。
/// 原子身份：读者序号区分时刻、atomTag 区分实例（调用点）。读者语句的值里有可能中途改写变量的部件，或读者所在段顺序不可信且
/// 段内可能写到该量（IrBlock.WritesTouching 有本段的写入，或本段有「可能写任何量」的语句：调用、文字段、指令盒语句、
/// 值/条件里内嵌定时器/计数器/边沿/指令盒/Calc/调用输出——即使 IR 未记 SideWrites 也按此保守处理；
/// 全局类名字（全局名、绝对地址、InOut）在本段写了任何非本块局部的量时——IrBlock 认不出符号之间的同址别名
/// （AliasBit/Flag、字节标签与位标签、符号与绝对地址），也认不出 InOut 按引用写到的调用方变量；
/// 以全局名读本次调用的背景实例时本段有任何写入）时，每一次读取都是
/// 独立的自由值，文字另带该次读取在读者语句里的位置；有状态部件同样按出现位置区分。文字相同的原子只出现在
/// 「同一调用点、同一读者语句、段内不会被改写」的读取上（确实是同一个值）——带标签原子同文字正反出现总被判冲突。
/// 调用方责任：atomTag 必须区分调用方的每一次读取（同一调用的同一输出在同一语句里被读两次，除非能证明两次之间该调用
/// 不会执行，须用两个不同标签）；本方法只按被调块内部的结构区分，不知道调用方读了几次。</summary>
public static class CallOutputDefinition
{
    const int MaxDepth = 6;
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    public static IrExpr? Resolve(IrBlock callee, IrCall call, string output, Func<IrExpr, IrExpr> expandCaller,
        bool useDefaults, string atomTag, out bool usedDefaults)
    {
        ArgumentNullException.ThrowIfNull(callee);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(expandCaller);
        if (string.IsNullOrEmpty(atomTag) || atomTag.Contains('|'))
            throw new ArgumentException("atomTag 不得为空，也不得含「|」", nameof(atomTag));
        usedDefaults = false;
        if (!C.Equals(call.BlockType?.Trim(), "FB")) return null;
        if (string.IsNullOrWhiteSpace(output) || callee.HasControlFlow || !callee.IsBoolFormal(output, "Output")) return null;
        var context = new Context(callee, call, expandCaller, useDefaults, atomTag);
        if (context.TextMentions(output) || context.OwnInstanceWritten || callee.Definition(output) is not { } definition) return null;
        var writer = callee.Writers(output)[0];
        if (context.ReaderOf(writer) is not { } reader) return null;
        var result = context.Expand(definition, 0, new HashSet<string>(C) { output }, reader, "v");
        usedDefaults = context.UsedDefaults;
        return result;
    }

    /// <summary>一次读取所在的语句：Order 为块内序号；OrderTrusted 为所在程序段语句顺序可信；
    /// CallInValue 为语句的值/条件里有可能中途改写变量的部件（调用输出、非纯函数、定时器、计数器、边沿、指令盒、Calc）。</summary>
    sealed record Reader(IrUnit Unit, int Order, bool OrderTrusted, bool CallInValue);

    sealed class Context(IrBlock callee, IrCall call, Func<IrExpr, IrExpr> expandCaller, bool useDefaults, string tag)
    {
        readonly Dictionary<IrUnit, bool> unitWritesAnything = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<IrUnit, (bool Any, bool NonLocal)> unitWrites = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<string, bool> mentions = new(C);
        string[]? texts;
        bool? bodyWritesAnything, bodyWritesGlobal;

        public bool UsedDefaults { get; private set; }

        public Reader? ReaderOf(BlockWrite write)
        {
            // 编号重复时 OrderOf 指向第一条同号语句，顺序不可用
            if (!callee.StatementsById.TryGetValue(write.Statement.Id, out var s) || !ReferenceEquals(s, write.Statement)) return null;
            var order = callee.OrderOf(write.Statement.Id);
            if (order < 0) return null;
            return new Reader(write.Unit, order, ExclusionProof.StatementOrderTrusted(write.Unit),
                write.Value is { } v && MayWriteMidway(v) || MayWriteMidway(IrBlock.ExpandNodesIn(write.Unit, write.Statement.Guard)));
        }

        public IrExpr Expand(IrExpr e, int depth, HashSet<string> stack, Reader reader, string path)
        {
            switch (e.Kind)
            {
                case IrKinds.Var: return ExpandVar(e.Name!, depth, stack, reader, path);
                case IrKinds.Const: return e;
                case IrKinds.Not: case IrKinds.And: case IrKinds.Or: case IrKinds.Xor: case IrKinds.Cmp: case IrKinds.Arith:
                    return ExpandArgs(e, depth, stack, reader, path);
                case IrKinds.Func when e.Name is { } f && ExclusionProof.PureFunctions.Contains(f):
                    return ExpandArgs(e, depth, stack, reader, path);
                default:
                    // 定时器/计数器/边沿/指令盒/Calc/调用输出/非纯函数/未识别：值取决于被调块内部状态或认不出，按出现位置独立
                    return ExclusionProof.TaggedAtom(tag, reader.Order, $"被调块内部部件 {IrText.Expr(e)}（读取位置 {path}）");
            }
        }

        IrExpr ExpandArgs(IrExpr e, int depth, HashSet<string> stack, Reader reader, string path) =>
            e.Args is null ? e : e with { Args = e.Args.Select((a, i) => Expand(a, depth, stack, reader, path + "." + i)).ToArray() };

        IrExpr ExpandVar(string name, int depth, HashSet<string> stack, Reader reader, string path)
        {
            var root = IrBlock.RootOf(name);
            var suffix = name[root.Length..];
            var formal = callee.Formal(root);
            IrExpr Opaque(string what, string shown) => Atom(what, shown, reader, path);
            // 下标变量属于被调块作用域（Static/Temp/形参），带进调用方会被重新解释；也可能在块内被改写
            if (HasVariableIndex(name)) return Opaque("变量下标", name);
            if (formal is { Section: "Input" })
            {
                if (callee.IsWritten(root) || callee.IsWritten(name) || TextMentions(root)) return Opaque("被调块改写的输入", name);
                var pin = call.Pins.FirstOrDefault(p => C.Equals(p.Name?.Trim(), root));
                if (!Unwired(pin))
                {
                    var v = pin!.Value!;
                    if (suffix.Length > 0)
                        return v.Kind == IrKinds.Var ? expandCaller(IrExpr.Var(v.Name + suffix)) : Opaque("成员取自非变量实参", name);
                    var actual = expandCaller(v);
                    return callee.IsBoolFormal(root) && actual.Kind == IrKinds.Const ? BoolConst(actual.Name) ?? actual : actual;
                }
                UsedDefaults = true;
                if (!useDefaults || suffix.Length > 0) return Opaque("接口初值", name);
                if (callee.IsBoolFormal(root))
                    return formal.StartValue is null ? IrExpr.Const("FALSE") : BoolConst(formal.StartValue) ?? IrExpr.Const(formal.StartValue);
                return IrExpr.Const(formal.StartValue ?? "0");
            }
            if (formal is { Section: "InOut" }) return Opaque("输入输出形参", name);
            if (formal is { Section: "Output" } || callee.IsLocal(name))
            {
                if (depth < MaxDepth && !stack.Contains(name) && DefinedBefore(name, reader) is { } found)
                {
                    stack.Add(name);
                    var r = Expand(found.Def, depth + 1, stack, found.Writer, "v");
                    stack.Remove(name);
                    return r;
                }
                return Opaque("被调块内部", name);
            }
            // 本块接口里的其它成员（常量、返回值等）不是调用方能读到的名字
            if (callee.Interface.Any(m => C.Equals(m.Name, root))) return Opaque("被调块内部", name);
            if (BodyWritesAnything || BodyWritesGlobal || callee.IsWritten(name)) return Opaque("被调块读写的全局量", name);
            // 被调块以全局名读自己的背景 DB（"Inst_DB".O2）：值在块内被形参写入改过，不是调用前的值
            if (IsOwnInstance(root)) return Opaque("被调块自己的背景实例", name);
            return IrExpr.Var(name);
        }

        /// <summary>name 的唯一定义及其写入语句，且写入在 reader 之前执行（见类注释）。</summary>
        (IrExpr Def, Reader Writer)? DefinedBefore(string name, Reader reader)
        {
            if (TextMentions(IrBlock.RootOf(name)) || callee.Definition(name) is not { } def || InstanceOverlaps(name)) return null;
            var write = callee.Writers(name)[0];
            if (ReaderOf(write) is not { } writer || writer.Order >= reader.Order) return null;
            if (ReferenceEquals(writer.Unit, reader.Unit) && !reader.OrderTrusted) return null;
            return (def, writer);
        }

        IrExpr Atom(string what, string name, Reader reader, string path) =>
            ExclusionProof.TaggedAtom(tag, reader.Order, Free(name, reader) ? $"{what} {name}（段内独立读取 {path}）" : $"{what} {name}");

        /// <summary>这次读取是否独立的自由值：读者语句中途可能改写，或读者所在段顺序不可信且段内可能写到该量
        /// （按 IrBlock 的写入口径，或本段有可能写任何量的语句）。</summary>
        bool Free(string name, Reader reader)
        {
            if (reader.CallInValue) return true;
            if (reader.OrderTrusted) return false;
            if (UnitWritesAnything(reader.Unit)) return true;
            if (callee.WritesTouching(name).Any(w => ReferenceEquals(w.Unit, reader.Unit))) return true;
            var (any, nonLocal) = UnitWrites(reader.Unit);
            // 以全局名读自己的背景实例：本段写任何 Static/形参都可能是它的别名
            if (IsOwnInstance(IrBlock.RootOf(name.Trim()))) return any;
            // 全局类名字：本段写了任何非本块局部的量，可能是它的同址别名（IrBlock 认不出符号别名与 InOut 引用）
            return !IsBlockOwned(name) && nonLocal;
        }

        /// <summary>本段的写入目标（与 IrBlock 同一套 ProjectWrites.StatementWrites）：有无写入、有无非本块局部的写入。</summary>
        (bool Any, bool NonLocal) UnitWrites(IrUnit unit)
        {
            if (unitWrites.TryGetValue(unit, out var known)) return known;
            var targets = unit.Statements.SelectMany(ProjectWrites.StatementWrites)
                .Select(w => w.Target).Where(t => t is not null && IsWriteTarget(t)).Select(t => t!.Trim()).ToArray();
            return unitWrites[unit] = (targets.Length > 0, targets.Any(t => !IsBlockOwned(t)));
        }

        /// <summary>只属于本块、不会被全局写入改到的名字：Static/Temp；FB 的输入/输出形参（背景实例里的副本）。</summary>
        bool IsBlockOwned(string name) =>
            callee.IsLocal(name) || callee.Formal(IrBlock.RootOf(name)) is { Section: "Input" or "Output" };

        static bool IsWriteTarget(string target) => !string.IsNullOrWhiteSpace(target) && target.Trim() != "?";

        /// <summary>块写了调用方/全局的变量：IrBlock 的全部写入目标里有不属于本块的（含绝对地址、认不出的目标、全局背景 DB、
        /// InOut）。</summary>
        bool BodyWritesGlobal => bodyWritesGlobal ??= callee.WriteTargets.Any(t => IsWriteTarget(t) && !IsBlockOwned(t.Trim()));

        /// <summary>被调块以全局名写本次调用的背景实例（"Inst".S := …），或文字段提到它。</summary>
        public bool OwnInstanceWritten =>
            call.Instance is { Length: > 0 } instance && !string.IsNullOrWhiteSpace(Bare(IrBlock.RootOf(instance.Trim())))
            && (callee.WriteTargets.Any(t => IsWriteTarget(t) && IsOwnInstance(IrBlock.RootOf(t.Trim())))
                || TextMentions(IrBlock.RootOf(instance.Trim())));

        bool BodyWritesAnything => bodyWritesAnything ??= callee.Units.Any(UnitWritesAnything);

        /// <summary>本段有可能写任何量的语句：调用、文字段（源码/顺控动作/未识别元素）、指令盒语句、值或条件里内嵌
        /// 有状态/未展开的部件（即使 IR 没记 SideWrites）。</summary>
        bool UnitWritesAnything(IrUnit unit)
        {
            if (unitWritesAnything.TryGetValue(unit, out var known)) return known;
            var result = unit.Statements.Any(st => st.Call is not null
                || st.Kind is IrStatementKinds.Call or IrStatementKinds.Source or IrStatementKinds.Action
                    or IrStatementKinds.Unrecognized or IrStatementKinds.Box
                || MayWriteMidway(IrBlock.ExpandNodesIn(unit, st.Guard))
                || st.Value is { } v && MayWriteMidway(IrBlock.ExpandNodesIn(unit, v)));
            return unitWritesAnything[unit] = result;
        }

        static bool HasVariableIndex(string name) =>
            IrBlock.Segments(name.Trim()).Any(seg => seg.StartsWith('[') && !IrBlock.LiteralIndex.IsMatch(seg));

        bool IsOwnInstance(string root) =>
            call.Instance is { Length: > 0 } instance && C.Equals(Bare(IrBlock.RootOf(instance.Trim())), Bare(root));

        /// <summary>被调块里某个调用的背景实例与 name 同根（多重背景的成员由调用内部写入，不进写入表）。</summary>
        bool InstanceOverlaps(string name)
        {
            var root = Bare(IrBlock.RootOf(name.Trim()));
            return callee.Units.SelectMany(u => u.Statements).Any(s => s.Call is { Instance: { Length: > 0 } instance }
                && C.Equals(Bare(IrBlock.RootOf(instance.Trim())), root));
        }

        static string Bare(string name) => name.Trim().TrimStart('#').Trim('"');

        /// <summary>任何文字段（源码、顺控动作、未识别元素）以标识符提到 root（其后可接成员/下标）：可能在文字里被写。
        /// 比 IrBlock 的完整名字匹配宽（root 后接 '.'、'[' 也算），只会多判。</summary>
        public bool TextMentions(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            if (mentions.TryGetValue(root, out var known)) return known;
            texts ??= callee.Units.SelectMany(u => u.Statements)
                .Where(s => s.Kind is IrStatementKinds.Source or IrStatementKinds.Action or IrStatementKinds.Unrecognized && s.Text is { Length: > 0 })
                .Select(s => s.Text!).ToArray();
            var pattern = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(Bare(root)) + @"(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return mentions[root] = texts.Any(pattern.IsMatch);
        }

        /// <summary>表达式里有求值时可能改写变量的部件：调用输出、非纯函数、定时器、计数器、边沿、指令盒、Calc。</summary>
        static bool MayWriteMidway(IrExpr e) =>
            e.Kind is IrKinds.CallOut or IrKinds.Timer or IrKinds.Counter or IrKinds.Rise or IrKinds.Fall or IrKinds.Instr or IrKinds.Calc
            || e.Kind == IrKinds.Func && !(e.Name is { } f && ExclusionProof.PureFunctions.Contains(f))
            || e.Args is not null && e.Args.Any(MayWriteMidway);
    }

    static bool Unwired(IrPin? pin) => pin?.Value is null || pin.Value.Kind == IrKinds.Unknown && pin.Value.Name == "未接线";

    static IrExpr? BoolConst(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "1" or "TRUE" => IrExpr.True,
        "0" or "FALSE" => IrExpr.Const("FALSE"),
        _ => null
    };
}
