using System;
using System.Threading.Tasks;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.DwEtl
{
    /// <summary>
    /// dw 增量提取器契约（方案 §6 通用 ETL 骨架）：一个提取器 = 一条「源表 → dw 目标表」增量管道。
    /// 生命周期由 <see cref="DwEtlOrchestratorService"/> 统一编排：
    /// 读 dw.etl_watermark 水位线 → 调提取器 → 幂等 upsert 目标表 → 写 dw.etl_batch 审计 → 推进水位线。
    /// 水位线语义：last_pk = 已处理到的源表主键（不含），提取器只消费 id &gt; watermark 的行；
    /// 批次失败不推进水位线，下轮对同一窗口幂等重跑（目标表必须以 ON CONFLICT 幂等写入）。
    /// </summary>
    public interface IDwExtractor
    {
        /// <summary>源表全名（含 schema），= dw.etl_watermark.source_table 主键。</summary>
        string SourceTable { get; }

        /// <summary>dw 目标表全名（含 schema）。</summary>
        string TargetTable { get; }

        /// <summary>
        /// 首跑引导水位线：T1 一次性回填（tools/backfill_dw.py）已覆盖到的源表位置，
        /// 本提取器从其后接管（增量与回填的衔接点）。目标表为空（从未回填）时返回 0，
        /// 表示从头全量处理现存源数据。
        /// </summary>
        Task<long> ResolveBootstrapWatermarkAsync(ISqlSugarClient db);

        /// <summary>
        /// 增量提取 + C# 物化 + 幂等 upsert 目标表（写入口径由提取器自知）。
        /// 幂等要求：同一窗口重复执行结果一致（ON CONFLICT），因为批次失败后下轮会重读同窗口。
        /// 返回写出行数与推进后水位线（= 本窗口处理到的源表 max id；无新行时等于传入 watermark）。
        /// </summary>
        Task<DwExtractorOutcome> RunIncrementalAsync(ISqlSugarClient db, long watermark, long batchId);
    }

    /// <summary>提取器一轮执行的结果。</summary>
    public sealed class DwExtractorOutcome
    {
        /// <summary>本轮写入目标表的行数（含冲突更新行；与 backfill 口径一致记入 etl_batch.rows_inserted）。</summary>
        public int RowsWritten { get; set; }

        /// <summary>推进后的源表主键水位线（应 &gt;= 传入 watermark）。</summary>
        public long NewWatermark { get; set; }

        /// <summary>水位线对应源行的业务时间（写 dw.etl_watermark.last_ts，便于人工核对）。</summary>
        public DateTime? NewWatermarkTs { get; set; }
    }
}
