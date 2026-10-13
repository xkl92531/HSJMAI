using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding
{
    /// <summary>
    /// GRAPH 程序块及其工站绑定信息（批量绑定弹框数据源）。
    /// Bindings 为空 = 未绑定（右侧候选区）；非空 = 已绑定（左侧树挂载 + 可解绑）。
    /// </summary>
    public class PLC_GraphStationBindingBlockDTO
    {
        /// <summary>程序块 Id（ai_plc_block_new.id）</summary>
        public long BlockId { get; set; }

        /// <summary>程序块名称（如 FB360_MainGraph）</summary>
        public string BlockName { get; set; }

        /// <summary>程序块类型（FB/FC/OB 等）</summary>
        public string BlockType { get; set; }

        /// <summary>程序块编号</summary>
        public int? BlockNumber { get; set; }

        /// <summary>所属 PLC 项目 Id（ai_plc_project_new.id）</summary>
        public long PlcProjectId { get; set; }

        /// <summary>该块当前的全部有效绑定（ai_plc_stationblockbinding，含自动提取与手动绑定）</summary>
        public List<PLC_GraphStationBindingItemDTO> Bindings { get; set; } = new List<PLC_GraphStationBindingItemDTO>();
    }

    /// <summary>
    /// 单条工站绑定信息
    /// </summary>
    public class PLC_GraphStationBindingItemDTO
    {
        /// <summary>绑定行 Id（ai_plc_stationblockbinding.id，解绑时回传）</summary>
        public long BindingId { get; set; }

        /// <summary>工站代码（Basic_WorkStation.Code，如 OP010）</summary>
        public string StationCode { get; set; }

        /// <summary>绑定原因（手动绑定 / DerivedFromStationColoring+BlockCall 等）</summary>
        public string Reason { get; set; }

        /// <summary>归属置信度（手动绑定恒为 1）</summary>
        public decimal? Confidence { get; set; }

        /// <summary>是否共享块（一块多站）</summary>
        public bool? IsShared { get; set; }

        /// <summary>绑定时间</summary>
        public DateTime? BoundAt { get; set; }
    }

    /// <summary>
    /// 批量绑定请求：把一批 GRAPH 块绑定到指定工站。
    /// ProjectId 取各块自身归属（block.projectid），请求不带项目参数。
    /// </summary>
    public class PLC_GraphStationBindingBindRequestDTO
    {
        /// <summary>目标工站代码（Basic_WorkStation.Code）</summary>
        public string StationCode { get; set; }

        /// <summary>GRAPH 程序块 Id 列表（ai_plc_block_new.id，多选）</summary>
        public List<long> BlockIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 解绑请求：按绑定行 Id 软删除
    /// </summary>
    public class PLC_GraphStationBindingUnbindRequestDTO
    {
        /// <summary>绑定行 Id 列表（ai_plc_stationblockbinding.id）</summary>
        public List<long> BindingIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 单个被跳过块的原因
    /// </summary>
    public class PLC_GraphStationBindingSkipDTO
    {
        public string BlockName { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// 批量绑定结果
    /// </summary>
    public class PLC_GraphStationBindingBindResultDTO
    {
        /// <summary>新增绑定数</summary>
        public int BoundCount { get; set; }

        /// <summary>因换绑被移除的旧绑定数（块原先绑在其它工站）</summary>
        public int MovedCount { get; set; }

        /// <summary>同步更新 stationcode 的块数</summary>
        public int UpdatedBlockCount { get; set; }

        /// <summary>跳过数（已绑定/白名单拦截等）</summary>
        public int SkippedCount { get; set; }

        /// <summary>跳过明细（最多保留 50 条）</summary>
        public List<PLC_GraphStationBindingSkipDTO> Skipped { get; set; } = new List<PLC_GraphStationBindingSkipDTO>();
    }

    /// <summary>
    /// 解绑结果
    /// </summary>
    public class PLC_GraphStationBindingUnbindResultDTO
    {
        /// <summary>删除的绑定行数</summary>
        public int UnboundCount { get; set; }

        /// <summary>完全解绑后清空 stationcode 的块数</summary>
        public int ClearedBlockCount { get; set; }
    }
}
