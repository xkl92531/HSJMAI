using System.IO;
using System.IO.Compression;
using System.Linq;
using HsjmTia.Core.Packaging;
using Xunit;

namespace HsjmTia.Core.Tests.Packaging
{
    public class ZipPackagerTests
    {
        [Fact]
        public void Create_PutsPlcContentAtTheZipRoot_NotUnderAPlcFolder()
        {
            var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var plcDir = Path.Combine(tmp, "PLC_1_1.1.1.1");
            Directory.CreateDirectory(Path.Combine(plcDir, "blocks", "LAD"));
            File.WriteAllText(Path.Combine(plcDir, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(plcDir, "blocks", "LAD", "FB1.xml"), "<x/>");

            var zip = Path.Combine(tmp, "out.zip");
            ZipPackager.Create(plcDir, zip);

            using (var archive = ZipFile.OpenRead(zip))
            {
                var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
                Assert.Contains("manifest.json", names);
                Assert.Contains("blocks/LAD/FB1.xml", names);
                Assert.DoesNotContain(names, n => n.StartsWith("PLC_1"));
            }

            Directory.Delete(tmp, true);
        }

        [Fact]
        public void Create_OverwritesAnExistingZip()
        {
            var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var plcDir = Path.Combine(tmp, "p");
            Directory.CreateDirectory(plcDir);
            File.WriteAllText(Path.Combine(plcDir, "manifest.json"), "{}");
            var zip = Path.Combine(tmp, "out.zip");
            File.WriteAllText(zip, "stale");

            ZipPackager.Create(plcDir, zip);

            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Single(archive.Entries);
            }

            Directory.Delete(tmp, true);
        }
    }
}
