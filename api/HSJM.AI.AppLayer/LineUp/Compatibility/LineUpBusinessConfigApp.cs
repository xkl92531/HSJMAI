using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Compatibility;

public sealed record LineUpBusinessConfigItemDto(
    long Id,
    string Kind,
    long ParentId,
    long SubStationId,
    string? Code,
    string? Name,
    string? Value,
    bool IsEnabled);

public sealed record LineUpBusinessConfigPageDto(long Total, IReadOnlyList<LineUpBusinessConfigItemDto> Items);

public sealed record LineUpBusinessConfigWriteDto(
    long ParentId,
    long SubStationId,
    string Code,
    string Name,
    string? Value,
    bool IsEnabled);

public sealed class LineUpBusinessConfigApp(ILineUpCompatibilityRepository repository) : ISelfScopedAutoInject
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Action", "Condition", "Solution"
    };

    public async Task<LineUpBusinessConfigPageDto> QueryAsync(
        string kind,
        long parentId,
        int page,
        int pageSize,
        string? keyword,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKind(kind);
        if (parentId <= 0) throw LineUpMasterDataApp.Error("LineUpParentRequired", "A parent selection is required.");
        LineUpMasterDataApp.ValidatePage(page, pageSize);
        var data = await repository.QueryBusinessAsync(
            new(normalized, parentId, page, pageSize, LineUpMasterDataApp.NormalizeOptional(keyword)), cancellationToken);
        return new(data.Total, data.Items.Select(x => new LineUpBusinessConfigItemDto(
            x.Id, x.Kind, x.ParentId, x.SubStationId, x.Code, x.Name, x.Value, x.IsEnabled)).ToArray());
    }

    public Task<long> CreateAsync(string kind, LineUpBusinessConfigWriteDto request, CancellationToken cancellationToken) =>
        SaveAsync(null, kind, request, cancellationToken);

    public Task<long> UpdateAsync(string kind, long id, LineUpBusinessConfigWriteDto request, CancellationToken cancellationToken)
    {
        if (id <= 0) throw LineUpMasterDataApp.Error("LineUpInvalidId", "The selected record is invalid.");
        return SaveAsync(id, kind, request, cancellationToken);
    }

    public async Task DeleteAsync(string kind, long id, CancellationToken cancellationToken)
    {
        var normalized = NormalizeKind(kind);
        if (id <= 0) throw LineUpMasterDataApp.Error("LineUpInvalidId", "The selected record is invalid.");
        if (!await repository.SoftDeleteBusinessAsync(normalized, id, cancellationToken))
            throw LineUpMasterDataApp.Error("LineUpRecordNotFound", "The selected record no longer exists.");
    }

    private async Task<long> SaveAsync(
        long? id,
        string kind,
        LineUpBusinessConfigWriteDto request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw LineUpMasterDataApp.Error("LineUpInvalidRequest", "The request is required.");
        var normalized = NormalizeKind(kind);
        if (request.ParentId <= 0 || request.SubStationId <= 0)
            throw LineUpMasterDataApp.Error("LineUpParentRequired", "A parent selection is required.");
        var parent = await repository.GetBusinessParentAsync(normalized, request.ParentId, cancellationToken);
        if (parent is null || !ParentMatches(normalized, request.SubStationId, parent))
            throw LineUpMasterDataApp.Error("LineUpParentMismatch", "The selected parent does not belong to this substation.");

        var code = LineUpMasterDataApp.Required(request.Code, 200, "LineUpCodeRequired");
        var name = LineUpMasterDataApp.Required(request.Name, 500, "LineUpNameRequired");
        var value = LineUpMasterDataApp.Limited(request.Value, 4000);
        if (await repository.BusinessCodeExistsAsync(normalized, request.ParentId, code, id, cancellationToken))
            throw LineUpMasterDataApp.Error("LineUpDuplicateCode", "The code already exists under the selected parent.");

        var write = new LineUpBusinessWrite(
            id, normalized, request.ParentId, request.SubStationId,
            code, name, value, request.IsEnabled, parent);
        if (id is null) return await repository.CreateBusinessAsync(write, cancellationToken);
        if (!await repository.UpdateBusinessAsync(write, cancellationToken))
            throw LineUpMasterDataApp.Error("LineUpRecordNotFound", "The selected record no longer exists.");
        return id.Value;
    }

    private static bool ParentMatches(string kind, long subStationId, LineUpBusinessParentLookup parent) => kind switch
    {
        "Action" => parent.Kind == "SubStation" && parent.SubStationId == subStationId,
        "Condition" or "Solution" => parent.Kind == "Action" && parent.SubStationId == subStationId,
        _ => false
    };

    private static string NormalizeKind(string kind)
    {
        var match = Kinds.SingleOrDefault(x => x.Equals(kind?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw LineUpMasterDataApp.Error("LineUpInvalidKind", "The requested configuration type is invalid.");
    }
}
