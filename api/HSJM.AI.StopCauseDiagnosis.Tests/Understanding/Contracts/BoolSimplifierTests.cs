#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class BoolSimplifierTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrExpr Eq(IrExpr a, string b) => new(IrKinds.Cmp, "==", [a, IrExpr.Const(b)]);

    [Fact]
    public void SubstitutingAConstantSelectsTheMatchingBranch()
    {
        var e = IrExpr.Or([
            IrExpr.And([V("A"), V("B"), Eq(V("N"), "2")]),
            IrExpr.And([V("A"), Eq(V("N"), "1")])
        ]);
        var folded = BoolSimplifier.Fold(BoolSimplifier.Substitute(e, n => n == "N" ? IrExpr.Const("1") : null));
        var dnf = BoolSimplifier.ToDnf(folded)!;
        Assert.Single(dnf);
        Assert.Equal(new[] { "A" }, dnf[0].Select(l => l.Key).ToArray());
    }

    [Theory]
    [InlineData("==", "1", "1.0", true)]
    [InlineData(">=", "10", "2", true)]
    [InlineData("<>", "3", "3", false)]
    [InlineData("<", "a", "1", null)]   // 非数值：不折叠
    public void ComparesNumericConstants(string op, string a, string b, bool? expected)
    {
        var folded = BoolSimplifier.Fold(new IrExpr(IrKinds.Cmp, op, [IrExpr.Const(a), IrExpr.Const(b)]));
        if (expected is null) Assert.Equal(IrKinds.Cmp, folded.Kind);
        else Assert.Equal(expected.Value ? "TRUE" : "FALSE", folded.Name);
    }

    [Fact]
    public void BoolTextConstantsFoldInAndOr()
    {
        Assert.Equal("FALSE", BoolSimplifier.Fold(IrExpr.And([V("A"), IrExpr.Const("false")])).Name);
        Assert.Equal("TRUE", BoolSimplifier.Fold(IrExpr.Or([V("A"), IrExpr.Const("1")])).Name);
        Assert.Equal("A", BoolSimplifier.Fold(IrExpr.And([V("A"), IrExpr.Const("TRUE")])).Name);
        Assert.Equal("FALSE", BoolSimplifier.Fold(IrExpr.Not(IrExpr.Const("true"))).Name);
    }

    [Fact]
    public void NumericOperandsOneAndZeroAreNotTurnedIntoBooleans()
    {
        var folded = BoolSimplifier.Fold(new IrExpr(IrKinds.Arith, "+", [V("X"), IrExpr.Const("1")]));
        Assert.Equal("1", folded.Args![1].Name);
        Assert.Equal("TRUE", BoolSimplifier.Fold(new IrExpr(IrKinds.Cmp, "==", [IrExpr.Const("0"), IrExpr.Const("0.0")])).Name);
    }

    [Fact]
    public void DnfDistributesAndMarksNegativeAndOpaqueLiterals()
    {
        var e = IrExpr.And([IrExpr.Or([V("A"), V("B")]), IrExpr.Not(V("C")),
            new IrExpr(IrKinds.Cmp, ">", [V("T"), V("P")])]);
        var dnf = BoolSimplifier.ToDnf(e)!;
        Assert.Equal(2, dnf.Count);
        Assert.All(dnf, term => Assert.Contains(term, l => l.Key == "C" && !l.Positive));
        Assert.All(dnf, term => Assert.Contains(term, l => l.Opaque));
    }

    [Fact]
    public void DnfGivesUpBeyondTheTermLimit()
    {
        var big = IrExpr.And(Enumerable.Range(0, 8).Select(i => IrExpr.Or([V($"A{i}"), V($"B{i}")])));
        Assert.Null(BoolSimplifier.ToDnf(big, maxTerms: 64));   // 2^8 = 256 > 64
    }

    [Fact]
    public void DnfOfConstants()
    {
        Assert.Empty(BoolSimplifier.ToDnf(IrExpr.Const("FALSE"))!);
        var t = BoolSimplifier.ToDnf(IrExpr.True)!;
        Assert.Single(t);
        Assert.Empty(t[0]);
    }

    [Fact]
    public void DnfDedupesLiteralsIgnoringCase()
    {
        var dnf = BoolSimplifier.ToDnf(IrExpr.And([V("A"), V("a")]))!;
        Assert.Single(dnf);
        Assert.Single(dnf[0]);
    }
}
