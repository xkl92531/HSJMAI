#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>Task 16 迭代 1：互斥判定改为可满足性检查（不再有 64 项析取范式上限）；只靠「程序从不写入的量两次读取取值不同」才成立的反例判 NotProven。</summary>
public class ExclusionProofSatTests
{
    static IrExpr V(string n, int t) => IrExpr.Var(ExclusionProof.At(n, t));
    static readonly Func<string, bool> Certain = _ => true;
    static UnorderedSegment Unordered(Func<string, bool> steady) => new(_ => [], steady, _ => true);

    /// <summary>(x1 ∨ y1) ∧ (x2 ∨ y2) ∧ … 展开成析取范式有 2^n 项。</summary>
    static IrExpr Wide(string prefix, int n, int t) =>
        IrExpr.And(Enumerable.Range(1, n).Select(i => IrExpr.Or([V($"{prefix}x{i}", t), V($"{prefix}y{i}", t)])));

    [Fact]
    public void MutexIsProvenEvenWhenTheDefinitionsExpandToThousandsOfTerms()
    {
        // Q1 := Wide ∧ S；Q2 := Wide' ∧ ¬S：DNF 各 2^10 = 1024 项，旧做法超过 64 项直接放弃
        var a = new TimedDef("Q1", 0, IrExpr.And([Wide("a", 10, 0), V("S", 0)]));
        var b = new TimedDef("Q2", 1, IrExpr.And([Wide("b", 10, 1), IrExpr.Not(V("S", 1))]));
        Assert.Equal(ProofVerdict.Proven, ExclusionProof.Mutex(a, b, Unordered(_ => true), Certain));
    }

    [Fact]
    public void CounterexampleIsFoundEvenWhenTheDefinitionsExpandToThousandsOfTerms()
    {
        var a = new TimedDef("Q1", 0, IrExpr.And([Wide("a", 10, 0), V("S", 0)]));
        var b = new TimedDef("Q2", 1, IrExpr.And([Wide("b", 10, 1), V("S", 1)]));
        Assert.Equal(ProofVerdict.Counterexample, ExclusionProof.Mutex(a, b, Unordered(_ => true), Certain));
    }

