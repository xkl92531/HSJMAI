using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpImageMarkRepository
{
    Task<LineUpImageWorkspaceData?> GetStationWorkspaceAsync(
        long subStationId,
        CancellationToken cancellationToken);

    Task<LineUpImageHierarchyData?> GetHierarchyAsync(
        long subStationId,
        CancellationToken cancellationToken);

    Task<LineUpImageData?> GetImageAsync(
        long imageId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LineUpImageBomData>> GetBomsByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken);

    Task<long> CreateImageAsync(
        LineUpImageCreateData data,
        CancellationToken cancellationToken);

    Task<LineUpImageSaveResult> SaveImageAsync(
        LineUpImageSaveData data,
        CancellationToken cancellationToken);

    Task<bool> SoftDeleteImageAsync(
        long imageId,
        CancellationToken cancellationToken);

    Task<LineUpImageWorkspaceData?> FindFaultCandidatesAsync(
        string? tagCode,
        long? bomId,
        CancellationToken cancellationToken);

    Task<LineUpImageBomData?> GetBomByIdAsync(
        long bomId,
        CancellationToken cancellationToken);

    Task<long> CreateBomAsync(
        LineUpBomDirectoryEntity entity,
        CancellationToken cancellationToken);

    Task<bool> UpdateBomFieldsAsync(
        long bomId,
        string? refNo,
        string? tagCode,
        string? describe,
        string? dbAddress,
        string? modelNo,
        string? brand,
        string? ioAddress,
        string? partNo,
        long? plcId,
        string? plcCode,
        string? plcName,
        string? plcIp,
        CancellationToken cancellationToken);

    Task<bool> UpdateBomAssociationAsync(long bomId, bool isAssociated, CancellationToken cancellationToken);

    Task<bool> SoftDeleteBomAsync(
        long bomId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LineUpPlcDeviceData>> ListPlcDevicesAsync(
        long? productionLineId,
        CancellationToken cancellationToken);

    Task<LineUpPlcDeviceData?> GetPlcDeviceAsync(
        long plcId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LineUpImageBomData>> GetBomsByLineAsync(
        long productionLineId,
        CancellationToken cancellationToken);

    Task<LineUpBomImportLineInfo?> GetProductionLineAsync(
        long productionLineId,
        CancellationToken cancellationToken);

    Task<int> ImportBomsAsync(
        IReadOnlyList<LineUpBomImportUpsert> upserts,
        CancellationToken cancellationToken);
}
