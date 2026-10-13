#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>只存 AI 回复原文与读取状态；核对与分级（Code:Network / AI:Network、拒绝原因）不进缓存，每次重新跑。</summary>
public sealed record NetworkAiCacheEntry(string Key, string BlockName, int? Network, string Status, string? ReplyJson, string? Error,
    string Model, string PromptVersion);

public interface INetworkAiCache
{
    Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct);
    Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct);
}

/// <summary>spec §6：按内容哈希缓存（翻译文本、上下文、模型、提示词与核对器版本）；表由服务自建。</summary>
public sealed class PostgresNetworkAiCache(string connectionString) : INetworkAiCache
{
    const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.network_ai_cache (
          cache_key text PRIMARY KEY, block_name text NOT NULL, network integer NULL, status text NOT NULL,
          reply_json text NULL, error text NULL, model text NOT NULL, prompt_version text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now());
        """;

    Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => PlcIndependentSchema.OpenAsync(connectionString, Ddl, ct);

    public async Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT block_name,network,status,reply_json,error,model,prompt_version FROM plc_independent.network_ai_cache WHERE cache_key=@k", db);
        cmd.Parameters.AddWithValue("k", key);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new(key, rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4), rd.GetString(5), rd.GetString(6));
    }

    public async Task SaveAsync(NetworkAiCacheEntry e, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO plc_independent.network_ai_cache(cache_key,block_name,network,status,reply_json,error,model,prompt_version)
            VALUES(@k,@b,@n,@s,@r,@e,@m,@p)
            ON CONFLICT(cache_key) DO UPDATE SET status=EXCLUDED.status,reply_json=EXCLUDED.reply_json,error=EXCLUDED.error,created_at=now()
            """, db);
        cmd.Parameters.AddWithValue("k", e.Key); cmd.Parameters.AddWithValue("b", e.BlockName);
        cmd.Parameters.AddWithValue("n", (object?)e.Network ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("s", e.Status);
        cmd.Parameters.AddWithValue("r", (object?)e.ReplyJson ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("e", (object?)e.Error ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("m", e.Model); cmd.Parameters.AddWithValue("p", e.PromptVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>评测工具与测试用的文件缓存，不碰数据库；文件损坏按未命中处理。</summary>
public sealed class FileNetworkAiCache(string directory) : INetworkAiCache
{
    public Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct) => JsonFileCache.GetAsync<NetworkAiCacheEntry>(directory, key, ct);

    public Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct) => JsonFileCache.SaveAsync(directory, entry.Key, entry, ct);
}
