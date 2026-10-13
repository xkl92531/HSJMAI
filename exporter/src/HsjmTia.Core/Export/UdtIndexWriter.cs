using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace HsjmTia.Core.Export
{
    /// <summary>
    /// UDT 类型名 → 相对文件路径索引。
    /// 因为文件名要清洗（A/B → A_B.xml）而 parser.py 按**类型名**精确查找，
    /// 两者对不上时 UDT 解析不出来，成员整体标记为 unmapped_type 被跳过。
    /// </summary>
    public static class UdtIndexWriter
    {
        /// <summary>不带 BOM 的 UTF-8：JSON 按规范就是 UTF-8，不该带 BOM——
        /// parser.py 用 Python 的 'utf-8' 编解码器打开这个文件，该编解码器不剥离 BOM，
        /// 带 BOM 会让 json.load 抛 JSONDecodeError，被调用方 except (OSError, ValueError)
        /// 悄悄吞掉，索引整体失效退化为按文件名匹配（见 §六 C1）。
        /// 与 tags.csv / block_path.txt 用的带 BOM 编码是两回事，不要合并成同一个常量：
        /// 那两个文件的 BOM 是后端 ParseTagCSV 选择编码分支时依赖的信号，必须保留。</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Serialize(IEnumerable<KeyValuePair<string, string>> typeNameToRelativePath)
        {
            var map = new Dictionary<string, string>();
            if (typeNameToRelativePath != null)
            {
                foreach (var kv in typeNameToRelativePath)
                {
                    map[kv.Key] = (kv.Value ?? string.Empty).Replace('\\', '/');
                }
            }

            return JsonConvert.SerializeObject(
                new { types = map }, Formatting.Indented);
        }

        /// <summary>Serialize 的字节层面版本：把结果编码为不带 BOM 的 UTF-8。
        /// 抽出这一步是为了让"写出的字节不带 BOM"这件事能在 HsjmTia.Core 里被单元测试断言——
        /// 实际落盘（File.WriteAllBytes）仍由 HsjmTia.Openness.ExportOrchestrator 做。</summary>
        public static byte[] SerializeToBytes(IEnumerable<KeyValuePair<string, string>> typeNameToRelativePath)
            => Utf8NoBom.GetBytes(Serialize(typeNameToRelativePath));
    }
}
