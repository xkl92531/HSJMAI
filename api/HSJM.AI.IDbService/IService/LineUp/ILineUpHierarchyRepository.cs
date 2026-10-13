using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpHierarchyRepository
{
    Task<LineUpHierarchyLookupContext> LoadContextAsync(
        long lineId,
        long deviceId,
        IReadOnlyCollection<string> sequenceSourceKeys,
        CancellationToken cancellationToken,
        string? graphBlockName = null);

    /// <summary>
    /// 按编码确保工序存在（同产线内 Code 忽略大小写匹配）：已存在返回其 Id（>1 条返回 -1 表示歧义），不存在则新建并返回新 Id。
    /// </summary>
    Task<long> EnsureProcessAsync(
        LineUpLineLookup line,
        string code,
        string name,
        CancellationToken cancellationToken);

    /// <summary>按编码确保工站存在（同工序内唯一）：语义同 <see cref="EnsureProcessAsync"/>。</summary>
    Task<long> EnsureWorkStationAsync(
        LineUpLineLookup line,
        LineUpProcessLookup process,
        string code,
        string name,
        CancellationToken cancellationToken);

    /// <summary>按编码确保子工站存在（同工站+同 PLC 内唯一），新建时 PlcID 绑定当前设备、Mode=GRAPH：语义同上。</summary>
    Task<long> EnsureSubStationAsync(
        LineUpLineLookup line,
        LineUpProcessLookup process,
        LineUpStationLookup station,
        LineUpDeviceLookup device,
        string code,
        string name,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按 SourceKey 确保 GRAPH 顺控行存在（AI_LineUp_GraphSequence）：已存在返回其 Id，不存在则新建并返回新 Id。
    /// 供「无绑定顺控直接绑定」路径补建顺控行（保存结构前的弹框直接绑定场景）。
    /// </summary>
    Task<long> EnsureGraphSequenceAsync(
        string sourceKey,
        string sourceName,
        string displayName,
        long plcProjectId,
        long graphBlockId,
        long? instanceDbBlockId,
        string? sourceHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// 新建结构绑定行（AI_LineUp_SubStationGraphBinding，BindingStatus='Unresolved'），返回新行 Id。
    /// 调用方须已确保该顺控当前无活动绑定（幂等由上层 LoadContext/绑定查询保证）。
    /// </summary>
    Task<long> InsertSubStationBindingAsync(
        long lineId,
        long processId,
        long stationId,
        long subStationId,
        long deviceId,
        long plcProjectId,
        long sequenceId,
        long graphBlockId,
        long? instanceDbBlockId,
        CancellationToken cancellationToken);
}
