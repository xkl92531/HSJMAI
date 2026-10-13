using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HsjmTia.Core.Export;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Naming;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;

namespace HsjmTia.Openness.Exporters
{
    public static class TagExporter
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IReadOnlyList<TagRow> Collect(PlcSoftware software, IList<ExportGap> gaps)
        {
            var rows = new List<TagRow>();
            Traverse(software.TagTableGroup, "Root", rows, gaps);
            return rows;
        }

        private static void Traverse(
            PlcTagTableGroup group, string path, List<TagRow> rows, IList<ExportGap> gaps)
        {
            foreach (PlcTagTable table in group.TagTables)
            {
                foreach (PlcTag tag in table.Tags)
                {
                    rows.Add(new TagRow
                    {
                        Path = path,
                        TableName = table.Name,
                        TagName = tag.Name,
                        Address = tag.LogicalAddress,
                        DataType = tag.DataTypeName,
                        IsSafety = tag.IsSafety,
                        Retentive = ReadRetentive(tag, gaps),
                        HmiVisible = tag.ExternalVisible,
                        HmiWritable = tag.ExternalWritable,
                        Comment = PickComment(tag)
                    });
                }
            }

            foreach (PlcTagTableGroup sub in group.Groups)
            {
                Traverse(sub, path + "/" + sub.Name, rows, gaps);
            }
        }

        /// <summary>
        /// Retentive 仅 M 区支持，I/Q 区会抛异常 —— 这是正常情况，不记缺口。
        ///
        /// TagCsvWriter 把 Retentive 列**不加引号**直接写入 CSV（其余字符串列都带引号转义），
        /// 这是既定格式不能改；所以这里必须把结果收敛成精确的 "True"/"False" 两个值之一 ——
        /// 任何带逗号的原始值（例如属性 ToString() 输出异常）落进 CSV 都会把该行后续列
        /// （HmiVisible/HmiWritable/Comment）整体错位，后端按位置解析会悄悄读到脏数据。
        /// 因此绝不把 GetAttribute 的原始 ToString() 透传出去。
        /// </summary>
        private static string ReadRetentive(PlcTag tag, IList<ExportGap> gaps)
        {
            try
            {
                var value = tag.GetAttribute("Retentive");
                if (value is bool boolValue)
                {
                    return boolValue ? "True" : "False";
                }

                if (value != null && bool.TryParse(value.ToString(), out var parsed))
                {
                    return parsed ? "True" : "False";
                }

                // 属性有值但无法识别为布尔——记录缺口，带上实际值与其运行时类型方便排查
                if (value != null)
                {
                    var detail = $"Retentive 属性值无法识别：{value}（类型：{value.GetType().Name}）";
                    gaps.Add(new ExportGap
                    {
                        Target = tag.Name,
                        Kind = GapKinds.ExportFailed,
                        Detail = detail
                    });
                }

                return "False";
            }
            catch (EngineeringException)
            {
                return "False";
            }
        }

        private static string PickComment(PlcTag tag)
        {
            if (tag.Comment == null || tag.Comment.Items == null)
            {
                return string.Empty;
            }

            var items = tag.Comment.Items.Select(i =>
                new KeyValuePair<string, string>(i.Language.Culture.Name, i.Text));
            return MultilingualTextPicker.Pick(items);
        }
    }
}