    [Fact]
    public void CounterexampleMustNotDependOnUncertainLiterals()
    {
        // 唯一能让两者同时成立的方式要经过不透明原子 → 不是反例，也不是证明
        var gt = new IrExpr(IrKinds.Cmp, ">", [V("X", 0), IrExpr.Const("5")]);
        var a = new TimedDef("Q1", 0, IrExpr.And([V("S", 0), IrExpr.Or([gt, IrExpr.Not(V("S", 0))])]));
        var b = new TimedDef("Q2", 1, V("S", 1));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, Unordered(_ => true), Certain));
    }

    [Fact]
    public void CounterexampleRelyingOnAnExternallyChangedBitIsNotProven()
    {
        // Q1 := H ∧ A；Q2 := ¬H ∧ B：H 程序里不写（HMI 位）、两次读取不能证同值（F8），
        // 但「两次读到不同值」只可能来自程序外改写 → 既不证明、也不反驳
        var a = new TimedDef("Q1", 0, IrExpr.And([V("H", 0), V("A", 0)]));
        var b = new TimedDef("Q2", 1, IrExpr.And([IrExpr.Not(V("H", 1)), V("B", 1)]));
        var segment = Unordered(n => n != "H");
        Assert.Equal(ProofVerdict.Counterexample, ExclusionProof.Mutex(a, b, segment, Certain));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, segment, Certain, externalOnly: n => n == "H"));
    }

    [Fact]
    public void GenuineCounterexampleSurvivesTheExternalOnlyRule()
    {
        // 同时成立不需要 H 两次取不同值（H、A、B 都为真即可）→ 仍是反例
        var a = new TimedDef("Q1", 0, IrExpr.And([V("H", 0), V("A", 0)]));
        var b = new TimedDef("Q2", 1, IrExpr.And([V("H", 1), V("B", 1)]));
        Assert.Equal(ProofVerdict.Counterexample,
            ExclusionProof.Mutex(a, b, Unordered(n => n != "H"), Certain, externalOnly: n => n == "H"));
    }

    [Fact]
    public void OrderedSegmentAppliesTheExternalOnlyRuleToo()
    {
        var a = new TimedDef("Q1", 0, IrExpr.And([V("H", 0), V("A", 0)]));
        var b = new TimedDef("Q2", 1, IrExpr.And([IrExpr.Not(V("H", 1)), V("B", 1)]));
        Func<string, int, int, bool> stable = (n, _, t2) => t2 == ExclusionProof.End && n.StartsWith('Q');
        Assert.Equal(ProofVerdict.Counterexample, ExclusionProof.Mutex(a, b, stable, Certain));
        Assert.Equal(ProofVerdict.NotProven, ExclusionProof.Mutex(a, b, stable, Certain, externalOnly: n => n == "H"));
    }

    [Fact]
    public void SatisfiabilityAgreesWithBruteForceOnSmallRandomFormulas()
    {
        var rng = new Random(20261002);
        string[] names = ["A", "B", "C", "D"];
        for (var round = 0; round < 300; round++)
        {
            IrExpr Gen(int depth)
            {
                if (depth == 0 || rng.Next(3) == 0)
                {
                    var v = IrExpr.Var(names[rng.Next(names.Length)]);
                    return rng.Next(2) == 0 ? v : IrExpr.Not(v);
                }
                var kids = Enumerable.Range(0, 2 + rng.Next(2)).Select(_ => Gen(depth - 1)).ToArray();
                return rng.Next(2) == 0 ? IrExpr.And(kids) : IrExpr.Or(kids);
            }
            var e = BoolSimplifier.ToNnf(Gen(4));
            bool Eval(IrExpr x, int mask) => x.Kind switch
            {
                IrKinds.Var => (mask >> Array.IndexOf(names, x.Name) & 1) == 1,
                IrKinds.Not => !Eval(x.Args![0], mask),
                IrKinds.And => x.Args!.All(y => Eval(y, mask)),
                IrKinds.Or => x.Args!.Any(y => Eval(y, mask)),
                IrKinds.Const => x.Name == "TRUE",
                _ => throw new InvalidOperationException()
            };
            var expected = Enumerable.Range(0, 1 << names.Length).Any(m => Eval(e, m));
            PropositionalSat.Formula Build(IrExpr x) => x.Kind switch
            {
                IrKinds.Var => new PropositionalSat.Lit(Array.IndexOf(names, x.Name), true),
                IrKinds.Not => new PropositionalSat.Lit(Array.IndexOf(names, x.Args![0].Name), false),
                IrKinds.And => PropositionalSat.And(x.Args!.Select(Build)),
                IrKinds.Or => PropositionalSat.Or(x.Args!.Select(Build)),
                IrKinds.Const => x.Name == "TRUE" ? PropositionalSat.True : PropositionalSat.False,
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(expected, PropositionalSat.Satisfiable(Build(e), names.Length));
        }
    }

    [Fact]
    public void PigeonholeIsUnsatisfiable()
    {
        // 4 只鸽子放进 3 个笼子：每只至少一个笼子，同一笼子至多一只
        const int pigeons = 4, holes = 3;
        int P(int p, int h) => p * holes + h;
        var clauses = new List<int[]>();
        for (var p = 0; p < pigeons; p++) clauses.Add(Enumerable.Range(0, holes).Select(h => 2 * P(p, h)).ToArray());
        for (var h = 0; h < holes; h++)
            for (var p = 0; p < pigeons; p++)
                for (var q = p + 1; q < pigeons; q++) clauses.Add([2 * P(p, h) + 1, 2 * P(q, h) + 1]);
        Assert.False(PropositionalSat.Satisfiable(PropositionalSat.True, pigeons * holes, clauses));
    }
}
