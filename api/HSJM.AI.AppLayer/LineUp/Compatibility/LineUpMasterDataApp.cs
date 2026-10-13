using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Compatibility;

public sealed record LineUpMasterDataItemDto(
    long Id,
    string Level,
    long? ParentId,
    long? LineId,
    long? PlcId,
    string? Code,
    string? Name,
    bool IsEnabled,
    string? PlcName,
    string? ProjectName,
    string? Remark,
    string? IpAddress = null);

public sealed record LineUpMasterDataPageDto(long Total, IReadOnlyList<LineUpMasterDataItemDto> Items);

public sealed record LineUpMasterDataWriteDto(
    long? ParentId,
    long? LineId,
    string Code,
    string Name,
    bool IsEnabled,
    long? PlcId,
    string? Remark,
    string? IpAddress = null);

public sealed class LineUpCompatibilityException(string code, string safeSummary) : Exception(safeSummary)
{
    public string Code { get; } = code;
}

public sealed class LineUpMasterDataApp(ILineUpCompatibilityRepository repository) : ISelfScopedAutoInject
{
    private static readonly HashSet<string> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProductionLine", "Process", "WorkStation", "SubStation", "Device"
    };

    /// <summary>需要显式选择父级的层级（Device 与 ProductionLine 一样支持顶层平铺列表）。</summary>
    private static bool LevelRequiresParent(string level) =>
        level is "Process" or "WorkStation" or "SubStation";

    public async Task<LineUpMasterDataPageDto> QueryAsync(
        string level,
        long? parentId,
        int page,
        int pageSize,
        string? keyword,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeLevel(level);
        ValidatePage(page, pageSize);
        if (LevelRequiresParent(normalized) && parentId is not > 0)
            throw Error("LineUpParentRequired", "A parent selection is required.");

        var data = await repository.QueryMasterAsync(
            new(normalized, parentId, page, pageSize, NormalizeOptional(keyword)), cancellationToken);
        return new(data.Total, data.Items.Select(ToDto).ToArray());
    }

    public Task<long> CreateAsync(
        string level,
        LineUpMasterDataWriteDto request,
        CancellationToken cancellationToken) => SaveAsync(null, level, request, cancellationToken);

    public Task<long> UpdateAsync(
        string level,
        long id,
        LineUpMasterDataWriteDto request,
        CancellationToken cancellationToken)
    {
        if (id <= 0) throw Error("LineUpInvalidId", "The selected record is invalid.");
        return SaveAsync(id, level, request, cancellationToken);
    }

    public async Task DeleteAsync(string level, long id, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLevel(level);
        if (id <= 0) throw Error("LineUpInvalidId", "The selected record is invalid.");

        var check = await repository.GetMasterDeleteCheckAsync(normalized, id, cancellationToken);
        if (!check.Exists)
            throw Error("LineUpRecordNotFound", "The selected record no longer exists.");

        if (normalized.Equals("Device", StringComparison.Ordinal))
        {
            // PLC 设备保持引用保护：被子工站/PLC 项目绑定时拒绝删除（409），避免破坏项目绑定
            if (check.ChildCount > 0)
                throw Error("LineUpMasterHasChildren", $"The device is still referenced by {check.ChildCount} record(s).");
            if (!await repository.SoftDeleteMasterAsync(normalized, id, cancellationToken))
                throw Error("LineUpRecordNotFound", "The selected record no longer exists.");
            return;
        }

        // 层级级别：级联软删（自身 + 全部下级 + 动作的执行条件/异常解决方案/动作步骤）
        var affected = await repository.CascadeSoftDeleteMasterAsync(normalized, id, cancellationToken);
        if (affected <= 0)
            throw Error("LineUpRecordNotFound", "The selected record no longer exists.");
    }

    private async Task<long> SaveAsync(
        long? id,
        string level,
        LineUpMasterDataWriteDto request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw Error("LineUpInvalidRequest", "The request is required.");
        var normalized = NormalizeLevel(level);
        var code = Required(request.Code, 100, "LineUpCodeRequired");
        // Device 层级：Name 映射 ProjectName（可空），Code 映射 DeviceName（全局唯一）
        var name = normalized.Equals("Device", StringComparison.Ordinal)
            ? Limited(request.Name, 200)
            : Required(request.Name, 200, "LineUpNameRequired");
        var remark = Limited(request.Remark, 1000);
        var ipAddress = normalized.Equals("Device", StringComparison.Ordinal)
            ? Limited(request.IpAddress, 100)
            : null;

        LineUpMasterParentLookup? parent = null;
        var parentId = request.ParentId ?? 0;
        if (LevelRequiresParent(normalized))
        {
            if (parentId <= 0) throw Error("LineUpParentRequired", "A parent selection is required.");
            parent = await repository.GetMasterParentAsync(normalized, parentId, cancellationToken);
            if (parent is null || !ParentMatches(normalized, request.LineId, parent))
                throw Error("LineUpParentMismatch", "The selected parent does not belong to this hierarchy.");
        }
        else if (normalized.Equals("Device", StringComparison.Ordinal))
        {
            // Device 的父级是可选的产线（LineID）；填了就必须真实存在
            var lineId = request.LineId ?? 0;
            if (lineId > 0)
            {
                parent = await repository.GetMasterParentAsync("ProductionLine", lineId, cancellationToken);
                if (parent is null)
                    throw Error("LineUpParentMismatch", "The selected production line no longer exists.");
            }
        }

        // 子工站绑定 PLC 设备时校验设备存在（PlcName 由仓储按 PlcID 回填，保证工位白名单数据一致）
        if (normalized.Equals("SubStation", StringComparison.Ordinal) && (request.PlcId ?? 0) > 0)
        {
            var device = await repository.GetMasterParentAsync("Device", request.PlcId!.Value, cancellationToken);
            if (device is null)
                throw Error("LineUpParentMismatch", "The selected PLC device no longer exists.");
        }

        if (await repository.MasterCodeExistsAsync(normalized, parentId, code, id, cancellationToken))
            throw Error("LineUpDuplicateCode", "The code already exists under the selected parent.");

        var write = new LineUpMasterWrite(
            id, normalized, request.ParentId, request.LineId, code, name ?? string.Empty,
            request.IsEnabled, request.PlcId, remark, parent, ipAddress);
        if (id is null) return await repository.CreateMasterAsync(write, cancellationToken);
        if (!await repository.UpdateMasterAsync(write, cancellationToken))
            throw Error("LineUpRecordNotFound", "The selected record no longer exists.");
        return id.Value;
    }

    private static bool ParentMatches(string level, long? requestedLineId, LineUpMasterParentLookup parent) => level switch
    {
        "Process" => parent.Level == "ProductionLine" && (requestedLineId is null || requestedLineId == parent.LineId),
        "WorkStation" => parent.Level == "Process" && requestedLineId == parent.LineId,
        "SubStation" => parent.Level == "WorkStation" && requestedLineId == parent.LineId,
        _ => false
    };

    private static LineUpMasterDataItemDto ToDto(LineUpMasterItemData item) => new(
        item.Id, item.Level, item.ParentId, item.LineId, item.PlcId, item.Code, item.Name, item.IsEnabled,
        item.PlcName, item.ProjectName, item.Remark, item.IpAddress);

    internal static string Required(string? value, int maxLength, string code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) throw Error(code, "A required value is missing.");
        if (normalized.Length > maxLength) throw Error("LineUpValueTooLong", "A value is longer than allowed.");
        return normalized;
    }

    internal static string? Limited(string? value, int maxLength)
    {
        var normalized = NormalizeOptional(value);
        if (normalized?.Length > maxLength) throw Error("LineUpValueTooLong", "A value is longer than allowed.");
        return normalized;
    }

    internal static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 200)
            throw Error("LineUpInvalidPage", "The requested page is invalid.");
    }

    internal static LineUpCompatibilityException Error(string code, string summary) => new(code, summary);

    private static string NormalizeLevel(string level)
    {
        var match = Levels.SingleOrDefault(x => x.Equals(level?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw Error("LineUpInvalidLevel", "The requested hierarchy level is invalid.");
    }
}
