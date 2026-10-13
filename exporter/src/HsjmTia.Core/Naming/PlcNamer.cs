using System;
using HsjmTia.Core.Model;

namespace HsjmTia.Core.Naming
{
    public static class PlcNamer
    {
        /// <summary>
        /// 目录名 = 清洗后的 PLC 名 + 消歧后缀。
        /// 后缀无条件追加（哪怕名字已有区分度），保证格式统一、下游不必分支判断。
        /// </summary>
        public static string BuildFolderName(PlcIdentity plc)
        {
            if (plc == null)
            {
                throw new ArgumentNullException(nameof(plc));
            }

            var name = FileNameSanitizer.Sanitize(plc.Name);
            var suffix = string.IsNullOrWhiteSpace(plc.Ip)
                ? "#" + plc.DeviceIndex
                : FileNameSanitizer.Sanitize(plc.Ip);

            return name + "_" + suffix;
        }

        public static string BuildPackageFileName(string tiaProjectName, PlcIdentity plc, DateTime stamp)
        {
            var project = FileNameSanitizer.Sanitize(tiaProjectName);
            return project + "_" + BuildFolderName(plc) + "_" + stamp.ToString("yyyyMMddHHmmss") + ".zip";
        }
    }
}
