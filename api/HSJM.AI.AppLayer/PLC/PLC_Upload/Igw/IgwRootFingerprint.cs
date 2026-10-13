using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 根组内容指纹（2026-09-22，用户拍板「定义变了就重发」）。
    ///
    /// 用途：注册时先比指纹——一致就**整根跳过**（一个点都不发），不一致才发该根的 items
    /// 让 IGW 走 upsert 更新。**任何情况下都不发 replaceGroups**：上传解析这条链路只负责新增，
    /// PLC 里删掉的东西留在 IGW 上不管它（用户明确决定）。
    ///
    /// 动机：现场实测一次全量注册 29 万点的设备要 519 秒，基本都耗在「整组删光再重写」上。
    /// 上传是高频动作，不该每次都付这个代价。
    ///
    /// ⚠️ 指纹必须覆盖 IGW 那条 UPDATE 会写的**每一个**字段：
    /// <code>
    /// UPDATE tags SET group_id,name,canonical_path,address,data_type,access,scan_rate_ms,
    ///                 string_length,eng_unit,description...
    /// </code>
    /// 漏掉任何一个，那个字段以后就「改了不生效」——2026-09-22 的工步块提速就**只改了
    /// scan_rate_ms**（点名地址类型一个没动），指纹若不含它，那次改动会被判成"没变"而永远跳过。
    /// 将来往 <see cref="IgwBulkItem"/> 加字段时，如果它会被 IGW 写进 tags 表，**必须同步加进这里**。
    /// </summary>
    public static class IgwRootFingerprint
    {
        /// <summary>分隔符用控制字符，避免点名/注释里的普通标点造成歧义拼接。</summary>
        private const char FieldSeparator = '';
        private const char ItemSeparator = '';

        /// <summary>
        /// 算指纹。与点的先后次序无关——规划器排序变了不代表 IGW 上的内容变了，
        /// 所以先把每个点序列化再排序。空集合有稳定指纹（表示"这个根没有点"）。
        /// </summary>
        public static string Compute(IEnumerable<IgwBulkItem> items)
        {
            var lines = (items ?? Enumerable.Empty<IgwBulkItem>())
                .Where(item => item != null)
                .Select(Serialize)
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            var payload = string.Join(ItemSeparator, lines);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string Serialize(IgwBulkItem item)
        {
            var sb = new StringBuilder();
            sb.Append(item.GroupPath == null ? string.Empty : string.Join("/", item.GroupPath));
            sb.Append(FieldSeparator).Append(item.Name);
            sb.Append(FieldSeparator).Append(item.Address);
            sb.Append(FieldSeparator).Append(item.DataType);
            sb.Append(FieldSeparator).Append(item.ScanRateMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(FieldSeparator).Append(item.StringLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            sb.Append(FieldSeparator).Append(item.Description ?? string.Empty);
            return sb.ToString();
        }
    }
}
