#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>常量位：点表里唯一的 Bool %M 位，全程序取值恒定（恒为 1 / 恒为 0）。来源两种，结果按点表规范名给出：
/// ① 按程序结构（不看名字）：全项目全部写入都给出同一个常数——无条件赋值布尔常数，或条件恒真（可满足性检查其否定不可满足，如
///    「A OR NOT A」）的置位（= 1）/ 复位（= 0）；没有不确定写入、文字段不提到；每条写入所在块无跳转/返回，且每个扫描都执行：
///    程序循环组织块（导出事件类型 ProgramCycle，不按名字），或从它经无跳转/返回的块逐层无条件调用到的块（第一个扫描执行之前的读取不建模）。
///    中断、错误、启动等组织块（以及只从它们调用到的块）里的写入一律使该位不是常量；认不出组织块类型就不认（审查 I-1）。
/// ② <b>特例：全仓唯一一处按名字/注释判断</b>（用户 2026-10-02 裁定，系统存储位当常量）：全项目从不写入（写入索引含 FC、含地址别名）、
///    没有可能写到它的不确定写入、文字段也不提到的 Bool %M 位，点名是博图默认系统存储位名 AlwaysTRUE / AlwaysFALSE（忽略大小写），
///    或点表注释整条就是恒为 1 / 恒为 0 的说法（「始终为1」「恒为 1（高电平）」「Always TRUE」「always = 1」等，带条件的描述不算）。名字与注释说法相反、
///    注释同时出现两种说法时不认。值来自 CPU 硬件配置，程序里看不到，所以只能这样认；其它任何地方都不得按名字判断。</summary>
public static class ConstantBits
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    // 注释只认整条就是「恒为 1/0」的说法（去掉末尾括号说明与标点后），描述条件的注释（「自动模式下始终为1」「Always ON during auto mode」）不算（审查 M-3）
    static readonly Regex CommentTrue = new(
        @"^(?:(?:始终|恒|总是|一直|永远)\s*为?\s*(?:1|TRUE|真|高电平)|always\s*(?:=\s*)?(?:true|1|high))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex CommentFalse = new(
        @"^(?:(?:始终|恒|总是|一直|永远)\s*为?\s*(?:0|FALSE|假|低电平)|always\s*(?:=\s*)?(?:false|0|low))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex TrailingNote = new(@"\s*(?:\([^()]*\)|（[^（）]*）|[。.;；,，!！])\s*$", RegexOptions.CultureInvariant);

    static string CoreComment(string comment)
    {
        var c = comment.Trim();
        for (var k = 0; k < 4 && TrailingNote.Match(c) is { Success: true } m; k++) c = c[..m.Index].Trim();
        return c;
    }

    /// <summary>特例 ②：按点名/注释认出的值（不核对写入）；认不出或说法矛盾为 null。</summary>
    internal static bool? ByNameOrComment(PipelineTag tag)
    {
        bool? byName = tag.Name.Trim() switch
        {
            var n when C.Equals(n, "AlwaysTRUE") => true,
            var n when C.Equals(n, "AlwaysFALSE") => false,
            _ => null
        };
        var comment = CoreComment(tag.Comment ?? "");
        var t = CommentTrue.IsMatch(comment);
        var f = CommentFalse.IsMatch(comment);
        bool? byComment = t && f ? null : t ? true : f ? false : null;
        if (t && f) return null;
        if (byName is not null && byComment is not null && byName != byComment) return null;
        return byName ?? byComment;
    }

    /// <param name="units">全项目程序段（含 FC）。</param>
    /// <summary>每个扫描都执行、且块内无跳转/返回的块：程序循环组织块，以及从它们逐层无条件（EN 与条件恒真）调用到的块。</summary>
    /// <param name="isProgramCycleBlock">程序循环组织块（按导出事件类型 ProgramCycle 判断）。</param>
    /// <param name="knownBits">已认出的常量位：EN/条件里读到它们时按常数代入（如 OB1 里 EN 接恒真位的调用）。</param>
    internal static HashSet<string> EveryScanBlocks(IReadOnlyList<IrUnit> all, Func<string, bool> isProgramCycleBlock,
        IReadOnlyDictionary<string, bool>? knownBits = null)
    {
        bool Always(IrExpr e) => CallSiteSubstitution.IsTrue(knownBits is null ? e
            : BoolSimplifier.Substitute(e, n => knownBits.TryGetValue(n.Trim(), out var v) ? (v ? IrExpr.True : IrExpr.Const("FALSE")) : null));
        var controlFlow = all.Where(u => u.Statements.Any(s => s.Kind is IrStatementKinds.Jump or IrStatementKinds.JumpNot or IrStatementKinds.Return))
            .Select(u => u.BlockName.Trim()).ToHashSet(C);
        var everyScan = all.Select(u => u.BlockName.Trim()).Where(b => isProgramCycleBlock(b) && !controlFlow.Contains(b)).ToHashSet(C);
        var byBlock = all.GroupBy(u => u.BlockName.Trim(), C).ToDictionary(g => g.Key, g => g.ToArray(), C);
        var queue = new Queue<string>(everyScan);
        while (queue.Count > 0)
            foreach (var u in byBlock.GetValueOrDefault(queue.Dequeue()) ?? [])
                foreach (var s in u.Statements)
                    if (s.Call is { } call && Always(IrBlock.ExpandNodesIn(u, call.En)) && Always(IrBlock.ExpandNodesIn(u, s.Guard))
                        && call.Callee is { Length: > 0 } callee && !controlFlow.Contains(callee.Trim()) && everyScan.Add(callee.Trim()))
                        queue.Enqueue(callee.Trim());
        return everyScan;
    }

    /// <param name="isProgramCycleBlock">程序循环组织块（按导出事件类型 ProgramCycle 判断，不按名字）。</param>
    public static IReadOnlyDictionary<string, bool> Find(IEnumerable<IrUnit> units, IReadOnlyList<PipelineTag> tags, ProjectWrites writes,
        Func<string, bool> isProgramCycleBlock)
    {
        var all = units.ToArray();
        var statements = new Dictionary<(long, string), (IrUnit Unit, IrStatement Statement)>();
        foreach (var u in all)
            foreach (var s in u.Statements)
                statements.TryAdd((u.UnitId, s.Id), (u, s));
        // 先认按名字/注释的常量位（从不写入，不依赖执行路径），再用它们判断哪些调用每扫描都执行（OB1 里 EN 接恒真位的调用很常见）
        var unique = ConstantOutputs.UniqueTags(tags).Values.Where(tag => string.Equals(tag.DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase)
            && tag.Address.Trim().StartsWith("%M", StringComparison.OrdinalIgnoreCase)
            && !writes.HasUncertainWriter(tag.Name.Trim()) && !writes.MentionedInText(tag.Name.Trim())).ToArray();
        var byName = new Dictionary<string, bool>(C);
        foreach (var tag in unique)
            if (writes.Writers(tag.Name.Trim()).Count == 0 && ByNameOrComment(tag) is { } named) byName[tag.Name] = named;
        var everyScan = EveryScanBlocks(all, isProgramCycleBlock, byName);

        var result = new Dictionary<string, bool>(byName, C);
        foreach (var tag in unique)
        {
            var ws = writes.Writers(tag.Name.Trim());
            if (ws.Count > 0 && Written(ws) is { } v) result[tag.Name] = v;
        }
        return result;

        bool? Written(IReadOnlyList<(long UnitId, string StatementId)> ws)
        {
            bool? value = null;
            foreach (var w in ws)
            {
                if (!statements.TryGetValue(w, out var hit)) return null;
                var (unit, s) = hit;
                var block = unit.BlockName.Trim();
                if (!everyScan.Contains(block) || s.SideWrites is { Count: > 0 }) return null;
                bool? v = s.Kind switch
                {
                    IrStatementKinds.Assign when CallSiteSubstitution.IsTrue(s.Guard) && s.Value is not null
                        => IrBlock.ExpandNodesIn(unit, s.Value) is { Kind: IrKinds.Const, Name: var k } ? BoolText(k) : null,
                    IrStatementKinds.Set when AlwaysTrue(IrBlock.ExpandNodesIn(unit, s.Guard)) => true,
                    IrStatementKinds.Reset when AlwaysTrue(IrBlock.ExpandNodesIn(unit, s.Guard)) => false,
                    _ => null
                };
                if (v is null || value is not null && value != v) return null;
                value = v;
            }
            return value;
        }
    }

    static bool? BoolText(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" => true, "FALSE" or "0" => false, _ => null
    };

    /// <summary>条件恒真：其否定不可满足。变量按名字（忽略大小写）、其它原子按文字各取一个命题变量（同一条件里同一读取取值相同）。</summary>
    internal static bool AlwaysTrue(IrExpr guard) => CallSiteSubstitution.IsTrue(guard) || Satisfiable(IrExpr.Not(guard)) == false;

    /// <summary>命题可满足性：变量按名字（忽略大小写）、其它原子（比较、定时器等）按文字各取一个命题变量；判不了为 null。</summary>
    internal static bool? Satisfiable(IrExpr expr)
    {
        var vars = new Dictionary<string, int>(StringComparer.Ordinal);
        PropositionalSat.Formula Atom(string key, bool positive)
        {
            if (!vars.TryGetValue(key, out var id)) vars[key] = id = vars.Count;
            return new PropositionalSat.Lit(id, positive);
        }
        PropositionalSat.Formula Build(IrExpr e) => e.Kind switch
        {
            IrKinds.And => PropositionalSat.And(e.Args!.Select(Build)),
            IrKinds.Or => PropositionalSat.Or(e.Args!.Select(Build)),
            IrKinds.Const => BoolText(e.Name) switch { true => PropositionalSat.True, false => PropositionalSat.False, _ => Atom("C:" + e.Name, true) },
            IrKinds.Not => Negated(e.Args![0]),
            IrKinds.Var => Atom("V:" + e.Name!.Trim().ToUpperInvariant(), true),
            _ => Atom("A:" + IrText.Expr(e), true)
        };
        PropositionalSat.Formula Negated(IrExpr x) => x.Kind switch
        {
            IrKinds.Var => Atom("V:" + x.Name!.Trim().ToUpperInvariant(), false),
            _ => Atom("A:" + IrText.Expr(x), false)
        };
        var formula = Build(BoolSimplifier.ToNnf(expr));
        return PropositionalSat.Satisfiable(formula, vars.Count);
    }
}
