#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>spec §7.1「信号在全程序只被写入常数」：这样的 %Q 没有诊断价值，含它的规则（任何来源）都不产。
/// 已知代价（只会漏）：程序从不写入的 %M 当常量，CPU 按硬件配置置位的时钟存储位也在其中（不能按名字区分），
/// 所以只由时钟存储位驱动的输出（如闪烁灯）会被当成常量输出，含它的规则被删。</summary>
public static class ConstantOutputs
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyDictionary<string, PipelineTag> UniqueTags(IReadOnlyList<PipelineTag> tags) =>
        tags.GroupBy(t => t.Name, C).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), C);

    /// <summary>常量样变量：点表唯一、地址在 %M 区、程序从不写入（含地址别名）、文字段也不提到（系统存储位/时钟存储位一类；
    /// 按地址区判定，不看名字）。程序从不写入的 DB 变量不算（可能由 HMI 写）。另：常量位（ConstantBits，含程序里只写同一常数的 %M 位）也算。</summary>
    public static bool IsConstantLike(string name, IReadOnlyDictionary<string, PipelineTag> uniqueTags, ProjectWrites writes,
        IReadOnlyDictionary<string, bool>? constantBits = null) =>
        constantBits?.ContainsKey(name.Trim()) == true
        || uniqueTags.TryGetValue(name, out var t) && t.Address.StartsWith("%M", StringComparison.OrdinalIgnoreCase)
        && writes.Writers(name).Count == 0 && !writes.MentionedInText(name);

    /// <summary>值只由常数与变量经与/或/非/异或组成（认不出的、比较、定时器等都不算常数；读不到的常数「?」也不算）。</summary>
    static bool IsPlainLogic(IrExpr e) =>
        e.Kind == IrKinds.Const && e.Name is { Length: > 0 } c && c != "?"
        || e.Kind == IrKinds.Var
        || e.Kind is IrKinds.Not or IrKinds.And or IrKinds.Or or IrKinds.Xor && e.Args is { Count: > 0 } args && args.All(IsPlainLogic);

    /// <summary>表达式的规范文本（忽略大小写），用于比较各处写入的值是否完全相同。</summary>
    static string Canon(IrExpr e) =>
        e.Kind + "(" + (e.Name ?? "").ToUpperInvariant() + (e.Args is null ? "" : ":" + string.Join(",", e.Args.Select(Canon))) + ")";

    /// <summary>唯一 Bool %Q 点，且全项目每次写入都是无条件赋值、值只读常数或常量样变量，并且各处写入的值完全相同
    /// （OB1 写 TRUE、另一块写 FALSE 时输出随执行路径变化，不是常量）；同位的别名/字节字写入、可能写入（见 ProjectWrites）、
    /// 文字段写入都会让写入处多于按名字找到的语句，此时不算。返回点表里的规范名。</summary>
    /// <param name="constantBits">常量位（ConstantBits）：由它们驱动的输出同样是常量输出。</param>
    public static IReadOnlySet<string> Find(IEnumerable<IrUnit> units, IReadOnlyList<PipelineTag> tags, ProjectWrites writes,
        Func<string, int> qWriteCount, IReadOnlyDictionary<string, bool>? constantBits = null)
    {
        var unique = UniqueTags(tags);
        var writers = new Dictionary<string, List<(IrUnit Unit, IrStatement Statement)>>(C);
        void Add(string? target, IrUnit unit, IrStatement s)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            var key = target.Trim();
            if (!writers.TryGetValue(key, out var list)) writers[key] = list = [];
            list.Add((unit, s));
        }
        foreach (var unit in units)
            foreach (var s in unit.Statements)
            {
                if (s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset) Add(s.Target, unit, s);
                if (s.Call is not null) foreach (var t in s.Call.Pins.SelectMany(p => p.Targets)) Add(t, unit, s);
            }
        var result = new HashSet<string>(C);
        foreach (var (name, list) in writers)
        {
            if (!unique.TryGetValue(name, out var tag) || !string.Equals(tag.DataType, "Bool", StringComparison.OrdinalIgnoreCase)
                || !tag.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) continue;
            if (writes.Writers(name).Count != list.Count || writes.MentionedInText(name) || qWriteCount(name) > list.Count) continue;
            if (list.Any(w => w.Statement.Kind != IrStatementKinds.Assign || !w.Statement.Guard.IsTrue || w.Statement.Value is null)) continue;
            var values = list.Select(w => IrBlock.ExpandNodesIn(w.Unit, w.Statement.Value!)).ToArray();
            if (values.All(v => IsPlainLogic(v) && v.Vars().All(x => IsConstantLike(x, unique, writes, constantBits)))
                && values.Select(Canon).Distinct(StringComparer.Ordinal).Count() == 1)
                result.Add(tag.Name);
        }
        return result;
    }
}
