using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353ManualModePolicyTests
{
    [Fact]
    public void RunModeAndAutoSelectionAreDistinctAndBothCanBeOnInAutomaticOperation()
    {
        var bindings = new[] { new Original353ManualModeBinding("DB1", "General.ManualMod", "General.AutoMod", "General.AutoRunMod") };
        var values = new Dictionary<string, bool?> { ["DB1.General.ManualMod"] = false,
            ["DB1.General.AutoMod"] = true, ["DB1.General.AutoRunMod"] = true };
        Assert.Equal("Other", Original353ManualModePolicy.Classify(bindings, values));
        values["DB1.General.ManualMod"] = true;
        values["DB1.General.AutoMod"] = false;
        Assert.Equal("Conflict", Original353ManualModePolicy.Classify(bindings, values));
        values["DB1.General.AutoRunMod"] = null;
        Assert.Equal("Unknown", Original353ManualModePolicy.Classify(bindings, values));
        values["DB1.General.AutoRunMod"] = false;
        Assert.Equal("Manual", Original353ManualModePolicy.Classify(bindings, values));
    }

    private static readonly Original353ManualModeBinding[] Bindings = [
        new("DB1", "General.ManualMod"), new("DB2", "General.ManualMod")
    ];

    [Fact]
    public void OnlyGoodManualTrueAndAutoFalseForEveryStationSuppressesConstraints()
    {
        var values = new Dictionary<string, bool?> {
            ["DB1.General.ManualMod"] = true, ["DB1.General.AutoMod"] = false,
            ["DB2.General.ManualMod"] = true, ["DB2.General.AutoMod"] = false
        };
        Assert.Equal("Manual", Original353ManualModePolicy.Classify(Bindings, values));
        values["DB2.General.ManualMod"] = null;
        Assert.Equal("Unknown", Original353ManualModePolicy.Classify(Bindings, values));
        values["DB2.General.ManualMod"] = false;
        Assert.Equal("Other", Original353ManualModePolicy.Classify(Bindings, values));
        values["DB2.General.ManualMod"] = true;
        values["DB2.General.AutoMod"] = true;
        Assert.Equal("Conflict", Original353ManualModePolicy.Classify(Bindings, values));
    }

    [Fact]
    public void KnownConflictIsNotHiddenByAnUnreadSignalInAnotherGroup()
    {
        var values = new Dictionary<string, bool?> {
            ["DB1.General.ManualMod"] = true, ["DB1.General.AutoMod"] = true,
            ["DB2.General.ManualMod"] = null, ["DB2.General.AutoMod"] = null
        };
        Assert.Equal("Conflict", Original353ManualModePolicy.Classify(Bindings, values));
    }

    [Fact]
    public void AutoFalseAloneAndMissingBindingsCannotClaimManual()
    {
        Assert.Equal("Unknown", Original353ManualModePolicy.Classify([], new Dictionary<string, bool?>()));
        Assert.Equal("Unknown", Original353ManualModePolicy.Classify(Bindings,
            new Dictionary<string, bool?> { ["DB1.General.AutoMod"] = false }));
    }

    [Fact]
    public void RegisteredDbAliasUsesS1500DeviceAndBothExplicitModeBits()
    {
        var aliases = Original353ManualModeReader.BuildAliases("10.11.38.10",
            HSJM.AI.IDbService.IgwLedgerSnapshot.Empty,
            [new Original353ManualModeBinding("DB220_OP20Manual", "General.ManualMod")]);
        Assert.Equal("通用.S1500_10.11.38.10.DB220_OP20Manual.General.ManualMod",
            aliases["DB220_OP20Manual.General.ManualMod"]);
        Assert.Equal("通用.S1500_10.11.38.10.DB220_OP20Manual.General.AutoMod",
            aliases["DB220_OP20Manual.General.AutoMod"]);
    }
}
