#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class InterfaceReaderTests
{
    const string Xml = """
        <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Input">
            <Member Name="C_SensorNumber" Datatype="Int"><Comment><MultiLanguageText Lang="zh-CN">传感器个数</MultiLanguageText></Comment><StartValue>1</StartValue></Member>
            <Member Name="I_Pos1" Datatype="Bool" />
          </Section>
          <Section Name="Static">
            <Member Name="Inner" Datatype="&quot;FbInner&quot;">
              <Sections><Section Name="Output"><Member Name="Hidden" Datatype="Bool" /></Section></Sections>
            </Member>
            <Member Name="Cmd" Datatype="Struct"><Member Name="QFwd" Datatype="Bool" /></Member>
          </Section>
        </Sections></Interface>
        """;

    [Fact]
    public void ReadsOnlyTopLevelSectionsWithDefaultsAndComments()
    {
        var members = InterfaceReader.Read(Xml);
        Assert.Equal(["Input.C_SensorNumber", "Input.I_Pos1", "Static.Inner", "Static.Cmd"],
            members.Select(m => m.Section + "." + m.Name));
        var n = members[0];
        Assert.Equal(("Int", "1", "传感器个数"), (n.DataType, n.StartValue, n.Comment));
        Assert.Empty(members[2].Children);                       // 多重背景内嵌的 Sections 不当成员
        Assert.Equal(["QFwd"], members[3].Children.Select(c => c.Name));
    }

    [Fact]
    public void RendersSectionsAsList()
    {
        var text = InterfaceReader.Render(InterfaceReader.Read(Xml));
        Assert.Contains("- Input:", text);
        Assert.Contains("  - C_SensorNumber : Int 默认=1 // 传感器个数", text);
        Assert.Contains("  - Cmd : Struct { QFwd:Bool }", text);
    }
}
