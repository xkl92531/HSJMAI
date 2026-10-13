#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class AiCandidateFactsTests
{
    static readonly Dictionary<string, string> Addr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OP02012-SQ17"] = "%I20.1", ["OP02012-SQ19"] = "%I20.3", ["YV_W"] = "%Q1.0", ["CYL_W"] = "%I2.1",
        ["1#CleanOn"] = "%Q5.0", ["3#CleanOn"] = "%Q5.2"
    };

    static AiCandidateResult Parse(string? content) => AiCandidateFacts.Parse(content, Addr, "伺服轴", "FB240_OP20Drive", 21);

    // 取自 2026-10-01 试跑的真实回复（FB240_OP20Drive_21.A.json），外面包着 ```json 代码块
    const string ValidReply = """
        ```json
        {
          "understanding": "同一滑台的前后极限位传感器不可能同时触发。",
          "constraints": [
            {
              "operator": "AT_MOST_ONE",
              "signals": [
                {"name": "OP02012-SQ17", "role": "Member"},
                {"name": "OP02012-SQ19", "role": "Member"}
              ],
              "targetGroups": [],
              "timing": "instant",
              "delaySeconds": null,
              "bypass": [],
              "evidence": "[FB240_OP20Drive/21/1]"
            }
          ],
          "timeoutAlarms": []
        }
        ```
        """;

    [Fact]
    public void ValidReplyIsParsed()
    {
        var r = Parse(ValidReply);
        Assert.Equal(AiReplyStatus.Parsed, r.Status);
        var f = Assert.Single(r.Facts);
        Assert.Equal("Mutex|OP02012-SQ17|OP02012-SQ19", f.Key);
        Assert.Equal(("伺服轴", "FB240_OP20Drive", 21), (f.Category, f.Block, f.Network));
        Assert.Equal((0, 0, 0), (r.SkippedItems, r.NonIoConstraints, r.NonIoAlarms));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void EmptyReplyIsEmpty(string? content)
    {
        var r = Parse(content);
        Assert.Equal(AiReplyStatus.Empty, r.Status);
        Assert.Empty(r.Facts);
    }

    [Fact]
    public void TruncatedReplyIsUnparseable()
    {
        var truncated = ValidReply[..(ValidReply.IndexOf("\"timing\"", StringComparison.Ordinal))] + "}";
        var r = Parse(truncated);
        Assert.Equal(AiReplyStatus.Unparseable, r.Status);
        Assert.Empty(r.Facts);
    }

    [Fact]
    public void ReplyWithoutJsonObjectIsUnparseable() =>
        Assert.Equal(AiReplyStatus.Unparseable, Parse("我无法判断这段程序的约束。").Status);

    [Fact]
    public void ClosedThinkBlockIsIgnored()
    {
        var r = Parse("<think>先看 {\"constraints\": 这里不是答案}</think>\n" + ValidReply);
        Assert.Equal(AiReplyStatus.Parsed, r.Status);
        Assert.Single(r.Facts);
    }

    [Fact]
    public void ReplyAfterLoneClosingThinkTagIsUsed()
    {
        // 模板把 <think> 放进提示词时，回复只含结束标签
        var r = Parse("思考过程 {草稿}\n</think>\n" + ValidReply);
        Assert.Equal(AiReplyStatus.Parsed, r.Status);
        Assert.Single(r.Facts);
    }

    [Fact]
    public void UnclosedThinkBlockIsUnparseable()
    {
        var r = Parse("<think>还在想 " + ValidReply);
        Assert.Equal(AiReplyStatus.Unparseable, r.Status);
        Assert.Empty(r.Facts);
    }

    [Fact]
    public void ConstraintWithNonIoSignalIsDroppedAndCounted()
    {
        const string reply = """
            {"constraints":[
              {"operator":"MUST_BE_FALSE","signals":[{"name":"AlwaysFALSE","role":"Target"}]},
              {"operator":"IMPLIES","signals":[{"name":"YV_W","role":"Source"},{"name":"DB1.Step","role":"Target"}]},
              {"operator":"IMPLIES","signals":[{"name":"YV_W","role":"Source"},{"name":"CYL_W","role":"Target"}]}
            ]}
            """;
        var r = Parse(reply);
        Assert.Equal(AiReplyStatus.Parsed, r.Status);
        Assert.Equal(2, r.NonIoConstraints);
        Assert.Equal(["Feedback|YV_W->CYL_W"], r.Facts.Select(f => f.Key));
    }

    [Fact]
    public void NonIoTargetGroupMemberDropsConstraint()
    {
        const string reply = """
            {"constraints":[{"operator":"IMPLIES_ANY","signals":[{"name":"YV_W","role":"Source"},{"name":"CYL_W","role":"Target"}],
              "targetGroups":[["CYL_W"],["DB1.Bypass"]]}]}
            """;
        var r = Parse(reply);
        Assert.Equal(1, r.NonIoConstraints);
        Assert.Empty(r.Facts);
    }

    [Fact]
    public void AfterDelayMakesDelayedKey()
    {
        const string reply = """
            {"constraints":[{"operator":"SAME_STATE","signals":[{"name":"1#CleanOn","role":"Member"},{"name":"3#CleanOn","role":"Member"}],
              "timing":"after_delay","delaySeconds":2}]}
            """;
        Assert.Equal("SameState|1#CLEANON|3#CLEANON" + ConstraintFacts.DelayedSuffix, Assert.Single(Parse(reply).Facts).Key);
    }

    [Fact]
    public void InstantTimingIsNotDelayed()
    {
        const string reply = """
            {"constraints":[{"operator":"SAME_STATE","signals":[{"name":"1#CleanOn","role":"Member"},{"name":"3#CleanOn","role":"Member"}],
              "timing":"instant"}]}
            """;
        Assert.Equal("SameState|1#CLEANON|3#CLEANON", Assert.Single(Parse(reply).Facts).Key);
    }

    [Fact]
    public void LowerCaseOperatorAndRoleAreNormalized()
    {
        const string reply = """
            {"constraints":[{"operator":"implies","signals":[{"name":"YV_W","role":"source"},{"name":"CYL_W","role":"TARGET"}]}]}
            """;
        Assert.Equal("Feedback|YV_W->CYL_W", Assert.Single(Parse(reply).Facts).Key);
    }

    [Fact]
    public void UnknownRoleOrMalformedItemIsSkippedAndCounted()
    {
        const string reply = """
            {"constraints":[
              {"operator":"IMPLIES","signals":[{"name":"YV_W","role":"Driver"},{"name":"CYL_W","role":"Target"}]},
              "not an object",
              {"operator":"IMPLIES","signals":[{"name":"YV_W","role":"Source"},{"name":"CYL_W","role":"Target"}]}
            ],"timeoutAlarms":[42]}
            """;
        var r = Parse(reply);
        Assert.Equal(3, r.SkippedItems);
        Assert.Equal(["Feedback|YV_W->CYL_W"], r.Facts.Select(f => f.Key));
    }

    [Fact]
    public void TimeoutAlarmWithNonIoFeedbackIsDroppedAndCounted()
    {
        // 取自试跑回复 FB660_MainGraph_1.A.json：output 与 feedback 都不是 IO 点
        const string reply = """
            {"constraints":[],"timeoutAlarms":[
              {"alarm":"DB690_OP60Alarm.Type.Warnings.Warning10","output":"步1~11相关动作输出",
               "feedback":["DB610_OP060_User.Status.Sync.ReceiveInSyncFbk[1]"],"delaySeconds":20},
              {"alarm":"DB690.Warnings.W1","output":"YV_W","feedback":["CYL_W","DB645.INP.CYL_FwdPos"]},
              {"alarm":"DB690.Warnings.W2","output":"yv_w","feedback":["cyl_w"],"delaySeconds":5}
            ]}
            """;
        var r = Parse(reply);
        Assert.Equal(2, r.NonIoAlarms);
        var f = Assert.Single(r.Facts);
        Assert.Equal("TimeoutAlarm|DB690.Warnings.W2|YV_W|CYL_W", f.Key);
    }
}
