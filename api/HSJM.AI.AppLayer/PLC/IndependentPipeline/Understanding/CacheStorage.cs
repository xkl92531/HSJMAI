#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>plc_independent 下 AI 缓存表的连接与自建表（FB 契约缓存、普通网络缓存共用）。每次发布都会新建缓存实例，
/// 建表在本进程内对每个（连接串, DDL）只做一次；并发首次建表撞上的唯一约束视为别人已建好。</summary>
internal static class PlcIndependentSchema
{
    static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.Ordinal);

    public static async Task<NpgsqlConnection> OpenAsync(string connectionString, string ddl, CancellationToken ct)
    {
        var db = new NpgsqlConnection(connectionString);
        try
        {
            await db.OpenAsync(ct);
            var key = connectionString + "\u0001" + ddl;
            if (!Ready.ContainsKey(key))
            {
                try { await using var cmd = new NpgsqlCommand(ddl, db); await cmd.ExecuteNonQueryAsync(ct); }
                // 并发首次建表：IF NOT EXISTS 仍可能撞上 pg_type/pg_namespace 唯一约束，说明别人已建好
                catch (PostgresException ex) when (IsConcurrentCreate(ex.SqlState)) { }
                Ready[key] = true;
            }
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    /// <summary>42P07 duplicate_table、42P06 duplicate_schema、23505 unique_violation（pg_type 等系统表）。</summary>
    internal static bool IsConcurrentCreate(string? sqlState) => sqlState is "42P07" or "42P06" or "23505";
}

/// <summary>评测工具与测试用的 JSON 文件缓存（一键一文件），不碰数据库；文件损坏/截断按未命中处理（重新读取后会覆盖）。</summary>
internal static class JsonFileCache
{
    static string PathOf(string directory, string key) => Path.Combine(directory, key + ".json");

    public static async Task<T?> GetAsync<T>(string directory, string key, CancellationToken ct) where T : class
    {
        var path = PathOf(directory, key);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct)); }
        catch (JsonException) { return null; }
    }

    public static async Task SaveAsync<T>(string directory, string key, T entry, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathOf(directory, key), JsonSerializer.Serialize(entry), ct);
    }
}
