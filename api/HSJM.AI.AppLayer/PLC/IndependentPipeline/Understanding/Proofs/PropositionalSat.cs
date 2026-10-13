#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>命题可满足性判定（CDCL：单元传播 + 冲突学习 + 活跃度选变量，带冲突次数上限）。纯函数，只用于代码证明：
/// 把「两个输出定义同时成立」这类公式编码成合取范式（Plaisted-Greenbaum 单向编码，公式须为否定已推到文字上的形式），
/// 判定能否同时为真。取代按析取范式逐项枚举（项数一多就放弃）的做法。</summary>
internal static class PropositionalSat
{
    internal abstract record Formula;
    /// <summary>命题变量 Var（从 0 起）的正/负文字。</summary>
    internal sealed record Lit(int Var, bool Positive) : Formula;
    internal sealed record Gate(bool IsAnd, IReadOnlyList<Formula> Children) : Formula;
    internal sealed record Value(bool IsTrue) : Formula;

    public static readonly Formula True = new Value(true), False = new Value(false);

    public static Formula And(IEnumerable<Formula> items) => Join(true, items);
    public static Formula Or(IEnumerable<Formula> items) => Join(false, items);

    /// <summary>化简常量：合取里有假即假、真略去；析取里有真即真、假略去；空合取为真、空析取为假；单个子项直接返回。</summary>
    static Formula Join(bool isAnd, IEnumerable<Formula> items)
    {
        var kids = new List<Formula>();
        foreach (var item in items)
        {
            if (item is Value v)
            {
                if (v.IsTrue != isAnd) return v;   // 合取遇假、析取遇真
                continue;
            }
            if (item is Gate g && g.IsAnd == isAnd) kids.AddRange(g.Children);
            else kids.Add(item);
        }
        if (kids.Count == 0) return isAnd ? True : False;
        return kids.Count == 1 ? kids[0] : new Gate(isAnd, kids);
    }

    /// <summary>公式（只含 Lit/Gate/Value，常量已化简）与附加子句（文字编码：变量 v 的正文字 2v、负文字 2v+1）同时可满足：
    /// true 可满足，false 不可满足，null 超出冲突上限（判不了）。varCount 为公式与附加子句里出现的命题变量数。</summary>
    public static bool? Satisfiable(Formula formula, int varCount, IReadOnlyList<int[]>? extraClauses = null, int maxConflicts = 100_000)
    {
        if (formula is Value { IsTrue: false }) return false;
        var clauses = new List<int[]>();
        var next = varCount;
        int Encode(Formula f)
        {
            switch (f)
            {
                case Lit l: return 2 * l.Var + (l.Positive ? 0 : 1);
                case Gate g:
                {
                    var aux = 2 * next++;
                    var kids = g.Children.Select(Encode).ToArray();
                    if (g.IsAnd) foreach (var k in kids) clauses.Add([aux ^ 1, k]);
                    else clauses.Add([aux ^ 1, .. kids]);
                    return aux;
                }
                default: throw new InvalidOperationException("公式里不应再有常量（已化简）");
            }
        }
        if (formula is not Value) clauses.Add([Encode(formula)]);
        if (extraClauses is not null) clauses.AddRange(extraClauses.Select(c => c.ToArray()));
        return new Solver(next, clauses).Solve(maxConflicts);
    }

    sealed class Solver
    {
        readonly int n;
        readonly List<int[]> clauses = [];
        readonly List<int>[] watches;
        readonly sbyte[] value;
        readonly int[] level, reason;
        readonly double[] activity;
        readonly bool[] phase, seen;
        readonly List<int> trail = [], limits = [];
        int qhead;
        double increment = 1;
        readonly bool unsatAtStart;

        public Solver(int vars, List<int[]> input)
        {
            n = vars;
            watches = new List<int>[2 * n];
            for (var i = 0; i < watches.Length; i++) watches[i] = [];
            value = new sbyte[n]; level = new int[n]; reason = new int[n]; activity = new double[n];
            phase = new bool[n]; seen = new bool[n];
            foreach (var raw in input)
            {
                var c = raw.Distinct().ToArray();
                if (c.Any(l => c.Contains(l ^ 1))) continue;   // 恒真子句
                if (c.Length == 0) { unsatAtStart = true; return; }
                foreach (var l in c) activity[l >> 1] += 1e-6;   // 出现多的变量先选
                if (c.Length == 1)
                {
                    if (!Enqueue(c[0], -1)) { unsatAtStart = true; return; }
                    continue;
                }
                clauses.Add(c);
                watches[c[0]].Add(clauses.Count - 1);
                watches[c[1]].Add(clauses.Count - 1);
            }
        }

        sbyte Of(int lit) { var v = value[lit >> 1]; return (lit & 1) == 0 ? v : (sbyte)-v; }

