using System.Collections.Generic;

namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpMasterQuery(
    string Level,
    long? ParentId,
    int Page,
    int PageSize,
    string? Keyword);

public sealed record LineUpMasterItemData(
    long Id,
    string Level,
    long? ParentId,
    long? LineId,
    string? Code,
    string? Name,
    bool IsEnabled,
    long? PlcId,
    string? PlcName,
    string? ProjectName,
    string? Remark,
    string? IpAddress = null);

public sealed record LineUpMasterPageData(long Total, IReadOnlyList<LineUpMasterItemData> Items);

public sealed record LineUpMasterParentLookup(
    long Id,
    string Level,
    long LineId,
    long? ProcessId,
    long? WorkStationId);

public sealed record LineUpMasterWrite(
    long? Id,
    string Level,
    long? ParentId,
    long? LineId,
    string Code,
    string Name,
    bool IsEnabled,
    long? PlcId,
    string? Remark,
    LineUpMasterParentLookup? Parent,
    string? IpAddress = null);

/// <summary>
/// 删除前置检查：记录是否存在、被多少下级（或关联记录）引用、下级层级名。
/// 用于把「有下级禁止删除」(409) 与「记录不存在」(404) 区分开，不再一律 404。
/// </summary>
public sealed record LineUpMasterDeleteCheck(bool Exists, int ChildCount, string ChildLevel);

public sealed record LineUpBusinessQuery(
    string Kind,
    long ParentId,
    int Page,
    int PageSize,
    string? Keyword);

public sealed record LineUpBusinessItemData(
    long Id,
    string Kind,
    long ParentId,
    long SubStationId,
    string? Code,
    string? Name,
    string? Value,
    bool IsEnabled);

public sealed record LineUpBusinessPageData(long Total, IReadOnlyList<LineUpBusinessItemData> Items);

public sealed record LineUpBusinessParentLookup(
    long Id,
    string Kind,
    long SubStationId,
    long? ActionId);

public sealed record LineUpBusinessWrite(
    long? Id,
    string Kind,
    long ParentId,
    long SubStationId,
    string Code,
    string Name,
    string? Value,
    bool IsEnabled,
    LineUpBusinessParentLookup Parent);
