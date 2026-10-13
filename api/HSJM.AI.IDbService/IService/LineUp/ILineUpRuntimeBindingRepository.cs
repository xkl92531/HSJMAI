using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpRuntimeBindingRepository
{
    Task<LineUpRuntimeBindingData?> LoadBindingAsync(
        long bindingId,
        CancellationToken cancellationToken);

    Task<LineUpRuntimeBindingContext?> LoadAsync(
        long bindingId,
        CancellationToken cancellationToken);

    Task<bool> SavePreparedAsync(
        long bindingId,
        string runtimeBindingJson,
        CancellationToken cancellationToken);

    Task<bool> SaveValidatedAsync(
        long bindingId,
        string expectedRuntimeBindingJson,
        DateTime validatedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> SetMonitoringAsync(
        long bindingId,
        bool enabled,
        CancellationToken cancellationToken);
}
