using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HsjmTia.Core.Discovery
{
    public static class OpennessProbe
    {
        public const string RegistryRoot = @"SOFTWARE\Siemens\Automation\Openness";

        private static readonly Dictionary<string, string> ExtensionToVersion =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".ap15", "V15.1" },
                { ".ap16", "V16" },
                { ".ap17", "V17" },
                { ".ap18", "V18" },
                { ".ap19", "V19" },
            };

        public static IReadOnlyList<OpennessInstallation> FindInstallations(IRegistryReader registry)
        {
            var result = new List<OpennessInstallation>();
            foreach (var tiaVersion in registry.GetSubKeyNames(RegistryRoot))
            {
                var apiRoot = RegistryRoot + "\\" + tiaVersion + "\\PublicAPI";
                foreach (var apiVersion in registry.GetSubKeyNames(apiRoot))
                {
                    var path = registry.GetValue(apiRoot + "\\" + apiVersion, "Siemens.Engineering");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        result.Add(new OpennessInstallation
                        {
                            TiaVersion = tiaVersion,
                            ApiVersion = apiVersion,
                            AssemblyPath = path
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 工程文件扩展名直接决定需要哪个版本。不按"最高版本"猜：
        /// 用高版本 TIA 打开低版本工程会触发升级流程并改写工程文件，现场不可接受。
        /// </summary>
        public static string RequiredVersionForProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return null;
            }
            var ext = Path.GetExtension(projectPath);
            return ExtensionToVersion.TryGetValue(ext ?? string.Empty, out var v) ? v : null;
        }

        public static OpennessInstallation Select(
            IReadOnlyList<OpennessInstallation> installations, string projectPath, out string error)
        {
            error = null;

            if (installations == null || installations.Count == 0)
            {
                error = "本机未检测到任何 TIA Openness 安装。请确认已安装 TIA Portal，且当前用户属于 \"Siemens TIA Openness\" 本地用户组。";
                return null;
            }

            var required = RequiredVersionForProject(projectPath);
            if (required == null)
            {
                error = "无法从工程文件扩展名判断 TIA 版本：" + projectPath;
                return null;
            }

            var hit = installations.FirstOrDefault(i =>
                string.Equals(i.ApiVersion, required, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                return hit;
            }

            var installed = string.Join("、", installations.Select(i => i.ApiVersion));
            error = $"该工程需要 TIA {required}，但本机只装了 {installed}。请安装对应版本，或用 {required} 打开该工程。";
            return null;
        }
    }
}