        bool Enqueue(int lit, int from)
        {
            var current = Of(lit);
            if (current != 0) return current > 0;
            value[lit >> 1] = (sbyte)((lit & 1) == 0 ? 1 : -1);
            level[lit >> 1] = limits.Count;
            reason[lit >> 1] = from;
            trail.Add(lit);
            return true;
        }

        /// <summary>单元传播：返回冲突子句编号，无冲突返回 -1。watches[l] 是监视文字 l 的子句（l 变假时检查）。</summary>
        int Propagate()
        {
            while (qhead < trail.Count)
            {
                var falseLit = trail[qhead++] ^ 1;
                var ws = watches[falseLit];
                int i = 0, j = 0;
                while (i < ws.Count)
                {
                    var ci = ws[i++];
                    var c = clauses[ci];
                    if (c[0] == falseLit) (c[0], c[1]) = (c[1], c[0]);
                    if (Of(c[0]) > 0) { ws[j++] = ci; continue; }
                    var moved = false;
                    for (var k = 2; k < c.Length; k++)
                        if (Of(c[k]) >= 0)
                        {
                            (c[1], c[k]) = (c[k], c[1]);
                            watches[c[1]].Add(ci);
                            moved = true;
                            break;
                        }
                    if (moved) continue;
                    ws[j++] = ci;
                    if (Of(c[0]) < 0)
                    {
                        while (i < ws.Count) ws[j++] = ws[i++];
                        ws.RemoveRange(j, ws.Count - j);
                        return ci;
                    }
                    Enqueue(c[0], ci);
                }
                ws.RemoveRange(j, ws.Count - j);
            }
            return -1;
        }

        void Bump(int v)
        {
            if ((activity[v] += increment) > 1e100)
            {
                for (var i = 0; i < n; i++) activity[i] *= 1e-100;
                increment *= 1e-100;
            }
        }

        /// <summary>第一唯一蕴含点学习：返回学到的子句（[0] 为回跳后成为单元的文字，[1] 为回跳层最高的文字）与回跳层。</summary>
        (int[] Learnt, int Level) Analyze(int conflict)
        {
            var learnt = new List<int> { -1 };
            var pending = 0;
            var p = -1;
            var index = trail.Count - 1;
            var ci = conflict;
            do
            {
                var c = clauses[ci];
                for (var k = p == -1 ? 0 : 1; k < c.Length; k++)
                {
                    var q = c[k];
                    var v = q >> 1;
                    if (seen[v] || level[v] == 0) continue;
                    seen[v] = true;
                    Bump(v);
                    if (level[v] >= limits.Count) pending++;
                    else learnt.Add(q);
                }
                while (!seen[trail[index] >> 1]) index--;
                p = trail[index--];
                ci = reason[p >> 1];
                seen[p >> 1] = false;
                pending--;
            } while (pending > 0);
            learnt[0] = p ^ 1;
            var back = 0;
            for (var k = 1; k < learnt.Count; k++)
            {
                seen[learnt[k] >> 1] = false;
                if (level[learnt[k] >> 1] > back)
                {
                    back = level[learnt[k] >> 1];
                    (learnt[1], learnt[k]) = (learnt[k], learnt[1]);
                }
            }
            return (learnt.ToArray(), back);
        }

        void CancelUntil(int target)
        {
            if (limits.Count <= target) return;
            for (var i = trail.Count - 1; i >= limits[target]; i--)
            {
                var v = trail[i] >> 1;
                phase[v] = value[v] > 0;
                value[v] = 0;
            }
            trail.RemoveRange(limits[target], trail.Count - limits[target]);
            limits.RemoveRange(target, limits.Count - target);
            qhead = trail.Count;
        }

        public bool? Solve(int maxConflicts)
        {
            if (unsatAtStart) return false;
            var conflicts = 0;
            while (true)
            {
                var conflict = Propagate();
                if (conflict >= 0)
                {
                    if (limits.Count == 0) return false;
                    if (++conflicts > maxConflicts) return null;
                    var (learnt, back) = Analyze(conflict);
                    CancelUntil(back);
                    if (learnt.Length == 1) Enqueue(learnt[0], -1);
                    else
                    {
                        clauses.Add(learnt);
                        watches[learnt[0]].Add(clauses.Count - 1);
                        watches[learnt[1]].Add(clauses.Count - 1);
                        Enqueue(learnt[0], clauses.Count - 1);
                    }
                    increment /= 0.95;
                    continue;
                }
                var pick = -1;
                for (var v = 0; v < n; v++)
                    if (value[v] == 0 && (pick < 0 || activity[v] > activity[pick])) pick = v;
                if (pick < 0) return true;
                limits.Add(trail.Count);
                Enqueue(2 * pick + (phase[pick] ? 0 : 1), -1);
            }
        }
    }
}
