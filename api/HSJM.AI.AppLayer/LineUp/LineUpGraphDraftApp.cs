#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpGraphDraftApp : ISelfScopedAutoInject
{
    private readonly ILineUpGraphDraftRepository repository;

    public LineUpGraphDraftApp(ILineUpGraphDraftRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<LineUpGraphDraftPersistenceResult> CreateDraftAsync(
        LineUpGraphDraftCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return repository.SaveDraftAsync(candidate.ToWrite(), cancellationToken);
    }
}
