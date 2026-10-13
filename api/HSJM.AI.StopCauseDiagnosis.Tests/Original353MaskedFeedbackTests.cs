#nullable enable
using System.Text.Json;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353MaskedFeedbackTests
{
    [Fact]
    public void SharedPrefixAndRepeatedContactAreRenderedWithoutInventingAllRequired()
    {
        var plain = SiemensFlgNetConstraintExtractor.BuildParsedLogicDescription(
            CallerXml(repeatH3: true), Tags().ToDictionary(tag => tag.Name,
                tag => (tag.Address, (string?)null)), networkId: 4, blockName: "Caller");

        var assignment = plain.Split("IHP :=", 2)[1].Split('\n')[0];
        Assert.Contains("(H1 AND H2)", assignment);
        Assert.Contains("(H1 AND H3)", assignment);
        var feedback = plain.Split("### 7. Q→I 驱动对应链", 2)[1]
            .Split("### 9. 互斥关系", 2)[0];
        Assert.Contains(" OR ", feedback);
        Assert.DoesNotContain("AllRequired", feedback);
    }

    [Fact]
    public void RepeatedH3MakesH2IneffectiveButOrdinaryOrAlternativesStayInfluential()
    {
        var maskedSource = Source(repeatH3: true);
        var masked = Original353FeedbackGroups.Extract(maskedSource, maskedSource.Units[0]);
        var group = Assert.Single(masked);
        Assert.Equal(["H1", "H3", "H4"], group.Rule.Signals
            .Where(signal => signal.Role == "Target").Select(signal => signal.Name).ToArray());
        Assert.Contains("MaskedFeedbackInput:H2", group.Evidence);
        var persisted = Original353LegacyRuleMapper.Map(1, 1, group.Rule, Tags());
        using (var json = JsonDocument.Parse(persisted.Rule.ValidStates!))
            Assert.Equal("H2", Assert.Single(json.RootElement.GetProperty("MaskedFeedbackInputs")
                .EnumerateArray()).GetString());
        Assert.Contains("MaskedFeedbackInput:H2",
            new Original353Generator(maskedSource).Generate(maskedSource.Units[0]).Issues);

        var ordinarySource = Source(repeatH3: false);
        var ordinary = Assert.Single(Original353FeedbackGroups.Extract(ordinarySource, ordinarySource.Units[0]));
        Assert.DoesNotContain(ordinary.Evidence, item => item.StartsWith("MaskedFeedbackInput:"));
    }

    [Fact]
    public void InterfacePositionFeedbackSwitchDropsTheConditionalFeedback()
    {
        var source = Source(repeatH3: false);
        Assert.NotEmpty(new Original353Generator(source).Generate(source.Units[0]).ConditionalFeedbackRules);
        var off = new Original353Generator(source, NameRuleSwitches.AllOn.Without(NameRuleFamily.InterfacePositionFeedback))
            .Generate(source.Units[0]);
        Assert.Empty(off.ConditionalFeedbackRules);
        Assert.DoesNotContain(off.Issues, i => i.StartsWith("MaskedFeedbackInput:", StringComparison.Ordinal));
    }

    [Fact]
    public void UploadedOp020NetworkReportsTheMaskedSecondHomeFeedback()
    {
        var archive = Environment.GetEnvironmentVariable("HSJM_XMLV2_PLC1_ARCHIVE");
        if (string.IsNullOrWhiteSpace(archive)) return;
        var tags = new PipelineTag[] {
            new("OP020_CYL02_1H", "%I1360.0", "Bool"),
            new("OP020_CYL02_2H", "%I1360.2", "Bool"),
            new("OP020_CYL02_3H", "%I1360.4", "Bool"),
            new("OP020_CYL02_4H", "%I1360.6", "Bool"),
            new("OP020_YV02_H", "%Q1361.6", "Bool")
        };
        var source = Original353ArchiveSource.Read(archive, 1, tags);
        var unit = source.Units.Single(unit => unit.BlockName == "FB220_OP20Manual"
            && unit.RawContent.Contains("OP020_CYL02_2H", StringComparison.Ordinal));
        var group = Assert.Single(Original353FeedbackGroups.Extract(source, unit)
            .Where(item => item.Rule.Signals.Any(signal => signal.Name == "OP020_YV02_H")));
        Assert.Equal(["OP020_CYL02_1H", "OP020_CYL02_3H", "OP020_CYL02_4H"],
            group.Rule.Signals.Where(signal => signal.Role == "Target")
                .Select(signal => signal.Name).ToArray());
        Assert.Contains("MaskedFeedbackInput:OP020_CYL02_2H", group.Evidence);
        var plain = SiemensFlgNetConstraintExtractor.BuildParsedLogicDescription(
            unit.RawContent, tags.ToDictionary(tag => tag.Name,
                tag => (tag.Address, (string?)null)), unit.NetworkOrder, blockName: unit.BlockName);
        var assignment = plain.Split("IHP :=", 2)[1].Split('\n')[0];
        Assert.Contains("OP020_CYL02_1H AND OP020_CYL02_3H", assignment);
        Assert.DoesNotContain("AllRequired", plain.Split("### 7. Q→I 驱动对应链", 2)[1]
            .Split("### 9. 互斥关系", 2)[0]);
    }

    [Fact]
    public void WordingCannotTurnAMaskedInputIntoRequiredFeedback()
    {
        var rule = new PipelineRule("IMPLIES", [new("Q", "Source"),
            new("H1", "Target"), new("H3", "Target"), new("H4", "Target")], "code") {
            Applicability = "AfterSettling", RequiresSettlingEvidence = true,
            MaskedFeedbackInputs = ["H2"]
        };
        Assert.False(Original353RuleText.IsValidAiText(rule, "Q_到位反馈",
            "Q发出命令且超过动作等待时间后，H1、H2、H3、H4应同时到位。"));
    }

    private static PipelineSnapshot Source(bool repeatH3)
    {
        const string interfaceXml = """
            <Interface><Section Name="Input"><Member Name="IHP" Datatype="Bool" /></Section>
            <Section Name="Output"><Member Name="QBkd" Datatype="Bool" /></Section></Interface>
            """;
        const string calleeXml = """
            <FlgNet><Parts>
              <Access Scope="LocalVariable" UId="1"><Symbol><Component Name="IHP" /></Symbol></Access>
              <Access Scope="LocalVariable" UId="2"><Symbol><Component Name="QBkd" /></Symbol></Access>
              <Part Name="Contact" UId="3" /><Part Name="Coil" UId="4" />
            </Parts><Wires>
              <Wire UId="5"><IdentCon UId="1" /><NameCon UId="3" Name="operand" /></Wire>
              <Wire UId="6"><IdentCon UId="2" /><NameCon UId="4" Name="operand" /></Wire>
            </Wires></FlgNet>
            """;
        var caller = new PipelineUnit(1, "Caller", 1, "LAD", CallerXml(repeatH3), "", [], NetworkOrder: 4);
        var callee = new PipelineUnit(2, "ValveFB", 2, "LAD", calleeXml, "", [], InterfaceXml: interfaceXml);
        return new PipelineSnapshot(1, [caller, callee], Tags());
    }

    private static PipelineTag[] Tags() => [
        new("H1", "%I0.0", "Bool"), new("H2", "%I0.1", "Bool"),
        new("H3", "%I0.2", "Bool"), new("H4", "%I0.3", "Bool"),
        new("Q", "%Q1.0", "Bool")
    ];

    private static string CallerXml(bool repeatH3)
    {
        static XElement Access(int id, string name) => new("Access",
            new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", id),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", name))));
        static XElement Part(int id, string name) => new("Part",
            new XAttribute("Name", name), new XAttribute("UId", id));
        static XElement Contact(int id) => Part(id, "Contact");
        static XElement Or(int id) => new("Part", new XAttribute("Name", "O"),
            new XAttribute("UId", id), new XElement("TemplateValue", new XAttribute("Name", "Card"), "2"));
        static XElement Id(int uid) => new("IdentCon", new XAttribute("UId", uid));
        static XElement Pin(int uid, string name) => new("NameCon", new XAttribute("UId", uid), new XAttribute("Name", name));
        static XElement Wire(int uid, params XElement[] connectors) => new("Wire", new XAttribute("UId", uid), connectors);
        var parts = new XElement("Parts",
            Access(1, "H1"), Access(2, "H2"), Access(3, "H3"), Access(4, "H3"),
            Access(5, "H4"), Access(6, "Bypass"), Access(7, "Q"),
            Contact(11), Contact(12), Contact(13), Contact(14), Contact(15), Contact(16),
            Or(17), Or(18),
            new XElement("Call", new XAttribute("UId", 20),
                new XElement("CallInfo", new XAttribute("Name", "ValveFB"),
                    new XElement("Parameter", new XAttribute("Name", "IWP"), new XAttribute("Section", "Input"), new XAttribute("Type", "Bool")),
                    new XElement("Parameter", new XAttribute("Name", "IHP"), new XAttribute("Section", "Input"), new XAttribute("Type", "Bool")),
                    new XElement("Parameter", new XAttribute("Name", "QFwd"), new XAttribute("Section", "Output"), new XAttribute("Type", "Bool")),
                    new XElement("Parameter", new XAttribute("Name", "QBkd"), new XAttribute("Section", "Output"), new XAttribute("Type", "Bool")))));
        var wires = new XElement("Wires",
            Wire(30, new XElement("Powerrail"), Pin(11, "in"), Pin(16, "in")),
            Wire(31, Id(1), Pin(11, "operand")),
            Wire(32, Pin(11, "out"), Pin(12, "in"), Pin(13, "in")),
            Wire(33, Id(2), Pin(12, "operand")), Wire(34, Id(3), Pin(13, "operand")),
            Wire(35, Pin(12, "out"), Pin(17, "in1")), Wire(36, Pin(13, "out"), Pin(17, "in2")),
            Wire(37, Pin(17, "out"), Pin(repeatH3 ? 14 : 15, "in")),
            Wire(38, Id(4), Pin(14, "operand")),
            repeatH3 ? Wire(39, Pin(14, "out"), Pin(15, "in")) : null,
            Wire(40, Id(5), Pin(15, "operand")),
            Wire(41, Pin(15, "out"), Pin(18, "in1")),
            Wire(42, Id(6), Pin(16, "operand")),
            Wire(43, Pin(16, "out"), Pin(18, "in2")),
            Wire(44, Pin(18, "out"), Pin(20, "IHP")),
            Wire(45, Pin(20, "QBkd"), Id(7)));
        return new XElement("FlgNet", parts, wires).ToString(SaveOptions.DisableFormatting);
    }
}
