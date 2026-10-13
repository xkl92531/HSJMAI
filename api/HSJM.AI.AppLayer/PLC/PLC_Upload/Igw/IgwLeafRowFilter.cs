using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// DB 接口行叶子过滤（纯函数）。接口表 ai_plc_blockinterface 的 ParentId / 结构标记在实际数据里不可用，
    /// 因此按路径判定容器行：同一块内，若某行路径 P 是另一行路径的前缀、且前缀后紧跟 '.' 或 '['，
    /// 则 P 是结构 / UDT / 数组容器行，丢弃（与 tools/igw_bulk_e2e/make_fixtures.py 的 db_rows_of 同规则，
    /// 并额外覆盖中间段数组下标 `A.X[3].B` 的容器 `A.X`）。空路径行同样丢弃。输出保持输入顺序。
    /// </summary>
    public static class IgwLeafRowFilter
    {
        /// <summary>块名与路径拼复合键的分隔符：复用 IgwTagNaming.KeySeparator（不会出现在块名 / 变量路径中的控制字符）</summary>
        private const char KeySeparator = IgwTagNaming.KeySeparator;

        public static List<T> FilterLeaves<T>(IEnumerable<T> rows, Func<T, string> blockOf, Func<T, string> pathOf)
        {
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentNullException.ThrowIfNull(blockOf);
            ArgumentNullException.ThrowIfNull(pathOf);

            var list = rows as IList<T> ?? new List<T>(rows);
            // 容器键：块名 + KeySeparator + 路径前缀（路径在 '.' / '[' 处截断得到的全部前缀）
            var containers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in list)
            {
                var path = pathOf(row);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                var prefixKey = (blockOf(row) ?? string.Empty) + KeySeparator;
                for (var i = 1; i < path.Length; i++)
                {
                    if (path[i] == '.' || path[i] == '[')
                    {
                        containers.Add(prefixKey + path.Substring(0, i));
                    }
                }
            }

            var result = new List<T>(list.Count);
            foreach (var row in list)
            {
                var path = pathOf(row);
                if (string.IsNullOrEmpty(path) || containers.Contains((blockOf(row) ?? string.Empty) + KeySeparator + path))
                {
                    continue;
                }
                result.Add(row);
            }
            return result;
        }
    }
}
