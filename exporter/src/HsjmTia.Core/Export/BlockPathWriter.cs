using System.Collections.Generic;
using System.IO;

namespace HsjmTia.Core.Export
{
    /// <summary>
    /// 一次性写全量。旧实现用 File.AppendText 逐块追加，
    /// 导致每导一次就把全量再追加一遍。
    /// </summary>
    public static class BlockPathWriter
    {
        public static void Write(IEnumerable<BlockPathEntry> entries, TextWriter writer)
        {
            if (entries == null)
            {
                return;
            }

            foreach (var e in entries)
            {
                writer.WriteLine((e.FolderPath ?? string.Empty) + "," + (e.BlockName ?? string.Empty));
            }
        }
    }
}
