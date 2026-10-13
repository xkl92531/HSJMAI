using System.Collections.Generic;
using System.Linq;
using HsjmTia.Core.Manifest;

namespace HsjmTia.Core.Upload
{
    /// <summary>
    /// 判断"这个 PLC 能不能安全地做全项目更新（fullProjectUpdate=true）"。
    ///
    /// 后端把"数据库里有、zip 里没有"一律当成"块被删除"处理：整块硬删（含逻辑单元/接口/变量
    /// 访问/块调用/DB-FB 绑定/源文件），约束规则/信号软删，GRAPH 步位标签软删，项目全部 IO
    /// 标签的 ConstraintStatus 清零，IGW 台账对应根清空。它自己的防护只在缺失块 > 10 个且占比
    /// > 0.8 时才拦截——OEM 产线上几个知产保护的供应商 FB、或几个导出失败的块，都轻松滑过这道
    /// 门槛，却足以让后端把它们当成"已删除"把历史数据全部清空。
    ///
    /// 导出端自己知道哪些块是"故意不在 zip 里"的：block.IsKnowHowProtected（无法导出）、
    /// block.Export 抛出的 EngineeringException（导出失败）——两者都落进 GapKinds.BlockMissing。
    /// 任何一个 PLC 只要有这类缺口，就不该无人值守地做全量更新：调用方（CLI/GUI）必须拒绝或
    /// 要求人工确认。
    ///
    /// 不要在这里加回 GapKinds.ExportFailed：那个 Kind 被 6 个生产点共用，其中只有块 XML
    /// 导出失败那一处代表"块缺席 zip"（该处已改发 BlockMissing）；其余 4 处（.scl/.awl 源码
    /// 导出失败——此时块 XML 已经导出成功、entries 里已经有这个块了；Tag 的 Retentive 属性
    /// 无法识别——Target 压根是个 Tag 名不是块名；UDT 导出失败/重名——Target 是类型名）都跟
    /// "块是否进了 zip"无关。把它们也判成不安全只会让一个不相关的坏点永久拒绝该 PLC 的
    /// --full，还会在 CLI/GUI 上打出"以下 N 个块本次未包含在导出里"这种指名道姓却不成立的话。
    /// </summary>
    public static class FullUpdateSafety
    {
        private static readonly HashSet<string> UnsafeKinds = new HashSet<string>
        {
            GapKinds.BlockMissing,
        };

        /// <summary>
        /// 返回让全项目更新不安全的那些缺口（保留 Kind/Target/Detail，供调用方直接列出块名）。
        /// gaps 为 null 时按"没有缺口"处理，返回空列表。
        /// </summary>
        public static IReadOnlyList<ExportGap> FindUnsafeGaps(IEnumerable<ExportGap> gaps)
        {
            if (gaps == null)
            {
                return new List<ExportGap>();
            }

            return gaps.Where(g => g != null && UnsafeKinds.Contains(g.Kind)).ToList();
        }

        /// <summary>FindUnsafeGaps(gaps).Count > 0 的简写，调用方只想要一个布尔判断时用。</summary>
        public static bool IsUnsafe(IEnumerable<ExportGap> gaps) => FindUnsafeGaps(gaps).Count > 0;
    }
}
