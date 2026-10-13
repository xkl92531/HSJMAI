#nullable enable
using System.Text;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class ValveAlarmPromptFormatterTests
{
    private static StationDiagnosisSummaryDto Station(string code, int order, params ActiveValveAlarmDto[] alarms) =>
        new() { StationCode = code, StationOrder = order, OverallStatus = "Running", ActiveAlarms = alarms.ToList() };

    private static ActiveValveAlarmDto Alarm(string kind, string? text) => new()
    {
        AlarmVariable = "DB290_OP20Alarm.Type.CylinderOutErrors.OP020_CYL07_FwdError",
        Kind = kind,
        Text = text,
        ValveInstance = "Valve[7]",
        BlockName = "FB220_OP20Manual",
        OutputSignal = new() { Name = "OP020_YV07_W", Address = "%Q10.1", Value = true },
        Feedbacks = [new() { Name = "OP020_CYL07_W", Address = "%I20.1", Value = false }, new() { Name = "X", Value = null }],
    };

    private static string Render(params StationDiagnosisSummaryDto[] stations)
    {
        var sb = new StringBuilder();
        ValveAlarmPromptFormatter.AppendSection(sb, stations);
        return sb.ToString();
    }

    [Fact]
    public void NoActiveAlarmsWritesNothing()
    {
        Assert.Equal(string.Empty, Render(Station("OP010", 1)));
    }

    [Fact]
    public void SectionListsAlarmsWithKindTextAndSignalValues()
    {
        var text = Render(Station("OP030", 2, Alarm("BkdLock", null)), Station("OP020", 1, Alarm("FwdError", "气缸7伸出未到位报警")));

        Assert.Contains("## 阀门报警（PLC 实时值）", text);
        Assert.True(text.IndexOf("### OP020", StringComparison.Ordinal) < text.IndexOf("### OP030", StringComparison.Ordinal));
        Assert.Contains("[伸出未到位报警] 气缸7伸出未到位报警（Valve[7]，FB220_OP20Manual）", text);
        Assert.Contains("输出 OP020_YV07_W(%Q10.1)=true", text);
        Assert.Contains("反馈 OP020_CYL07_W(%I20.1)=false", text);
        Assert.Contains("反馈 X=未读到", text);
        Assert.Contains("[缩回条件不满足警告] DB290_OP20Alarm.Type.CylinderOutErrors.OP020_CYL07_FwdError", text);
        Assert.Contains("读取失败的报警位", text);
        Assert.Contains("工位即使在下方被列为正常/运行中，其阀门报警仍是有效证据，需要结合判断。", text);
    }

    [Fact]
    public void LatchedAndExpectedStateAlarmsHaveTheirOwnLabelsAndNoEmptyInstance()
    {
        var latched = new ActiveValveAlarmDto { AlarmVariable = "M010_0_Alarm.Alarm.X_3", Kind = "Latched", Text = "压力2超限", BlockName = "FB130_Sensor" };
        var expected = new ActiveValveAlarmDto
        {
            AlarmVariable = "DB_S.Sensor[36].CheckOffAlarm", Kind = "ExpectedState", ValveInstance = "Sensor[36]", BlockName = "FB130_Sensor",
            Feedbacks = [new() { Name = "SQ40", Address = "%I10.0", Value = true }],
        };
        var text = Render(Station("OP010", 1, latched, expected));
        Assert.Contains("- [锁存报警位] 压力2超限（FB130_Sensor）", text);
        Assert.DoesNotContain("（，", text);
        Assert.Contains("- [期望状态报警] DB_S.Sensor[36].CheckOffAlarm（Sensor[36]，FB130_Sensor）；监视输入 SQ40(%I10.0)=true", text);
        Assert.Contains("供 HMI 显示的报警位", text.Split(Environment.NewLine)[1]);   // 说明行列出新种类
    }
}
