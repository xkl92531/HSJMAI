#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>按 IR 的写入关系定位约束来自哪个程序段（结构判定，不看名字）。</summary>
public sealed class UnitLocator
{
    readonly IReadOnlyList<(PipelineUnit Unit, HashSet<string> Writes, HashSet<string> Reads)> index;

    readonly Dictionary<PipelineUnit, (HashSet<string> Writes, HashSet<string> Reads)> byUnit = new(ReferenceEqualityComparer.Instance);

    public UnitLocator(IReadOnlyList<PipelineUnit> units)
    {
        index = units.Select(u =>
        {
            var (writes, reads) = Sets(u);
            byUnit.TryAdd(u, (writes, reads));
            return (u, writes, reads);
        }).ToArray();
    }

    static (HashSet<string> Writes, HashSet<string> Reads) Sets(PipelineUnit u) => Sets(UnitTranslator.Translate(u));

    /// <summary>程序段写入的变量（赋值/置位/复位目标、调用输出引脚接线）与读到的变量（条件、值、调用输入引脚、提取出的共享节点）。</summary>
    static (HashSet<string> Writes, HashSet<string> Reads) Sets(IrUnit ir)
    {
        var writes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ir.Statements)
        {
            if (s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset && s.Target != null) writes.Add(s.Target);
            if (s.Call != null)
                foreach (var pin in s.Call.Pins)
                {
                    foreach (var t in pin.Targets) writes.Add(t);
                    if (pin.Value != null) reads.UnionWith(pin.Value.Vars());
                }
            reads.UnionWith(s.Guard.Vars());
            if (s.Value != null) reads.UnionWith(s.Value.Vars());
        }
        foreach (var n in ir.Nodes) reads.UnionWith(n.Value.Vars());
        return (writes, reads);
    }

    /// <summary>程序段读写到的 IO 点个数（按 isIo 判定，不分大小写去重）。含共享节点里的变量与任意语句的写入目标。</summary>
    public static int IoTouched(IrUnit ir, Func<string, bool> isIo)
    {
        var (writes, reads) = Sets(ir);
        var all = new HashSet<string>(writes, StringComparer.OrdinalIgnoreCase);
        all.UnionWith(reads);
        foreach (var s in ir.Statements) if (s.Target != null) all.Add(s.Target);
        return all.Count(isIo);
    }

    public PipelineUnit? Find(IReadOnlyList<(string Name, string Role)> signals)
    {
        var ordered = signals.Where(s => s.Role == "Source").Concat(signals.Where(s => s.Role != "Source")).Select(s => s.Name).ToArray();
        foreach (var name in ordered)
        {
            var writer = index.Where(x => x.Writes.Contains(name))
                .OrderBy(x => x.Unit.BlockName, StringComparer.Ordinal).ThenBy(x => x.Unit.NetworkOrder).FirstOrDefault();
            if (writer.Unit != null) return writer.Unit;
        }
        var best = index.Select(x => (x.Unit, Count: ordered.Count(x.Reads.Contains))).Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Unit.BlockName, StringComparer.Ordinal).ThenBy(x => x.Unit.NetworkOrder)
            .FirstOrDefault();
        return best.Unit;
    }

    /// <summary>该程序段是否写入或读到规则中的至少一个信号（用于核对按块名+网络号定位的结果；
    /// 归档的网络号与正式库解析器的网络号口径可能错位，错位时定位到的程序段与规则无关）。</summary>
    public bool Touches(PipelineUnit unit, IReadOnlyList<(string Name, string Role)> signals)
    {
        var (writes, reads) = byUnit.TryGetValue(unit, out var sets) ? sets : Sets(unit);
        return signals.Any(s => writes.Contains(s.Name) || reads.Contains(s.Name));
    }

    public PipelineUnit? Find(string block, int? network) =>
        index.Select(x => x.Unit).FirstOrDefault(u => string.Equals(u.BlockName, block, StringComparison.OrdinalIgnoreCase) && u.NetworkOrder == network);
}
