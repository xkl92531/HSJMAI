using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpWorkspaceRepository
{
    Task<LineUpWorkspaceLookupData> LoadAsync(
        long? requestedLineId,
        int recentImportLimit,
        CancellationToken cancellationToken);
}
