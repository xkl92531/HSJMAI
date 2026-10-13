using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 锚点发现结果
    /// </summary>
    public class AnchorDiscoveryResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public List<AnchorCandidate> PhysicalAnchors { get; set; } = new List<AnchorCandidate>();

        public List<AnchorCandidate> ConfirmedPhysicalAnchors { get; set; } = new List<AnchorCandidate>();

        public List<AnchorCandidate> CorePhysicalAnchors { get; set; } = new List<AnchorCandidate>();

        public List<AnchorCandidate> VirtualAnchors { get; set; } = new List<AnchorCandidate>();

        public List<AnchorCandidate> CoreVirtualAnchors { get; set; } = new List<AnchorCandidate>();

        public List<AnchorCandidate> VirtualAnchorReviewQueue { get; set; } = new List<AnchorCandidate>();

        public List<AnchorCandidate> HandshakeAnchors { get; set; } = new List<AnchorCandidate>();

        /// <summary>
        /// I 点输入锚点（IncludeInputAnchors=true 时填充）：物理输入信号及其关联的 AST/Q 点。
        /// </summary>
        public List<AnchorCandidate> InputAnchors { get; set; } = new List<AnchorCandidate>();

        public List<StopCauseAstSeed> AstSeeds { get; set; } = new List<StopCauseAstSeed>();

        public List<StopCauseAstCompileResult> AstCompileResults { get; set; } = new List<StopCauseAstCompileResult>();

        /// <summary>
        /// 基于 AST + LLM 的语义发现结果，包含虚拟Q点、握手位和 IoMapping 建议。
        /// </summary>
        public List<AstSemanticFinding> AstSemanticFindings { get; set; } = new List<AstSemanticFinding>();

        /// <summary>
        /// 工位染色结果：各 LogicUnit 的工位归属标签（IncludeStationColoring=true 时填充）
        /// </summary>
        public List<LogicUnitStationLabel> StationColoringResult { get; set; } = new List<LogicUnitStationLabel>();

        /// <summary>
        /// 工位地图层输出：站点桶、最终归属、冲突、孤立候选。
        /// </summary>
        public StationMapResult StationMap { get; set; } = new StationMapResult();

        /// <summary>
        /// 本次发现汇总统计（始终填充）
        /// </summary>
        public AnchorDiscoverySummary Summary { get; set; }

        /// <summary>
        /// LLM 候选发现诊断信息，用于解释是否真正走到了 LLM 主分类路径。
        /// </summary>
        public LlmDiscoveryDiagnostics LlmDiagnostics { get; set; } = new LlmDiscoveryDiagnostics();

        public List<string> Warnings { get; set; } = new List<string>();
    }
}
