using System.IO;
using System.IO.Compression;

namespace HsjmTia.Core.Packaging
{
    public static class ZipPackager
    {
        /// <summary>
        /// 把单个 PLC 目录打成 zip，**PLC 目录本身不进包**——
        /// 包内根就是 blocks/ plcTags/ udt/，与 v1 布局一致，旧后端无需改动即可消费。
        /// </summary>
        public static void Create(string plcDir, string zipPath)
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ZipFile.CreateFromDirectory(plcDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
    }
}
