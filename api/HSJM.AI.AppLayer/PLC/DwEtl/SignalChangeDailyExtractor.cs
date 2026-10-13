using System;
using System.Collections.Generic;

using System.Linq;
using System.Threading.Tasks;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.DwEtl
{
    /// <summary>
    /// 提取器 2：public.ai_plc_station_signal_history → dw.dw_signal_change_daily（信号变化日聚合，P0 抢救性保全）。
    /// 聚合粒度 (project_id, station_code, signal_key, stat_date)（= 目标表主键），口径照抄 T1 回填
    /// tools/backfill_dw.py §4：change_count / first/last_change_at / active_hour_count / last_value。
    ///
    /// 幂等设计（相对任务书"ON CONFLICT 累加 change_count=dw.change_count+EXCLUDED..."的改进）：
    /// 采用「触达组合全日重算」而非增量累加——本轮水位线新行先求触达的 (project,station,signal,date) 组合，
    /// 再对每个组合从源表全量重算当日指标后整行 REPLACE upsert。原因：
    /// ① 累加式在批次中途失败重跑（水位线未推进→同窗口重读）会双计，全日重算天然幂等；
    /// ② 增量与 T1 回填并存同样安全：触达日的重算值 = 全日真值，直接覆盖回填值，无叠加误差；
    /// ③ 源表保留 3 天而 ETL 每日先跑（01:30 &lt; 清理 02:00），触达日数据必完整，重算不缺行。
    /// </summary>
    public class SignalChangeDailyExtractor : IDwExtractor
    {
        public string SourceTable => "public.ai_plc_station_signal_history";
        public string TargetTable => "dw.dw_signal_change_daily";

        /// <summary>单条 upsert 语句行数上限。</summary>
        private const int UpsertChunkSize = 500;

        /// <summary>聚合结果行（列名 AS 别名对齐属性）。</summary>
        private sealed class DailyAggRow
        {
            public long? ProjectId { get; set; }
            public string StationCode { get; set; }
            public string SignalKey { get; set; }
            public string VariableName { get; set; }
            public DateTime StatDate { get; set; }
            public int ChangeCount { get; set; }
            public DateTime? FirstChangeAt { get; set; }
            public DateTime? LastChangeAt { get; set; }
            public int ActiveHourCount { get; set; }
            public string LastValue { get; set; }

            /// <summary>本轮 SQL 快照的水位线上界（bounds CTE 常量列，各行相同；Minor #13 修复引入）。</summary>
            public long? NewWatermark { get; set; }

            /// <summary>水位线上界行的 changed_at。</summary>
            public DateTime? NewWatermarkTs { get; set; }
        }

        public async Task<long> ResolveBootstrapWatermarkAsync(ISqlSugarClient db)
        {
            // 回填边界 = 目标表已聚合到的最后业务时刻（dw_signal_change_daily 无源主键列）：
            // changed_at 不超过该时刻的源行都已被回填聚合过，从其后的行接管。
            // 目标表为空（从未回填）→ 返回 0，从现存源数据头开始全量聚合。
            return await db.Ado.SqlQuerySingleAsync<long>(@"
SELECT COALESCE(MAX(h.id), 0)
FROM public.ai_plc_station_signal_history h
WHERE h.changed_at <= (SELECT MAX(last_change_at) FROM dw.dw_signal_change_daily)");
        }

        public async Task<DwExtractorOutcome> RunIncrementalAsync(ISqlSugarClient db, long watermark, long batchId)
        {
            // 触达组合全日重算：touched CTE = 本轮水位线新行触达的组合；
            // agg 对每个组合聚合源表【当日全部行】（不只新行）；last_value 取组合当日最新一条（照抄回填 DISTINCT ON 口径）。
            // bounds CTE（终审 Minor #13 修复）：水位线上界 hi 与触达读取收进同一条 SQL 快照，touched 收窄 id<=hi，
            // 消除「聚合读取→事后 max(id)」两次读之间的秒级竞态窗（窗口内新落库行会被永久少计、无自愈）。
            // PK 三列 IS NOT NULL 防御（终审 M2）：dw_signal_change_daily 主键 (project_id, station_code, signal_key,
            // stat_date) 为 NOT NULL 而源列可空，脏行不触达组合即不会进入 INSERT，防单条脏行让 chunk 失败→水位线停摆。
            var aggregateSql = $@"
WITH bounds AS (
    SELECT b.hi, h.changed_at AS hi_ts
    FROM (SELECT max(id) AS hi
          FROM public.ai_plc_station_signal_history
          WHERE id > @watermark) b
    LEFT JOIN public.ai_plc_station_signal_history h ON h.id = b.hi
),
touched AS (
    SELECT DISTINCT project_id, station_code, signal_key, changed_at::date AS stat_date
    FROM public.ai_plc_station_signal_history
    WHERE id > @watermark AND id <= (SELECT hi FROM bounds)
      AND COALESCE(isdeleted, false) = false
      AND project_id IS NOT NULL AND station_code IS NOT NULL AND signal_key IS NOT NULL
),
agg AS (
    SELECT h.project_id, h.station_code, h.signal_key, t.stat_date,
           count(*)::int AS change_count,
           min(h.changed_at) AS first_change_at,
           max(h.changed_at) AS last_change_at,
           count(DISTINCT extract(hour FROM h.changed_at))::int AS active_hour_count,
           max(h.variable_name) AS variable_name
    FROM public.ai_plc_station_signal_history h
    JOIN touched t
      ON t.project_id = h.project_id AND t.station_code = h.station_code
     AND t.signal_key = h.signal_key AND t.stat_date = h.changed_at::date
    WHERE COALESCE(h.isdeleted, false) = false
    GROUP BY 1, 2, 3, 4
),
lastval AS (
    SELECT DISTINCT ON (h.project_id, h.station_code, h.signal_key, h.changed_at::date)
           h.project_id, h.station_code, h.signal_key, h.changed_at::date AS stat_date, h.new_value::text
    FROM public.ai_plc_station_signal_history h
    WHERE COALESCE(h.isdeleted, false) = false
    ORDER BY 1, 2, 3, 4, h.changed_at DESC
)
SELECT agg.project_id AS ""ProjectId"", agg.station_code AS ""StationCode"", agg.signal_key AS ""SignalKey"",
       agg.variable_name AS ""VariableName"", agg.stat_date AS ""StatDate"", agg.change_count AS ""ChangeCount"",
       agg.first_change_at AS ""FirstChangeAt"", agg.last_change_at AS ""LastChangeAt"",
       agg.active_hour_count AS ""ActiveHourCount"", lastval.new_value AS ""LastValue"",
       bounds.hi AS ""NewWatermark"", bounds.hi_ts AS ""NewWatermarkTs""
FROM agg
JOIN lastval ON lastval.project_id = agg.project_id AND lastval.station_code = agg.station_code
            AND lastval.signal_key = agg.signal_key AND lastval.stat_date = agg.stat_date
CROSS JOIN bounds";

            // 快路径：本轮无新行（水位线已追平）直接空转返回
            var newRowCount = await db.Ado.SqlQuerySingleAsync<int>(
                "SELECT count(*)::int FROM public.ai_plc_station_signal_history WHERE id > @watermark",
                new { watermark });
            if (newRowCount <= 0)
            {
                return new DwExtractorOutcome { RowsWritten = 0, NewWatermark = watermark };
            }

            var rows = await db.Ado.SqlQueryAsync<DailyAggRow>(aggregateSql, new { watermark });

            var rowsWritten = 0;
            if (rows.Count > 0)
            {
                rowsWritten = await UpsertRowsAsync(db, rows, batchId);
            }

            // 水位线推进（Minor #13 修复）：上界 hi 取自聚合语句本身的 bounds CTE（同一 SQL 快照），
            // 不再用事后的独立 max(id) 查询——那条路径在聚合读取与 max(id) 两次读之间落库的行会被
            // 永久少计（该行日期未被重算但水位线已越过它，次日触达的是新日期，无自愈）。
            // 空行兜底：本轮触达行全为脏行/软删（agg 无输出）时水位线不推进——宁可次日空转重读也不越界丢行
            //（与 SilenceFact 提取器"水位线=实际读到的行"语义对齐）。
            var bound = rows.Where(r => r.NewWatermark.HasValue)
                .OrderByDescending(r => r.NewWatermark.Value)
                .FirstOrDefault();

            return new DwExtractorOutcome
            {
                RowsWritten = rowsWritten,
                NewWatermark = bound?.NewWatermark ?? watermark,
                NewWatermarkTs = bound?.NewWatermarkTs,
            };
        }

        /// <summary>分块整行 REPLACE upsert（幂等：同组合重算重跑结果一致），返回影响行数。</summary>
        private static async Task<int> UpsertRowsAsync(ISqlSugarClient db, List<DailyAggRow> rows, long batchId)
        {
            var columns = "(project_id, station_code, signal_key, variable_name, stat_date, " +
                          "change_count, first_change_at, last_change_at, active_hour_count, last_value, etl_batch_id)";

            var total = 0;
            for (var offset = 0; offset < rows.Count; offset += UpsertChunkSize)
            {
                var chunk = rows.Skip(offset).Take(UpsertChunkSize).ToList();
                var values = new List<string>();
                var parameters = new List<SugarParameter>(chunk.Count * 11);
                for (var i = 0; i < chunk.Count; i++)
                {
                    var row = chunk[i];
                    var p = i.ToString();
                    values.Add($"(@pid{p}, @station{p}, @sig{p}, @var{p}, @date{p}, " +
                               $"@cnt{p}, @first{p}, @last{p}, @hours{p}, @lastval{p}, @batch{p})");

                    parameters.Add(new SugarParameter($"@pid{p}", (object)row.ProjectId ?? DBNull.Value, global::System.Data.DbType.Int64));
                    parameters.Add(new SugarParameter($"@station{p}", (object)row.StationCode ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@sig{p}", (object)row.SignalKey ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@var{p}", (object)row.VariableName ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@date{p}", ToDateText(row.StatDate), global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@cnt{p}", row.ChangeCount, global::System.Data.DbType.Int32));
                    parameters.Add(new SugarParameter($"@first{p}", ToTimestampText(row.FirstChangeAt), global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@last{p}", ToTimestampText(row.LastChangeAt), global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@hours{p}", (short)row.ActiveHourCount, global::System.Data.DbType.Int16));
                    parameters.Add(new SugarParameter($"@lastval{p}", (object)row.LastValue ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@batch{p}", batchId, global::System.Data.DbType.Int64));
                }

                var sql = $"INSERT INTO dw.dw_signal_change_daily {columns}\nVALUES\n" +
                          string.Join(",\n", values) + @"
ON CONFLICT (project_id, station_code, signal_key, stat_date) DO UPDATE SET
    variable_name = EXCLUDED.variable_name,
    change_count = EXCLUDED.change_count,
    first_change_at = EXCLUDED.first_change_at,
    last_change_at = EXCLUDED.last_change_at,
    active_hour_count = EXCLUDED.active_hour_count,
    last_value = EXCLUDED.last_value,
    etl_batch_id = EXCLUDED.etl_batch_id";
                total += await db.Ado.ExecuteCommandAsync(sql, parameters.ToArray());
            }

            return total;
        }

        private static string ToDateText(DateTime value)
        {
            return value.ToString("yyyy-MM-dd");
        }

        private static string ToTimestampText(DateTime? value)
        {
            return value.HasValue
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff")
                : null;
        }
    }
}
