using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class PipelineInterfaceXmlBuilderTests
{
    [Fact]
    public void RebuildsTypedInputAndOutputSectionsFromCurrentBlockInterfaceRows()
    {
        var xml = PipelineInterfaceXmlBuilder.Build([
            new("IHP", "Bool", "IN"), new("QBkd", "Bool", "OUT"),
            new("CylinderCmd", "Struct", "STAT")]);
        var members = XDocument.Parse(xml).Descendants("Member").ToArray();

        Assert.Contains(members, m => (string?)m.Attribute("Name") == "IHP"
            && (string?)m.Attribute("Datatype") == "Bool"
            && (string?)m.Parent?.Attribute("Name") == "Input");
        Assert.Contains(members, m => (string?)m.Attribute("Name") == "QBkd"
            && (string?)m.Parent?.Attribute("Name") == "Output");
    }

    [Fact]
    public void RebuildsNestedBooleanMemberWithoutInventingAnArrayElement()
    {
        var xml = PipelineInterfaceXmlBuilder.Build([
            new("Gate1", "Struct", "STAT"),
            new("Gate1.DoorSafety", "Bool", "STAT"),
            new("Valve[8].Feedback", "Bool", "STAT")]);
        var members = XDocument.Parse(xml).Descendants("Member").ToArray();

        Assert.Contains(members, m => (string?)m.Attribute("Name") == "DoorSafety"
            && (string?)m.Parent?.Attribute("Name") == "Gate1");
        Assert.DoesNotContain(members, m => ((string?)m.Attribute("Name"))?.Contains("Valve") == true);
    }
}
