#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>计划 2 遗留（Task 3 裁定，并入 Task 7）：Q 写入索引对指针常数与区未知/Q 区间接写入的处理。</summary>
public class QWriteIndexPointerTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q0", "%Q0.0", "Bool"), new("Q3", "%Q0.3", "Bool"), new("Q10", "%Q1.0", "Bool"), new("Q20", "%Q2.0", "Bool"),
        new("QB2", "%QB2", "Byte")
    ];

    static IrStatement Assign(string target) => new("OB1/1/1", IrStatementKinds.Assign, target, IrExpr.True, IrExpr.Var("X"));
    static IrStatement Src(string text) => new("OB1/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, text);
    static IrStatement CallOut(string target) => new("OB1/1/1", IrStatementKinds.Call, null, IrExpr.True, null,
        new IrCall("BLKMOV", "FC", "", IrExpr.True, [new IrPin("DSTBLK", "Output", "Any", null, [target])]));

    static UnderstandingRuleSource.QWriteIndex Index(params IrStatement[] s) =>
        new(Tags, [new IrUnit(1, "OB1", 1, "LAD", "FlgNet", s, [], [])]);

    [Theory]
    [InlineData("P#Q0.0 BYTE 1")]
    [InlineData("P#Q 0.0 BYTE 2")]
    [InlineData("P#A0.0 BYTE 1")]   // 德文输出区
    [InlineData("P#Q0.3 BOOL 1")]
    public void PointerConstantTargetCountsAsAWriteOverItsRange(string target)
    {
        var q = Index(CallOut(target));
        Assert.False(q.HasUnresolved);
        Assert.Equal(1, q.Count("Q3"));
        Assert.Equal(0, q.Count("Q20"));
    }

    [Fact]
    public void PointerConstantRangeIsExact()
    {
        var q = Index(CallOut("P#Q0.0 BYTE 2"));   // Q0.0~Q1.7
        Assert.Equal(1, q.Count("Q0"));
        Assert.Equal(1, q.Count("Q10"));
        Assert.Equal(0, q.Count("Q20"));
        Assert.Equal(0, q.Count("QB2"));
    }

    [Fact]
    public void PointerConstantOfAnotherAreaIsIgnored()
    {
        var q = Index(CallOut("P#M0.0 BYTE 10"), CallOut("P#DB5.DBX0.0 BYTE 4"), CallOut("P#I0.0 BYTE 4"));
        Assert.False(q.HasUnresolved);
        Assert.Equal(0, q.Count("Q3"));
    }

    [Theory]
    [InlineData("P#Q0.0")]               // 缺类型与长度：Q 区范围未知
    [InlineData("P#Q0.0 FOO 3")]         // 类型不认识
    [InlineData("P#0.0 BYTE 10")]        // 区未知的指针常数
    [InlineData("B[AR1,P#0.0]")]         // 区未知的寄存器间接
    [InlineData("D[AR2,P#4.0]")]
    [InlineData("[AR1,P#0.0]")]
    [InlineData("QB[AR1,P#0.0]")]        // Q 区间接（原有）
    [InlineData("AW[AR1,P#0.0]")]
    public void UnresolvableQOrUnknownAreaWritesAreUnresolved(string target)
    {
        Assert.True(Index(Assign(target)).HasUnresolved);
        Assert.True(Index(CallOut(target)).HasUnresolved);
    }

    [Theory]
    [InlineData("MB[AR1,P#0.0]")]       // 带非 Q 区字母的间接只影响该区
    [InlineData("DBX[AR1,P#0.0]")]
    [InlineData("Arr[#ar1]")]           // 符号数组，不是寄存器间接
    [InlineData("Arr[i]")]
    public void IndirectWritesOfOtherAreasAreNotUnresolved(string target)
        => Assert.False(Index(Assign(target)).HasUnresolved);

    [Theory]
    [InlineData("T B [AR1,P#0.0]")]
    [InlineData("T W[AR1,P#2.0]")]
    [InlineData("= [AR1,P#0.0]")]
    public void SourceTextAreaUnknownIndirectWriteIsUnresolved(string text)
        => Assert.True(Index(Src(text)).HasUnresolved);

    [Theory]
    [InlineData("L B [AR1,P#0.0]")]     // 读，不算
    [InlineData("T MB [AR1,P#0.0]")]    // M 区
    [InlineData("LAR1 P#0.0")]
    public void SourceTextReadsAndOtherAreasAreNotUnresolved(string text)
        => Assert.False(Index(Src(text)).HasUnresolved);

    [Fact]
    public void PointerConstantInTextCountsAsAWrite()
    {
        var q = Index(Src("CALL BLKMOV\n SRCBLK := P#M10.0 BYTE 1\n DSTBLK => P#Q0.0 BYTE 1"));
        Assert.False(q.HasUnresolved);
        Assert.Equal(1, q.Count("Q3"));
        Assert.Equal(0, q.Count("Q10"));
    }

    [Fact]
    public void PointerConstantInTextWithoutRangeIsUnresolved()
        => Assert.True(Index(Src("L P#Q 0.0\n LAR1")).HasUnresolved);
}
