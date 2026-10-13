using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 逻辑单元工位染色结果（Phase 1 Step 7-8）。
    /// 表示一个 LogicUnit 被某锚点工位认领后的归属信息。
    /// </summary>
    public class LogicUnitStationLabel
    {
        /// <summary>逻辑单元 ID</summary>
        public long LogicUnitId { get; set; }

        /// <summary>所属程序块 ID</summary>
        public long? BlockId { get; set; }

        /// <summary>所属程序块名称（冗余，便于前端展示）</summary>
        public string BlockName { get; set; }

        /// <summary>网络标题 / Transition 标题</summary>
        public string NetworkTitle { get; set; }

        /// <summary>推断出的归属工位编码</summary>
        public string StationCode { get; set; }

        /// <summary>归属置信度 0-1（多锚点累加后封顶 0.98）</summary>
        public decimal Confidence { get; set; }

        /// <summary>使该 LogicUnit 染色的锚点键</summary>
        public string AnchorKey { get; set; }

        /// <summary>锚点变量名</summary>
        public string AnchorVariable { get; set; }

        /// <summary>
        /// 染色来源描述：
        /// DirectWrite           — LogicUnit 直接写该锚点变量
        /// DirectWrite+BlockMatch — 直接写 且 Block.StationCode 与锚点一致（置信度额外加权）
        /// </summary>
        public string ColoringReason { get; set; }

        /// <summary>
        /// 竞争工位列表：置信度贡献 ≥ 0.30 但不是本次获胜的工位。
        /// 非空时请触发人工复核。
        /// </summary>
        public List<string> ConflictingStations { get; set; } = new List<string>();

        /// <summary>是否存在工位冲突</summary>
        public bool HasConflict => ConflictingStations.Count > 0;
    }
}
