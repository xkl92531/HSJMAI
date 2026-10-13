#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Jobs;

/// <summary>RegisterService's ISingletonAutoInject scan registers this as IHostedService.</summary>
public sealed class IndependentJobWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<IndependentJobWorker> logger) : BackgroundService, ISingletonAutoInject
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!configuration.GetValue<bool>("IndependentPlcPipeline:Jobs:WorkerEnabled")) return;
        var options = IndependentJobOptions.Read(configuration);
        if (!options.Enabled) return;
        // No DDL here. Operators must explicitly initialize the guarded test database first.
        await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(slot => WorkSlot(slot, options, stoppingToken)));
    }
    private async Task WorkSlot(int slot, IndependentJobOptions options, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IndependentJobStore>();
                await using var lease = await store.ClaimAsync(slot, token);
                if (lease != null) {
                    var pipeline = scope.ServiceProvider.GetRequiredService<IndependentPipelineService>();
                    await IndependentJobProcessor.RunAsync(lease, async executionToken => {
                        await pipeline.CreateRunAsync(lease.Job.RunId, lease.Job.ProjectId, executionToken);
                        var run = await pipeline.ExecuteAsync(lease.Job.RunId, lease.Job.ProjectId,
                            lease.Job.RegenerateConstraints && lease.Job.Attempt == 1, executionToken);
                        return run.Status;
                    }, TimeSpan.FromSeconds(options.LeaseSeconds / 3), token);
                    continue;
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) {
                // Do not log connection strings, model credentials, or raw provider exceptions.
                logger.LogWarning("Independent PLC job slot {Slot} paused: {Code}", slot,
                    ex is PipelineException p ? p.Code : "JobStoreUnavailable");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
