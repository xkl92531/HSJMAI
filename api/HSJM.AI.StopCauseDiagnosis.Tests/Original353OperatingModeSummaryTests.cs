using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353OperatingModeSummaryTests
{
    [Fact]
    public void ManualNoticeIsMandatoryEvenWhenModelOmitsItAndIsNotDuplicated()
    {
        var modes = new[] { new Original353ManualModeRead(1, "Manual", "") { ProjectName = "PLC1" } };
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseDescription = "安全门报警。" };
        Original353OperatingModeSummary.Apply(report, modes);
        Original353OperatingModeSummary.Apply(report, modes);
        Assert.Contains("处于手动模式", report.RootCauseDescription);
        Assert.Contains("安全门报警", report.RootCauseDescription);
        Assert.Single(report.SuggestedActions);
        Assert.Contains("PLC1", report.SuggestedActions[0]);
    }

    [Fact]
    public void UnknownOrConflictNeverBecomesManualAndMissingProjectPreventsSkipping()
    {
        var modes = new[] { new Original353ManualModeRead(1, "Manual", "") };
        Assert.True(Original353OperatingModeSummary.AllManual([1], modes));
        Assert.False(Original353OperatingModeSummary.AllManual([1, 2], modes));
        Assert.False(Original353OperatingModeSummary.AllManual([], []));
        var report = new LineDiagnosisLlmReport { RootCauseDescription = "结果" };
        Original353OperatingModeSummary.Apply(report, [new(1, "Unknown", ""), new(2, "Conflict", "")]);
        Assert.Equal("结果", report.RootCauseDescription);
        Assert.Empty(report.SuggestedActions);
    }

    [Fact]
    public void PartialManualIsReportedWithGroupScopeWithoutSkippingThePlc()
    {
        var modes = new[] { new Original353ManualModeRead(1, "Other", "") {
            ProjectName = "PLC1", ManualGroups = ["DB220_OP20Manual"] } };
        Assert.False(Original353OperatingModeSummary.AllManual([1], modes));
        Assert.True(Original353OperatingModeSummary.AllSelectedHaveManual([1], modes));
        Assert.False(Original353OperatingModeSummary.AllSelectedHaveManual([1, 2], modes));
        Assert.False(Original353OperatingModeSummary.AllSelectedHaveManual([1], [new(1, "Conflict", "") {
            ManualGroups = ["DB220_OP20Manual"],
            Conflicts = [new("DB320_OP30Manual", "DB320_OP30Manual.General.ManualMod", "DB320_OP30Manual.General.AutoMod")]
        }]));
        var text = Original353OperatingModeSummary.Report(modes);
        Assert.Contains("OP020", text);
        Assert.Contains("处于手动模式", text);
        Assert.DoesNotContain("分析要求", text);
        Assert.Contains("不能仅凭这些状态断言设备故障", Original353OperatingModeSummary.ForModel(text, modes));
    }
}
