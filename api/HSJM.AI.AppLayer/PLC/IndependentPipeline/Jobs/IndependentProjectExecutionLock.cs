#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Jobs;

/// <summary>Shared by synchronous API, durable workers, and explicitly opted-in command-line runs.</summary>
public sealed class IndependentProjectExecutionLock : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly CancellationTokenSource lifetime;
    private readonly Task monitor;
    private IndependentProjectExecutionLock(NpgsqlConnection connection, CancellationToken token) {
        this.connection = connection;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        monitor = MonitorAsync();
    }
    public CancellationToken Token => lifetime.Token;
    public static async Task<IndependentProjectExecutionLock> AcquireAsync(PostgresPipelineStore store,
        long projectId, CancellationToken token) {
        if (projectId <= 0) throw new PipelineException("ProjectIdRequired");
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(store.ConnectionString) { Pooling = false }.ConnectionString);
        try {
            await connection.OpenAsync(token);
            while (true) {
                token.ThrowIfCancellationRequested();
                await using var command = new NpgsqlCommand(
                    "SELECT pg_try_advisory_lock(hashtextextended('plc_independent/execution/' || @project::text, 0))", connection);
                command.Parameters.AddWithValue("project", projectId);
                if ((bool)(await command.ExecuteScalarAsync(token))!) return new(connection, token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
        } catch { await connection.DisposeAsync(); throw; }
    }
    private async Task MonitorAsync() {
        try {
            while (true) {
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                await using var command = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout = 5 };
                await command.ExecuteScalarAsync(lifetime.Token);
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { lifetime.Cancel(); }
    }
    public async ValueTask DisposeAsync() {
        lifetime.Cancel(); await monitor;
        await connection.DisposeAsync(); lifetime.Dispose();
    }
}
