using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Model;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace HsjmTia.Openness
{
    public class CpuHandle
    {
        public PlcIdentity Identity { get; set; }
        public PlcSoftware Software { get; set; }
    }

    public class TiaSession : IDisposable
    {
        private TiaPortal _portal;
        private Project _project;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static TiaSession Connect()
        {
            var session = new TiaSession();
            var running = TiaPortal.GetProcesses();
            session._portal = running.Any()
                ? running.First().Attach()
                : new TiaPortal(TiaPortalMode.WithoutUserInterface);
            return session;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OpenProject(string projectPath)
        {
            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException("工程文件不存在", projectPath);
            }
            _project = _portal.Projects.Open(new FileInfo(projectPath));
        }

        public string ProjectName => _project?.Name;

        /// <summary>
        /// 递归枚举全部 CPU。旧实现只看 device.DeviceItems 一层，
        /// 机架式站点的 CPU 在更深层，会整台漏掉。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IReadOnlyList<CpuHandle> EnumerateCpus(IList<ExportGap> gaps)
        {
            var result = new List<CpuHandle>();
            var index = 0;

            foreach (Device device in _project.Devices)
            {
                foreach (var item in Flatten(device.DeviceItems))
                {
                    if (item.Classification != DeviceItemClassifications.CPU)
                    {
                        continue;
                    }

                    var container = item.GetService<SoftwareContainer>();
                    if (!(container?.Software is PlcSoftware software))
                    {
                        continue;
                    }

                    index++;
                    result.Add(new CpuHandle
                    {
                        Software = software,
                        Identity = new PlcIdentity
                        {
                            Name = item.Name,
                            Ip = ReadIp(item, gaps),
                            OrderNumber = ReadAttribute(item, "OrderNumber"),
                            DeviceIndex = index
                        }
                    });
                }
            }

            return result;
        }

        private static IEnumerable<DeviceItem> Flatten(DeviceItemComposition items)
        {
            foreach (DeviceItem item in items)
            {
                yield return item;
                foreach (var child in Flatten(item.DeviceItems))
                {
                    yield return child;
                }
            }
        }

        /// <summary>读不到就返回 null 并记缺口 —— 绝不返回 192.168.1.100 这类假值。</summary>
        private static string ReadIp(DeviceItem cpu, IList<ExportGap> gaps)
        {
            foreach (var item in Flatten(cpu.DeviceItems))
            {
                var network = item.GetService<NetworkInterface>();
                var node = network?.Nodes?.FirstOrDefault();
                var ip = node?.GetAttribute("Address")?.ToString();
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    return ip;
                }
            }

            gaps?.Add(ExportGap.Create(GapKinds.PlcIpMissing, cpu.Name,
                "该 PLC 未组态 IP，上传时无法自动校验目标项目"));
            return null;
        }

        private static string ReadAttribute(DeviceItem item, string name)
        {
            try
            {
                return item.GetAttribute(name)?.ToString();
            }
            catch (EngineeringException)
            {
                // 该设备不支持此属性，属正常情况
                return null;
            }
        }

        // Dispose 同样触碰 Siemens 类型（Close/Dispose），与其它 SDK 入口一致地禁止内联，
        // 避免被内联进尚未触发 OpennessAssemblyResolver.Install 的调用方后提前触发程序集加载。
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            // try/finally 而不是两条独立 try：_project.Close() 一旦抛出任何异常（不只是
            // EngineeringException），_portal.Dispose() 也必须执行——否则一个无界面的 TIA
            // 进程会残留在后台，比"工程关闭失败"本身更糟。
            try
            {
                try
                {
                    _project?.Close();
                }
                catch (EngineeringException)
                {
                    // 工程已被关闭、连接已断开等场景下 Close 本身失败是正常情况：
                    // 目标状态（工程不再占用）已经达成，无需向上抛出打断整个 Dispose。
                }
            }
            finally
            {
                try
                {
                    _portal?.Dispose();
                }
                catch (EngineeringException)
                {
                    // Portal 所在进程可能已经因为上面 Close 失败的同一原因失效（如宿主已退出），
                    // 这里再抛出也无法挽救，忽略即可——Dispose 不应该让调用方处理清理阶段的异常。
                }
            }
        }
    }
}
