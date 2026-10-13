using System;
using System.Collections.Generic;
using HsjmTia.Core.Model;

namespace HsjmTia.Core.Manifest
{
    public class ExportCounts
    {
        public int Blocks { get; set; }
        public int Tags { get; set; }
        public int Udts { get; set; }
    }

    public class ExportManifest
    {
        public int SchemaVersion { get; set; } = 2;
        public DateTimeOffset ExportedAt { get; set; }

        public string ToolVersion { get; set; }
        /// <summary>内嵌 plc-analyzer 脚本的来源 git commit，用于判断本地解析器是否落后于后端。</summary>
        public string AnalyzerCommit { get; set; }

        public string TiaProjectName { get; set; }
        public string TiaProjectPath { get; set; }
        public string TiaVersion { get; set; }
        public string Machine { get; set; }

        public PlcIdentity Plc { get; set; }
        public ExportCounts Counts { get; set; } = new ExportCounts();
        public List<ExportGap> Gaps { get; set; } = new List<ExportGap>();

        public bool HasGaps => Gaps != null && Gaps.Count > 0;
    }
}
