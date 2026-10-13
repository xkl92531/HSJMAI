using System;
using System.IO;
using System.Reflection;
using HsjmTia.Core.Discovery;

namespace HsjmTia.Openness
{
    /// <summary>
    /// 把对 Siemens.Engineering 的程序集请求重定向到本机实际安装的版本。
    ///
    /// 必须在**任何触碰 Openness 类型的方法被 JIT 之前**调用 —— 放在 Main 第一行。
    /// 访问 Openness 的代码都隔离在带 NoInlining 的独立方法里，
    /// 否则 JIT 会在挂钩生效前就尝试加载程序集。
    /// </summary>
    public static class OpennessAssemblyResolver
    {
        private static string _assemblyDirectory;
        private static bool _installed;

        public static void Install(OpennessInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException(nameof(installation));
            if (_installed) return;

            _assemblyDirectory = Path.GetDirectoryName(installation.AssemblyPath);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            _installed = true;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = Path.Combine(_assemblyDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
