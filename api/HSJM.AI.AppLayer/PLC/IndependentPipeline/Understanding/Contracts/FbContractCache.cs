#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record FbContractCacheEntry(string Key, string FbName, string Status, string? ContractJson, string? Error,
    string Model, string PromptVersion);

public interface IFbContractCache
{
    Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct);
    Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct);
}

public static class FbContractStatus
{
    public const string Ok = "Ok", Failed = "Failed", NoBody = "NoBody", TextOnly = "TextOnly", TooLarge = "TooLarge", Rejected = "Rejected";
}

/// <summary>spec §4.3：按内容哈希缓存，不同项目同一套库直接复用。表由服务自建。</summary>
public sealed class PostgresFbContractCache(string connectionString) : IFbContractCache
{
    const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.fb_contract_cache (
          cache_key text PRIMARY KEY, fb_name text NOT NULL, status text NOT NULL,
          contract_json text NULL, error text NULL, model text NOT NULL, prompt_version text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now());
        """;
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => PlcIndependentSchema.OpenAsync(connectionString, Ddl, ct);

    /// <summary>42P07 duplicate_table、42P06 duplicate_schema、23505 unique_violation（pg_type 等系统表）。</summary>
    internal static bool IsConcurrentCreate(string? sqlState) => PlcIndependentSchema.IsConcurrentCreate(sqlState);

    public async Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT fb_name,status,contract_json,error,model,prompt_version FROM plc_independent.fb_contract_cache WHERE cache_key=@k", db);
        cmd.Parameters.AddWithValue("k", key);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new(key, rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4), rd.GetString(5));
    }

    public async Task SaveAsync(FbContractCacheEntry e, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO plc_independent.fb_contract_cache(cache_key,fb_name,status,contract_json,error,model,prompt_version)
            VALUES(@k,@f,@s,@c,@e,@m,@p)
            ON CONFLICT(cache_key) DO UPDATE SET status=EXCLUDED.status,contract_json=EXCLUDED.contract_json,
              error=EXCLUDED.error,created_at=now()
            """, db);
        cmd.Parameters.AddWithValue("k", e.Key); cmd.Parameters.AddWithValue("f", e.FbName);
        cmd.Parameters.AddWithValue("s", e.Status);
        cmd.Parameters.AddWithValue("c", (object?)e.ContractJson ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("e", (object?)e.Error ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("m", e.Model); cmd.Parameters.AddWithValue("p", e.PromptVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>评测工具与测试用的文件缓存，不碰数据库。</summary>
public sealed class FileFbContractCache(string directory) : IFbContractCache
{
    /// <summary>文件损坏/截断按未命中处理（重新读取后会覆盖）。</summary>
    public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct) => JsonFileCache.GetAsync<FbContractCacheEntry>(directory, key, ct);

    public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct) => JsonFileCache.SaveAsync(directory, entry.Key, entry, ct);
}
