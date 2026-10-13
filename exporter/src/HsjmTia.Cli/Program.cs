using System;
using System.Runtime.CompilerServices;
using HsjmTia.Core.Cli;
using HsjmTia.Core.Discovery;
using HsjmTia.Openness;

namespace HsjmTia.Cli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            CliArguments parsed;
            try
            {
                parsed = CliArguments.Parse(args);
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(
                    "用法: HsjmTia.Cli export --project <工程文件> [--out <目录>] " +
                    "[--upload --server <地址> --user <账号> --password <密码> --plc PLC名@IP=projectId ...]");
                return 2;
            }

            // 必须在任何触碰 Openness 类型的方法被 JIT 之前完成
            var installation = OpennessProbe.Select(
                OpennessProbe.FindInstallations(new WindowsRegistryReader()), parsed.ProjectPath, out var error);
            if (installation == null)
            {
                Console.Error.WriteLine(error);
                return 3;
            }
            OpennessAssemblyResolver.Install(installation);

            try
            {
                return RunPipeline(parsed, installation);
            }
            catch (Exception ex)
            {
                // 兜底：导出/上传管线里任何没被专门捕获的异常（COM 抖动、工程文件损坏等）
                // 都不该在操作员面前甩出一屏原始堆栈——打印一句人话、给一个区别于其它
                // 退出码的非零值，让无人值守脚本能识别出"这里出了没预料到的问题"。
                // 注意：这个 catch 块本身不引用任何 Siemens 类型，只依赖 System.Exception。
                Console.Error.WriteLine($"未预期的错误：{ex.Message}");
                return 5;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunPipeline(CliArguments args, OpennessInstallation installation)
            => ExportPipeline.Run(args, installation);
    }
}
