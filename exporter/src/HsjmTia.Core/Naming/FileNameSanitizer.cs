using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HsjmTia.Core.Naming
{
    /// <summary>
    /// 文件名/目录名清洗。非法字符一律"替换"为下划线，绝不删除——
    /// 删除会让 UDT 文件名与类型名对不上，下游 parser.py 按类型名精确查找就会落空。
    /// </summary>
    public static class FileNameSanitizer
    {
        private const string Placeholder = "_";

        private static readonly HashSet<char> Illegal = BuildIllegalSet();

        private static HashSet<char> BuildIllegalSet()
        {
            var set = new HashSet<char>(Path.GetInvalidFileNameChars());
            // Path.GetInvalidFileNameChars 在某些运行时不含这几个，显式补齐以保证跨机一致
            foreach (var c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            {
                set.Add(c);
            }
            return set;
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Placeholder;
            }

            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(Illegal.Contains(c) || c < ' ' ? '_' : c);
            }

            // Windows 不接受以点或空格结尾的名字
            var cleaned = sb.ToString().TrimEnd('.', ' ');
            return cleaned.Length == 0 ? Placeholder : cleaned;
        }
    }
}
