using System.IO.Compression;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353ArchiveSourceTests
{
    [Fact]
    public void ReadsCurrentUploadedBlockXmlWithItsTypedInterface()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("blocks/FB_Test.xml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""
                    <Document><SW.Blocks.FB><AttributeList><Name>FB_Test</Name><ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections><Section Name="Input"><Member Name="Sensor" Datatype="Bool" /></Section></Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit><NetworkSource><FlgNet /></NetworkSource></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>
                    """);
            }
            var source = Original353ArchiveSource.Read(path, 42, []);
            Assert.Single(source.Units);
            Assert.Contains("Sensor", source.Units[0].InterfaceXml);
        }
        finally { File.Delete(path); }
    }
}
