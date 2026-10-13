#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class NnfDnfTests
{
    static IrExpr V(string n) => IrExpr.Var(n);

    static string[] Terms(IrExpr e) => BoolSimplifier.ToDnfNnf(e)!
        .Select(t => string.Join("&", t.Select(l => (l.Positive ? "" : "!") + l.Key))).ToArray();

    [Fact]
    public void NegatedOrBecomesConjunctionOfNegatedVariables() =>
        Assert.Equal(["!A&!B"], Terms(IrExpr.Not(IrExpr.Or([V("A"), V("B")]))));

    [Fact]
    public void NegatedAndBecomesTwoTerms() =>
        Assert.Equal(["!A", "!B"], Terms(IrExpr.Not(IrExpr.And([V("A"), V("B")]))));

    [Fact]
    public void DoubleNegationCancels() =>
        Assert.Equal(["A"], Terms(new IrExpr(IrKinds.Not, null, [new IrExpr(IrKinds.Not, null, [V("A")])])));

    [Fact]
    public void NegatedComparisonStaysAnOpaqueNegativeLiteral()
    {
        var term = Assert.Single(BoolSimplifier.ToDnfNnf(IrExpr.Not(new IrExpr(IrKinds.Cmp, "==", [V("N"), IrExpr.Const("1")])))!);
        var literal = Assert.Single(term);
        Assert.False(literal.Positive);
        Assert.True(literal.Opaque);
        Assert.Equal("(N == 1)", literal.Key);
    }

    [Fact]
    public void NegatedTrueIsFalse() => Assert.Empty(BoolSimplifier.ToDnfNnf(IrExpr.Not(IrExpr.True))!);

    [Fact]
    public void PlainToDnfIsUnchanged()
    {
        var term = Assert.Single(BoolSimplifier.ToDnf(IrExpr.Not(IrExpr.Or([V("A"), V("B")])))!);
        Assert.True(Assert.Single(term).Opaque);
    }
}
