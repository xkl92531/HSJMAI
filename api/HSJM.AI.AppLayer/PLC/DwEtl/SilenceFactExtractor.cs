using System;
using System.Collections.Generic;

using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.PLC.DwEtl
{
    /// <summary>
    /// 提取器 1：public.ai_plc_station_shadow_event → dw.dw_silence_fact（诊断报告事实表，核心）。
    /// 物化口径逐条照抄 T1 一次性回填 tools/backfill_dw.py（§3 dw_silence_fact）：
    /// - diagnosis_result_json 物化：confidence（根级，缺省回退 llmFallback.confidence）、
    ///   llm_conclusion（llmFallback 的 conclusion/summary/reason/rootCause/text 首个非空文本，兜底整体序列化截断 2000）、
    ///   ast_failure_path（ast 的 failurePath/path/description 同法）；
    /// - confidence_tier：TIER_MAP 按 diagnosis_status 分层（AST确定/LLM兜底/未诊断/恢复，缺省未诊断）；
    /// - shift_code/hour_of_day：triggered_at 按 8-16/16-24/0-8 三班推导；
    /// - is_storm_duplicate：同工位时间上紧邻的前一条 Silent 间隔 ≤ 130s 的后续条
    ///   （130s=轮询 2 周期+10s 容差，backfill_dw.py 2026-09-08 校准值，非任务书旧值 65s）；
    /// - silence_seconds（backfill 留 NULL 的增量增强）：Resume 事件 = 与紧邻前一条 Silent 的间隔秒数
    ///   （MTTR 代理口径；风暴簇多列时按最后一条 Silent 起算，偏保守——口径见列注释）。
    /// 增量差异点（相对回填）：
    /// - 风暴回看不再需要 C# 上下文——「紧邻前一条 Silent」由 SQL LATERAL 对全源计算，
    ///   天然覆盖跨批次边界（任务书"回看上一条非本次批次的最后事件"的更稳实现）；
    /// - 补刷新窗口：id ≤ 水位线 且 diagnosis_status='Pending' 的行重读（诊断晚于入仓完成时物化刷新），
    ///   有界 LIMIT 防膨胀；源表保留期 3 天，早于保留期的 Pending 行自然随源删除消失。
    /// - 已知数据边界：源表 last_bundle_signature 可为 NULL——静默前束取自 Redis RecentBundles
    ///   （TTL 24h），停机超 24h 的 Silent 事件无签名（→ NoSignature 诊断链），BI 统计签名覆盖率时
    ///   需知该缺口属长期停机场景，非管道丢数；现场由事件上下文固化表部分补偿（见 T4 固化注释）。
    /// </summary>
    public class SilenceFactExtractor : IDwExtractor
    {
        public string SourceTable => "public.ai_plc_station_shadow_event";
        public string TargetTable => "dw.dw_silence_fact";

        /// <summary>风暴重复判定窗（秒）= 轮询 2 周期 + 10s 容差（backfill_dw.py 2026-09-08 校准）。</summary>
        private const int StormGapSeconds = 130;

        /// <summary>单轮主窗口行数上限（防御性；超出的行下轮从水位线继续）。</summary>
        private const int MainWindowLimit = 20000;

        /// <summary>单轮 Pending 补刷新行数上限。</summary>
        private const int PendingRefreshLimit = 2000;

        /// <summary>单条 upsert 语句行数上限（控制参数个数，PG 上限 65535 远够）。</summary>
        private const int UpsertChunkSize = 500;

        /// <summary>文本物化截断（与 backfill text_of 的 limit=2000 一致）。</summary>
        private const int TextLimit = 2000;

        /// <summary>confidence_tier 分层表（照抄 backfill_dw.py TIER_MAP；键精确匹配 diagnosis_status）。</summary>
        private static readonly IReadOnlyDictionary<string, string> TierMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Diagnosed"] = "AST确定",
            ["DiagnosedPartial"] = "AST确定",
            ["LlmDiagnosed"] = "LLM兜底",
            ["NoSignature"] = "未诊断",
            ["NoTemplate"] = "未诊断",
            ["NoSignalMapping"] = "未诊断",
            ["NoAst"] = "未诊断",
            ["NoExpectedBundle"] = "未诊断",
            ["Skipped"] = "未诊断",
            ["Pending"] = "未诊断",
            ["Error"] = "未诊断",
            ["Recovered"] = "恢复",
        };

        /// <summary>llmFallback 文本提取键序（照抄 backfill text_of）。</summary>
        private static readonly string[] LlmTextKeys = { "conclusion", "summary", "reason", "rootCause", "text" };

        /// <summary>ast 失败路径提取键序（照抄 backfill text_of）。</summary>
        private static readonly string[] AstTextKeys = { "failurePath", "path", "description" };

        /// <summary>源行（列名用 AS "PascalCase" 别名对齐属性，避免 SqlSugar snake 映射歧义）。</summary>
        private sealed class SilenceSourceRow
        {
            public long Id { get; set; }
            public long? ProjectId { get; set; }
            public string StationCode { get; set; }
            public string EventType { get; set; }
            public DateTime? TriggeredAt { get; set; }
            public string StationMode { get; set; }
            public string TargetAnchorKey { get; set; }
            public string ExpectedNextBundleJson { get; set; }
            public string MissingBundleJson { get; set; }
            public string DiagnosisStatus { get; set; }
            public string DiagnosisResultJson { get; set; }

            /// <summary>同工位时间上紧邻的前一条 Silent（LATERAL 计算；风暴标记与 MTTR 的基准）。</summary>
            public DateTime? PrevSilentAt { get; set; }
        }

        public async Task<long> ResolveBootstrapWatermarkAsync(ISqlSugarClient db)
        {
            // T1 回填以 dw_silence_fact.id（=源主键）落库：目标表现有 max(id) 即回填边界
            return await db.Ado.SqlQuerySingleAsync<long>(
                "SELECT COALESCE(MAX(id), 0) FROM dw.dw_silence_fact");
        }

        public async Task<DwExtractorOutcome> RunIncrementalAsync(ISqlSugarClient db, long watermark, long batchId)
        {
            // ── 主窗口：id > 水位线（新事件全量物化）──
            // prev_silent_at：同项目同工位 triggered_at 早于本行的最后一条 Silent（与 backfill 逐行推进 prev_t 等价：
            // backfill 按 (project, station, 时间) 排序逐行处理，处理到 E 时 prev_t 恰为 E 之前最近一条 Silent 的时间）。
            // project_id 隔离：多项目同名站码（如两个项目都有 OP010）不得互相串台（终审 M1）。
            // IS NOT NULL 防御：dw_silence_fact 的 project_id/triggered_at 为 NOT NULL 而源列可空，
            // 脏行过滤掉不入 dw，避免整 chunk 失败→批次 failed→水位线停摆（终审 M2）。
            var mainSql = $@"
SELECT e.id AS ""Id"", e.project_id AS ""ProjectId"", e.station_code AS ""StationCode"",
       e.event_type AS ""EventType"", e.triggered_at AS ""TriggeredAt"", e.station_mode AS ""StationMode"",
       e.target_anchor_key AS ""TargetAnchorKey"", e.expected_next_bundle_json AS ""ExpectedNextBundleJson"",
       e.missing_bundle_json AS ""MissingBundleJson"", e.diagnosis_status AS ""DiagnosisStatus"",
       e.diagnosis_result_json AS ""DiagnosisResultJson"", prev.prev_silent_at AS ""PrevSilentAt""
FROM public.ai_plc_station_shadow_event e
LEFT JOIN LATERAL (
    SELECT max(s2.triggered_at) AS prev_silent_at
    FROM public.ai_plc_station_shadow_event s2
    WHERE s2.project_id = e.project_id
      AND s2.station_code = e.station_code
      AND s2.event_type = 'Silent'
      AND s2.triggered_at < e.triggered_at
) prev ON true
WHERE e.id > @watermark
  AND e.project_id IS NOT NULL
  AND e.triggered_at IS NOT NULL
ORDER BY e.id
LIMIT {MainWindowLimit}";

            // ── 补刷新窗口：诊断在事件入仓后才完成的行（Pending → 已诊断）重读刷新物化列 ──
            var refreshSql = $@"
SELECT e.id AS ""Id"", e.project_id AS ""ProjectId"", e.station_code AS ""StationCode"",
       e.event_type AS ""EventType"", e.triggered_at AS ""TriggeredAt"", e.station_mode AS ""StationMode"",
       e.target_anchor_key AS ""TargetAnchorKey"", e.expected_next_bundle_json AS ""ExpectedNextBundleJson"",
       e.missing_bundle_json AS ""MissingBundleJson"", e.diagnosis_status AS ""DiagnosisStatus"",
       e.diagnosis_result_json AS ""DiagnosisResultJson"", prev.prev_silent_at AS ""PrevSilentAt""
FROM public.ai_plc_station_shadow_event e
LEFT JOIN LATERAL (
    SELECT max(s2.triggered_at) AS prev_silent_at
    FROM public.ai_plc_station_shadow_event s2
    WHERE s2.project_id = e.project_id
      AND s2.station_code = e.station_code
      AND s2.event_type = 'Silent'
      AND s2.triggered_at < e.triggered_at
) prev ON true
WHERE e.id <= @watermark
  AND e.diagnosis_status = 'Pending'
  AND e.project_id IS NOT NULL
  AND e.triggered_at IS NOT NULL
ORDER BY e.id
LIMIT {PendingRefreshLimit}";

            var mainRows = await db.Ado.SqlQueryAsync<SilenceSourceRow>(mainSql, new { watermark });
            var refreshRows = mainRows.Count >= MainWindowLimit
                ? new List<SilenceSourceRow>()
                : await db.Ado.SqlQueryAsync<SilenceSourceRow>(refreshSql, new { watermark });

            var allRows = mainRows.Concat(refreshRows).ToList();
            var rowsWritten = 0;
            if (allRows.Count > 0)
            {
                rowsWritten = await UpsertRowsAsync(db, allRows, batchId);
            }

            // 水位线推进只看主窗口（补刷新行本来就在水位线之下）
            var lastRow = mainRows.OrderByDescending(it => it.Id).FirstOrDefault();
            return new DwExtractorOutcome
            {
                RowsWritten = rowsWritten,
                NewWatermark = lastRow?.Id ?? watermark,
                NewWatermarkTs = lastRow?.TriggeredAt,
            };
        }

        /// <summary>物化 + 分块幂等 upsert，返回影响行数。</summary>
        private static async Task<int> UpsertRowsAsync(ISqlSugarClient db, List<SilenceSourceRow> rows, long batchId)
        {
            var columns = "(id, project_id, station_code, event_type, triggered_at, station_mode, " +
                          "silence_seconds, shift_code, hour_of_day, diagnosis_status, diagnosis_confidence, confidence_tier, " +
                          "root_cause_anchor, ast_failure_path, llm_conclusion, root_cause_family, " +
                          "expected_bundle, missing_bundle, is_storm_duplicate, etl_batch_id)";

            var total = 0;
            for (var offset = 0; offset < rows.Count; offset += UpsertChunkSize)
            {
                var chunk = rows.Skip(offset).Take(UpsertChunkSize).ToList();
                var values = new List<string>();
                var parameters = new List<SugarParameter>(chunk.Count * 20);
                for (var i = 0; i < chunk.Count; i++)
                {
                    var row = chunk[i];
                    var p = i.ToString();
                    values.Add($"(@id{p}, @pid{p}, @station{p}, @etype{p}, @trig{p}, @smode{p}, " +
                               $"@sil{p}, @shift{p}, @hour{p}, @status{p}, @conf{p}, @tier{p}, " +
                               $"@anchor{p}, @ast{p}, @llm{p}, @family{p}, " +
                               $"@expb{p}, @missb{p}, @dup{p}, @batch{p})");

                    parameters.Add(new SugarParameter($"@id{p}", row.Id, global::System.Data.DbType.Int64));
                    parameters.Add(new SugarParameter($"@pid{p}", (object)row.ProjectId ?? DBNull.Value, global::System.Data.DbType.Int64));
                    parameters.Add(new SugarParameter($"@station{p}", (object)row.StationCode ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@etype{p}", (object)row.EventType ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@trig{p}", ToTimestampText(row.TriggeredAt), global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@smode{p}", (object)row.StationMode ?? DBNull.Value, global::System.Data.DbType.String));

                    var silenceSeconds = ResolveSilenceSeconds(row);
                    parameters.Add(new SugarParameter($"@sil{p}", (object)silenceSeconds ?? DBNull.Value, global::System.Data.DbType.Int32));

                    parameters.Add(new SugarParameter($"@shift{p}", (object)ShiftOf(row.TriggeredAt) ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@hour{p}", (object)(row.TriggeredAt?.Hour) ?? DBNull.Value, global::System.Data.DbType.Int16));

                    var materialized = MaterializeDiagnosis(row.DiagnosisResultJson, row.DiagnosisStatus);
                    parameters.Add(new SugarParameter($"@status{p}", (object)row.DiagnosisStatus ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@conf{p}", (object)materialized.Confidence ?? DBNull.Value, global::System.Data.DbType.Decimal));
                    parameters.Add(new SugarParameter($"@tier{p}", materialized.Tier, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@anchor{p}", (object)row.TargetAnchorKey ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@ast{p}", (object)materialized.AstFailurePath ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@llm{p}", (object)materialized.LlmConclusion ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@family{p}", DBNull.Value, global::System.Data.DbType.String)); // root_cause_family 由 T7 聚类任务回填，不在此写

                    parameters.Add(new SugarParameter($"@expb{p}", (object)row.ExpectedNextBundleJson ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@missb{p}", (object)row.MissingBundleJson ?? DBNull.Value, global::System.Data.DbType.String));
                    parameters.Add(new SugarParameter($"@dup{p}", IsStormDuplicate(row), global::System.Data.DbType.Boolean));
                    parameters.Add(new SugarParameter($"@batch{p}", batchId, global::System.Data.DbType.Int64));
                }

                var sql = $"INSERT INTO dw.dw_silence_fact {columns}\nVALUES\n" +
                          string.Join(",\n", values) + @"
ON CONFLICT (id) DO UPDATE SET
    diagnosis_status = EXCLUDED.diagnosis_status,
    diagnosis_confidence = EXCLUDED.diagnosis_confidence,
    confidence_tier = EXCLUDED.confidence_tier,
    root_cause_anchor = EXCLUDED.root_cause_anchor,
    ast_failure_path = EXCLUDED.ast_failure_path,
    llm_conclusion = EXCLUDED.llm_conclusion,
    expected_bundle = EXCLUDED.expected_bundle,
    missing_bundle = EXCLUDED.missing_bundle,
    silence_seconds = COALESCE(EXCLUDED.silence_seconds, dw.dw_silence_fact.silence_seconds),
    etl_batch_id = EXCLUDED.etl_batch_id";
                // DO UPDATE 列集口径：
                // - 不更新 root_cause_family（T7 聚类回填专属，防止每日 ETL 清空聚类结果）；
                // - expected_bundle/missing_bundle 参与更新（终审 M3）：与 status/confidence/tier 等同属
                //   诊断产物，Pending 补刷新时须一并刷新（SELECT 已带出新值，EXCLUDED 天然携带）；
                // - 不更新 triggered_at/shift_code/hour_of_day/is_storm_duplicate（事件创建即不变的客观事实；
                //   风暴标记只在首次 INSERT 落库——补刷新窗口重读旧行时源表可能已被保留清理删去相邻 Silent，
                //   LATERAL 会算出不同的 prev_silent_at，若参与 UPDATE 反而会改坏已定标记）；
                // - silence_seconds 用 COALESCE 保留已算得值，同理防源行被清理后回退成 NULL。
                total += await db.Ado.ExecuteCommandAsync(sql, parameters.ToArray());
            }

            return total;
        }

        /// <summary>风暴重复：Silent 且与紧邻前一条 Silent 间隔 ≤ 130s（照抄 backfill 口径，紧邻 Silent 含重复条自身序列）。</summary>
        private static bool IsStormDuplicate(SilenceSourceRow row)
        {
            return string.Equals(row.EventType, "Silent", StringComparison.Ordinal)
                && row.PrevSilentAt.HasValue
                && row.TriggeredAt.HasValue
                && (row.TriggeredAt.Value - row.PrevSilentAt.Value).TotalSeconds <= StormGapSeconds;
        }

        /// <summary>MTTR 口径：Resume 与紧邻前一条 Silent 的间隔秒数（风暴簇按最后一条 Silent 起算，保守值）。</summary>
        private static int? ResolveSilenceSeconds(SilenceSourceRow row)
        {
            if (string.Equals(row.EventType, "Resume", StringComparison.Ordinal)
                && row.PrevSilentAt.HasValue
                && row.TriggeredAt.HasValue)
            {
                return Math.Max(0, (int)(row.TriggeredAt.Value - row.PrevSilentAt.Value).TotalSeconds);
            }

            return null;
        }

        /// <summary>三班推导（照抄 backfill shift_of：8-16 早班 / 16-24 中班 / 0-8 夜班）。</summary>
        private static string ShiftOf(DateTime? triggeredAt)
        {
            if (!triggeredAt.HasValue)
            {
                return null;
            }

            var hour = triggeredAt.Value.Hour;
            if (hour >= 8 && hour < 16)
            {
                return "早班";
            }

            return hour >= 16 ? "中班" : "夜班";
        }

        private sealed class DiagnosedMaterial
        {
            public decimal? Confidence;
            public string LlmConclusion;
            public string AstFailurePath;
            public string Tier;
        }

        /// <summary>diagnosis_result_json 物化（照抄 backfill_dw.py §3 的 JSON 解析口径）。</summary>
        private static DiagnosedMaterial MaterializeDiagnosis(string diagnosisResultJson, string status)
        {
            var material = new DiagnosedMaterial
            {
                Tier = TierMap.TryGetValue(status ?? string.Empty, out var tier) ? tier : "未诊断",
            };

            if (string.IsNullOrWhiteSpace(diagnosisResultJson))
            {
                return material;
            }

            try
            {
                using var document = JsonDocument.Parse(diagnosisResultJson);
                var root = document.RootElement;

                material.Confidence = ReadNumberOrNull(root, "confidence");

                if (root.TryGetProperty("llmFallback", out var llmFallback))
                {
                    // confidence 缺省（根级无该字段）才回退 llmFallback.confidence（照抄 backfill：仅 None 回退，0 是合法值）
                    material.Confidence ??= ReadNumberOrNull(llmFallback, "confidence");
                    material.LlmConclusion = TextOf(llmFallback, LlmTextKeys);
                }

                if (root.TryGetProperty("ast", out var ast))
                {
                    material.AstFailurePath = TextOf(ast, AstTextKeys);
                }
            }
            catch (JsonException)
            {
                // 非法 JSON：仅物化分层，诊断字段留空（照抄 backfill 的 try-pass 口径）
            }

            return material;
        }

        private static decimal? ReadNumberOrNull(JsonElement parent, string propertyName)
        {
            if (parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDecimal();
            }

            return null;
        }

        /// <summary>
        /// 照抄 backfill text_of：优先按键序取非空字符串；否则整体序列化截断（空对象/空值返回 null）。
        /// </summary>
        private static string TextOf(JsonElement element, string[] keys)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (element.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Length <= TextLimit ? text : text.Substring(0, TextLimit);
                    }
                }
            }

            var serialized = JsonSerializer.Serialize(element);
            if (serialized == "{}" || serialized == "null")
            {
                return null;
            }

            return serialized.Length <= TextLimit ? serialized : serialized.Substring(0, TextLimit);
        }

        /// <summary>timestamp 无时区列：以文本传入（PG 隐式转换），与项目 LineUpMonitorBindingSource 同法。</summary>
        private static string ToTimestampText(DateTime? value)
        {
            return value.HasValue
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff")
                : null;
        }
    }
}
