#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>给措辞 AI 的程序段白话文：超长截断、缓存键版本、坏 XML 不外抛。</summary>
public class Original353PlainLanguageRenderTests
{
    static string Lines(int count, int width)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++) sb.Append(i.ToString("D6")).Append(new string('x', width - 6)).Append("\r\n");
        return sb.ToString();
    }

    [Fact]
    public void ClipKeepsTextWithinTheLimitUnchanged()
    {
        var text = Lines(10, 50);
        Assert.Same(text, Original353RuleTextService.ClipPlainLanguage(text));
        var exact = new string('a', 60000);
        Assert.Same(exact, Original353RuleTextService.ClipPlainLanguage(exact));
    }

    [Fact]
    public void ClipCutsOversizeTextAtALineBreakAndStatesBothLengths()
    {
        var text = Lines(2700, 97); // 99 字/行 × 2700 ≈ 267k，接近泰星 PLC2 FB250 GRAPH 实测
        var clipped = Original353RuleTextService.ClipPlainLanguage(text);

        var markerStart = clipped.LastIndexOf("\n…（程序段翻译过长", StringComparison.Ordinal);
        Assert.True(markerStart > 0);
        var kept = clipped[..markerStart];
        Assert.True(kept.Length <= 60000);
        Assert.StartsWith(kept, text, StringComparison.Ordinal);
        Assert.StartsWith("\r\n", text[kept.Length..]); // 截在换行处，不切断一行
        Assert.EndsWith("x", kept);           // 行尾的 \r 已去掉
        Assert.Contains($"原文 {text.Length} 字", clipped);
        Assert.Contains($"仅保留前 {kept.Length} 字", clipped);
        Assert.DoesNotContain("XML 解析失败", clipped);
        Assert.DoesNotContain("XML 无法解析", clipped);
        Assert.True(Original353RuleTextService.UsablePlainLanguage(clipped));
        Assert.False(Original353RuleTextService.UsablePlainLanguage(text));
    }

    [Fact]
    public void ClipWithoutAnyLineBreakCutsAtTheLimit()
    {
        var text = new string('a', 150);
        var clipped = Original353RuleTextService.ClipPlainLanguage(text, 100);
        Assert.True(clipped.Length <= 100);
        var kept = clipped[..clipped.IndexOf('\n')];
        Assert.Equal(new string('a', kept.Length), kept);
        Assert.Equal(kept + $"\n…（程序段翻译过长：原文 150 字，此处仅保留前 {kept.Length} 字）", clipped);
    }

    [Fact]
    public void ClipResultFitsTheLimitSoClippingAgainChangesNothing()
    {
        // 缓存读出后会再截一次：结果必须连同标记不超过上限，否则标记会被当作正文再截、长度说明被改写
        foreach (var text in new[] { Lines(2700, 97), new string('b', 70000), Lines(605, 99) + new string('c', 500) })
        {
            var once = Original353RuleTextService.ClipPlainLanguage(text);
            Assert.True(once.Length <= Original353RuleTextService.PlainLanguageMaxChars);
            Assert.Same(once, Original353RuleTextService.ClipPlainLanguage(once));
        }
    }

    [Fact]
    public void PlainLanguageKeyUsesTheCurrentRendererVersion()
    {
        var unit = new PipelineUnit(1, "BlockA", 2, "FBD", "<FlgNet/>", "", [], 3);
        string KeyWith(string renderer) => PipelineHash.Of(JsonSerializer.Serialize(new {
            Renderer = renderer, unit.RawContent, unit.BlockName, unit.NetworkOrder, Tags = "tags-a" }));

        var key = Original353RuleTextService.PlainLanguageKey(unit, "tags-a");
        Assert.NotEqual(KeyWith("logic-ir-1"), key); // 旧版本缓存（可能超长未截断）必须失效重生成
        Assert.NotEqual(KeyWith("logic-ir-2"), key); // eno 曾被译成 en，旧译文必须失效重生成
        Assert.Equal(KeyWith("logic-ir-3"), key);
    }

    [Fact]
    public void SclUnitsUseTheirOwnRendererVersion()
    {
        var scl = new PipelineUnit(1, "BlockA", 2, "SCL", "<StructuredText/>", "", [], 3);
        var flg = new PipelineUnit(1, "BlockA", 2, "FBD", "<FlgNet/>", "", [], 3);
        Assert.Equal(Original353RuleTextService.SclPlainLanguageRenderer, Original353RuleTextService.RendererFor(scl));
        Assert.Equal(Original353RuleTextService.PlainLanguageRenderer, Original353RuleTextService.RendererFor(flg));
        Assert.Equal(PipelineHash.Of(JsonSerializer.Serialize(new {
            Renderer = "logic-ir-3-scl", scl.RawContent, scl.BlockName, scl.NetworkOrder, Tags = "t" })),
            Original353RuleTextService.PlainLanguageKey(scl, "t"));
    }

    [Fact]
    public void OversizeTranslationIsClippedInsteadOfDroppingTheUnit()
    {
        var b = new FlgNetBuilder();
        for (var i = 0; i < 4000; i++)
            b.Series("Coil", $"DB_Very_Long_Output_Name_For_Testing.Output_{i:D5}", ($"DB_Very_Long_Input_Name_For_Testing.Input_{i:D5}", false));
        var unit = new PipelineUnit(1, "BlockA", 2, "LAD", b.Build(), "", [], 1);

        Assert.True(Original353RuleTextService.TryRenderPlainLanguage(unit, out var text, out var rawLength, out var error));
        Assert.Null(error);
        Assert.True(rawLength > 60000);
        Assert.Contains($"原文 {rawLength} 字", text);
        Assert.True(Original353RuleTextService.UsablePlainLanguage(text));
    }

    public static TheoryData<string, string> MalformedUnits => new()
    {
        { "LAD", "<FlgNet><Parts><Call UId=\"1\"/></Parts><Wires><Wire UId=\"2\"><Powerrail/><NameCon UId=\"1\" Name=\"en\"/></Wire></Wires></FlgNet>" },
        { "LAD", "<FlgNet><Parts><Part Name=\"Coil\" UId=\"1\"/></Parts><Wires><Wire UId=\"2\"/><Wire UId=\"3\"></Wire></Wires></FlgNet>" },
        { "LAD", "<FlgNet><Parts><Part Name=\"Contact\" UId=\"1\"/><Part Name=\"Coil\" UId=\"1\"/></Parts><Wires><Wire UId=\"2\"><NameCon UId=\"99\" Name=\"in\"/></Wire></Wires></FlgNet>" },
        { "STL", "      L #SrcOffSetByte;\n      L 8;\n      *I;" },
        { "LAD", "<FlgNet><Parts><Access UId=\"1\"" },
        { "LAD", "" },
        { "LAD", OverflowPortNetwork },
    };

    /// <summary>O 盒的输入端口编号超出 int 范围：翻译器 int.Parse 抛 OverflowException（旧的捕获过滤器不含此类型）。</summary>
    const string OverflowPortNetwork = "<FlgNet><Parts><Access Scope=\"GlobalVariable\" UId=\"3\"><Symbol><Component Name=\"Y\"/></Symbol></Access>"
        + "<Part Name=\"O\" UId=\"1\"/><Part Name=\"Coil\" UId=\"2\"/></Parts><Wires>"
        + "<Wire UId=\"4\"><Powerrail/><NameCon UId=\"1\" Name=\"in99999999999\"/></Wire>"
        + "<Wire UId=\"5\"><NameCon UId=\"1\" Name=\"out\"/><NameCon UId=\"2\" Name=\"in\"/></Wire>"
        + "<Wire UId=\"6\"><IdentCon UId=\"3\"/><NameCon UId=\"2\" Name=\"operand\"/></Wire></Wires></FlgNet>";

    [Fact]
    public void RenderExceptionOfAnyTypeIsReturnedNotThrown()
    {
        var unit = new PipelineUnit(1, "BlockA", 2, "LAD", OverflowPortNetwork, "", [], 1);
        Assert.False(Original353RuleTextService.TryRenderPlainLanguage(unit, out var text, out var error));
        Assert.IsType<OverflowException>(error);
        Assert.Equal("", text);
    }

    [Theory]
    [MemberData(nameof(MalformedUnits))]
    public void MalformedRawContentNeverThrowsFromTheRenderStep(string language, string raw)
    {
        var unit = new PipelineUnit(1, "BlockA", 2, language, raw, "", [], 1);
        var thrown = Record.Exception(() =>
        {
            var ok = Original353RuleTextService.TryRenderPlainLanguage(unit, out var text, out _, out var error);
            if (ok) { Assert.Null(error); Assert.True(Original353RuleTextService.UsablePlainLanguage(text)); }
        });
        Assert.Null(thrown);
    }
}
