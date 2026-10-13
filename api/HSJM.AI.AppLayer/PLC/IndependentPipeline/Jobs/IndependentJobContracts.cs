#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Jobs;

public sealed record PipelineJob(Guid JobId, long ProjectId, Guid RequestId, Guid RunId,
    bool RegenerateConstraints, string Status, int Attempt, int MaxAttempts, bool CancellationRequested,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LeaseUntil, string? ErrorCode, Guid? RetryOfJobId);
public sealed record EnqueueIndependentJobRequest(Guid RequestId, bool RegenerateConstraints = false);
public sealed record RetryIndependentJobRequest(Guid RequestId);
public sealed record IndependentJobOptions(bool Enabled, int Concurrency, int MaxAttempts, int LeaseSeconds, int PollSeconds)
{
    public static IndependentJobOptions Read(IConfiguration configuration) {
        var section = configuration.GetSection("IndependentPlcPipeline:Jobs");
        var value = new IndependentJobOptions(section.GetValue<bool>("WorkerEnabled"), section.GetValue("Concurrency", 1),
            section.GetValue("MaxAttempts", 3), section.GetValue("LeaseSeconds", 60), section.GetValue("PollSeconds", 2));
        if (value.Concurrency is < 1 or > 8 || value.MaxAttempts is < 1 or > 10 ||
            value.LeaseSeconds is < 15 or > 300 || value.PollSeconds is < 1 or > 30)
            throw new PipelineException("InvalidJobOptions");
        return value;
    }
}
public static class IndependentJobPolicy
{
    public static void ValidateDuplicate(PipelineJob job, long projectId, bool regenerate, Guid? retryOf) {
        if (job.ProjectId != projectId || job.RegenerateConstraints != regenerate || job.RetryOfJobId != retryOf)
            throw new PipelineException("JobIdempotencyConflict");
    }
    public static void ValidateRetry(PipelineJob job) {
        if (job.Status is not ("Failed" or "PartialFailure")) throw new PipelineException("JobNotFailed");
    }
}
public enum JobHeartbeat { Continue, CancelRequested, LeaseLost }
public interface IIndependentJobLease
{
    PipelineJob Job { get; }
    Task<JobHeartbeat> HeartbeatAsync(CancellationToken token);
    Task CompleteAsync(string status, string? errorCode, CancellationToken token);
}

/// <summary>Cancellation reaches the pipeline; a lost owner never publishes a terminal job status.</summary>
public static class IndependentJobProcessor
{
    public static async Task RunAsync(IIndependentJobLease lease, Func<CancellationToken, Task<string>> execute,
        TimeSpan heartbeatInterval, CancellationToken stoppingToken) {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ownership = JobHeartbeat.Continue;
        async Task Heartbeat() {
            try {
                while (true) {
                    await Task.Delay(heartbeatInterval, heartbeatStop.Token);
                    ownership = await lease.HeartbeatAsync(heartbeatStop.Token);
                    if (ownership != JobHeartbeat.Continue) { execution.Cancel(); return; }
                }
            } catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch { ownership = JobHeartbeat.LeaseLost; execution.Cancel(); }
        }
        var heartbeats = Heartbeat(); string status; string? code = null;
        try { status = await execute(execution.Token); }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { status = "Cancelled"; code = "Cancelled"; }
        catch (Exception ex) { status = "Failed"; code = ex is PipelineException p ? p.Code : "JobExecutionFailed"; }
        heartbeatStop.Cancel(); await heartbeats;
        // Host shutdown leaves a recoverable Running lease. Explicit cancellation is terminal.
        if (ownership == JobHeartbeat.LeaseLost || stoppingToken.IsCancellationRequested) return;
        if (ownership == JobHeartbeat.CancelRequested) { status = "Cancelled"; code = "Cancelled"; }
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await lease.CompleteAsync(status, code, finish.Token);
    }
}
