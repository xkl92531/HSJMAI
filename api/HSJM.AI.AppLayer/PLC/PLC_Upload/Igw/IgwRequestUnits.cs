using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 批量请求的「根组单元」（R-1，2026-09-20，纯函数）：同一请求内按设备根下第一级组（块根组 / IO）分组。
    /// 规划器保证一个根组的 replaceGroups 与其点位落在同一请求的首片，取子集时二者同进同出，替换语义不变；
    /// 不同根组的点名去重互不影响（组路径首段不同），拆开发送与合并发送写入结果一致。
    /// 单元名比较大小写不敏感（IGW 组名查找不区分大小写）。
    /// </summary>
    public static class IgwRequestUnits
    {
        /// <summary>组路径为空（设备根层）的单元名</summary>
        public const string DeviceRoot = "";

        public static string UnitOf(IReadOnlyList<string> path) =>
            path is { Count: > 0 } && path[0] != null ? path[0] : DeviceRoot;

        /// <summary>请求涉及的单元（先 replaceGroups 后 items，按首次出现去重）</summary>
        public static IReadOnlyList<string> Units(IgwBulkRequest request)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var units = new List<string>();
            foreach (var group in request?.ReplaceGroups ?? new List<List<string>>())
            {
                var unit = UnitOf(group);
                if (seen.Add(unit))
                {
                    units.Add(unit);
                }
            }
            foreach (var item in request?.Items ?? new List<IgwBulkItem>())
            {
                var unit = UnitOf(item.GroupPath);
                if (seen.Add(unit))
                {
                    units.Add(unit);
                }
            }
            return units;
        }

        /// <summary>只保留指定单元的 replaceGroups 与点位（通道/设备定义沿用原请求；原请求不被修改）</summary>
        public static IgwBulkRequest Subset(IgwBulkRequest request, IEnumerable<string> units)
        {
            var keep = new HashSet<string>(units ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return new IgwBulkRequest
            {
                Channel = request.Channel,
                Device = request.Device,
                ReplaceGroups = request.ReplaceGroups.Where(g => keep.Contains(UnitOf(g))).Select(g => new List<string>(g)).ToList(),
                Items = request.Items.Where(i => keep.Contains(UnitOf(i.GroupPath))).ToList(),
            };
        }

        /// <summary>IGW 逐项错误下标 → 出错单元（去重、按下标出现顺序）；无下标或任一下标越界 → null（无法定位）</summary>
        public static IReadOnlyList<string> UnitsAt(IgwBulkRequest request, IReadOnlyCollection<int> itemIndexes)
        {
            if (itemIndexes == null || itemIndexes.Count == 0)
            {
                return null;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var units = new List<string>();
            foreach (var index in itemIndexes)
            {
                if (index < 0 || index >= request.Items.Count)
                {
                    return null;
                }
                var unit = UnitOf(request.Items[index].GroupPath);
                if (seen.Add(unit))
                {
                    units.Add(unit);
                }
            }
            return units;
        }
    }
}
