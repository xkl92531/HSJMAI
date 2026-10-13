using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 台账不可用（读取失败、按空快照回退）时的改名风险判定（hardening D14，纯函数）。
    /// 注册规划器（AppLayer IgwRegistrationPlanner）会给重名的块根组 / 同组点名加 _n 后缀，映射只记在台账里；
    /// 没有台账时读链按「清洗后原名」拼别名，对可能被改名的块/点可能读到另一个同名点并报 Good。本类找出这些块/点，
    /// 读链对它们拒读。判定口径与规划器一致（清洗规则 IgwTagNaming、大小写不敏感、叶子行规则同 IgwLeafRowFilter），
    /// 宁可多拒（如同时存在 Motor 与 Motor_2 时两块都拒读），只在台账不可用期间生效。
    /// </summary>
    public static class IgwLedgerOutageGuard
    {
        /// <summary>清洗后块名与 IO 根组冲突时规划器改用的根组名（与 IgwRegistrationPlanner.IoClashBlockRoot 同值，单测断言）</summary>
        public const string IoClashRoot = "IO_DB";

        /// <summary>名称达到此长度时加后缀会截断原名，截断后可能与其它名称相撞 → 一律视为有风险</summary>
        public const int SuffixRiskLength = 120;

        /// <summary>IO 点的重名判定键：清洗后表名 + 分隔符 + 清洗后点名；任一清洗为空 → null（该点本身不可注册）</summary>
        public static string? IoKey(string? tableName, string? tagName)
        {
            var table = IgwTagNaming.SanitizeName(tableName ?? string.Empty);
            var name = IgwTagNaming.SanitizeName(tagName ?? string.Empty);
            return table == null || name == null ? null : table + IgwTagNaming.KeySeparator + name;
        }

        /// <summary>IO 点重名分组：键（大小写不敏感）→ 该组全部原始点（≥2 个）。与规划器 IO 组路径 [IO, 清洗后表名] + 清洗后点名同口径。</summary>
        public static Dictionary<string, List<T>> FindCollidingIoGroups<T>(IEnumerable<T> tags, Func<T, string?> tableName, Func<T, string?> tagName)
        {
            var groups = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags ?? Enumerable.Empty<T>())
            {
                var key = IoKey(tableName(tag), tagName(tag));
                if (key == null)
                {
                    continue;
                }
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<T>();
                    groups[key] = list;
                }
                list.Add(tag);
            }
            foreach (var key in groups.Where(kv => kv.Value.Count < 2).Select(kv => kv.Key).ToList())
            {
                groups.Remove(key);
            }
            return groups;
        }

        /// <summary>根组可能不等于清洗后块名的存活 DB 块（大小写不敏感集合）</summary>
        public static IReadOnlySet<string> FindAmbiguousBlocks(IEnumerable<string?> liveDbBlocks)
        {
            var sanitizedByBlock = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in liveDbBlocks ?? Enumerable.Empty<string?>())
            {
                if (!string.IsNullOrWhiteSpace(block))
                {
                    sanitizedByBlock.TryAdd(block, IgwTagNaming.SanitizeName(block));
                }
            }
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sanitized in sanitizedByBlock.Values)
            {
                if (sanitized != null)
                {
                    counts[sanitized] = counts.TryGetValue(sanitized, out var c) ? c + 1 : 1;
                }
            }
            var suffixBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in counts.Keys)
            {
                if (TryStripNumericSuffix(name, out var baseName))
                {
                    suffixBases.Add(baseName);
                }
            }
            var ioExists = counts.ContainsKey(IgwTagNaming.IoRootGroup);

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (block, sanitized) in sanitizedByBlock)
            {
                if (sanitized == null
                    || sanitized.Length >= SuffixRiskLength
                    || counts[sanitized] > 1
                    || string.Equals(sanitized, IgwTagNaming.IoRootGroup, StringComparison.OrdinalIgnoreCase)
                    || suffixBases.Contains(sanitized))
                {
                    result.Add(block);
                    continue;
                }
                var hasBase = TryStripNumericSuffix(sanitized, out var ownBase);
                if (hasBase && counts.ContainsKey(ownBase))
                {
                    result.Add(block);
                    continue;
                }
                if (ioExists && (string.Equals(sanitized, IoClashRoot, StringComparison.OrdinalIgnoreCase)
                                 || (hasBase && string.Equals(ownBase, IoClashRoot, StringComparison.OrdinalIgnoreCase))))
                {
                    result.Add(block);
                }
            }
            return result;
        }

        /// <summary>块内可能被改名的变量路径（Ordinal 集合）：叶子行中清洗后（组路径, 点名）大小写不敏感重复者，以及无法安全命名者</summary>
        public static IReadOnlySet<string> FindCollidingPaths(IEnumerable<string?> variablePaths)
        {
            var paths = (variablePaths ?? Enumerable.Empty<string?>())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var containers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                for (var i = 1; i < path.Length; i++)
                {
                    if (path[i] == '.' || path[i] == '[')
                    {
                        containers.Add(path.Substring(0, i));
                    }
                }
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (containers.Contains(path))
                {
                    continue;
                }
                var segments = IgwTagNaming.SplitSegments(IgwTagNaming.NormalizeVariablePath(path));
                if (segments.Count == 0 || segments[segments.Count - 1].Length >= SuffixRiskLength)
                {
                    result.Add(path);
                    continue;
                }
                var key = string.Join(IgwTagNaming.KeySeparator, segments);
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    byKey[key] = list;
                }
                list.Add(path);
            }
            foreach (var list in byKey.Values.Where(l => l.Count > 1))
            {
                result.UnionWith(list);
            }
            return result;
        }

        /// <summary>name = base + "_" + n（n ≥ 2，纯 ASCII 数字）→ true 并输出 base</summary>
        private static bool TryStripNumericSuffix(string name, out string baseName)
        {
            baseName = string.Empty;
            var index = name.LastIndexOf('_');
            if (index <= 0 || index == name.Length - 1)
            {
                return false;
            }
            for (var i = index + 1; i < name.Length; i++)
            {
                if (name[i] < '0' || name[i] > '9')
                {
                    return false;
                }
            }
            if (!int.TryParse(name.AsSpan(index + 1), out var n) || n < 2)
            {
                return false;
            }
            baseName = name.Substring(0, index);
            return true;
        }
    }
}
