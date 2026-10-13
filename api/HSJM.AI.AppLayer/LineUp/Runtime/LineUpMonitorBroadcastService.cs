using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public interface ILineUpMonitorBroadcastPump
{
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class LineUpMonitorBroadcastService :
    ILineUpMonitorDeltaPublisher,
    ILineUpMonitorBroadcastPump
{
    public const string DeltaMessage = "LineUpMonitorDelta";
    private readonly IHubContext<LineUpMonitorHub> hubContext;
    private readonly ILogger<LineUpMonitorBroadcastService> logger;
    private readonly Channel<LineUpRuntimeDelta> queue = Channel.CreateBounded<LineUpRuntimeDelta>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public LineUpMonitorBroadcastService(
        IHubContext<LineUpMonitorHub> hubContext,
        ILogger<LineUpMonitorBroadcastService> logger)
    {
        this.hubContext = hubContext;
        this.logger = logger;
    }

    public bool TryPublish(LineUpRuntimeDelta delta) => delta is not null && queue.Writer.TryWrite(delta);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var delta in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var clients = hubContext.Clients
                    .Group(LineUpMonitorHub.BuildGroup(new(delta.BindingId, delta.SequenceSourceKey)));
                if (delta.RequiresSnapshot)
                {
                    await clients.SendAsync(LineUpMonitorHub.SnapshotRequiredMessage, new
                    {
                        bindingId = delta.BindingId.ToString(),
                        sequenceSourceKey = delta.SequenceSourceKey,
                        definitionRevision = delta.DefinitionRevision,
                        snapshotRevision = delta.ToSnapshotRevision.ToString()
                    }, cancellationToken);
                }
                else
                {
                    await clients.SendAsync(DeltaMessage, LineUpMonitorWireProjection.Delta(delta), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (System.Exception error)
            {
                logger.LogError(error,
                    "LineUp monitor delta broadcast failed for binding {BindingId} sequence {SequenceSourceKey}.",
                    delta.BindingId,
                    delta.SequenceSourceKey);
            }
        }
    }
}
