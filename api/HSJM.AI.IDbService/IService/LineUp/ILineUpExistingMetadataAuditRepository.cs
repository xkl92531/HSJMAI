namespace HSJM.AI.IDbService.LineUp;

public interface ILineUpExistingMetadataAuditRepository
{
    Task<LineUpExistingGraphMetadataAudit> LoadGraphMetadataAsync(
        long plcProjectId,
        CancellationToken cancellationToken);
}
