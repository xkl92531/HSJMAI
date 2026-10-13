#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Networks;

public class AiNetworkReplyTests
{
    [Fact]
    public void ParsesConstraintsWithRolesTimingAndEvidence()
    {
        var (status, reply, skipped) = AiNetworkReplyParser.Parse("""
            <think>先看线圈</think>{"understanding":"两盏灯互斥","constraints":[
              {"operator":"at_most_one","signals":[{"name":"Q1","role":"member"},{"name":"Q2","role":"Member"}],"timing":"instant","evidence":["OB1/1/1","OB1/1/2"]},
              {"operator":"MUST_BE_FALSE","signals":[{"name":"I3","role":"Target"}],"timing":"after_delay","delaySeconds":5,"evidence":"OB1/1/4， OB1/1/5"}]}
            """);
        Assert.Equal(AiReplyStatus.Parsed, status);
        Assert.Equal(0, skipped);
        Assert.Equal("两盏灯互斥", reply!.Understanding);
        var mutex = reply.Constraints[0];
        Assert.Equal("AT_MOST_ONE", mutex.Operator);
        Assert.Equal(["Member", "Member"], mutex.Signals.Select(s => s.Role).ToArray());
        Assert.False(mutex.Delayed);
        var mbf = reply.Constraints[1];
        Assert.True(mbf.Delayed);
        Assert.Equal(5, mbf.DelaySeconds);
        Assert.Equal(["OB1/1/4", "OB1/1/5"], mbf.Evidence);
    }

    [Theory]
    [InlineData(null, AiReplyStatus.Empty)]
    [InlineData("  ", AiReplyStatus.Empty)]
    [InlineData("<think>没写完", AiReplyStatus.Unparseable)]
    [InlineData("{\"constraints\":[", AiReplyStatus.Unparseable)]
    [InlineData("{\"understanding\":\"x\"}", AiReplyStatus.Unparseable)]
    [InlineData("{\"constraints\":[],\"constraints\":[]}", AiReplyStatus.Unparseable)]
    [InlineData("[1,2]", AiReplyStatus.Unparseable)]
    [InlineData("{\"constraints\":{}}", AiReplyStatus.Unparseable)]
    public void UnusableRepliesAreClassifiedWithoutThrowing(string? content, AiReplyStatus expected) =>
        Assert.Equal(expected, AiNetworkReplyParser.Parse(content).Status);

    [Fact]
    public void BadItemsAreSkipped()
    {
        var (status, reply, skipped) = AiNetworkReplyParser.Parse("""
            {"constraints":[
              {"operator":"NOT_A_KIND","signals":[{"name":"Q1","role":"Member"}]},
              {"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Boss"}]},
              {"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Member"}],"operator":"X"},
              {"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Member"}],"delaySeconds":"五秒"},
              {"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Member"}],"evidence":[1]},
              "not an object",
              {"operator":"SAME_STATE","signals":[{"name":"Q1","role":"Member"},{"name":"I1","role":"Member"}],"evidence":["OB1/1/1"]}]}
            """);
        Assert.Equal(AiReplyStatus.Parsed, status);
        Assert.Equal(6, skipped);
        var kept = Assert.Single(reply!.Constraints);
        Assert.Equal(["Q1", "I1"], kept.Signals.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void ReplyAfterAClosingThinkTagIsRead() =>
        Assert.Equal(AiReplyStatus.Parsed, AiNetworkReplyParser.Parse("推理……{草稿}</think>{\"constraints\":[]}").Status);

    [Fact]
    public void FbContractParserSharesTheThinkBlockHandling() =>
        Assert.Equal(AiContractParseStatus.Parsed, AiFbContractParser.Parse("推理……{草稿}</think>{\"relations\":[]}").Status);

    [Fact]
    public void UserMaterialsListTheNetworkInterfaceCallersContractsAndIoTable()
    {
        var unit = new IrUnit(1, "FB_A", 2, "LAD", "FlgNet",
            [new IrStatement("FB_A/2/1", IrStatementKinds.Assign, "Q1", IrExpr.True, IrExpr.Var("I1"))], [], []);
        var block = IrBlock.FromUnits("FB_A", [unit], [new IfaceMember("Static", "S", "Bool", null, null, [])]);
        var user = NetworkAiPrompt.BuildUser(unit, block, ["**OB1** 网络 1：[OB1/1/1] 调用 FB FB_A"],
            [new PipelineTag("I1", "%I0.0", "Bool"), new PipelineTag("Q1", "%Q0.0", "Bool")], ["- 实例 V1（FB_V）：mutex: O1/O2"]);
        Assert.Contains("# 程序段 FB_A/2（LAD）", user);
        Assert.Contains("[FB_A/2/1] Q1 := I1", user);
        Assert.Contains("  - S : Bool", user);
        Assert.Contains("**OB1** 网络 1：", user);
        Assert.Contains("- 实例 V1（FB_V）：mutex: O1/O2", user);
        Assert.Contains("| I1 | %I0.0 | Bool |", user);
    }

    [Fact]
    public void MaterialsWithoutCallersOrContractsSaySo()
    {
        var unit = new IrUnit(1, "OB1", 1, "LAD", "FlgNet",
            [new IrStatement("OB1/1/1", IrStatementKinds.Assign, "Q1", IrExpr.True, IrExpr.Var("I1"))], [], []);
        var user = NetworkAiPrompt.BuildUser(unit, IrBlock.FromUnits("OB1", [unit], []), [], [], []);
        Assert.Contains("（无接口）", user);
        Assert.Contains("（未找到调用处）", user);
        Assert.DoesNotContain("## 4.", user);
    }
}
