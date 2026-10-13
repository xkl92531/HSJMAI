#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>DNF 中的一个文字：Var（正/负）或不能再分解的原子（比较、计时器、运算等，Opaque=true）。</summary>
public sealed record Literal(string Key, bool Positive, bool Opaque, IrExpr Atom);

/// <summary>布尔化简：代入、常数折叠、析取范式。纯函数，不看任何名字。</summary>
public static class BoolSimplifier
{
    public static IrExpr Substitute(IrExpr e, Func<string, IrExpr?> resolve)
    {
        if (e.Kind == IrKinds.Var) return resolve(e.Name!) ?? e;
        return e.Args is null ? e : e with { Args = e.Args.Select(a => Substitute(a, resolve)).ToArray() };
    }

    static bool? BoolConst(IrExpr e) => e.Kind != IrKinds.Const ? null : e.Name?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" => true, "FALSE" or "0" => false, _ => null
    };

    static IrExpr B(bool v) => v ? IrExpr.True : IrExpr.Const("FALSE");

    static bool TryNumber(IrExpr e, out double value)
    {
        value = 0;
        return e.Kind == IrKinds.Const && double.TryParse(e.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static IrExpr Fold(IrExpr e)
    {
        switch (e.Kind)
        {
            case IrKinds.Const:
                // 只统一 true/false 文本；1/0 可能是比较/运算的数值操作数，保持原样（在 And/Or/Not 里仍按布尔常数处理）。
                return e.Name?.Trim().ToUpperInvariant() switch { "TRUE" => B(true), "FALSE" => B(false), _ => e };
            case IrKinds.Not:
            {
                var x = Fold(e.Args![0]);
                return BoolConst(x) is { } c ? B(!c) : IrExpr.Not(x);
            }
            case IrKinds.And:
            {
                var items = new List<IrExpr>();
                foreach (var a in e.Args!.Select(Fold))
                {
                    var c = BoolConst(a);
                    if (c == false) return B(false);
                    if (c != true) items.Add(a);
                }
                return IrExpr.And(items);
            }
            case IrKinds.Or:
            {
                var items = new List<IrExpr>();
                foreach (var a in e.Args!.Select(Fold))
                {
                    var c = BoolConst(a);
                    if (c == true) return B(true);
                    if (c != false) items.Add(a);
                }
                return items.Count == 0 ? B(false) : IrExpr.Or(items);
            }
            case IrKinds.Cmp:
            {
                var a = Fold(e.Args![0]); var b = Fold(e.Args[1]);
                if (TryNumber(a, out var x) && TryNumber(b, out var y))
                    switch (e.Name)
                    {
                        case "==": return B(x == y);
                        case "<>": return B(x != y);
                        case ">": return B(x > y);
                        case ">=": return B(x >= y);
                        case "<": return B(x < y);
                        case "<=": return B(x <= y);
                    }
                return e with { Args = [a, b] };
            }
            default:
                return e.Args is null ? e : e with { Args = e.Args.Select(Fold).ToArray() };
        }
    }

    public static IReadOnlyList<IReadOnlyList<Literal>>? ToDnf(IrExpr e, int maxTerms = 64)
    {
        var result = Dnf(Fold(e), maxTerms);
        return result?.Select(t => (IReadOnlyList<Literal>)t.DistinctBy(l => (l.Key.ToUpperInvariant(), l.Positive)).ToArray()).ToArray();
    }

    /// <summary>同 ToDnf，但先把取反推到变量上（德摩根）：NOT(a OR b) → NOT a AND NOT b，NOT(a AND b) → NOT a OR NOT b，
    /// NOT NOT x → x。比较、计时器等原子的取反仍是不透明负文字。只供代码证明用（ToDnf 的既有行为不变）。</summary>
    public static IReadOnlyList<IReadOnlyList<Literal>>? ToDnfNnf(IrExpr e, int maxTerms = 64) =>
        ToDnf(PushNot(Fold(e), false), maxTerms);

    /// <summary>常数折叠后把取反推到变量/原子上（同 ToDnfNnf 的前半步），不展开成析取范式：供可满足性判定用。</summary>
    internal static IrExpr ToNnf(IrExpr e) => PushNot(Fold(e), false);

    static IrExpr PushNot(IrExpr e, bool negate)
    {
        switch (e.Kind)
        {
            case IrKinds.Not:
                return PushNot(e.Args![0], !negate);
            case IrKinds.And:
            {
                var items = e.Args!.Select(a => PushNot(a, negate)).ToArray();
                return negate ? IrExpr.Or(items) : IrExpr.And(items);
            }
            case IrKinds.Or:
            {
                var items = e.Args!.Select(a => PushNot(a, negate)).ToArray();
                return negate ? IrExpr.And(items) : IrExpr.Or(items);
            }
            case IrKinds.Const when BoolConst(e) is { } c:
                return B(negate ? !c : c);
            default:
                return negate ? IrExpr.Not(e) : e;
        }
    }

    static List<List<Literal>>? Dnf(IrExpr e, int max)
    {
        switch (e.Kind)
        {
            case IrKinds.Const when BoolConst(e) == true: return [[]];
            case IrKinds.Const when BoolConst(e) == false: return [];
            case IrKinds.Or:
            {
                var all = new List<List<Literal>>();
                foreach (var a in e.Args!)
                {
                    var d = Dnf(a, max);
                    if (d is null) return null;
                    all.AddRange(d);
                    if (all.Count > max) return null;
                }
                return all;
            }
            case IrKinds.And:
            {
                List<List<Literal>> acc = [[]];
                foreach (var a in e.Args!)
                {
                    var d = Dnf(a, max);
                    if (d is null) return null;
                    var next = new List<List<Literal>>();
                    foreach (var left in acc)
                        foreach (var right in d)
                        {
                            next.Add([.. left, .. right]);
                            if (next.Count > max) return null;
                        }
                    acc = next;
                }
                return acc;
            }
            case IrKinds.Not when e.Args![0].Kind == IrKinds.Var:
                return [[new Literal(e.Args[0].Name!, false, false, e.Args[0])]];
            case IrKinds.Var:
                return [[new Literal(e.Name!, true, false, e)]];
            case IrKinds.Not:
                return [[new Literal(IrText.Expr(e.Args![0]), false, true, e.Args[0])]];
            default:
                return [[new Literal(IrText.Expr(e), true, true, e)]];
        }
    }
}
