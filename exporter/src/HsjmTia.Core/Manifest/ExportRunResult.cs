using System.Collections.Generic;
using HsjmTia.Core.Model;

namespace HsjmTia.Core.Manifest
{
    /// <summary>
    /// 一次导出运行（工程内全部 PLC）的结果。CLI 用它打印报告并驱动无人值守上传；
    /// GUI 用它直接绑定界面（PLC 列表 / 缺口清单 / 本地解析计数 / 上传用的包路径）。
    /// </summary>
    public class ExportRunResult
    {
        public string ProjectName { get; set; }
        public IReadOnlyList<PlcIdentity> Plcs { get; set; } = new List<PlcIdentity>();
        public IReadOnlyList<ExportGap> Gaps { get; set; } = new List<ExportGap>();
        public int AnalyzeTotal { get; set; }
        public int AnalyzeSucceeded { get; set; }

        /// <summary>
        /// 通过打包校验、可以上传的产物（PLC 身份 + zip 路径）。
        /// 校验未通过的包仍会落盘（方便工程师排查）但不会出现在这里。
        /// </summary>
        public IReadOnlyList<ExportPackage> Packages { get; set; } = new List<ExportPackage>();
    }

    /// <summary>一个 PLC 导出产物的落盘位置，供上传步骤使用。</summary>
    public class ExportPackage
    {
        public PlcIdentity Plc { get; set; }
        public string ZipPath { get; set; }

        /// <summary>
        /// 这个 PLC 自己的缺口清单（ExportOrchestrator 按 CPU 收集的那一份，
        /// 而不是 ExportRunResult.Gaps 里跨全部 PLC 拍平后的列表）。
        /// 上传前判断"全项目更新是否安全"（见 HsjmTia.Core.Upload.FullUpdateSafety）
        /// 必须落在单个 PLC 的缺口上，不能被别的 PLC 的缺口污染或者反过来漏看。
        /// </summary>
        public IReadOnlyList<ExportGap> Gaps { get; set; } = new List<ExportGap>();
    }
}
