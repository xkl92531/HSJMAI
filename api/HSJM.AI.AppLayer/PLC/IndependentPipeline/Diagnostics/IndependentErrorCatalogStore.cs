#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Npgsql;
using NpgsqlTypes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

/// <summary>All catalog mutations use only the independent schema through the existing explicit test guard.</summary>
public sealed class IndependentErrorCatalogStore(PostgresPipelineStore pipeline) : IIndependentErrorCatalogStore, IScopedAutoInject
{
    public const string MigrationSql = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.error_catalog_history (
          project_id bigint NOT NULL, catalog_id text NOT NULL, payload jsonb NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(project_id,catalog_id));
        CREATE TABLE IF NOT EXISTS plc_independent.error_catalog_active (
          project_id bigint PRIMARY KEY, catalog_id text NOT NULL,
          FOREIGN KEY(project_id,catalog_id) REFERENCES plc_independent.error_catalog_history(project_id,catalog_id));
        """;

    public async Task<string> SourceFingerprintAsync(long project, CancellationToken token) =>
        (await pipeline.LoadSourceAsync(project, token)).Fingerprint();

    public async Task SaveAsync(PersistedErrorCatalog catalog, CancellationToken token) {
        await using var connection = new NpgsqlConnection(pipeline.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var migration = new NpgsqlCommand(MigrationSql, connection, transaction)) await migration.ExecuteNonQueryAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO plc_independent.error_catalog_history(project_id,catalog_id,payload)
            VALUES(@project,@id,@payload) ON CONFLICT(project_id,catalog_id) DO NOTHING;
            INSERT INTO plc_independent.error_catalog_active(project_id,catalog_id) VALUES(@project,@id)
            ON CONFLICT(project_id) DO UPDATE SET catalog_id=excluded.catalog_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("project", catalog.ProjectId);
        command.Parameters.AddWithValue("id", catalog.CatalogId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(catalog));
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<PersistedErrorCatalog?> LoadAsync(long project, CancellationToken token) {
        await using var connection = new NpgsqlConnection(pipeline.ConnectionString);
        await connection.OpenAsync(token);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('plc_independent.error_catalog_active') IS NOT NULL", connection))
            if (!(bool)(await exists.ExecuteScalarAsync(token))!) return null;
        await using var command = new NpgsqlCommand("""
            SELECT h.payload::text FROM plc_independent.error_catalog_active a
            JOIN plc_independent.error_catalog_history h ON h.project_id=a.project_id AND h.catalog_id=a.catalog_id
            WHERE a.project_id=@project
            """, connection);
        command.Parameters.AddWithValue("project", project);
        var json = await command.ExecuteScalarAsync(token) as string;
        return json == null ? null : JsonSerializer.Deserialize<PersistedErrorCatalog>(json)
            ?? throw new PipelineException("InvalidPersistedErrorCatalog");
    }
}
