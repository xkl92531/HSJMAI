using System.Collections.Generic;
using System.IO;
using HsjmTia.Core.Export;
using Xunit;

namespace HsjmTia.Core.Tests.Export
{
    public class TagCsvWriterTests
    {
        private static string Render(params TagRow[] rows)
        {
            var sw = new StringWriter();
            TagCsvWriter.Write(rows, sw);
            return sw.ToString().Replace("\r\n", "\n");
        }

        private static TagRow Row(string name, string comment = "")
            => new TagRow
            {
                Path = "Root", TableName = "默认变量表", TagName = name, Address = "%I1.0",
                DataType = "Bool", IsSafety = false, Retentive = "False",
                HmiVisible = true, HmiWritable = true, Comment = comment
            };

        [Fact]
        public void Write_EmitsTheExactHeaderTheBackendLooksFor()
            => Assert.StartsWith(
                "Path,TableName,TagName,Address,DataType,IsSafety,Retentive,HmiVisible,HmiWritable,Comment",
                Render());

        [Fact]
        public void Write_ReplacesQuotesWithApostrophes_BecauseBackendParserTogglesOnEveryQuote()
        {
            // 后端 ParseCsvLine 把每个 " 当开关翻转，不认 RFC4180 的 ""
            var line = Render(Row("T", "说\"引号\""));
            Assert.DoesNotContain("\"\"", line.Substring(line.IndexOf('\n')));
            Assert.Contains("说'引号'", line);
        }

        [Fact]
        public void Write_KeepsCommasInsideQuotedFields()
            => Assert.Contains("\"OP010, 上料\"", Render(Row("T", "OP010, 上料")));

        [Fact]
        public void Write_FlattensNewlines_BecauseBackendReadsLineByLine()
        {
            var body = Render(Row("T", "第一行\r\n第二行"));
            Assert.Equal(2, body.TrimEnd('\n').Split('\n').Length);
            Assert.Contains("第一行 第二行", body);
        }

        [Fact]
        public void Write_RendersBooleansAsTrueFalse()
            => Assert.Contains(",False,", Render(Row("T")));

        [Fact]
        public void Write_EmitsOneLinePerTag()
            => Assert.Equal(3, Render(Row("A"), Row("B")).TrimEnd('\n').Split('\n').Length);

        [Fact]
        public void Escape_TurnsNullIntoEmpty()
            => Assert.Equal(string.Empty, CsvField.Escape(null));
    }
}
