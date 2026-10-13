#nullable enable
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class ValveAlarmEvaluatorTests
{
    private static readonly ValveAlarmSignalRef Output = new("YV_W", "IO表", "%Q10.1");
    private static readonly ValveAlarmSignalRef Feedback = new("CYL_W", "IO表", "%I20.1");

    private static ValveAlarmDefinition Def(string variable, string kind = "FwdError") =>
        new(1, variable, kind, "Fwd", "Valve[7]", "FB220", 100, 200, 7, Output, [Feedback])
        { StationCode = "OP020", Text = "气缸7伸出未到位" };

    private static PLC_ReadTagResult Row(string key, object? value, bool good = true) =>
        new() { TagName = key, Value = value, IsGood = good };

    [Fact]
    public void ActiveAlarmCarriesTheFeedbackGroups()
    {
        var reads = new Dictionary<string, PLC_ReadTagResult>(StringComparer.OrdinalIgnoreCase) { ["DB1.A.X"] = Row("DB1.A.X", true) };
        var def = Def("DB1.A.X", "Timeout") with { FeedbackGroups = [["CYL_W"], ["CYL_X", "CYL_Y"]] };
        var alarm = Assert.Single(ValveAlarmEvaluator.Evaluate([def], reads).Active);
        Assert.Equal(new[] { new[] { "CYL_W" }, new[] { "CYL_X", "CYL_Y" } }, alarm.FeedbackGroups.Select(g => g.ToArray()).ToArray());
        Assert.Empty(Assert.Single(ValveAlarmEvaluator.Evaluate([Def("DB1.A.X")], reads).Active).FeedbackGroups);
    }

    [Fact]
    public void ReadKeysSeparateIoPointsFromDbMembers()
    {
        Assert.Equal("io::IO表\u0001YV_W", ValveAlarmEvaluator.ReadKey(Output));
        Assert.Equal("DB1.A.B", ValveAlarmEvaluator.ReadKey(new("DB1.A.B", null, null)));
        Assert.Null(ValveAlarmEvaluator.ReadKey(new("Bare", null, null)));
    }

    [Fact]
    public void RequestsCoverAlarmOutputAndFeedbackWithCorrectSplit()
    {
        var requests = ValveAlarmEvaluator.BuildRequests([Def("DB290_OP20Alarm.Type.CylinderOutErrors.X")]);

        var alarm = requests["DB290_OP20Alarm.Type.CylinderOutErrors.X"];
        Assert.Equal("DB290_OP20Alarm", alarm.BlockName);
        Assert.Equal("Type.CylinderOutErrors.X", alarm.VariablePath);
        var io = requests["io::IO表\u0001YV_W"];
        Assert.Equal("IO表", io.BlockName);
        Assert.Equal("YV_W", io.VariablePath);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public void TrueAndGoodIsActiveWithSignalValues()
    {
        var reads = new Dictionary<string, PLC_ReadTagResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB1.A.X"] = Row("DB1.A.X", true),
            ["io::IO表\u0001YV_W"] = Row("io::IO表\u0001YV_W", "true"),
            ["io::IO表\u0001CYL_W"] = Row("io::IO表\u0001CYL_W", false),
        };

        var (active, failed) = ValveAlarmEvaluator.Evaluate([Def("DB1.A.X")], reads);

        var alarm = Assert.Single(active);
        Assert.Equal(0, failed);
        Assert.Equal("气缸7伸出未到位", alarm.Text);
        Assert.Equal("FwdError", alarm.Kind);
        Assert.True(alarm.OutputSignal!.Value);
        Assert.Equal("%Q10.1", alarm.OutputSignal.Address);
        Assert.False(Assert.Single(alarm.Feedbacks).Value);
    }

    [Fact]
    public void FalseIsNotActiveAndNotFailed()
    {
        var reads = new Dictionary<string, PLC_ReadTagResult> { ["DB1.A.X"] = Row("DB1.A.X", false) };

        var (active, failed) = ValveAlarmEvaluator.Evaluate([Def("DB1.A.X")], reads);

        Assert.Empty(active);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void BadMissingOrNonBooleanCountsAsFailedNotAsNoAlarm()
    {
        var reads = new Dictionary<string, PLC_ReadTagResult>
        {
            ["DB1.A.Bad"] = Row("DB1.A.Bad", true, good: false),
            ["DB1.A.Text"] = Row("DB1.A.Text", "abc"),
        };

        var (active, failed) = ValveAlarmEvaluator.Evaluate(
            [Def("DB1.A.Bad"), Def("DB1.A.Missing"), Def("DB1.A.Text")], reads);

        Assert.Empty(active);
        Assert.Equal(3, failed);
    }

    [Fact]
    public void UnreadSignalValuesAreNull()
    {
        var reads = new Dictionary<string, PLC_ReadTagResult> { ["DB1.A.X"] = Row("DB1.A.X", 1) };

        var alarm = Assert.Single(ValveAlarmEvaluator.Evaluate([Def("DB1.A.X")], reads).Active);

        Assert.Null(alarm.OutputSignal!.Value);
        Assert.Null(alarm.Feedbacks[0].Value);
    }

    [Fact]
    public void WordBitSliceReadsTheWholeWordAndTakesTheBit()
    {
        // HMI 离散报警常按「报警字 + 位号」：DB 成员的位切片 %Xn 读整个字，取第 n 位（%X0 为最低位）
        var def = new ValveAlarmDefinition(1, "AlarmDB.Word1.%X12", "Latched", "", "", "FB_A", 0, null, null, null, []) { StationCode = "OP020" };
        var request = Assert.Single(ValveAlarmEvaluator.BuildRequests([def]));
        Assert.Equal("AlarmDB.Word1", request.Key);
        Assert.Equal("Word1", request.Value.VariablePath);

        Dictionary<string, PLC_ReadTagResult> Reads(object? v, bool good = true) =>
            new(StringComparer.OrdinalIgnoreCase) { ["AlarmDB.Word1"] = Row("AlarmDB.Word1", v, good) };
        Assert.Single(ValveAlarmEvaluator.Evaluate([def], Reads(4096)).Active);
        Assert.Single(ValveAlarmEvaluator.Evaluate([def], Reads("4096")).Active);
        Assert.Single(ValveAlarmEvaluator.Evaluate([def], Reads("16#1000")).Active);
        Assert.Single(ValveAlarmEvaluator.Evaluate([def], Reads(65535L)).Active);
        var off = ValveAlarmEvaluator.Evaluate([def], Reads(4095));
        Assert.Empty(off.Active);
        Assert.Equal(0, off.FailedCount);
        Assert.Equal(1, ValveAlarmEvaluator.Evaluate([def], Reads("abc")).FailedCount);
        Assert.Equal(1, ValveAlarmEvaluator.Evaluate([def], Reads(true)).FailedCount);   // 字读成布尔：认不出
        Assert.Equal(1, ValveAlarmEvaluator.Evaluate([def], Reads(4096, good: false)).FailedCount);
        Assert.Equal(1, ValveAlarmEvaluator.Evaluate([def], Reads(-1)).FailedCount);       // 负数：不是位串的值
    }

    [Fact]
    public void ReadVariableIsUsedForTheRequestAndTheLookupButTheDisplayKeepsTheCatalogName()
    {
        // 终审 I-2：清单写法 Type.4.Servo[10].X，注册/读别名用解析器写法 Type.4.Servo.4.Servo[10].X
        var def = new ValveAlarmDefinition(1, "M010_0_Alarm.Type.4.Servo[10].X", "Latched", "", "", "FB124", 0, null, null, null, [])
            { StationCode = "OP010", ReadVariable = "M010_0_Alarm.Type.4.Servo.4.Servo[10].X" };
        var request = Assert.Single(ValveAlarmEvaluator.BuildAlarmRequests([def]));
        Assert.Equal("M010_0_Alarm.Type.4.Servo.4.Servo[10].X", request.Key);
        Assert.Equal("Type.4.Servo.4.Servo[10].X", request.Value.VariablePath);
        var reads = new Dictionary<string, PLC_ReadTagResult>(StringComparer.OrdinalIgnoreCase) { [request.Key] = Row(request.Key, true) };
        Assert.Equal("M010_0_Alarm.Type.4.Servo[10].X", Assert.Single(ValveAlarmEvaluator.Evaluate([def], reads).Active).AlarmVariable);
    }

    [Fact]
    public void AlarmRequestsCarryOnlyAlarmBitsAndSignalRequestsOnlyOutputsAndFeedbacks()
    {
        var defs = new[] { Def("DB1.A.X"), Def("DB1.A.Y") };
        Assert.Equal(new[] { "DB1.A.X", "DB1.A.Y" }, ValveAlarmEvaluator.BuildAlarmRequests(defs).Keys.Order().ToArray());
        var signals = ValveAlarmEvaluator.BuildSignalRequests(defs);
        Assert.Equal(new[] { "io::IO表CYL_W", "io::IO表YV_W" }, signals.Keys.Order(StringComparer.Ordinal).ToArray());
        var reads = new Dictionary<string, PLC_ReadTagResult>(StringComparer.OrdinalIgnoreCase)
            { ["DB1.A.X"] = Row("DB1.A.X", true), ["DB1.A.Y"] = Row("DB1.A.Y", false) };
        Assert.True(ValveAlarmEvaluator.IsActive(defs[0], reads));
        Assert.False(ValveAlarmEvaluator.IsActive(defs[1], reads));
    }
}
