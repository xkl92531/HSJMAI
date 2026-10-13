#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>报警清单行。Preconditions：超时报警对应反馈规则的旁路信号（大写、去重、序数排序）。
/// FeedbackGroups：超时报警的到位组（任一组全部为 true 即到位）；空 = 视为单组（全部到位点）。
/// SourceNetwork：与库 ai_plc_logicunit.networkid 同口径（块内第几个 CompileUnit，空网络也计）。
/// 2026-10-01 之前写入的旧行是归档根元素序号（空网络不计），在含空网络的块里与库口径错位——这类行 LogicUnitId 为空，排查时不按网络号定位。
/// LogicUnitId：发布时按（块名, SourceNetwork）回查到、且该逻辑单元确有对 Callee 的调用的 ai_plc_logicunit.id；查不到 / 不唯一为空。</summary>
public sealed record AlarmCatalogRow(string AlarmSymbol, string Kind, string? OutputSignal, IReadOnlyList<string> FeedbackSignals,
    string? Delay, string SourceBlock, int? SourceNetwork, string SourceInstance, string Callee, IReadOnlyList<string> Evidence)
{
    public IReadOnlyList<string> Preconditions { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> FeedbackGroups { get; init; } = [];
    public long? LogicUnitId { get; init; }
    /// <summary>报警文本的后备：全局 DB 成员没有注释时用（经 FB 输出引脚的锁存报警位取 FB 形参注释）；可为 null。</summary>
    public string? FallbackText { get; init; }
}

/// <summary>发布时回查用的库逻辑单元：块名、networkid、该逻辑单元 AI_PLC_BlockCall 里的被调块名。</summary>
public sealed record AlarmCatalogUnitCandidate(long LogicUnitId, string BlockName, int NetworkId, IReadOnlyCollection<string> Callees);

/// <summary>发布时把清单行定位到库里的逻辑单元（填 LogicUnitId）。只收紧：
/// （块名忽略大小写, networkid）相同、且该逻辑单元的调用记录里有本行的被调块（忽略大小写），满足的逻辑单元恰好一个才定位；否则留空。
/// 没有被调块的行（锁存报警位，Callee 为空）只要求（块名, networkid）唯一。</summary>
public static class AlarmCatalogLocator
{
    public static IReadOnlyList<AlarmCatalogRow> Locate(IReadOnlyList<AlarmCatalogRow> rows, IReadOnlyList<AlarmCatalogUnitCandidate> candidates)
    {
        var byKey = candidates
            .GroupBy(c => (Block: (c.BlockName ?? "").Trim().ToUpperInvariant(), c.NetworkId))
            .ToDictionary(g => g.Key, g => g.ToArray());
        return rows.Select(r =>
        {
            long? id = null;
            if (r.SourceNetwork is int n
                && byKey.TryGetValue(((r.SourceBlock ?? "").Trim().ToUpperInvariant(), n), out var group))
            {
                // 锁存报警位没有被调块（Callee 为空）：只按块名 + networkid 唯一定位到置位所在程序段
                var matched = (string.IsNullOrWhiteSpace(r.Callee) ? group
                        : group.Where(c => c.Callees.Any(x => string.Equals(x?.Trim(), r.Callee.Trim(), StringComparison.OrdinalIgnoreCase))))
                    .Select(c => c.LogicUnitId).Distinct().ToArray();
                if (matched.Length == 1) id = matched[0];
            }
            return r with { LogicUnitId = id };
        }).ToArray();
    }
}

/// <summary>spec §8：调用点代入得到的报警位（超时 Timeout / 位置矛盾 PositionConflict / 期望状态 ExpectedState）与
/// 锁存报警位（Latched，<see cref="LatchedAlarmBits"/>）→ 报警清单行。不看任何名字。</summary>
public static class AlarmCatalogBuilder
{
    /// <summary>同一报警位既是契约报警又是锁存报警位时，取契约报警（带输出/到位/延时，信息更全）；同类内取程序段 Id 最小的。</summary>
    public static IReadOnlyList<AlarmCatalogRow> Build(IReadOnlyList<UnderstandingAlarm> alarms) =>
        alarms.OrderBy(a => a.Alarm.Kind == LatchedAlarmBits.Kind ? 1 : 0).ThenBy(a => a.UnitId)
            .GroupBy(a => a.Alarm.AlarmSymbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(a => new AlarmCatalogRow(a.Alarm.AlarmSymbol, a.Alarm.Kind,
                string.IsNullOrWhiteSpace(a.Alarm.Output) ? null : a.Alarm.Output, a.Alarm.Feedback, a.Alarm.Delay,
                a.BlockName, a.DbNetwork, a.Instance, a.Callee, a.Alarm.Evidence)
            {
                Preconditions = a.Alarm.Bypass.Select(s => s.ToUpperInvariant()).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(),
                FeedbackGroups = a.Alarm.FeedbackGroups,
                FallbackText = string.IsNullOrWhiteSpace(a.Alarm.Text) ? null : a.Alarm.Text.Trim(),
            })
            .OrderBy(r => r.AlarmSymbol, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>报警清单按范围替换（2026-10-02 审查 P2/P3/P4）：范围外的旧行原样保留；范围内取新行，
/// 新行报警位与保留的旧行同名时不覆盖保留行（主键是项目 + 报警位）。</summary>
public static class AlarmCatalogMerge
{
    public static IReadOnlyList<AlarmCatalogRow> Merge(IReadOnlyList<AlarmCatalogRow> old, IReadOnlyList<AlarmCatalogRow> fresh,
        Func<AlarmCatalogRow, bool> inScope)
    {
        var kept = old.Where(r => !inScope(r)).ToList();
        var taken = kept.Select(r => r.AlarmSymbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in fresh.Where(inScope))
            if (taken.Add(row.AlarmSymbol)) kept.Add(row);
        return kept.OrderBy(r => r.AlarmSymbol, StringComparer.Ordinal).ToArray();
    }
}

public sealed class AlarmCatalogStore(string connectionString)
{
    public const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.alarm_catalog (
          project_id bigint NOT NULL, alarm_symbol text NOT NULL, kind text NOT NULL,
          output_signal text NULL, feedback_signals jsonb NOT NULL, delay_text text NULL,
          source_block text NOT NULL, source_network int NULL, source_instance text NOT NULL, callee text NOT NULL,
          evidence jsonb NOT NULL, preconditions jsonb NOT NULL DEFAULT '[]', feedback_groups jsonb NOT NULL DEFAULT '[]',
          logic_unit_id bigint NULL, fallback_text text NULL,
          generator_version text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY (project_id, alarm_symbol));
        ALTER TABLE plc_independent.alarm_catalog ADD COLUMN IF NOT EXISTS preconditions jsonb NOT NULL DEFAULT '[]';
        ALTER TABLE plc_independent.alarm_catalog ADD COLUMN IF NOT EXISTS feedback_groups jsonb NOT NULL DEFAULT '[]';
        ALTER TABLE plc_independent.alarm_catalog ADD COLUMN IF NOT EXISTS logic_unit_id bigint NULL;
        ALTER TABLE plc_independent.alarm_catalog ADD COLUMN IF NOT EXISTS fallback_text text NULL;
        """;

    /// <summary>本进程内已成功建好表的连接串。DDL 里的 ALTER TABLE 会短暂独占锁表，不能每次发布都跑。</summary>
    static readonly ConcurrentDictionary<string, bool> SchemaReady = new(StringComparer.Ordinal);

    /// <summary>每个连接串只在首次成功时执行 DDL；失败不记为已建，下次再试。并发首调可能各跑一次（DDL 幂等）。</summary>
    public static async Task EnsureSchemaAsync(string connectionString, Func<CancellationToken, Task> runDdl, CancellationToken ct)
    {
        if (SchemaReady.ContainsKey(connectionString)) return;
        await runDdl(ct);
        SchemaReady[connectionString] = true;
    }

    public async Task ReplaceForProjectAsync(long projectId, IReadOnlyList<AlarmCatalogRow> rows, string generatorVersion, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await EnsureSchemaAsync(connectionString, async token =>
        {
            await using var ddl = new NpgsqlCommand(Ddl, db);
            await ddl.ExecuteNonQueryAsync(token);
        }, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("DELETE FROM plc_independent.alarm_catalog WHERE project_id=@p", db, tx))
        {
            del.Parameters.AddWithValue("p", projectId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var r in rows)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO plc_independent.alarm_catalog(project_id,alarm_symbol,kind,output_signal,feedback_signals,delay_text,
                  source_block,source_network,source_instance,callee,evidence,preconditions,feedback_groups,logic_unit_id,fallback_text,generator_version)
                VALUES(@p,@s,@k,@o,@f,@d,@b,@n,@i,@c,@e,@pre,@fg,@lu,@ft,@v)
                """, db, tx);
            ins.Parameters.AddWithValue("p", projectId); ins.Parameters.AddWithValue("s", r.AlarmSymbol);
            ins.Parameters.AddWithValue("k", r.Kind); ins.Parameters.AddWithValue("o", (object?)r.OutputSignal ?? DBNull.Value);
            ins.Parameters.AddWithValue("f", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(r.FeedbackSignals));
            ins.Parameters.AddWithValue("d", (object?)r.Delay ?? DBNull.Value); ins.Parameters.AddWithValue("b", r.SourceBlock);
            ins.Parameters.AddWithValue("n", (object?)r.SourceNetwork ?? DBNull.Value); ins.Parameters.AddWithValue("i", r.SourceInstance);
            ins.Parameters.AddWithValue("c", r.Callee);
            ins.Parameters.AddWithValue("e", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(r.Evidence));
            ins.Parameters.AddWithValue("pre", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(r.Preconditions));
            ins.Parameters.AddWithValue("fg", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(r.FeedbackGroups));
            ins.Parameters.Add(new NpgsqlParameter("lu", NpgsqlDbType.Bigint) { Value = (object?)r.LogicUnitId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("ft", NpgsqlDbType.Text) { Value = (object?)r.FallbackText ?? DBNull.Value });
            ins.Parameters.AddWithValue("v", generatorVersion);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<(IReadOnlyList<AlarmCatalogRow> Rows, string? Version)> LoadAsync(long projectId, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('plc_independent.alarm_catalog') IS NOT NULL", db))
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return ([], null);
        // 旧 DDL 建的表没有 preconditions / feedback_groups / logic_unit_id 列：只读路径不改表，按空值读出（下次 ReplaceForProjectAsync 会补列）。
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var hasColumn = new NpgsqlCommand("""
            SELECT column_name FROM information_schema.columns
              WHERE table_schema='plc_independent' AND table_name='alarm_catalog'
                AND column_name IN ('preconditions','feedback_groups','logic_unit_id','fallback_text')
            """, db))
        await using (var cr = await hasColumn.ExecuteReaderAsync(ct))
            while (await cr.ReadAsync(ct)) columns.Add(cr.GetString(0));
        var hasPreconditions = columns.Contains("preconditions");
        var hasFeedbackGroups = columns.Contains("feedback_groups");
        var hasLogicUnitId = columns.Contains("logic_unit_id");
        var hasFallbackText = columns.Contains("fallback_text");
        await using var cmd = new NpgsqlCommand($"""
            SELECT alarm_symbol,kind,output_signal,feedback_signals::text,delay_text,source_block,source_network,source_instance,callee,
                   evidence::text,generator_version,created_at,{(hasPreconditions ? "preconditions::text" : "'[]'::text")},
                   {(hasFeedbackGroups ? "feedback_groups::text" : "'[]'::text")},
                   {(hasLogicUnitId ? "logic_unit_id" : "NULL::bigint")},
                   {(hasFallbackText ? "fallback_text" : "NULL::text")}
            FROM plc_independent.alarm_catalog WHERE project_id=@p ORDER BY alarm_symbol
            """, db);
        cmd.Parameters.AddWithValue("p", projectId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AlarmCatalogRow>(); string? version = null;
        while (await rd.ReadAsync(ct))
        {
            rows.Add(new AlarmCatalogRow(rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                JsonSerializer.Deserialize<string[]>(rd.GetString(3)) ?? [], rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetInt32(6), rd.GetString(7), rd.GetString(8),
                JsonSerializer.Deserialize<string[]>(rd.GetString(9)) ?? [])
            {
                Preconditions = JsonSerializer.Deserialize<string[]>(rd.GetString(12)) ?? [],
                FeedbackGroups = JsonSerializer.Deserialize<string[][]>(rd.GetString(13)) ?? [],
                LogicUnitId = rd.IsDBNull(14) ? null : rd.GetInt64(14),
                FallbackText = rd.IsDBNull(15) ? null : rd.GetString(15),
            });
            version ??= rd.GetString(10) + "@" + rd.GetDateTime(11).ToString("O");
        }
        return (rows, version);
    }
}
