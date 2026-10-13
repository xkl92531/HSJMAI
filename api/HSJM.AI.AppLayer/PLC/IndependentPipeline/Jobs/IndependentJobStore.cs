#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Jobs;

public sealed class IndependentJobStore(PostgresPipelineStore pipeline, IConfiguration configuration) : ISelfScopedAutoInject
{
    public const string MigrationSql = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.job (
          job_id uuid PRIMARY KEY, project_id bigint NOT NULL, request_id uuid NOT NULL UNIQUE,
          run_id uuid NOT NULL, regenerate boolean NOT NULL, status text NOT NULL,
          attempt integer NOT NULL DEFAULT 0, max_attempts integer NOT NULL,
          cancellation_requested boolean NOT NULL DEFAULT false,
          created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
          lease_until timestamptz, owner_id uuid, error_code text, retry_of_job_id uuid,
          CHECK (status IN ('Queued','Running','Completed','PartialFailure','Failed','Cancelled')),
          CHECK (attempt >= 0 AND max_attempts BETWEEN 1 AND 10));
        CREATE INDEX IF NOT EXISTS independent_job_queue ON plc_independent.job(status,created_at);
        CREATE INDEX IF NOT EXISTS independent_job_project ON plc_independent.job(project_id,created_at);
        """;
    private const string Columns = "job_id,project_id,request_id,run_id,regenerate,status,attempt,max_attempts,cancellation_requested,created_at,updated_at,lease_until,error_code,retry_of_job_id";
    // Separate key spaces: 64-bit project locks; two-int slot/dispatcher locks.
    private const int LockNamespace = 1852067696;
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token) {
        // Dedicated non-pooled connections ensure session advisory locks release on disposal.
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(pipeline.ConnectionString) { Pooling = false }.ConnectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns,-1)", connection, transaction)) {
            gate.Parameters.AddWithValue("ns", LockNamespace); await gate.ExecuteNonQueryAsync(token);
        }
        await using var command = new NpgsqlCommand(MigrationSql, connection, transaction);
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
    }
    public async Task<PipelineJob> EnqueueAsync(long projectId, Guid requestId, bool regenerate, Guid? retryOf, CancellationToken token) {
        if (projectId <= 0 || requestId == Guid.Empty) throw new PipelineException("JobRequestInvalid");
        var options = IndependentJobOptions.Read(configuration);
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO plc_independent.job(job_id,project_id,request_id,run_id,regenerate,status,max_attempts,retry_of_job_id)
            VALUES(@id,@project,@request,@run,@regenerate,'Queued',@max,@retry)
            ON CONFLICT(request_id) DO NOTHING;
            SELECT {Columns} FROM plc_independent.job WHERE request_id=@request;
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("request", requestId); command.Parameters.AddWithValue("run", Guid.NewGuid());
        command.Parameters.AddWithValue("regenerate", regenerate); command.Parameters.AddWithValue("max", options.MaxAttempts);
        command.Parameters.AddWithValue("retry", NpgsqlDbType.Uuid, (object?)retryOf ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token); var job = Read(reader);
        IndependentJobPolicy.ValidateDuplicate(job, projectId, regenerate, retryOf); return job;
    }
    public async Task<PipelineJob?> GetAsync(Guid id, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM plc_independent.job WHERE job_id=@id", connection);
        command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }
    public async Task<IReadOnlyList<PipelineJob>> ListAsync(long projectId, int limit, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM plc_independent.job WHERE project_id=@project ORDER BY created_at DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(token); var result = new List<PipelineJob>();
        while (await reader.ReadAsync(token)) result.Add(Read(reader)); return result;
    }
    public async Task<PipelineJob?> CancelAsync(Guid id, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand($"""
            UPDATE plc_independent.job SET cancellation_requested=true, updated_at=now(),
              status=CASE WHEN status='Queued' THEN 'Cancelled' ELSE status END
            WHERE job_id=@id AND status IN ('Queued','Running');
            SELECT {Columns} FROM plc_independent.job WHERE job_id=@id;
            """, connection);
        command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }
    public async Task<PipelineJob?> RetryAsync(Guid id, Guid requestId, CancellationToken token) {
        var previous = await GetAsync(id, token); if (previous is null) return null;
        IndependentJobPolicy.ValidateRetry(previous);
        // Complete generations stay cached; only failed or stale units execute again.
        return await EnqueueAsync(previous.ProjectId, requestId, false, id, token);
    }
    public async Task<IndependentJobLease?> ClaimAsync(int slot, CancellationToken token) {
        var options = IndependentJobOptions.Read(configuration);
        if (slot < 0 || slot >= options.Concurrency) throw new PipelineException("JobSlotInvalid");
        var connection = await OpenAsync(token); var retained = false;
        try {
            await using (var slotLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(@ns,@slot)", connection)) {
                slotLock.Parameters.AddWithValue("ns", LockNamespace); slotLock.Parameters.AddWithValue("slot", slot);
                if (!(bool)(await slotLock.ExecuteScalarAsync(token))!) return null;
            }
            await using var transaction = await connection.BeginTransactionAsync(token);
            // Serialize claims, while the long-running execution remains outside this transaction.
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns,-1)", connection, transaction)) {
                gate.Parameters.AddWithValue("ns", LockNamespace); await gate.ExecuteNonQueryAsync(token);
            }
            var candidates = new List<PipelineJob>();
            await using (var select = new NpgsqlCommand($"""
                SELECT {Columns} FROM plc_independent.job j
                WHERE (status='Queued' OR (status='Running' AND lease_until < now()))
                  AND NOT EXISTS (SELECT 1 FROM plc_independent.job other WHERE other.project_id=j.project_id
                    AND other.job_id<>j.job_id AND other.status='Running' AND other.lease_until >= now())
                ORDER BY created_at LIMIT 100 FOR UPDATE SKIP LOCKED
                """, connection, transaction)) {
                await using var reader = await select.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) candidates.Add(Read(reader));
            }
            foreach (var candidate in candidates) {
                await using var projectLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended('plc_independent/project/' || @project::text, 0))", connection, transaction);
                projectLock.Parameters.AddWithValue("project", candidate.ProjectId);
                if (!(bool)(await projectLock.ExecuteScalarAsync(token))!) continue;
                if (candidate.CancellationRequested || candidate.Attempt >= candidate.MaxAttempts) {
                    await using var terminate = new NpgsqlCommand("""
                        UPDATE plc_independent.job SET status=@status,error_code=@code,owner_id=NULL,lease_until=NULL,updated_at=now()
                        WHERE job_id=@id
                        """, connection, transaction);
                    terminate.Parameters.AddWithValue("id", candidate.JobId);
                    terminate.Parameters.AddWithValue("status", candidate.CancellationRequested ? "Cancelled" : "Failed");
                    terminate.Parameters.AddWithValue("code", candidate.CancellationRequested ? "Cancelled" : "LeaseAttemptsExhausted");
                    await terminate.ExecuteNonQueryAsync(token);
                    await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('plc_independent/project/' || @project::text, 0))", connection, transaction);
                    unlock.Parameters.AddWithValue("project", candidate.ProjectId); await unlock.ExecuteNonQueryAsync(token);
                    continue;
                }
                var owner = Guid.NewGuid();
                await using var claim = new NpgsqlCommand($"""
                    UPDATE plc_independent.job SET status='Running',attempt=attempt+1,owner_id=@owner,
                      run_id=CASE WHEN attempt=0 THEN run_id ELSE @run END,
                      lease_until=now()+make_interval(secs => @seconds),updated_at=now(),error_code=NULL
                    WHERE job_id=@id RETURNING {Columns}
                    """, connection, transaction);
                claim.Parameters.AddWithValue("id", candidate.JobId); claim.Parameters.AddWithValue("owner", owner);
                claim.Parameters.AddWithValue("run", Guid.NewGuid()); claim.Parameters.AddWithValue("seconds", options.LeaseSeconds);
                PipelineJob job;
                await using (var reader = await claim.ExecuteReaderAsync(token)) { await reader.ReadAsync(token); job = Read(reader); }
                await transaction.CommitAsync(token); retained = true;
                return new IndependentJobLease(connection, job, owner, options.LeaseSeconds);
            }
            await transaction.CommitAsync(token); return null;
        } finally { if (!retained) await connection.DisposeAsync(); }
    }
    private static PipelineJob Read(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetInt64(1),r.GetGuid(2),r.GetGuid(3),r.GetBoolean(4),
        r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetBoolean(8),r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<DateTimeOffset>(10),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),r.IsDBNull(12) ? null : r.GetString(12),r.IsDBNull(13) ? null : r.GetGuid(13));
}

public sealed class IndependentJobLease(NpgsqlConnection connection, PipelineJob job, Guid owner, int leaseSeconds)
    : IIndependentJobLease, IAsyncDisposable
{
    public PipelineJob Job { get; } = job;
    public async Task<JobHeartbeat> HeartbeatAsync(CancellationToken token) {
        await using var command = new NpgsqlCommand("""
            UPDATE plc_independent.job SET lease_until=now()+make_interval(secs => @seconds),updated_at=now()
            WHERE job_id=@id AND owner_id=@owner AND status='Running' AND lease_until >= now()
            RETURNING cancellation_requested
            """, connection);
        command.Parameters.AddWithValue("seconds",leaseSeconds); command.Parameters.AddWithValue("id",Job.JobId);
        command.Parameters.AddWithValue("owner",owner); var result = await command.ExecuteScalarAsync(token);
        return result is bool cancel ? cancel ? JobHeartbeat.CancelRequested : JobHeartbeat.Continue : JobHeartbeat.LeaseLost;
    }
    public async Task CompleteAsync(string status, string? errorCode, CancellationToken token) {
        await using var command = new NpgsqlCommand("""
            UPDATE plc_independent.job SET status=CASE WHEN cancellation_requested THEN 'Cancelled' ELSE @status END,
              error_code=CASE WHEN cancellation_requested THEN 'Cancelled' ELSE @code END,
              lease_until=NULL,owner_id=NULL,updated_at=now()
            WHERE job_id=@id AND owner_id=@owner AND status='Running' AND lease_until >= now()
            """, connection);
        command.Parameters.AddWithValue("status",status); command.Parameters.AddWithValue("code",NpgsqlDbType.Text,(object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("id",Job.JobId); command.Parameters.AddWithValue("owner",owner);
        await command.ExecuteNonQueryAsync(token);
    }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
