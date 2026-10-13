using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353ManualModeBindingsTests
{
    private const string Db = """
        <Document><SW.Blocks.InstanceDB><AttributeList><Name>DB_Mode</Name>
        <InstanceOfName>FB_Mode</InstanceOfName><Interface><Sections><Section Name="Static">
        <Member Name="General" Datatype="&quot;UDT_General&quot;" /></Section></Sections></Interface>
        </AttributeList></SW.Blocks.InstanceDB></Document>
        """;
    private const string Udt = """
        <Document><SW.Types.PlcStruct><AttributeList><Name>UDT_General</Name>
        <Interface><Sections><Section Name="None">
        <Member Name="ManualMod" Datatype="Bool" /><Member Name="AutoMod" Datatype="Bool" />
        </Section></Sections></Interface></AttributeList></SW.Types.PlcStruct></Document>
        """;
    private const string Fb = """
        <Document><SW.Blocks.FB><AttributeList><Name>FB_Mode</Name>
        <FlgNet><Parts>
        <Access UId="1"><Symbol><Component Name="General" /><Component Name="ManualMod" /></Symbol></Access>
        <Access UId="2"><Symbol><Component Name="General" /><Component Name="AutoMod" /></Symbol></Access>
        <Call UId="3"><CallInfo Name="FB3002_Valve" /></Call>
        </Parts><Wires>
        <Wire><IdentCon UId="1" /><NameCon UId="3" Name="ManualMod" /></Wire>
        <Wire><IdentCon UId="2" /><NameCon UId="3" Name="AutoRunMod" /></Wire>
        </Wires></FlgNet></AttributeList></SW.Blocks.FB></Document>
        """;

    [Fact]
    public void DirectFbPinWiringAndInstanceDbProduceModeReadPath()
    {
        var found = Original353ManualModeBindings.Extract([
            new("DB_Mode.xml", Db), new("UDT_General.xml", Udt), new("FB_Mode.xml", Fb)
        ]);
        var binding = Assert.Single(found);
        Assert.Equal("DB_Mode", binding.BlockName);
        Assert.Equal("General.ManualMod", binding.VariablePath);
        Assert.Equal("General.AutoMod", binding.AutoVariablePath);
    }

    [Fact]
    public void MissingAutoPinOrUdtProofDoesNotGuessMode()
    {
        var withoutAuto = Fb.Replace("<Wire><IdentCon UId=\"2\" /><NameCon UId=\"3\" Name=\"AutoRunMod\" /></Wire>", "");
        Assert.Empty(Original353ManualModeBindings.Extract([
            new("DB_Mode.xml", Db), new("UDT_General.xml", Udt), new("FB_Mode.xml", withoutAuto)
        ]));
        Assert.Empty(Original353ManualModeBindings.Extract([
            new("DB_Mode.xml", Db), new("FB_Mode.xml", Fb)
        ]));
    }

    [Theory]
    [InlineData("test-v2-source-plc1.zip", 6)]
    [InlineData("test-v2-source-plc2.zip", 3)]
    public void CurrentCompleteXmlArchivesExposeVerifiedInstanceModeBits(string fileName, int expected)
    {
        var archive = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".artifacts", fileName));
        if (!System.IO.File.Exists(archive)) return;
        Assert.Equal(expected, Original353ManualModeBindings.Read(archive).Count);
    }

    [Fact]
    public void ModeBindingsAreEvidenceMetadataAndCannotChangeConstraintSignals()
    {
        var rule = new HSJM.AI.AppLayer.PLC.IndependentPipeline.PipelineRule("AT_MOST_ONE",
            [new("A", "Member"), new("B", "Member")], "raw");
        var tags = new HSJM.AI.AppLayer.PLC.IndependentPipeline.PipelineTag[] {
            new("A", "%Q1.0", "Bool"), new("B", "%Q1.1", "Bool")
        };
        var rows = Original353LegacyRuleMapper.Map(1, 2, rule, tags, modeBindings:
            [new Original353ManualModeBinding("DB_Mode", "General.ManualMod")]);
        Assert.Contains("DB_Mode", rows.Rule.ValidStates);
        Assert.Contains("General.ManualMod", rows.Rule.ValidStates);
        Assert.Equal("AT_MOST_ONE", rows.Rule.Operator);
        Assert.Equal("%Q1.0", rows.Signals[0].SignalAddress);
    }
}
