using System;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Model;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HsjmTia.Core.Tests.Manifest
{
    public class ManifestSerializerTests
    {
        private static ExportManifest Sample()
        {
            var m = new ExportManifest
            {
                ExportedAt = new DateTimeOffset(2026, 9, 20, 10, 30, 0, TimeSpan.FromHours(8)),
                TiaProjectName = "泰兴产线",
                TiaProjectPath = @"D:\proj\x.ap16",
                TiaVersion = "V16",
                Plc = new PlcIdentity { Name = "PLC_1", Ip = "192.168.6.31", OrderNumber = "6ES7", DeviceIndex = 2 }
            };
            m.Counts.Blocks = 1247;
            m.Gaps.Add(ExportGap.Create(GapKinds.KnowHowProtected, "FB9001", "块受知识产权保护"));
            return m;
        }

        [Fact]
        public void Serialize_UsesCamelCase_SoTheFileIsConsistentWithBackendJson()
        {
            var json = JObject.Parse(ManifestSerializer.Serialize(Sample()));
            Assert.NotNull(json["exportedAt"]);
            Assert.NotNull(json["plc"]["ip"]);
            Assert.Equal(1247, (int)json["counts"]["blocks"]);
        }

        [Fact]
        public void Serialize_WritesSchemaVersion2()
            => Assert.Equal(2, (int)JObject.Parse(ManifestSerializer.Serialize(Sample()))["schemaVersion"]);

        [Fact]
        public void RoundTrip_PreservesGaps()
        {
            var back = ManifestSerializer.Deserialize(ManifestSerializer.Serialize(Sample()));
            Assert.Single(back.Gaps);
            Assert.Equal(GapKinds.KnowHowProtected, back.Gaps[0].Kind);
            Assert.Equal("FB9001", back.Gaps[0].Target);
        }

        [Fact]
        public void RoundTrip_PreservesPlcIdentity()
        {
            var back = ManifestSerializer.Deserialize(ManifestSerializer.Serialize(Sample()));
            Assert.Equal("192.168.6.31", back.Plc.Ip);
            Assert.Equal(2, back.Plc.DeviceIndex);
        }

        [Fact]
        public void HasGaps_IsFalse_ForACleanExport()
        {
            var m = Sample();
            m.Gaps.Clear();
            Assert.False(m.HasGaps);
        }

        [Fact]
        public void HasGaps_IsTrue_WhenAnythingWasSkipped()
            => Assert.True(Sample().HasGaps);

        [Fact]
        public void SerializeToBytes_DoesNotWriteAUtf8Bom()
        {
            // manifest.json 是 udt_index.json 的同类文件——JSON、装在包里、旁边就是
            // parser.py 用原生 'utf-8' 编解码器打开一切。带 BOM 会让 json.load 抛
            // JSONDecodeError，被调用方 except (OSError, ValueError) 悄悄吞掉
            // （见 §六 C1，本文件即是那次修复的同款回归）。
            var bytes = ManifestSerializer.SerializeToBytes(Sample());

            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "manifest.json 的字节不应以 UTF-8 BOM（EF BB BF）开头");
        }

        [Fact]
        public void SerializeToBytes_RoundTripsTheSameContentAsSerialize()
        {
            // 字节版本只是编码不同，内容必须和字符串版本完全一致。
            var bytes = ManifestSerializer.SerializeToBytes(Sample());
            var text = new System.Text.UTF8Encoding(false).GetString(bytes);

            Assert.Equal(ManifestSerializer.Serialize(Sample()), text);
        }
    }
}
