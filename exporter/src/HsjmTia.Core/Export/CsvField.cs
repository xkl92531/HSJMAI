namespace HsjmTia.Core.Export
{
    /// <summary>
    /// 后端 PLC_UploadApp.ParseCsvLine 把每个 '"' 当作 inQuotes 开关翻转，
    /// 不支持 RFC4180 的 "" 转义。因此这里必须把引号替换成撇号，
    /// 并把换行压成空格（后端逐行读取，字段内换行会把一行拆成两行）。
    /// </summary>
    public static class CsvField
    {
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace('"', '\'')
                .Replace("\r\n", " ")
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }
    }
}
