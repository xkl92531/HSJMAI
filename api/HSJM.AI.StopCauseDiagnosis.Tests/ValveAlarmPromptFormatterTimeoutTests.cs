#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class ValveAlarmPromptFormatterTimeoutTests
{
    /// <summary>新清单行：全部到位点为一组（单组），与 CallSiteSubstitution 的单组产出一致。</summary>
    static StationDiagnosisSummaryDto Station(bool? output, params (string Name, bool? Value)[] feedback)
        => StationWith(new ValveAlarmSignalValueDto { Name = "Q122.5", Address = "%Q122.5", Value = output }, feedback);

    static StationDiagnosisSummaryDto StationWith(ValveAlarmSignalValueDto? outputSignal, params (string Name, bool? Value)[] feedback)
    {
        var station = Legacy(outputSignal, feedback);
        station.ActiveAlarms[0].FeedbackGroups = [feedback.Select(f => f.Name).ToList()];
        return station;
    }

    /// <summary>旧清单行：没有分组。</summary>
    static StationDiagnosisSummaryDto Legacy(ValveAlarmSignalValueDto? outputSignal, params (string Name, bool? Value)[] feedback) => new()
    {
        StationCode = "OP1090", ActiveAlarms =
        [
            new ActiveValveAlarmDto
            {
                AlarmVariable = "M010_0_Alarm.Type.1.Cylinder[4].Alarm1", Kind = "Timeout", Text = "1号气缸伸出超时",
                ValveInstance = "Valve[4]", BlockName = "FB121_Cylinder",
                OutputSignal = outputSignal,
                Feedbacks = [.. feedback.Select(f => new ValveAlarmSignalValueDto { Name = f.Name, Value = f.Value })]
            }
        ]
    };

    static ValveAlarmSignalValueDto Output(bool? value) => new() { Name = "Q122.5", Address = "%Q122.5", Value = value };

    static string Render(StationDiagnosisSummaryDto s) { var sb = new StringBuilder(); ValveAlarmPromptFormatter.AppendSection(sb, [s]); return sb.ToString(); }

    /// <summary>报警行本身（不含小节说明）。</summary>
    static string AlarmLine(StationDiagnosisSummaryDto s) => Render(s).Split("### OP1090")[1];

    [Fact]
    public void NamesTheSensorThatIsNotInPosition()
    {
        var text = Render(Station(true, ("S1", true), ("S2", false)));
        Assert.Contains("[动作超时未到位报警]", text);
        Assert.Contains("疑似 S2 未到位", text);
        Assert.DoesNotContain("疑似 S1", text);
    }

    [Fact]
    public void OutputOffGivesNoConclusion()
    {
        // 报警可能只依赖 FB 内部命令（输出 = 命令 ∧ 联锁）：输出为 false 时报警可能正在真实触发，不说「锁存」，也不点名传感器
        var line = AlarmLine(Station(false, ("S1", false)));
        Assert.DoesNotContain("→", line);
        Assert.DoesNotContain("锁存", line);
        Assert.DoesNotContain("疑似", line);
    }

    [Fact]
    public void UnreadOutputGivesNoConclusion()
        => Assert.DoesNotContain("→", AlarmLine(Station(null, ("S1", false))));

    [Fact]
    public void ReachedGroupGivesNoSuffix()
    {
        // 组里只记了正的到位点（常闭/比较条件不在组里），全为 true 也不能断定已到位：不追加任何后缀
        var line = AlarmLine(Station(true, ("S1", true)));
        Assert.DoesNotContain("→", line);
        Assert.DoesNotContain("已到位", line);
        Assert.DoesNotContain("疑似", line);
    }

    [Fact]
    public void ForwardAndBackBothTrueGivesNoSuffix()
    {
        // 到位条件 W ∧ ¬H：组里只有 W；W、H 同时为 true 时程序并不认为到位，不能说「已到位」，也没有为 false 的点可点名
        var line = AlarmLine(Station(true, ("W", true), ("H", true)));
        Assert.DoesNotContain("→", line);
        Assert.DoesNotContain("已到位", line);
    }

    [Fact]
    public void PositionConflictHasItsOwnLabel()
        => Assert.Equal("到位信号矛盾报警", ValveAlarmPromptFormatter.KindLabel("PositionConflict"));

    [Fact]
    public void TimeoutLabel()
        => Assert.Equal("动作超时未到位报警", ValveAlarmPromptFormatter.KindLabel("Timeout"));

    [Fact]
    public void WithoutOutputSignalOnlyFeedbackDecides()
    {
        var notReached = Render(StationWith(null, ("S1", true), ("S2", false)));
        Assert.Contains("疑似 S2 未到位", notReached);
        Assert.DoesNotContain("疑似 S1", notReached);
        Assert.DoesNotContain("锁存", notReached);

        var reached = AlarmLine(StationWith(null, ("S1", true)));
        Assert.DoesNotContain("→", reached);
    }

    [Fact]
    public void PartlyUnreadPositionPointsGiveNoConclusion()
    {
        Assert.DoesNotContain("→", AlarmLine(Station(true, ("S1", null), ("S2", true))));
        Assert.DoesNotContain("→", AlarmLine(Station(true, ("S1", null), ("S2", false))));
    }

    // 到位条件 (S1 ∧ S2) ∨ S3：任一组全部为 true 时不下结论；每组都有 false 才点名
    static StationDiagnosisSummaryDto OrGroups(bool? s1, bool? s2, bool? s3)
    {
        var station = Station(true, ("S1", s1), ("S2", s2), ("S3", s3));
        station.ActiveAlarms[0].FeedbackGroups = [["S1", "S2"], ["S3"]];
        return station;
    }

    [Fact]
    public void FirstGroupAllTrueGivesNoSuffix()
        => Assert.DoesNotContain("→", AlarmLine(OrGroups(true, true, false)));

    [Fact]
    public void SecondGroupAllTrueGivesNoSuffix()
        => Assert.DoesNotContain("→", AlarmLine(OrGroups(false, false, true)));

    [Fact]
    public void NoGroupAllTrueListsTheMissingPointsPerGroup()
    {
        var text = Render(OrGroups(true, false, false));
        Assert.Contains("→ 疑似未到位（任一组到位即可）：组1 S2；组2 S3", text);
        Assert.DoesNotContain("S1；", text);
    }

    [Fact]
    public void UnreadPointGivesNoConclusion()
    {
        Assert.DoesNotContain("→", AlarmLine(OrGroups(null, true, false)));
        Assert.DoesNotContain("→", AlarmLine(OrGroups(null, false, true)));
    }

    [Fact]
    public void GroupNamesMatchFeedbackNamesIgnoringCase()
    {
        var station = Station(true, ("S1", false), ("S3", false));
        station.ActiveAlarms[0].FeedbackGroups = [["s1"], ["s3"]];
        Assert.Contains("→ 疑似未到位（任一组到位即可）：组1 S1；组2 S3", Render(station));
    }

    [Fact]
    public void LegacyRowWithSeveralPositionPointsNamesNothing()
    {
        // 旧清单行没有分组，不知道到位点之间是「与」还是「或」：不点名、不追加
        Assert.DoesNotContain("→", AlarmLine(Legacy(Output(true), ("S1", true), ("S2", false))));
        Assert.DoesNotContain("→", AlarmLine(Legacy(Output(true), ("S1", false), ("S2", false))));
        // 输出为 false：不下结论
        Assert.DoesNotContain("→", AlarmLine(Legacy(Output(false), ("S1", false), ("S2", false))));
    }

    [Fact]
    public void LegacyRowWithOnePositionPointIsASingleGroup()
    {
        Assert.Contains("→ 疑似 S1 未到位", Render(Legacy(Output(true), ("S1", false))));
        Assert.DoesNotContain("→", AlarmLine(Legacy(Output(true), ("S1", true))));
    }

    [Fact]
    public void UnreadOutputOrFeedbackDoesNotProduceAConclusion()
    {
        // 输出未读到：不下结论
        Assert.DoesNotContain("→", AlarmLine(Station(null, ("S2", false))));
        // 到位点都未读到：不下结论
        Assert.DoesNotContain("→", AlarmLine(Station(true, ("S1", null))));
    }

    [Fact]
    public void OtherKindsHaveNoSuffix()
    {
        var s = Station(true, ("S1", false));
        s.ActiveAlarms[0].Kind = "FwdError";
        Assert.DoesNotContain("→", Render(s));
    }
}
