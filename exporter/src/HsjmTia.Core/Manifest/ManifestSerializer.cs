using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HsjmTia.Core.Manifest
{
    public static class ManifestSerializer
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        };

        /// <summary>不带 BOM 的 UTF-8：与 UdtIndexWriter 的 Utf8NoBom 同样的理由——
        /// manifest.json 是同一类文件（JSON，装在包里，parser.py 用 'utf-8' 编解码器打开），
        /// 带 BOM 会让 json.load 抛 JSONDecodeError，被调用方 except (OSError, ValueError)
        /// 悄悄吞掉（见 §六 C1）。与 tags.csv / block_path.txt 的 BOM 不是一回事，不要合并：
        /// 那两个文件的 BOM 是后端 ParseTagCSV 选择编码分支时依赖的信号，必须保留。</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Serialize(ExportManifest manifest)
            => JsonConvert.SerializeObject(manifest, Settings);

        public static ExportManifest Deserialize(string json)
            => JsonConvert.DeserializeObject<ExportManifest>(json, Settings);

        /// <summary>Serialize 的字节层面版本：编码为不带 BOM 的 UTF-8，供调用方直接
        /// File.WriteAllBytes 落盘，同时让"写出的字节不带 BOM"这件事能在 HsjmTia.Core
        /// 里被单元测试断言（实际落盘仍由 HsjmTia.Openness.ExportOrchestrator 做）。</summary>
        public static byte[] SerializeToBytes(ExportManifest manifest)
            => Utf8NoBom.GetBytes(Serialize(manifest));
    }
}
