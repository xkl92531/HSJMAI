using System;
using System.Collections.Generic;
using System.Linq;

namespace HsjmTia.Core.Layout
{
    public static class OrphanCleanupPlanner
    {
        /// <summary>
        /// 只有在"本次导出了工程里的全部 PLC"时才清理孤儿目录。
        /// 部分导出时其余目录可能是同事刚导的结果，不能动。
        /// </summary>
        public static IReadOnlyList<string> Plan(
            IEnumerable<string> existingPlcDirNames,
            IEnumerable<string> currentPlcDirNames,
            bool isFullExport)
        {
            if (!isFullExport || existingPlcDirNames == null)
            {
                return new List<string>();
            }

            var keep = new HashSet<string>(
                currentPlcDirNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            return existingPlcDirNames.Where(d => !keep.Contains(d)).ToList();
        }
    }
}
