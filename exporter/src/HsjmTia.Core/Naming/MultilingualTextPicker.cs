using System;
using System.Collections.Generic;
using System.Linq;

namespace HsjmTia.Core.Naming
{
    /// <summary>
    /// 多语言文本取值。旧实现取 Items[0]，工程语言顺序一变就拿到德语或空串，
    /// 而源工程有大量 TagName 是裸地址、业务名只在注释里 —— 注释丢了诊断报告就只剩地址。
    /// </summary>
    public static class MultilingualTextPicker
    {
        private static readonly string[] Preference = { "zh-CN", "zh", "en-GB", "en-US" };

        public static string Pick(IEnumerable<KeyValuePair<string, string>> items)
        {
            if (items == null)
            {
                return string.Empty;
            }

            var list = items.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
            if (list.Count == 0)
            {
                return string.Empty;
            }

            foreach (var culture in Preference)
            {
                var hit = list.FirstOrDefault(x =>
                    string.Equals(x.Key, culture, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(hit.Value))
                {
                    return hit.Value;
                }
            }

            return list[0].Value;
        }
    }
}
