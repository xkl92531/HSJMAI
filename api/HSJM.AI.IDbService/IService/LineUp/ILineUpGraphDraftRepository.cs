using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpGraphDraftRepository
{
    Task<LineUpGraphDraftPersistenceResult> SaveDraftAsync(
        LineUpGraphDraftWrite draft,
        CancellationToken cancellationToken);
}
