using System.Collections.Generic;
using System.IO;

namespace HsjmTia.Core.Export
{
    public static class TagCsvWriter
    {
        /// <summary>列名必须与后端 ParseTagCSV 的 headers.IndexOf 完全一致。</summary>
        public const string Header =
            "Path,TableName,TagName,Address,DataType,IsSafety,Retentive,HmiVisible,HmiWritable,Comment";

        public static void Write(IEnumerable<TagRow> rows, TextWriter writer)
        {
            writer.WriteLine(Header);
            if (rows == null)
            {
                return;
            }

            foreach (var r in rows)
            {
                writer.WriteLine(
                    "\"" + CsvField.Escape(r.Path) + "\"," +
                    "\"" + CsvField.Escape(r.TableName) + "\"," +
                    "\"" + CsvField.Escape(r.TagName) + "\"," +
                    "\"" + CsvField.Escape(r.Address) + "\"," +
                    "\"" + CsvField.Escape(r.DataType) + "\"," +
                    (r.IsSafety ? "True" : "False") + "," +
                    CsvField.Escape(r.Retentive) + "," +
                    (r.HmiVisible ? "True" : "False") + "," +
                    (r.HmiWritable ? "True" : "False") + "," +
                    "\"" + CsvField.Escape(r.Comment) + "\"");
            }
        }
    }
}
