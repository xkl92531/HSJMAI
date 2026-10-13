using System.IO;
using HsjmTia.Core.Layout;
using HsjmTia.Core.Model;
using Xunit;

namespace HsjmTia.Core.Tests.Layout
{
    public class ExportLayoutTests
    {
        private static ExportLayout Make()
            => ExportLayout.For(@"C:\work\xml", "泰兴产线",
                new PlcIdentity { Name = "PLC_1", Ip = "192.168.6.31", DeviceIndex = 1 });

        [Fact]
        public void For_InsertsAPlcLevelBelowTheProject()
            => Assert.Equal(Path.Combine(@"C:\work\xml", "泰兴产线", "PLC_1_192.168.6.31"), Make().PlcDir);

        [Fact]
        public void For_PlacesBlocksTagsAndUdtUnderThePlcDir()
        {
            var l = Make();
            Assert.Equal(Path.Combine(l.PlcDir, "blocks"), l.BlocksDir);
            Assert.Equal(Path.Combine(l.PlcDir, "plcTags"), l.TagsDir);
            Assert.Equal(Path.Combine(l.PlcDir, "udt"), l.UdtDir);
        }

        [Fact]
        public void For_UsesAFixedTagsFileName_SoRepeatedExportsCannotAccumulate()
            => Assert.Equal(Path.Combine(Make().TagsDir, "tags.csv"), Make().TagsCsvPath);

        [Fact]
        public void For_SanitisesTheProjectName()
        {
            var l = ExportLayout.For(@"C:\x", "A:B", new PlcIdentity { Name = "P", Ip = "1.1.1.1" });
            Assert.Contains("A_B", l.PlcDir);
        }

        [Fact]
        public void For_PutsManifestAtThePlcRoot()
            => Assert.Equal(Path.Combine(Make().PlcDir, "manifest.json"), Make().ManifestPath);
    }
}
