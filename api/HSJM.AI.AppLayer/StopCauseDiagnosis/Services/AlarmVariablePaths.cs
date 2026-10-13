#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 报警位的读取路径（已知问题第三批终审 I-2）：报警清单里的报警位是程序（IR）写法，如 <c>Type.4.Servo[10].HomeInterLockAlarm6</c>；
    /// IGW 注册与老网关登记用的是解析器的 VariablePath（ai_plc_blockinterface），数组容器前多一段同名占位：
    /// <c>Type.4.Servo.4.Servo[10].HomeInterLockAlarm6</c>。容器名不带点时（<c>Sensor.Sensor[1]</c>）IgwTagNaming.NormalizeVariablePath
    /// 能去掉占位、两边一致；成员名本身带点（泰星报警 DB 的 <c>4.Servo</c>、<c>1.Cylinder</c>）时去不掉，读别名就对不上。
    /// 所以读之前先按块接口把清单写法换回解析器写法（读别名、台账改名映射、老网关点名都以它为准）。
    /// </summary>
    public static class AlarmVariablePaths
    {
        private static readonly Regex BitSlice = new(@"^(?<parent>.+)\.(?<slice>%X\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>去掉解析器在数组元素前重复的容器名占位段：「…X.X[i]…」→「…X[i]…」，X 可以含点（按段边界匹配，取最短的 X）。</summary>
        public static string StripContainerPlaceholders(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var s = path;
            var depth = 0;
            for (var j = 0; j < s.Length; j++)
            {
                if (s[j] == ']') { depth = Math.Max(0, depth - 1); continue; }
                if (s[j] != '[') continue;
                if (depth++ > 0) continue;
                var left = s[..j];
                // 从最后一个点往前试：left = Q + X + "." + X，Q 为空或以 '.' 结尾
                for (var k = left.LastIndexOf('.'); k > 0; k = left.LastIndexOf('.', k - 1))
                {
                    var x = left[(k + 1)..];
                    var prefix = left[..k];
                    if (x.Length == 0 || x.Contains('[') || x.Contains(']')) break;
                    if (!prefix.EndsWith(x, StringComparison.Ordinal)) continue;
                    var before = prefix.Length - x.Length;
                    if (before != 0 && prefix[before - 1] != '.') continue;
                    s = prefix + s[j..];
                    j = prefix.Length;
                    break;
                }
            }
            return s;
        }

        /// <summary>块接口的 VariablePath → 按「去占位、下标去空白、忽略大小写」规整后的键；规整后撞键的几行都不收（认不出就不换）。</summary>
        public static IReadOnlyDictionary<string, string> Index(IEnumerable<string> interfacePaths)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in interfacePaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var path = raw.Trim();
                var key = Key(path);
                if (ambiguous.Contains(key)) continue;
                if (map.TryGetValue(key, out var existing) && !string.Equals(existing, path, StringComparison.Ordinal))
                {
                    map.Remove(key);
                    ambiguous.Add(key);
                    continue;
                }
                map[key] = path;
            }
            return map;
        }

        /// <summary>清单里的成员路径（不含块名）对应的解析器写法；位切片按父成员换、切片原样接回；找不到为 null（调用方照用原写法）。</summary>
        public static string? Find(IReadOnlyDictionary<string, string> index, string memberPath)
        {
            if (string.IsNullOrWhiteSpace(memberPath)) return null;
            var path = memberPath.Trim();
            if (index.TryGetValue(Key(path), out var hit)) return hit;
            var slice = BitSlice.Match(path);
            return slice.Success && index.TryGetValue(Key(slice.Groups["parent"].Value), out var parent)
                ? parent + "." + slice.Groups["slice"].Value
                : null;
        }

        private static string Key(string path)
        {
            var stripped = StripContainerPlaceholders(path);
            var sb = new StringBuilder(stripped.Length);
            var depth = 0;
            foreach (var ch in stripped)
            {
                if (ch == '[') depth++;
                else if (ch == ']') depth = Math.Max(0, depth - 1);
                if (depth > 0 && char.IsWhiteSpace(ch)) continue;
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
