#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;
using Npgsql;
using NpgsqlTypes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Test-only business generations. Rejected decisions remain audit data, never accepted rules.</summary>
public sealed class BusinessPipelineStore(PostgresPipelineStore sourceStore) : ISelfScopedAutoInject, IBusinessPipelineStore
{
    public const string MigrationSql = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.business_description (
          project_id bigint NOT NULL,lu_id bigint NOT NULL,fingerprint text NOT NULL,payload jsonb NOT NULL,
          PRIMARY KEY(project_id,lu_id,fingerprint),UNIQUE(project_id,fingerprint));
        ALTER TABLE plc_independent.business_description ADD COLUMN IF NOT EXISTS payload_gzip bytea;
        ALTER TABLE plc_independent.business_description ADD COLUMN IF NOT EXISTS payload_hash text;
        CREATE TABLE IF NOT EXISTS plc_independent.business_result (
          project_id bigint NOT NULL,lu_id bigint NOT NULL,run_id uuid NOT NULL,
          source_fingerprint text NOT NULL,description_fingerprint text NOT NULL,model_version text NOT NULL,
          result_status text NOT NULL CHECK(result_status IN ('Completed','CompletedEmpty','PartialFailure')),
          accepted_count integer NOT NULL CHECK(accepted_count >= 0),payload jsonb NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY(project_id,lu_id,run_id),
          FOREIGN KEY(project_id,lu_id,description_fingerprint)
            REFERENCES plc_independent.business_description(project_id,lu_id,fingerprint),
          CHECK((result_status <> 'CompletedEmpty' OR accepted_count=0) AND
                (result_status <> 'Completed' OR accepted_count>0)));
        CREATE TABLE IF NOT EXISTS plc_independent.business_active (
          project_id bigint NOT NULL,lu_id bigint NOT NULL,run_id uuid NOT NULL,
          PRIMARY KEY(project_id,lu_id),FOREIGN KEY(project_id,lu_id,run_id)
            REFERENCES plc_independent.business_result(project_id,lu_id,run_id));
        """;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token) {
        // Accessing ConnectionString executes the existing explicit _test / expected-database guard.
        var connection = new NpgsqlConnection(sourceStore.ConnectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task InitializeAsync(CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand(MigrationSql,connection,transaction);
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyDictionary<string,BusinessDescription>> LoadDescriptionsAsync(long project,CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT fingerprint,lu_id,payload::text,payload_gzip,payload_hash FROM plc_independent.business_description WHERE project_id=@p",connection);
        command.Parameters.AddWithValue("p",project);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string,BusinessDescription>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token)) {
            var description = reader.IsDBNull(3)
                ? BusinessPayloadCodec.DecodeLegacy(reader.GetString(2))
                : BusinessPayloadCodec.Decode((byte[])reader[3],reader.IsDBNull(4) ? null : reader.GetString(4));
            if (description.Fingerprint != reader.GetString(0) || description.LogicUnitId != reader.GetInt64(1))
                throw new PipelineException("InvalidBusinessDescriptionPayload");
            result.Add(description.Fingerprint,description);
        }
        return result;
    }

    public async Task SaveDescriptionsAsync(long project,IReadOnlyList<BusinessDescription> descriptions,CancellationToken token) {
        if (descriptions.Count == 0) return;
        if (descriptions.Select(d => d.Fingerprint).Distinct(StringComparer.Ordinal).Count() != descriptions.Count)
            throw new PipelineException("DuplicateBusinessDescription");
        // Encode before taking the project lock. New JSON uses relaxed Unicode escaping only inside gzip.
        var encoded = new Dictionary<string,(BusinessDescription Description,BusinessPayloadCodec.Encoded Payload)>(StringComparer.Ordinal);
        foreach (var description in descriptions) {
            token.ThrowIfCancellationRequested(); encoded.Add(description.Fingerprint,(description,BusinessPayloadCodec.Encode(description)));
        }
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await LockProjectAsync(connection,transaction,project,token);
        // Compressed generations compare their canonical digest without retransmitting large blobs.
        // Old JSONB rows are decoded and canonically hashed, so PostgreSQL object-key ordering is irrelevant.
        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var collision = new NpgsqlCommand("""
            SELECT fingerprint,lu_id,CASE WHEN payload_gzip IS NULL THEN payload::text END,
              payload_gzip IS NOT NULL,payload_hash
            FROM plc_independent.business_description WHERE project_id=@p AND fingerprint=ANY(@keys)
            """,connection,transaction)) {
            collision.Parameters.AddWithValue("p",project); collision.Parameters.AddWithValue("keys",encoded.Keys.ToArray());
            await using var reader = await collision.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) {
                var key = reader.GetString(0); var requested = encoded[key];
                var hash = reader.GetBoolean(3)
                    ? reader.IsDBNull(4) ? throw new PipelineException("InvalidBusinessDescriptionPayload") : reader.GetString(4)
                    : BusinessPayloadCodec.Hash(BusinessPayloadCodec.DecodeLegacy(reader.GetString(2)));
                if (reader.GetInt64(1) != requested.Description.LogicUnitId || hash != requested.Payload.Hash)
                    throw new PipelineException("BusinessDescriptionFingerprintCollision");
                existing.Add(key);
            }
        }
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE business_description_batch (LIKE plc_independent.business_description INCLUDING DEFAULTS) ON COMMIT DROP",connection,transaction))
            await create.ExecuteNonQueryAsync(token);
        using (var copy = connection.BeginBinaryImport("COPY business_description_batch(project_id,lu_id,fingerprint,payload,payload_gzip,payload_hash) FROM STDIN (FORMAT BINARY)")) {
            foreach (var description in descriptions) {
                if (existing.Contains(description.Fingerprint)) continue;
                token.ThrowIfCancellationRequested(); copy.StartRow();
                copy.Write(project,NpgsqlDbType.Bigint);
                copy.Write(description.LogicUnitId,NpgsqlDbType.Bigint);
                copy.Write(description.Fingerprint,NpgsqlDbType.Text);
                // JSON null preserves the legacy column contract; legacy writers still insert their original JSON.
                copy.Write("null",NpgsqlDbType.Jsonb);
                copy.Write(encoded[description.Fingerprint].Payload.Gzip,NpgsqlDbType.Bytea);
                copy.Write(encoded[description.Fingerprint].Payload.Hash,NpgsqlDbType.Text);
            }
            copy.Complete();
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO plc_independent.business_description(project_id,lu_id,fingerprint,payload,payload_gzip,payload_hash) SELECT project_id,lu_id,fingerprint,payload,payload_gzip,payload_hash FROM business_description_batch ON CONFLICT DO NOTHING",connection,transaction))
            await insert.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    /// <summary>Explicit opt-in repack of one project's legacy JSON, never an automatic migration.</summary>
    public async Task<int> RepackDescriptionsAsync(long project,CancellationToken token) {
        await using var connection = await OpenAsync(token);
        var total = 0;
        while (true) {
            token.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(token);
            await LockProjectAsync(connection,transaction,project,token);
            var batch = new List<(string Fingerprint,long LogicUnitId,string RowVersion,BusinessPayloadCodec.Encoded Payload)>();
            var compressedBytes = 0L;
            // Retain only compressed rows, at most eight rows or 16 MiB (plus one oversized row).
            // Source JSON and temporary canonical UTF8 are discarded after encoding each row.
            await using (var select = new NpgsqlCommand("""
                SELECT fingerprint,lu_id,payload::text,xmin::text,payload_hash
                FROM plc_independent.business_description
                WHERE project_id=@p AND payload_gzip IS NULL ORDER BY lu_id,fingerprint LIMIT 8 FOR UPDATE
                """,connection,transaction)) {
                select.Parameters.AddWithValue("p",project);
                await using var reader = await select.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) {
                    token.ThrowIfCancellationRequested();
                    var description = BusinessPayloadCodec.DecodeLegacy(reader.GetString(2));
                    if (description.Fingerprint != reader.GetString(0) || description.LogicUnitId != reader.GetInt64(1))
                        throw new PipelineException("InvalidBusinessDescriptionPayload");
                    var payload = BusinessPayloadCodec.Encode(description);
                    if (!reader.IsDBNull(4) && reader.GetString(4) != payload.Hash)
                        throw new PipelineException("BusinessDescriptionDigestMismatch");
                    batch.Add((description.Fingerprint,description.LogicUnitId,reader.GetString(3),payload));
                    compressedBytes += payload.Gzip.Length;
                    if (compressedBytes >= 16 * 1024 * 1024) break;
                }
            }
            foreach (var row in batch) {
                await using var update = new NpgsqlCommand("""
                    UPDATE plc_independent.business_description SET payload='null'::jsonb,payload_gzip=@gzip,payload_hash=@hash
                    WHERE project_id=@p AND lu_id=@l AND fingerprint=@f AND payload_gzip IS NULL AND xmin::text=@version
                    """,connection,transaction);
                update.Parameters.AddWithValue("p",project); update.Parameters.AddWithValue("l",row.LogicUnitId);
                update.Parameters.AddWithValue("f",row.Fingerprint); update.Parameters.AddWithValue("version",row.RowVersion);
                update.Parameters.AddWithValue("gzip",NpgsqlDbType.Bytea,row.Payload.Gzip); update.Parameters.AddWithValue("hash",row.Payload.Hash);
                if (await update.ExecuteNonQueryAsync(token) != 1) throw new PipelineException("BusinessDescriptionRepackConflict");
            }
            await transaction.CommitAsync(token);
            total += batch.Count;
            if (batch.Count == 0) return total;
        }
    }

    public async Task SaveResultsAsync(long project,Guid run,string sourceFingerprint,IReadOnlyList<BusinessUnitResult> results,CancellationToken token) {
        if (results.Count == 0) return;
        if (results.Select(r => r.LogicUnitId).Distinct().Count() != results.Count) throw new PipelineException("DuplicateCommitUnit");
        foreach (var result in results) {
            var accepted = result.Decisions.Count(d => d.Status == "Accepted");
            var incomplete = result.Decisions.Any(d => d.Status != "Accepted" && !d.IsConclusiveRejection);
            if (result.Status is not ("Completed" or "CompletedEmpty" or "PartialFailure") ||
                result.Decisions.Any(d => d.Status is not ("Accepted" or "Rejected")) ||
                (result.Status == "Completed" && (accepted == 0 || incomplete)) ||
                (result.Status == "CompletedEmpty" && (accepted != 0 || incomplete)))
                throw new PipelineException("InvalidBusinessResultStatus");
        }
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,token);
        await LockProjectAsync(connection,transaction,project,token);
        await using (var sourceLock = new NpgsqlCommand("LOCK TABLE ai_plc_project,ai_plc_block,ai_plc_logicunit,ai_plc_blockcall,ai_plc_iotag IN SHARE MODE",connection,transaction))
            await sourceLock.ExecuteNonQueryAsync(token);
        var source = await PostgresPipelineStore.ReadSourceAsync(connection,transaction,project,token);
        var units = source.Units.ToDictionary(u => u.Id);
        if (source.Fingerprint() != sourceFingerprint || results.Any(r =>
                !units.TryGetValue(r.LogicUnitId,out var unit) || BusinessDescriptionBuilder.Key(sourceFingerprint,unit) != r.DescriptionFingerprint))
            throw new PipelineException("SourceChangedBeforeCommit");
        await using (var superseded = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM plc_independent.business_result r
              LEFT JOIN plc_independent.business_active a ON a.project_id=r.project_id AND a.lu_id=r.lu_id
              WHERE r.project_id=@p AND r.run_id=@r AND (a.run_id IS NULL OR a.run_id<>r.run_id))
            """,connection,transaction)) {
            superseded.Parameters.AddWithValue("p",project); superseded.Parameters.AddWithValue("r",run);
            if ((bool)(await superseded.ExecuteScalarAsync(token))!) throw new PipelineException("CommitSuperseded");
        }
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE business_result_batch (LIKE plc_independent.business_result INCLUDING DEFAULTS INCLUDING CONSTRAINTS) ON COMMIT DROP",connection,transaction))
            await create.ExecuteNonQueryAsync(token);
        using (var copy = connection.BeginBinaryImport("COPY business_result_batch(project_id,lu_id,run_id,source_fingerprint,description_fingerprint,model_version,result_status,accepted_count,payload) FROM STDIN (FORMAT BINARY)")) {
            foreach (var result in results) {
                token.ThrowIfCancellationRequested(); copy.StartRow();
                copy.Write(project,NpgsqlDbType.Bigint);
                copy.Write(result.LogicUnitId,NpgsqlDbType.Bigint);
                copy.Write(run,NpgsqlDbType.Uuid);
                copy.Write(sourceFingerprint,NpgsqlDbType.Text);
                copy.Write(result.DescriptionFingerprint,NpgsqlDbType.Text);
                copy.Write(result.ModelVersion,NpgsqlDbType.Text);
                copy.Write(result.Status,NpgsqlDbType.Text);
                copy.Write(result.Decisions.Count(d => d.Status == "Accepted"),NpgsqlDbType.Integer);
                copy.Write(JsonSerializer.Serialize(result),NpgsqlDbType.Jsonb);
            }
            copy.Complete();
        }
        await using (var mismatch = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM business_result_batch b JOIN plc_independent.business_result r
              ON r.project_id=b.project_id AND r.lu_id=b.lu_id AND r.run_id=b.run_id
              WHERE r.source_fingerprint<>b.source_fingerprint OR r.payload<>b.payload)
            """,connection,transaction))
            if ((bool)(await mismatch.ExecuteScalarAsync(token))!) throw new PipelineException("RunGenerationMismatch");
        // The result row is immutable commit history, including successful zero-rule generations.
        // The FK checks every persisted description; one bad row rolls back results and activation together.
        await using (var activate = new NpgsqlCommand("""
            INSERT INTO plc_independent.business_result SELECT * FROM business_result_batch ON CONFLICT DO NOTHING;
            INSERT INTO plc_independent.business_active(project_id,lu_id,run_id)
              SELECT project_id,lu_id,run_id FROM business_result_batch
              ON CONFLICT(project_id,lu_id) DO UPDATE SET run_id=excluded.run_id;
            """,connection,transaction)) await activate.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<BusinessUnitResult>> LoadResultsAsync(long project,string sourceFingerprint,string modelVersion,CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction))
            await readOnly.ExecuteNonQueryAsync(token);
        var source = await PostgresPipelineStore.ReadSourceAsync(connection,transaction,project,token);
        if (source.Fingerprint() != sourceFingerprint) return Array.Empty<BusinessUnitResult>();
        var descriptionKeys = source.Units.ToDictionary(u => u.Id,u => BusinessDescriptionBuilder.Key(sourceFingerprint,u));
        await using var command = new NpgsqlCommand("""
            SELECT r.lu_id,r.description_fingerprint,r.payload::text FROM plc_independent.business_active a
              JOIN plc_independent.business_result r ON r.project_id=a.project_id AND r.lu_id=a.lu_id AND r.run_id=a.run_id
              WHERE r.project_id=@p AND r.source_fingerprint=@s AND r.model_version=@m ORDER BY r.lu_id
            """,connection,transaction);
        command.Parameters.AddWithValue("p",project); command.Parameters.AddWithValue("s",sourceFingerprint); command.Parameters.AddWithValue("m",modelVersion);
        var results = new List<BusinessUnitResult>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) {
                if (!descriptionKeys.TryGetValue(reader.GetInt64(0),out var expected) || expected != reader.GetString(1)) continue;
                results.Add(JsonSerializer.Deserialize<BusinessUnitResult>(reader.GetString(2))
                    ?? throw new PipelineException("InvalidBusinessResultPayload"));
            }
        await transaction.CommitAsync(token); return results;
    }

    private static async Task LockProjectAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,long project,CancellationToken token) {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",connection,transaction);
        command.Parameters.AddWithValue("key","plc-independent-business-commit:"+project); await command.ExecuteNonQueryAsync(token);
    }
}
