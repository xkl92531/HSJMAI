using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 锚点发现汇总统计（Phase 1 交付物 AnchorDiscoverySummary）。
    /// 随 AnchorDiscoveryResultDto 一起返回，用于快速评估发现质量。
    /// </summary>
    public class AnchorDiscoverySummary
    {
        // ── 锚点数量 ──────────────────────────────────────────────

        /// <summary>物理 Q 点总数</summary>
        public int TotalPhysical { get; set; }

        /// <summary>虚拟 Q 点总数（含复核队列）</summary>
        public int TotalVirtual { get; set; }

        /// <summary>握手位总数</summary>
        public int TotalHandshake { get; set; }

        /// <summary>全部锚点总数</summary>
        public int TotalAnchors => TotalPhysical + TotalVirtual + TotalHandshake;

        /// <summary>核心硬件 Q 点数</summary>
        public int CorePhysicalCount { get; set; }

        /// <summary>核心虚拟 Q 点数</summary>
        public int CoreVirtualCount { get; set; }

        // ── 状态分布 ──────────────────────────────────────────────

        /// <summary>Confirmed 状态锚点数</summary>
        public int ConfirmedCount { get; set; }

        /// <summary>进入复核队列（Review）的锚点数</summary>
        public int ReviewQueueSize { get; set; }

        /// <summary>被排除（Rejected）的锚点数</summary>
        public int RejectedCount { get; set; }

        // ── LLM ──────────────────────────────────────────────────

        /// <summary>经 LLM 分类的虚拟 Q 点候选数</summary>
        public int LlmClassifiedCount { get; set; }

        // ── 工位覆盖 ──────────────────────────────────────────────

        /// <summary>各工位已确认锚点数  key=StationCode，value=锚点数</summary>
        public Dictionary<string, int> StationAnchorCount { get; set; } = new Dictionary<string, int>();

        /// <summary>有效工位覆盖数</summary>
        public int StationCoverage => StationAnchorCount.Count;

        /// <summary>工位桶数量（工位地图层输出）</summary>
        public int StationBucketCount { get; set; }

        /// <summary>工位冲突数量</summary>
        public int StationConflictCount { get; set; }

        /// <summary>孤立锚点数量</summary>
        public int OrphanAnchorCount { get; set; }

        /// <summary>工位未明确的锚点数（StationCode 为空）</summary>
        public int UnstationedCount { get; set; }

        // ── 置信度 ────────────────────────────────────────────────

        /// <summary>全部确认锚点的平均置信度</summary>
        public decimal AverageConfidence { get; set; }

        // ── 工位染色 ──────────────────────────────────────────────

        /// <summary>染色覆盖的逻辑单元数（IncludeStationColoring=true 时填充）</summary>
        public int ColoredLogicUnitCount { get; set; }

        /// <summary>有工位冲突的逻辑单元数</summary>
        public int ConflictLogicUnitCount { get; set; }
    }
}
