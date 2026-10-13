#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class AiFbContractTests
{
    const string Reply = """
        <think>先看网络 5……</think>
        ```json
        {"fb":"FB_V","understanding":"单电控阀门",
         "relations":[
          {"kind":"mutex","outputs":["O_FwdOut","O_BkdOut"],"evidence":["FB_V/5/2","FB_V/6/2"]},
          {"kind":"feedback","output":"O_FwdOut","positionVar":"Cmd.IWP","delayParam":"C_FwdPresetTime","evidence":["FB_V/5/1"]},
          {"kind":"timeoutAlarm","alarm":"A_Word.Alarm1","output":"O_FwdOut","positionVar":"Cmd.IWP","delayParam":"C_FwdPresetTime","evidence":["FB_V/5/3","FB_V/6/1"]},
          {"kind":"Gate","input":"I_Estop","outputs":["O_FwdOut"],"evidence":["FB_V/6/2"]},
          {"kind":"contradiction","inputs":["I_FwdPos1","I_BkdPos1"],"delaySeconds":2,"alarm":"A_Word.Alarm3","evidence":["FB_V/7/1"]},
          {"kind":"other","text":"三次确认"},
          {"kind":"feedback","evidence":"不是数组"}
         ]}
        ```
        """;

    [Fact]
    public void ParsesAllRelationKindsAndCountsMalformedItems()
    {
        var (status, c, skipped) = AiFbContractParser.Parse(Reply);
        Assert.Equal(AiContractParseStatus.Parsed, status);
        Assert.Equal("FB_V", c!.Fb);
        Assert.Equal(6, c.Relations.Count);
        Assert.Equal(1, skipped);
        var fb = c.Relations.Single(r => r.Kind == "feedback");
        Assert.Equal(new[] { "O_FwdOut" }, fb.Outputs);
        Assert.Equal("Cmd.IWP", fb.PositionVar);
        Assert.Equal("C_FwdPresetTime", fb.DelayParam);
        var gate = c.Relations.Single(r => r.Kind == "gate");
        Assert.Equal(new[] { "I_Estop" }, gate.Inputs);
        var con = c.Relations.Single(r => r.Kind == "contradiction");
        Assert.Equal(2, con.DelaySeconds);
        Assert.Equal("A_Word.Alarm3", con.Alarm);
    }

    [Theory]
    [InlineData(null, AiContractParseStatus.Empty)]
    [InlineData("   ", AiContractParseStatus.Empty)]
    [InlineData("{\"fb\":\"X\",\"relations\":[{\"kind\":\"mutex\"", AiContractParseStatus.Unparseable)]
    [InlineData("<think>还没想完", AiContractParseStatus.Unparseable)]
    [InlineData("{\"fb\":\"X\"}", AiContractParseStatus.Unparseable)]   // 缺 relations 数组
    public void ReportsEmptyAndUnparseableReplies(string? content, AiContractParseStatus expected)
        => Assert.Equal(expected, AiFbContractParser.Parse(content).Status);

    [Fact]
    public void DuplicateKeyInItemSkipsThatItem()
    {
        var (status, c, skipped) = AiFbContractParser.Parse(
            "{\"relations\":[{\"kind\":\"mutex\",\"kind\":\"gate\"},{\"kind\":\"other\",\"text\":\"t\"}]}");
        Assert.Equal(AiContractParseStatus.Parsed, status);
        Assert.Equal(1, skipped);
        Assert.Equal("other", Assert.Single(c!.Relations).Kind);
    }

    [Fact]
    public void DuplicateKeyAtRootIsUnparseable()
        => Assert.Equal(AiContractParseStatus.Unparseable,
            AiFbContractParser.Parse("{\"relations\":[],\"relations\":[]}").Status);

    public static TheoryData<string> HostileInputs()
    {
        var deep = new string('[', 2000) + new string(']', 2000);
        return new TheoryData<string>
        {
            "null", "[]", "{\"relations\":null}", "{\"relations\":[null,1,\"x\"]}",
            deep, "{\"relations\":" + deep + "}",
            "{\"fb\":1,\"understanding\":[],\"relations\":[{\"kind\":2,\"outputs\":{}}]}",
        };
    }

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void ParseNeverThrows(string content)
    {
        var ex = Record.Exception(() => AiFbContractParser.Parse(content));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("\"2\"")]
    [InlineData("-1")]
    [InlineData("1e400")]
    [InlineData("\"NaN\"")]
    [InlineData("true")]
    public void InvalidDelaySecondsSkipsItem(string delay)
    {
        var (status, c, skipped) = AiFbContractParser.Parse(
            "{\"relations\":[{\"kind\":\"contradiction\",\"inputs\":[\"A\",\"B\"],\"delaySeconds\":" + delay + "}]}");
        Assert.Equal(AiContractParseStatus.Parsed, status);
        Assert.Equal(1, skipped);
        Assert.Empty(c!.Relations);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("1.5", 1.5)]
    public void ValidDelaySecondsIsKept(string delay, double expected)
    {
        var (_, c, skipped) = AiFbContractParser.Parse(
            "{\"relations\":[{\"kind\":\"contradiction\",\"inputs\":[\"A\",\"B\"],\"delaySeconds\":" + delay + "}]}");
        Assert.Equal(0, skipped);
        Assert.Equal(expected, Assert.Single(c!.Relations).DelaySeconds);
    }

    [Fact]
    public void UserPromptContainsInterfaceBodyWithStatementIdsAndLowerContracts()
    {
        var text = FbContractPrompt.BuildUser(IrBlockTests.Valve(), [new LowerContractInfo("FB_Low", "mutex: QA/QB")]);
        Assert.Contains("C_SensorNumber", text);
        Assert.Contains("[FB_V/5/1]", text);
        Assert.Contains("FB_Low", text);
        Assert.Contains("mutex: QA/QB", text);
        Assert.Contains("语句编号", FbContractPrompt.System);
    }
}
