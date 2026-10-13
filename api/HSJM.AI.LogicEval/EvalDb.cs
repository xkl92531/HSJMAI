using System.Text.Json;
using Microsoft.Extensions.Configuration;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using Npgsql;

namespace HSJM.AI.LogicEval;

/// <summary>评测用数据库读取。连接串取自 api/HSJM.AI/appsettings.json 的 WebConfig:DBList:0；正式库只读。</summary>
static class EvalDb
{
    public sealed record RuleRow(long Id, string Operator, string? Source, long? SourceLuId, string? ValidStates,
        List<(string Name, string Role)> Signals);

    static string BaseConnection()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HSJM.AI"));
        var config = new ConfigurationBuilder().SetBasePath(root).AddJsonFile("appsettings.json").Build();
        return config["WebConfig:DBList:0:ConnectionString"] ?? throw new InvalidOperationException("缺少数据库连接串");
    }

    /// <summary>指定库名的连接串（cache-push 写测试库缓存表用）。</summary>
    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(BaseConnection()) { Database = database }.ConnectionString;

    public static async Task<NpgsqlConnection> OpenAsync(string database, bool readOnly)
    {
        var builder = new NpgsqlConnectionStringBuilder(BaseConnection()) { Database = database };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        if (readOnly)
        {
            await using var ro = new NpgsqlCommand("SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY", connection);
            await ro.ExecuteNonQueryAsync();
        }
        return connection;
    }

    public static async Task<Dictionary<string, string>> AddressesAsync(NpgsqlConnection c, long projectId)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand("select tagname, coalesce(address,'') from ai_plc_iotag where projectid=@p and not isdeleted and coalesce(tagname,'')<>''", c);
        cmd.Parameters.AddWithValue("p", projectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) map.TryAdd(rd.GetString(0), rd.GetString(1));
        return map;
    }

    /// <summary>点表（名字、地址、数据类型），供离线生成使用。</summary>
    public static async Task<List<PipelineTag>> TagsAsync(NpgsqlConnection c, long projectId)
    {
        var list = new List<PipelineTag>();
        await using var cmd = new NpgsqlCommand(
            "select tagname, coalesce(address,''), coalesce(datatype,''), comment from ai_plc_iotag where projectid=@p and not isdeleted and coalesce(tagname,'')<>''", c);
        cmd.Parameters.AddWithValue("p", projectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new PipelineTag(rd.GetString(0), rd.GetString(1), rd.GetString(2)) { Comment = rd.IsDBNull(3) ? null : rd.GetString(3) });
        return list;
    }

    /// <summary>读取项目未删除规则及其信号，按规则 id 排序（保证按 Key 去重时保留哪条是确定的）。
    /// operator 为空的规则、信号名或角色为空的信号行跳过并计数，不中断整次运行。</summary>
    public static async Task<(List<RuleRow> Rules, int SkippedRules, int SkippedSignals)> RulesAsync(NpgsqlConnection c, long projectId, string? sourceLike)
    {
        var ordered = new List<RuleRow>();
        var rules = new Dictionary<long, RuleRow>();
        int skippedRules = 0, skippedSignals = 0;
        var filter = sourceLike == null ? "" : " and r.source like @src";
        await using (var cmd = new NpgsqlCommand($"select r.id, r.operator, r.source, r.sourceluid, r.validstates from ai_plc_ioconstraintrule r where r.projectid=@p and not r.isdeleted{filter} order by r.id", c))
        {
            cmd.Parameters.AddWithValue("p", projectId);
            if (sourceLike != null) cmd.Parameters.AddWithValue("src", sourceLike);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                if (rd.IsDBNull(1)) { skippedRules++; continue; }
                var rule = new RuleRow(rd.GetInt64(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                    rd.IsDBNull(3) ? null : rd.GetInt64(3), rd.IsDBNull(4) ? null : rd.GetString(4), []);
                rules[rule.Id] = rule;
                ordered.Add(rule);
            }
        }
        await using (var cmd = new NpgsqlCommand("select s.ruleid, s.signalname, s.signalrole from ai_plc_ioconstraintsignal s join ai_plc_ioconstraintrule r on r.id=s.ruleid where r.projectid=@p and not r.isdeleted and not s.isdeleted order by s.ruleid, s.sortorder, s.id", c))
        {
            cmd.Parameters.AddWithValue("p", projectId);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                if (!rules.TryGetValue(rd.GetInt64(0), out var rule)) continue;
                if (rd.IsDBNull(1) || rd.IsDBNull(2)) { skippedSignals++; continue; }
                rule.Signals.Add((rd.GetString(1), rd.GetString(2)));
            }
        }
        return (ordered, skippedRules, skippedSignals);
    }

    /// <summary>正式库：来源程序段 id → (块名, 网络号)。</summary>
    public static async Task<Dictionary<long, (string Block, int Network)>> FormalUnitsAsync(NpgsqlConnection c, IEnumerable<long> luIds)
    {
        var map = new Dictionary<long, (string, int)>();
        var ids = luIds.Distinct().ToArray();
        if (ids.Length == 0) return map;
        await using var cmd = new NpgsqlCommand("select l.id, b.blockname, coalesce(l.networkid::text,'') from ai_plc_logicunit l join ai_plc_block_new b on b.id=l.blockid where l.id = any(@ids)", c);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            if (int.TryParse(rd.GetString(2), out var network)) map[rd.GetInt64(0)] = (rd.GetString(1), network);
        return map;
    }

    /// <summary>从 XMLV2 规则的 ValidStates 读取：旁路信号、目标分组、来源程序段 id、是否延时类
    /// （Original353LegacyRuleMapper 写入的 Applicability=AfterSettling 或 RequiresSettlingEvidence=true）。</summary>
    public static (IReadOnlyList<string> Bypass, IReadOnlyList<IReadOnlyList<string>>? Groups, string? LogicUnitId, bool Delayed) Meta(string? validStates)
    {
        if (string.IsNullOrWhiteSpace(validStates)) return ([], null, null, false);
        using var doc = JsonDocument.Parse(validStates);
        var root = doc.RootElement;
        IReadOnlyList<string> bypass = root.TryGetProperty("BypassSignals", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        IReadOnlyList<IReadOnlyList<string>>? groups = root.TryGetProperty("TargetGroups", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => (IReadOnlyList<string>)x.EnumerateArray().Select(y => y.GetString() ?? "").ToArray()).ToArray() : null;
        var lu = root.TryGetProperty("LogicUnitId", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        var delayed = root.TryGetProperty("Applicability", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "AfterSettling"
            || root.TryGetProperty("RequiresSettlingEvidence", out var r) && r.ValueKind == JsonValueKind.True;
        return (bypass, groups, lu, delayed);
    }
}
