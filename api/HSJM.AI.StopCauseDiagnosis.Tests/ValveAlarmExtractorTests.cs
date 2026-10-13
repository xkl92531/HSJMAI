#nullable enable
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class ValveAlarmExtractorTests
{
    private static Dictionary<string, PLC_IOTagEntity> Tags() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["YV_W"] = new() { TagName = "YV_W", Address = "%Q10.1", TableName = "IO表" },
        ["YV_H"] = new() { TagName = "YV_H", Address = "%Q10.0", TableName = "IO表" },
        ["CYL_1W"] = new() { TagName = "CYL_1W", Address = "%I20.1", TableName = "IO表" },
        ["CYL_2W"] = new() { TagName = "CYL_2W", Address = "%I20.3", TableName = "IO表" },
        ["CYL_H"] = new() { TagName = "CYL_H", Address = "%I20.0", TableName = "IO表" },
    };

    private static PldAnchorBlock Valve()
    {
        var fb = new PldAnchorBlock { Type = PldAnchorType.FbCall, Anchor = "Valve[7]", TypeText = "FB 调用: FB3002_Valve" };
        fb.OutPins["QFwd"] = "YV_W";
        fb.OutPins["QBkd"] = "YV_H";
        fb.OutPins["FwdErr"] = "DB290_OP20Alarm.Type.CylinderOutErrors.OP020_CYL07_FwdError";
        fb.OutPins["BkdErr"] = "DB290_OP20Alarm.Type.CylinderOutErrors.OP020_CYL07_BkdError";
        fb.OutPins["FwdLockErr"] = "DB290_OP20Alarm.Type.CylinderLockErrors.OP020_CYL07_FwdlockWarn";
        fb.OutPins["BkdLockErr"] = "(未绑定)";
        fb.InPinContacts["IWP"] = [new("NO", "CYL_1W"), new("NO", "CYL_2W"), new("NO", "DB210.Cmd.ByPass[1]")];
        fb.InPinContacts["IHP"] = [new("NO", "CYL_H"), new("NC", "YV_W")];
        return fb;
    }

    private static List<ValveAlarmDefinition> Run(params PldAnchorBlock[] anchors) =>
        ValveAlarmExtractor.Extract(1, 100, 200, "FB220_OP20Manual", 7, anchors, Tags());

    [Fact]
    public void BoundAlarmPinsBecomeDefinitionsAndUnboundPinsAreIgnored()
    {
        var defs = Run(Valve());

        Assert.Equal(["BkdError", "FwdError", "FwdLock"], defs.Select(d => d.Kind).OrderBy(k => k).ToArray());
        Assert.All(defs, d => Assert.Equal("Valve[7]", d.ValveInstance));
        Assert.All(defs, d => Assert.Equal(100, d.LogicUnitId));
    }

    [Fact]
    public void ForwardAlarmCarriesForwardOutputAndAllNormallyOpenInputFeedbacks()
    {
        var fwd = Run(Valve()).Single(d => d.Kind == "FwdError");

        Assert.Equal("Fwd", fwd.Direction);
        Assert.Equal("DB290_OP20Alarm.Type.CylinderOutErrors.OP020_CYL07_FwdError", fwd.AlarmVariable);
        Assert.Equal("YV_W", fwd.Output!.Name);
        Assert.Equal("IO表", fwd.Output.TableName);
        Assert.Equal(["CYL_1W", "CYL_2W"], fwd.Feedbacks.Select(f => f.Name).ToArray());
        Assert.Equal("%I20.1", fwd.Feedbacks[0].Address);
    }

    [Fact]
    public void BackwardAlarmUsesBackwardOutputAndIgnoresNormallyClosedContacts()
    {
        var bkd = Run(Valve()).Single(d => d.Kind == "BkdError");

        Assert.Equal("Bkd", bkd.Direction);
        Assert.Equal("YV_H", bkd.Output!.Name);
        Assert.Equal(["CYL_H"], bkd.Feedbacks.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void SignalNamesUseTheIoTagSpellingSoExactReadsMatch()
    {
        var fb = Valve();
        fb.OutPins["QFwd"] = " yv_w ";
        fb.InPinContacts["IWP"] = [new("NO", " cyl_1w ")];

        var fwd = Run(fb).Single(d => d.Kind == "FwdError");

        Assert.Equal("YV_W", fwd.Output!.Name);
        Assert.Equal("CYL_1W", Assert.Single(fwd.Feedbacks).Name);
        Assert.Equal("%I20.1", fwd.Feedbacks[0].Address);
    }

    [Fact]
    public void AlarmPinBoundToLocalOrNonMemberVariableIsIgnored()
    {
        var fb = new PldAnchorBlock { Type = PldAnchorType.FbCall, Anchor = "Valve[1]" };
        fb.OutPins["FwdErr"] = "#localAlarm";
        fb.OutPins["BkdErr"] = "PlainTag";

        Assert.Empty(Run(fb));
    }

    [Fact]
    public void PinNamesAreMatchedCaseInsensitivelyAndCoilAnchorsAreSkipped()
    {
        var fb = new PldAnchorBlock { Type = PldAnchorType.FbCall, Anchor = "Valve[2]" };
        fb.OutPins["FWDERR"] = "DB1_Alarm.A.X";
        var coil = new PldAnchorBlock { Type = PldAnchorType.Coil, Anchor = "DB1_Alarm.A.Y" };

        var defs = Run(fb, coil);

        Assert.Equal("DB1_Alarm.A.X", Assert.Single(defs).AlarmVariable);
        Assert.Null(defs[0].Output);
        Assert.Empty(defs[0].Feedbacks);
    }
}
