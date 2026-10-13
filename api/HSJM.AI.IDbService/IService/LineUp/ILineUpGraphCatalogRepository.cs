using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpGraphCatalogRepository
{
    Task<LineUpGraphCatalogLookupData> LoadLookupsAsync(
        long? plcProjectId,
        long? lineId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LineUpGraphCatalogAssetData>> QueryAssetsAsync(
        LineUpGraphCatalogQuery query,
        CancellationToken cancellationToken);

    Task<LineUpGraphCatalogDetailData?> LoadDetailAsync(
        long assetId,
        long? bindingId,
        CancellationToken cancellationToken);

    Task<LineUpGraphCatalogDetailData?> LoadBindingDetailAsync(
        long bindingId,
        CancellationToken cancellationToken);

    Task<LineUpGraphBindingData> SaveBindingAsync(
        LineUpGraphBindingWrite write,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LineUpGraphAliasData>> ReplaceAliasesAsync(
        LineUpGraphAliasWrite write,
        CancellationToken cancellationToken);

    Task<LineUpGraphPublishData> PublishAsync(
        LineUpGraphPublishWrite write,
        CancellationToken cancellationToken);
}
