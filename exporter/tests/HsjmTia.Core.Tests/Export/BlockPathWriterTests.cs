using System.IO;
using HsjmTia.Core.Export;
using Xunit;

namespace HsjmTia.Core.Tests.Export
{
    public class BlockPathWriterTests
    {
        private static string Render(params BlockPathEntry[] entries)
        {
            var sw = new StringWriter();
            BlockPathWriter.Write(entries, sw);
            return sw.ToString().Replace("\r\n", "\n");
        }

        [Fact]
        public void Write_EmitsFolderCommaName()
            => Assert.Equal("OP30/Manual,FB320\n",
                Render(new BlockPathEntry { FolderPath = "OP30/Manual", BlockName = "FB320" }));

        [Fact]
        public void Write_EmitsEmptyFolderForRootBlocks()
            => Assert.Equal(",FC1\n", Render(new BlockPathEntry { FolderPath = "", BlockName = "FC1" }));

        [Fact]
        public void Write_ProducesNothingButAnEmptyStringForNoEntries()
            => Assert.Equal(string.Empty, Render());

        [Fact]
        public void Write_DoesNotAppend_SoRepeatedExportsCannotDuplicate()
        {
            // 该方法只写不追加；重复调用同一个 writer 才会叠加，调用方负责用新文件流
            var sw = new StringWriter();
            BlockPathWriter.Write(new[] { new BlockPathEntry { FolderPath = "A", BlockName = "B" } }, sw);
            Assert.Single(sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
        }
    }
}
