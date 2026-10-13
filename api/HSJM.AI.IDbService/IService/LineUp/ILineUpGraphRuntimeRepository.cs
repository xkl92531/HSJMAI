using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpGraphRuntimeRepository
{
    Task<LineUpGraphRuntimeBindingStatusData?> LoadBindingAsync(
        long bindingId,
        CancellationToken cancellationToken);

    Task<LineUpGraphRuntimeContext?> LoadContextAsync(
        long bindingId,
        CancellationToken cancellationToken);

    Task<bool> SavePreparedAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string runtimeBindingJson,
        CancellationToken cancellationToken);

    Task<bool> ResetAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        CancellationToken cancellationToken);

    Task<bool> SaveValidatedAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string expectedRuntimeBindingJson,
        string runtimeValidationJson,
        string validationRevision,
        DateTime validatedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> SetMonitoringAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string expectedDefinitionRevision,
        bool enabled,
        CancellationToken cancellationToken);

    Task<bool> InvalidateAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string expectedDefinitionRevision,
        CancellationToken cancellationToken);
}
