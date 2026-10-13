using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphStepTag
{
    /// <summary>GRAPH 步位注册汇总（项目级）</summary>
    public class PLC_GraphStepTagSummaryDTO
    {
        /// <summary>项目 Id</summary>
        public long ProjectId { get; set; }
        /// <summary>符号总数（EXEC_BIT + STEP_NO）</summary>
        public int TotalSymbols { get; set; }
        /// <summary>覆盖的 GRAPH 步进 DB 数</summary>
        public int DbCount { get; set; }
        /// <summary>已验证数（L1/L2/L3 通过）</summary>
        public int VerifiedCount { get; set; }
        /// <summary>已注册待验证数</summary>
        public int RegisteredCount { get; set; }
        /// <summary>待注册数</summary>
        public int PendingCount { get; set; }
        /// <summary>注册失败数</summary>
        public int RegFailedCount { get; set; }
        /// <summary>验证失败数</summary>
        public int VerifyFailedCount { get; set; }
        /// <summary>最近一次注册时间</summary>
        public DateTime? LastRegTime { get; set; }
        /// <summary>按 DB 分组明细</summary>
        public List<PLC_GraphStepTagDbSummaryDTO> Databases { get; set; } = new();
    }

    /// <summary>GRAPH 步位注册汇总（单 DB 级）</summary>
    public class PLC_GraphStepTagDbSummaryDTO
    {
        /// <summary>实例 DB 名（如 DB161_HomeGraph）</summary>
        public string DbName { get; set; }
        /// <summary>绑定的 GRAPH FB 名</summary>
        public string FbName { get; set; }
        /// <summary>工站代码（stationblockbinding；无绑定为空）</summary>
        public string StationCode { get; set; }
        /// <summary>工站名称（主数据缺失为空）</summary>
        public string StationName { get; set; }
        /// <summary>工步数（EXEC_BIT 条数）</summary>
        public int StepCount { get; set; }
        /// <summary>符号总数（含 S_NO 校验位）</summary>
        public int SymbolCount { get; set; }
        public int VerifiedCount { get; set; }
        public int RegisteredCount { get; set; }
        public int PendingCount { get; set; }
        public int RegFailedCount { get; set; }
        public int VerifyFailedCount { get; set; }
        /// <summary>失败原因样本（一条非空 verifydetail）</summary>
        public string FailureSample { get; set; }
    }

    /// <summary>台账明细行（前端展开表格用）</summary>
    public class PLC_GraphStepTagRowDTO
    {
        public long Id { get; set; }
        /// <summary>完整符号路径</summary>
        public string Symbol { get; set; }
        /// <summary>注册到网关的实际点位地址</summary>
        public string DbAddress { get; set; }
        /// <summary>工站代码</summary>
        public string StationCode { get; set; }
        /// <summary>工站名称</summary>
        public string StationName { get; set; }
        /// <summary>EXEC_BIT / STEP_NO</summary>
        public string TagCategory { get; set; }
        /// <summary>工步号（1 基，仅 EXEC_BIT）</summary>
        public int? StepNumber { get; set; }
        /// <summary>数组下标（0 基 = StepNumber-1，仅 EXEC_BIT）</summary>
        public int? BitIndex { get; set; }
        /// <summary>Pending/Registered/RegFailed/Verified/VerifyFailed</summary>
        public string RegStatus { get; set; }
        /// <summary>验证结果详情 / 失败原因</summary>
        public string VerifyDetail { get; set; }
        public DateTime? RegTime { get; set; }
        public DateTime? VerifyTime { get; set; }
    }
}
