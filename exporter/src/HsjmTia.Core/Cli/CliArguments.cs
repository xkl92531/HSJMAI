using System;
using System.Collections.Generic;
using System.IO;

namespace HsjmTia.Core.Cli
{
    public class CliArguments
    {
        public string ProjectPath { get; set; }
        public string OutputRoot { get; set; }
        public bool Upload { get; set; }
        public string Server { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public bool FullProjectUpdate { get; set; }

        /// <summary>PLC IP → 后端 projectId。键用 IP，因为 PLC 名不可靠。</summary>
        public Dictionary<string, long> PlcTargets { get; } =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static CliArguments Parse(string[] args)
        {
            var a = new CliArguments
            {
                OutputRoot = Path.Combine(Directory.GetCurrentDirectory(), "xml")
            };

            for (var i = 0; i < args.Length; i++)
            {
                var token = args[i];
                if (token == "export")
                {
                    continue;
                }

                switch (token)
                {
                    case "--project": a.ProjectPath = Next(args, ref i); break;
                    case "--out": a.OutputRoot = Next(args, ref i); break;
                    case "--server": a.Server = Next(args, ref i); break;
                    case "--user": a.User = Next(args, ref i); break;
                    case "--password": a.Password = Next(args, ref i); break;
                    case "--upload": a.Upload = true; break;
                    case "--full": a.FullProjectUpdate = true; break;
                    case "--plc": AddPlcTarget(a, Next(args, ref i)); break;
                    default:
                        throw new FormatException("无法识别的参数：" + token);
                }
            }

            return a;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new FormatException("参数 " + args[i] + " 缺少取值");
            }
            return args[++i];
        }

        /// <summary>格式 PLC名@IP=projectId。名字只是给人看的，匹配一律按 IP。</summary>
        private static void AddPlcTarget(CliArguments a, string spec)
        {
            var at = spec.IndexOf('@');
            var eq = spec.IndexOf('=');
            if (at < 0 || eq < 0 || eq < at)
            {
                throw new FormatException("--plc 格式应为 PLC名@IP=projectId，实际是：" + spec);
            }

            var ip = spec.Substring(at + 1, eq - at - 1).Trim();
            if (!long.TryParse(spec.Substring(eq + 1).Trim(), out var projectId))
            {
                throw new FormatException("--plc 的 projectId 不是数字：" + spec);
            }

            a.PlcTargets[ip] = projectId;
        }
    }
}
