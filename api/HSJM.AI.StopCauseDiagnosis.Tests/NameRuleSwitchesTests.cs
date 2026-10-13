#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class NameRuleSwitchesTests
{
    static Dictionary<string, PLC_IOTagEntity> Tags() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["YV_W"] = new() { TagName = "YV_W", Address = "%Q2019.1", DataType = "Bool" },
        ["YV_H"] = new() { TagName = "YV_H", Address = "%Q2019.0", DataType = "Bool" },
        ["CYL_W"] = new() { TagName = "CYL_W", Address = "%I2018.1", DataType = "Bool" },
        ["CYL_H"] = new() { TagName = "CYL_H", Address = "%I2018.0", DataType = "Bool" },
        ["P1"] = new() { TagName = "P1", Address = "%I30.0", DataType = "Bool" },
        ["P2"] = new() { TagName = "P2", Address = "%I30.1", DataType = "Bool" },
        ["P3"] = new() { TagName = "P3", Address = "%I30.2", DataType = "Bool" },
        ["L1"] = new() { TagName = "L1", Address = "%Q40.0", DataType = "Bool" },
        ["L2"] = new() { TagName = "L2", Address = "%Q40.1", DataType = "Bool" },
        ["K"] = new() { TagName = "K", Address = "%I50.0", DataType = "Bool" },
    };

    // 一个阀门调用（引脚名配对 4 条）、一个三限位轴（位置组互斥 1 条）、两个同触点线圈（同步 1 条）
    static List<PldAnchorBlock> Anchors()
    {
        var valve = new PldAnchorBlock { Type = PldAnchorType.FbCall, Anchor = "Valve[1]", TypeText = "FB 调用: FB_V" };
        valve.OutPins["QFwd"] = "YV_W";
        valve.OutPins["QBkd"] = "YV_H";
        valve.InPinContacts["IWP"] = [new ContactRef("NO", "CYL_W")];
        valve.InPinContacts["IHP"] = [new ContactRef("NO", "CYL_H")];
        var axis = new PldAnchorBlock { Type = PldAnchorType.FbCall, Anchor = "Axis", TypeText = "FB 调用: FB_A" };
        axis.InPinContacts["A"] = [new ContactRef("NO", "P1")];
        axis.InPinContacts["B"] = [new ContactRef("NO", "P2")];
        axis.InPinContacts["C"] = [new ContactRef("NO", "P3")];
        var l1 = new PldAnchorBlock { Type = PldAnchorType.Coil, Anchor = "L1", TypeText = "LAD/FBD 网络 (FlgNet)" };
        l1.DirectContacts.Add(new ContactRef("NO", "K"));
        var l2 = new PldAnchorBlock { Type = PldAnchorType.Coil, Anchor = "L2", TypeText = "LAD/FBD 网络 (FlgNet)" };
        l2.DirectContacts.Add(new ContactRef("NO", "K"));
        return [valve, axis, l1, l2];
    }

    static string[] Families(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray()
        .Select(r => r.TryGetProperty("family", out var f) ? f.GetString()! : "(无)").ToArray();

    [Fact]
    public void AugmenterTagsTheGuessFamiliesAndLeavesPinNameRulesUntagged()
    {
        var families = Families(PldStructuralConstraintAugmenter.AugmentFromBlocks(Anchors(), Tags()));
        Assert.Equal(1, families.Count(f => f == NameRuleFamily.CoilSyncState));
        Assert.Equal(1, families.Count(f => f == NameRuleFamily.PositionGroupMutex));
        Assert.Equal(4, families.Count(f => f == "(无)"));   // QFwd/QBkd 互斥、IWP/IHP 互斥、两条到位：默认归引脚名配对族
    }

    [Fact]
    public void SwitchingOffAFamilyDropsOnlyItsRules()
    {
        var json = PldStructuralConstraintAugmenter.AugmentFromBlocks(Anchors(), Tags());
        Assert.Equal(6, NameRuleFamilies.ToRules(json, NameRuleFamily.PinNameAugment, NameRuleSwitches.AllOn).Count);
        var withoutPins = NameRuleFamilies.ToRules(json, NameRuleFamily.PinNameAugment,
            NameRuleSwitches.AllOn.Without(NameRuleFamily.PinNameAugment));
        Assert.Equal(new[] { "AT_MOST_ONE", "SAME_STATE" }, withoutPins.Select(r => r.Operator).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, withoutPins.Single(r => r.Operator == "AT_MOST_ONE").Signals.Count);
        Assert.Empty(NameRuleFamilies.ToRules(json, NameRuleFamily.PinNameAugment, NameRuleSwitches.AllOff));
    }

    [Fact]
    public void UntaggedRulesUseTheDefaultFamily()
    {
        const string json = """[{"operator":"MUST_BE_FALSE","signals":[{"signalName":"K","signalRole":"Target"}],"description":"d"}]""";
        var rule = Assert.Single(NameRuleFamilies.ToRules(json, NameRuleFamily.MaskedAlarm, NameRuleSwitches.AllOn));
        Assert.Equal("MUST_BE_FALSE", rule.Operator);
        Assert.Equal("d", rule.Description);
        Assert.Equal("K", Assert.Single(rule.Signals).Name);
        Assert.Empty(NameRuleFamilies.ToRules(json, NameRuleFamily.MaskedAlarm, NameRuleSwitches.AllOn.Without(NameRuleFamily.MaskedAlarm)));
    }

    [Fact]
    public void ConfigurationTurnsOffOnlyAnExplicitFalse()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IndependentPlcPipeline:NameRules:PinNameAugment"] = "false",
            ["IndependentPlcPipeline:NameRules:MaskedAlarm"] = "true",
            ["IndependentPlcPipeline:NameRules:CoilSyncState"] = "不是布尔",
        }).Build();
        var switches = NameRuleSwitches.FromConfiguration(config);
        Assert.False(switches.IsOn(NameRuleFamily.PinNameAugment));
        Assert.True(switches.IsOn(NameRuleFamily.MaskedAlarm));
        Assert.True(switches.IsOn(NameRuleFamily.CoilSyncState));
        Assert.Equal("PinNameAugment", switches.GeneratorSignature);
        Assert.Equal("", NameRuleSwitches.FromConfiguration(new ConfigurationBuilder().Build()).GeneratorSignature);
    }

    [Fact]
    public void GeneratorVersionNamesTheGeneratorFamiliesThatAreOff()
    {
        Assert.Equal(OriginalConstraintWorkflow.Version, OriginalConstraintWorkflow.VersionFor(null));
        Assert.Equal(OriginalConstraintWorkflow.Version, OriginalConstraintWorkflow.VersionFor(NameRuleSwitches.AllOn));
        Assert.Equal(OriginalConstraintWorkflow.Version + "|namerules-off:CoilSyncState+PinNameAugment",
            OriginalConstraintWorkflow.VersionFor(NameRuleSwitches.AllOn.Without(NameRuleFamily.PinNameAugment).Without(NameRuleFamily.CoilSyncState)));
    }
}
