using System;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.DwEtl
{
    /// <summary>
    /// dw 通用 ETL 骨架（方案 §6）：读水位线 → 调提取器 → 幂等 upsert → 批次审计 → 推进水位线。
    /// 由 Hangfire 每日调度（dw-etl-daily，见 DwEtlRecurringJobBootstrapService），也可手动调用 RunAllAsync。
    /// 铁律：
    /// ① 单提取器失败不影响其它提取器（逐个 try-catch，失败者记 dw.etl_batch=failed + 错误日志）；
    /// ② 水位线只在提取器成功后推进——失败批次的行下轮会被重读，目标表幂等 upsert 保证不重不漏；
    /// ③ 调度时刻必须早于 signal_history/shadow_event 保留清理任务（station-shadow-data-retention-cleanup，
    ///    每日 02:00 物理删除 3 天前数据），否则窗口内的源行被清理即永久丢失——见 Bootstrap 的 cron 注释。
    /// </summary>
    public class DwEtlOrchestratorService : ISelfScopedAutoInject
    {
        /// <summary>提取器登记表：新增 dw 管道在此追加一行即接入每日调度（骨架零改动）。</summary>
        private static readonly IDwExtractor[] Extractors =
        {
            new SilenceFactExtractor(),
            new SignalChangeDailyExtractor(),
        };

        private readonly ISqlSugarClient _db;
        private readonly ILogger<DwEtlOrchestratorService> _logger;

        public DwEtlOrchestratorService(ISqlSugarClient db, ILogger<DwEtlOrchestratorService> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger;
        }

        /// <summary>Hangfire 入口：按登记顺序依次跑全部提取器，单提取器失败不阻断后续。</summary>
        [Hangfire.JobDisplayName("DW数仓ETL（每日01:30，须早于02:00清理）")]
        public async Task RunAllAsync()
        {
            foreach (var extractor in Extractors)
            {
                try
                {
                    await RunOneAsync(extractor);
                }
                catch (Exception ex)
                {
                    // 单提取器失败不影响其它：记录后继续（该提取器下轮从水位线幂等重跑）
                    _logger.LogError(ex,
                        "dw ETL 提取器 {Source} -> {Target} 本轮失败，已写 etl_batch=failed；下轮从水位线幂等重跑，不影响其它提取器",
                        extractor.SourceTable, extractor.TargetTable);
                }
            }
        }

        /// <summary>单个提取器的完整链路：水位线 → 批次登记 → 提取+upsert → 审计收尾 → 推进水位线。</summary>
        private async Task RunOneAsync(IDwExtractor extractor)
        {
            // ── 1. 读水位线（dw.etl_watermark，源表名为主键）──
            // 首跑无水位线 → 引导值 = T1 回填已覆盖位置（提取器自知口径），落一行水位线后续全走增量。
            var watermark = await _db.Ado.SqlQuerySingleAsync<long?>(
                "SELECT last_pk FROM dw.etl_watermark WHERE source_table = @source",
                new { source = extractor.SourceTable });
            if (!watermark.HasValue)
            {
                watermark = await extractor.ResolveBootstrapWatermarkAsync(_db);
                await _db.Ado.ExecuteCommandAsync(
                    "INSERT INTO dw.etl_watermark (source_table, target_table, last_pk, last_ts, updated_at) " +
                    "VALUES (@source, @target, @pk, NULL, now())",
                    new { source = extractor.SourceTable, target = extractor.TargetTable, pk = watermark.Value });
                _logger.LogInformation(
                    "dw ETL 首跑引导：{Source} -> {Target} 水位线初始化为 {Watermark}（T1 回填边界，其后由增量接管）",
                    extractor.SourceTable, extractor.TargetTable, watermark.Value);
            }

            // ── 2. 批次登记（dw.etl_batch 一轮一行，running 起步，成败收尾时回写）──
            var batchId = await _db.Ado.SqlQuerySingleAsync<long>(
                "INSERT INTO dw.etl_batch (source_table, status) VALUES (@source, 'running') RETURNING batch_id",
                new { source = extractor.SourceTable });

            try
            {
                // ── 3. 提取器：读源（id > 水位线）→ C# 物化 → 幂等 upsert 目标表 ──
                var outcome = await extractor.RunIncrementalAsync(_db, watermark.Value, batchId);

                // ── 4. 批次收尾（rows_inserted 口径与 backfill 一致：写出行数，冲突更新不单列）──
                await _db.Ado.ExecuteCommandAsync(
                    "UPDATE dw.etl_batch SET status = 'succeeded', finished_at = now(), rows_inserted = @rows, rows_updated = 0 " +
                    "WHERE batch_id = @batchId",
                    new { rows = outcome.RowsWritten, batchId });

                // ── 5. 推进水位线（成功后；失败不推进 → 同一窗口下轮幂等重跑）──
                if (outcome.NewWatermark > watermark.Value)
                {
                    await _db.Ado.ExecuteCommandAsync(
                        "UPDATE dw.etl_watermark SET last_pk = @pk, last_ts = @ts, updated_at = now() WHERE source_table = @source",
                        new { pk = outcome.NewWatermark, ts = outcome.NewWatermarkTs, source = extractor.SourceTable });
                }

                _logger.LogInformation(
                    "dw ETL 完成：{Source} -> {Target} batch={BatchId} 写出 {Rows} 行，水位线 {Old} -> {New}",
                    extractor.SourceTable, extractor.TargetTable, batchId, outcome.RowsWritten, watermark.Value, outcome.NewWatermark);
            }
            catch (Exception ex)
            {
                // 失败收尾：批次标 failed + 错误文本；水位线不动（本方法向上抛，由 RunAllAsync 记日志后继续其它提取器）
                await _db.Ado.ExecuteCommandAsync(
                    "UPDATE dw.etl_batch SET status = 'failed', finished_at = now(), error_text = @err WHERE batch_id = @batchId",
                    new { err = TruncateText(ex.ToString(), 2000), batchId });
                throw;
            }
        }

        private static string TruncateText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }
    }
}
