#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

internal static class PipelineRecovery
{
    public static async Task WriteAsync(Func<Task> write, CancellationToken token) {
        for (int attempt = 0; ; attempt++) {
            token.ThrowIfCancellationRequested();
            try { await write(); return; }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested &&
                (ex is TimeoutException || ex is NpgsqlException { IsTransient: true })) {
                await Task.Delay(attempt == 0 ? 250 : 750, token);
            }
        }
    }
}
