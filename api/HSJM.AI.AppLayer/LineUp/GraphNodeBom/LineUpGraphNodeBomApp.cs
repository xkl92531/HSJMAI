using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.GraphNodeBom;

public sealed class LineUpGraphNodeBomException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>节点 BOM 图片关联行（Id 一律字符串输出，避免 JS 侧超 2^53 精度丢失）。</summary>
public sealed record LineUpGraphNodeBomDto(
    string BatchId,
    string LineId,
    string DeviceId,
    string BlockName,
    string SequenceSourceKey,
    string NodeSourceKey,
    string NodeKind,
    string SubStationId,
    string BomId,
    string? BomRefNo,
    string? TagCode,
    string ImageId,
    string MarkId,
    string? ImageStorageKey);

public sealed record LineUpGraphNodeBomRebuildDto(
    string BatchId,
    int NodeCount,
    int RowCount,
    IReadOnlyList<string> BlockNames);

/// <summary>
/// GRAPH 节点 → BOM → 点位图 关联快照生成与查询。
/// 匹配语义与前端弹框（TiaStepBomImages + tiaGraphSelection）保持一致：
/// - 转换节点：自身逻辑网络（owner=本节点，任意角色）部件符号的全路径 + 末段；
/// - 工步节点：上述自身网络 + 相邻转换（出入边）的 TransitionCondition 网络符号 + 自身动作 token；
/// - Lup_BomDirectory.TagCode（去引号、trim、小写）命中集合即匹配；
/// - 点位通过 Lup_StationImageMark.MarkTagBom（逗号分隔 BOM Id）关联到 BOM，图片取点位所属 Lup_StationImage。
/// </summary>
public sealed class LineUpGraphNodeBomApp(
    ILineUpImportRepository importRepository,
    ILineUpGraphNodeBomRepository repository) : ISelfScopedAutoInject
{
    public async Task<LineUpGraphNodeBomRebuildDto> RebuildAsync(long batchId, CancellationToken cancellationToken)
    {
        var batch = await importRepository.GetBatchAsync(batchId, cancellationToken)
            ?? throw new LineUpGraphNodeBomException("LineUpBatchNotFound");
        if (string.IsNullOrWhiteSpace(batch.PreviewJson))
        {
            throw new LineUpGraphNodeBomException("LineUpBatchPreviewNotReady");
        }

        var preview = LineUpImportPreviewCodec.Deserialize(batch.PreviewJson, batch.SourceFileName);
        var stationCache = new Dictionary<long, StationContext>();
        var rows = new List<LineUpGraphNodeBomEntity>();
        var nodesWithRows = new HashSet<string>(StringComparer.Ordinal);
        var blockNames = new List<string>();

        foreach (var document in preview.Documents)
        {
            if (document.Sequences.Count == 0)
            {
                continue;
            }

            if (!blockNames.Contains(document.BlockName, StringComparer.Ordinal))
            {
                blockNames.Add(document.BlockName);
            }

            foreach (var sequence in document.Sequences)
            {
                foreach (var node in sequence.Nodes)
                {
                    var isStep = node.NodeKind.Equals("step", StringComparison.OrdinalIgnoreCase);
                    var isTransition = node.NodeKind.Equals("transition", StringComparison.OrdinalIgnoreCase);
                    if (!isStep && !isTransition)
                    {
                        continue;
                    }

                    var referenceKeys = SymbolKeysForNode(sequence, node);
                    if (referenceKeys.Count == 0)
                    {
                        continue;
                    }

                    foreach (var subStationId in SubStationsFor(document.Hierarchy, sequence.SourceKey))
                    {
                        if (!stationCache.TryGetValue(subStationId, out var station))
                        {
                            var data = await repository.LoadStationDataAsync(subStationId, cancellationToken);
                            station = new StationContext(data);
                            stationCache[subStationId] = station;
                        }

                        foreach (var bom in station.Boms)
                        {
                            var tagCode = (bom.TagCode ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
                            if (tagCode.Length == 0 || !referenceKeys.Contains(tagCode))
                            {
                                continue;
                            }

                            foreach (var mark in station.Marks)
                            {
                                if (!MarkLinksBom(mark, bom.Id))
                                {
                                    continue;
                                }

                                station.ImagesByKey.TryGetValue(mark.StationImageId, out var image);
                                rows.Add(new LineUpGraphNodeBomEntity
                                {
                                    BatchId = batchId,
                                    LineId = batch.LineId,
                                    DeviceId = batch.DeviceId,
                                    BlockName = document.BlockName,
                                    SequenceSourceKey = sequence.SourceKey,
                                    NodeSourceKey = node.SourceKey,
                                    NodeKind = isStep ? "step" : "transition",
                                    SubStationId = subStationId,
                                    BomId = bom.Id,
                                    BomRefNo = bom.RefNo,
                                    TagCode = bom.TagCode,
                                    ImageId = mark.StationImageId,
                                    MarkId = mark.Id,
                                    ImageStorageKey = image?.CurrentStorageKey,
                                });
                                nodesWithRows.Add(node.SourceKey);
                            }
                        }
                    }
                }
            }
        }

        await repository.ReplaceBatchRowsAsync(batchId, rows, cancellationToken);
        return new LineUpGraphNodeBomRebuildDto(
            batchId.ToString(),
            nodesWithRows.Count,
            rows.Count,
            blockNames);
    }

    public async Task<IReadOnlyList<LineUpGraphNodeBomDto>> ListByBatchAsync(
        long batchId,
        string? nodeSourceKey,
        CancellationToken cancellationToken)
    {
        var rows = await repository.ListByBatchAsync(batchId, nodeSourceKey, cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<LineUpGraphNodeBomDto>> ListByNodeAsync(
        long lineId,
        string blockName,
        string nodeSourceKey,
        CancellationToken cancellationToken)
    {
        var batchId = await repository.FindLatestMaterializedBatchAsync(lineId, blockName, cancellationToken);
        if (batchId is null)
        {
            return Array.Empty<LineUpGraphNodeBomDto>();
        }

        return await ListByBatchAsync(batchId.Value, nodeSourceKey, cancellationToken);
    }

    private static LineUpGraphNodeBomDto ToDto(LineUpGraphNodeBomEntity row) => new(
        row.BatchId.ToString(),
        row.LineId.ToString(),
        row.DeviceId.ToString(),
        row.BlockName,
        row.SequenceSourceKey,
        row.NodeSourceKey,
        row.NodeKind,
        row.SubStationId.ToString(),
        row.BomId.ToString(),
        row.BomRefNo,
        row.TagCode,
        row.ImageId.ToString(),
        row.MarkId.ToString(),
        row.ImageStorageKey);

    /// <summary>与前端 referenceKeys 一致：全路径 + 末段裸名，统一 trim、去引号、小写。</summary>
    private static HashSet<string> SymbolKeysForNode(LineUpImportSequenceDto sequence, LineUpImportNodeDto node)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var isStep = node.NodeKind.Equals("step", StringComparison.OrdinalIgnoreCase);
        var isTransition = node.NodeKind.Equals("transition", StringComparison.OrdinalIgnoreCase);
        if (!isStep && !isTransition)
        {
            return keys;
        }

        AddNetworks(node.LogicNetworks, node.SourceKey, role: null, keys);

        if (isStep)
        {
            var adjacent = AdjacentTransitions(sequence, node.SourceKey);
            foreach (var other in sequence.Nodes)
            {
                if (!adjacent.Contains(other.SourceKey)
                    || !other.NodeKind.Equals("transition", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddNetworks(other.LogicNetworks, other.SourceKey, "TransitionCondition", keys);
            }

            foreach (var action in sequence.Actions)
            {
                if (!string.Equals(action.StepSourceKey, node.SourceKey, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var token in action.Tokens)
                {
                    AddKey(token, keys);
                }
            }
        }

        return keys;
    }

    private static void AddNetworks(
        IEnumerable<LineUpImportLogicNetworkDto> networks,
        string ownerSourceKey,
        string? role,
        HashSet<string> keys)
    {
        foreach (var network in networks)
        {
            if (!string.Equals(network.OwnerSourceKey, ownerSourceKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (role is not null && !string.Equals(network.Role, role, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var part in network.Parts)
            {
                if (part.SymbolPath.Count == 0)
                {
                    continue;
                }

                AddKey(string.Join(".", part.SymbolPath), keys);
                AddKey(part.SymbolPath[^1], keys);
            }
        }
    }

    private static HashSet<string> AdjacentTransitions(LineUpImportSequenceDto sequence, string stepSourceKey)
    {
        var adjacent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in sequence.Edges)
        {
            var fromIsStep = edge.FromNodeKind.Equals("step", StringComparison.OrdinalIgnoreCase);
            var toIsStep = edge.ToNodeKind.Equals("step", StringComparison.OrdinalIgnoreCase);
            var fromIsTransition = edge.FromNodeKind.Equals("transition", StringComparison.OrdinalIgnoreCase);
            var toIsTransition = edge.ToNodeKind.Equals("transition", StringComparison.OrdinalIgnoreCase);
            if (fromIsStep && toIsTransition && string.Equals(edge.FromSourceKey, stepSourceKey, StringComparison.Ordinal))
            {
                adjacent.Add(edge.ToSourceKey);
            }

            if (toIsStep && fromIsTransition && string.Equals(edge.ToSourceKey, stepSourceKey, StringComparison.Ordinal))
            {
                adjacent.Add(edge.FromSourceKey);
            }
        }

        return adjacent;
    }

    private static void AddKey(string raw, HashSet<string> keys)
    {
        var value = (raw ?? string.Empty).Trim().Replace('"', ' ').Trim().ToLowerInvariant();
        if (value.Length > 0)
        {
            keys.Add(value);
        }
    }

    private static IEnumerable<long> SubStationsFor(LineUpHierarchyResolution hierarchy, string sequenceSourceKey)
    {
        var seen = new HashSet<long>();
        foreach (var resolution in hierarchy.Sequences)
        {
            if (!string.Equals(resolution.SequenceSourceKey, sequenceSourceKey, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var candidate in resolution.Candidates)
            {
                if (candidate.SubStationId is > 0 && seen.Add(candidate.SubStationId.Value))
                {
                    yield return candidate.SubStationId.Value;
                }
            }
        }
    }

    private static bool MarkLinksBom(LineUpGraphNodeBomMarkRow mark, long bomId)
    {
        var raw = mark.MarkTagBom;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (var piece in raw.Split(','))
        {
            if (long.TryParse(piece.Trim(), out var id) && id == bomId)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class StationContext(LineUpGraphNodeBomStationData data)
    {
        public List<LineUpGraphNodeBomBomRow> Boms { get; } = data.Boms;

        public List<LineUpGraphNodeBomMarkRow> Marks { get; } = data.Marks;

        public Dictionary<long, LineUpGraphNodeBomImageRow> ImagesByKey { get; } =
            data.Images.ToDictionary(image => image.Id, image => image);
    }
}
