using System.Collections.Generic;
using HsjmTia.Core.Export;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HsjmTia.Core.Tests.Export
{
    public class UdtIndexWriterTests
    {
        [Fact]
        public void Serialize_MapsTypeNameToRelativePath()
        {
            var json = JObject.Parse(UdtIndexWriter.Serialize(new[]
            {
                new KeyValuePair<string, string>("UDT_General", "UDT_General.xml"),
                new KeyValuePair<string, string>("A/B", "Sub/A_B.xml"),
            }));

            Assert.Equal("UDT_General.xml", (string)json["types"]["UDT_General"]);
            // 键是**原始类型名**，值是清洗后的相对路径 —— parser.py 按类型名查找
            Assert.Equal("Sub/A_B.xml", (string)json["types"]["A/B"]);
        }

        [Fact]
        public void Serialize_UsesForwardSlashes_SoThePathIsPortable()
        {
            var json = JObject.Parse(UdtIndexWriter.Serialize(new[]
            {
                new KeyValuePair<string, string>("T", "Sub\\T.xml"),
            }));
            Assert.Equal("Sub/T.xml", (string)json["types"]["T"]);
        }

        [Fact]
        public void Serialize_EmitsAnEmptyMapForNoTypes()
            => Assert.NotNull(JObject.Parse(UdtIndexWriter.Serialize(new KeyValuePair<string, string>[0]))["types"]);

        [Fact]
        public void SerializeToBytes_DoesNotEmitAUtf8Bom()
        {
            // parser.py 曾用 encoding='utf-8' 打开这个文件——该编解码器不剥离 BOM，
            // json.load 会抛 JSONDecodeError，被 except (OSError, ValueError) 悄悄吞掉，
            // 索引整体失效退化为按文件名匹配。JSON 按规范就不该带 BOM，这里锁死字节层面不含它。
            var bytes = UdtIndexWriter.SerializeToBytes(new[]
            {
                new KeyValuePair<string, string>("T", "T.xml"),
            });

            Assert.True(bytes.Length >= 3);
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }

        [Fact]
        public void SerializeToBytes_RoundTripsToTheSameJsonAsSerialize()
        {
            var pairs = new[] { new KeyValuePair<string, string>("A/B", "Sub/A_B.xml") };
            var bytes = UdtIndexWriter.SerializeToBytes(pairs);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Equal(UdtIndexWriter.Serialize(pairs), text);
        }
    }
}
