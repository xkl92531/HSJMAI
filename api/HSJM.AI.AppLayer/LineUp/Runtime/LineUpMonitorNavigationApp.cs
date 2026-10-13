#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpMonitorNavigationNodeDto(
    string NodeId,
    string NodeType,
    string? ParentNodeId,
    string EntityId,
    string Code,
    string Name,
    int SortOrder,
    int DescendantCount,
    int IssueCount,
    string RuntimeStatus,
    int PublishedBindingCount);

public sealed class LineUpMonitorNavigationApp(
    ILineUpMonitorNavigationRepository repository,
    LineUpMonitorStateStore stateStore) : ISelfScopedAutoInject
{
    public async Task<IReadOnlyList<LineUpMonitorNavigationNodeDto>> GetNavigationAsync(
        bool includeUnconfigured,
        string? keyword,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await repository.LoadAsync(
            includeUnconfigured,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Build(rows, cancellationToken);
    }

    private IReadOnlyList<LineUpMonitorNavigationNodeDto> Build(
        IReadOnlyList<LineUpMonitorNavigationRow> rows,
        CancellationToken cancellationToken)
    {
        var result = new List<LineUpMonitorNavigationNodeDto>();
        foreach (var line in rows
                     .GroupBy(row => row.LineId)
                     .OrderBy(group => group.First().LineCode, StringComparer.Ordinal)
                     .ThenBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processNodes = BuildProcesses(line.ToArray(), cancellationToken);
            var processRoots = processNodes.Where(item => item.NodeType == "Process").ToArray();
            var first = line.First();
            result.Add(Node(
                $"line:{line.Key}", "Line", null, line.Key,
                first.LineCode, first.LineName, 0,
                processNodes.Count,
                processRoots.Sum(item => item.IssueCount),
                AggregateStatus(processRoots.Select(item => item.RuntimeStatus)),
                processRoots.Sum(item => item.PublishedBindingCount)));
            result.AddRange(processNodes);
        }
        return result;
    }

    private IReadOnlyList<LineUpMonitorNavigationNodeDto> BuildProcesses(
        IReadOnlyList<LineUpMonitorNavigationRow> rows,
        CancellationToken cancellationToken)
    {
        var result = new List<LineUpMonitorNavigationNodeDto>();
        foreach (var process in rows
                     .GroupBy(row => row.ProcessId)
                     .OrderBy(group => group.First().ProcessCode, StringComparer.Ordinal)
                     .ThenBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stationNodes = BuildStations(process.ToArray(), cancellationToken);
            var stationRoots = stationNodes.Where(item => item.NodeType == "Station").ToArray();
            var first = process.First();
            result.Add(Node(
                $"process:{process.Key}", "Process", $"line:{first.LineId}", process.Key,
                first.ProcessCode, first.ProcessName, 0,
                stationNodes.Count,
                stationRoots.Sum(item => item.IssueCount),
                AggregateStatus(stationRoots.Select(item => item.RuntimeStatus)),
                stationRoots.Sum(item => item.PublishedBindingCount)));
            result.AddRange(stationNodes);
        }
        return result;
    }

    private IReadOnlyList<LineUpMonitorNavigationNodeDto> BuildStations(
        IReadOnlyList<LineUpMonitorNavigationRow> rows,
        CancellationToken cancellationToken)
    {
        var result = new List<LineUpMonitorNavigationNodeDto>();
        foreach (var station in rows
                     .GroupBy(row => row.StationId)
                     .OrderBy(group => group.First().StationCode, StringComparer.Ordinal)
                     .ThenBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leafGroups = station
                .GroupBy(row => row.SubStationId)
                .OrderBy(group => group.First().SubStationSortOrder)
                .ThenBy(group => group.First().SubStationCode, StringComparer.Ordinal)
                .ThenBy(group => group.Key)
                .ToArray();
            var leaves = new List<LineUpMonitorNavigationNodeDto>();
            var subStationNodes = new List<LineUpMonitorNavigationNodeDto>();
            foreach (var leafGroup in leafGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nodes = BuildLeaf(leafGroup);
                subStationNodes.Add(nodes[0]);
                leaves.AddRange(nodes);
            }
            var first = station.First();
            result.Add(Node(
                $"station:{station.Key}", "Station", $"process:{first.ProcessId}", station.Key,
                first.StationCode, first.StationName, 0,
                subStationNodes.Count,
                subStationNodes.Sum(item => item.IssueCount),
                AggregateStatus(subStationNodes.Select(item => item.RuntimeStatus)),
                subStationNodes.Sum(item => item.PublishedBindingCount)));
            result.AddRange(leaves);
        }
        return result;
    }

    /// <summary>
    /// 工位叶子节点 + 其下已发布 GRAPH 子节点。
    /// Graph 节点：EntityId = BindingId，Code = graph 块名，状态 = 单绑定状态
    /// （任一顺控快照异常即 Error，全顺控在线才 Online —— 见 <see cref="Status"/>）。
    /// </summary>
    private IReadOnlyList<LineUpMonitorNavigationNodeDto> BuildLeaf(
        IGrouping<long, LineUpMonitorNavigationRow> group)
    {
        var rows = group.ToArray();
        var first = rows[0];
        var statuses = rows.Select(Status).ToArray();
        var cachedIssueCount = rows
            .Where(row => row.BindingId is > 0)
            .SelectMany(row => stateStore.ForBinding(row.BindingId!.Value))
            .Count(SnapshotHasError);
        var leaf = Node(
            $"substation:{group.Key}", "SubStation", $"station:{first.StationId}", group.Key,
            first.SubStationCode, first.SubStationName, first.SubStationSortOrder,
            0,
            rows.Sum(row => row.BindingIssueCount) + cachedIssueCount,
            AggregateStatus(statuses),
            rows.Where(row => row.BindingId is > 0 && row.PublishedRevisionId is > 0)
                .Select(row => row.BindingId!.Value)
                .Distinct()
                .Count());
        var graphs = rows
            .Where(row => row.BindingId is > 0
                && row.PublishedRevisionId is > 0
                && !string.IsNullOrWhiteSpace(row.GraphSourceName))
            .GroupBy(row => row.BindingId!.Value)
            .OrderBy(binding => binding.First().GraphSourceName, StringComparer.Ordinal)
            .ThenBy(binding => binding.Key)
            .Select(binding =>
            {
                var row = binding.First();
                return Node(
                    $"graph:{binding.Key}", "Graph", $"substation:{group.Key}", binding.Key,
                    row.GraphSourceName!, string.Empty, 0,
                    0,
                    Math.Max(0, row.BindingIssueCount)
                        + stateStore.ForBinding(binding.Key).Count(SnapshotHasError),
                    Status(row),
                    1);
            })
            .ToArray();
        return [leaf, .. graphs];
    }

    private string Status(LineUpMonitorNavigationRow row)
    {
        if (row.BindingId is not > 0 || row.PublishedRevisionId is not > 0) return "Unconfigured";
        if (row.BindingIssueCount > 0) return "Error";
        if (string.Equals(row.BindingStatus, "Unresolved", StringComparison.Ordinal)) return "NeedsPrepare";
        if (!string.Equals(row.BindingStatus, "Resolved", StringComparison.Ordinal)) return "NeedsReview";
        if (!row.MonitoringEnabled) return "Ready";
        var snapshots = stateStore.ForBinding(row.BindingId.Value);
        if (snapshots.Count == 0) return "Enabled";
        var statuses = snapshots.Select(SequenceStatus).ToArray();
        if (statuses.Any(status => string.Equals(status, "Error", StringComparison.Ordinal))) return "Error";
        if (statuses.All(status => string.Equals(status, "Online", StringComparison.Ordinal))) return "Online";
        if (statuses.Any(status => string.Equals(status, "Warning", StringComparison.Ordinal))) return "Warning";
        return "Enabled";
    }

    /// <summary>
    /// 单序列快照 → 运行态（Error/Online/Warning/Enabled）。2026-09-11 B+C 语义分层：
    /// Error = 读值 Bad / 互斥步冲突（Error 级异常）/ Degraded（数据质量）；
    /// Warning = 网关断线类（Offline/ConfigurationError 连接 + Warning 级异常）；
    /// 步超时（Info 级）不参与分层——树上保持健康态，图上步用淡色标注。
    /// 导航树 Graph 节点与绑定摘要序列状态共用此判定。
    /// </summary>
    internal static string SequenceStatus(LineUpRuntimeSnapshot snapshot)
    {
        if (SnapshotHasError(snapshot)) return "Error";
        if (SnapshotIsOnline(snapshot)) return "Online";
        if (SnapshotHasWarning(snapshot)) return "Warning";
        return "Enabled";
    }

    internal static bool SnapshotIsOnline(LineUpRuntimeSnapshot snapshot) =>
        snapshot.Connection.Status == LineUpGatewayStatus.Online &&
        snapshot.Steps.All(item => item.Quality == LineUpRuntimeQuality.Good) &&
        // 2026-09-15：转移/条件的质量判定收窄到【当前活动步的出边转移】——GRAPH 运行时只有活动步
        // 出边转移参与转换判定（如当前工步 S12 只看 T14），非活动步出边（T26）的条件位不参与
        // 在线判定。2026-09-11 的 Unknown 容忍（导入编译告警致结构性 Unknown）继续保留；
        // 步保持全 Good：EXEC_BITS 是实时读数本体，需求①下瞬时读坏保持 Good。
        TransitionsRelevant(snapshot).All(item => item.Quality != LineUpRuntimeQuality.Bad) &&
        ConditionsRelevant(snapshot).All(item => item.Quality != LineUpRuntimeQuality.Bad);

    internal static bool SnapshotHasError(LineUpRuntimeSnapshot snapshot) =>
        snapshot.Connection.Status == LineUpGatewayStatus.Degraded ||
        snapshot.Steps.Any(item => item.Quality == LineUpRuntimeQuality.Bad) ||
        // 2026-09-15：同上收窄——非活动步出边转移的条件位 Bad 不再把整个顺控打成 Error
        //（用户案例：FB360 OCV测试抓取当前工步 S12、其出边 T14 正常，T26 的条件位不应导致报异常）。
        TransitionsRelevant(snapshot).Any(item => item.Quality == LineUpRuntimeQuality.Bad) ||
        ConditionsRelevant(snapshot).Any(item => item.Quality == LineUpRuntimeQuality.Bad) ||
        snapshot.Exceptions.Any(item => string.Equals(item.Severity, "Error", StringComparison.Ordinal));

    /// <summary>
    /// 2026-09-15：快照中【当前活动步出边转移】的转移状态子集（ActiveTransitionSourceKeys
    /// 为空=顺控未启动/无活动步，此时转移质量不参与判定）。
    /// </summary>
    private static IEnumerable<LineUpTransitionRuntimeState> TransitionsRelevant(
        LineUpRuntimeSnapshot snapshot) =>
        snapshot.ActiveTransitionSourceKeys.IsDefaultOrEmpty
            ? []
            : snapshot.Transitions.Where(item =>
                snapshot.ActiveTransitionSourceKeys.Contains(item.SourceKey));

    /// <summary>同 TransitionsRelevant，条件按 OwnerSourceKey（所属转移）过滤。</summary>
    private static IEnumerable<LineUpConditionRuntimeValue> ConditionsRelevant(
        LineUpRuntimeSnapshot snapshot) =>
        snapshot.ActiveTransitionSourceKeys.IsDefaultOrEmpty
            ? []
            : snapshot.Conditions.Where(item =>
                snapshot.ActiveTransitionSourceKeys.Contains(item.OwnerSourceKey));

    internal static bool SnapshotHasWarning(LineUpRuntimeSnapshot snapshot) =>
        snapshot.Connection.Status is LineUpGatewayStatus.Offline or LineUpGatewayStatus.ConfigurationError ||
        snapshot.Exceptions.Any(item => string.Equals(item.Severity, "Warning", StringComparison.Ordinal));

    private static string AggregateStatus(IEnumerable<string> statuses) =>
        statuses.OrderByDescending(StatusRank).FirstOrDefault() ?? "Unconfigured";

    private static int StatusRank(string status) => status switch
    {
        "Error" => 7,
        "Warning" => 6,
        "NeedsPrepare" => 5,
        "NeedsReview" => 4,
        "Unconfigured" => 3,
        "Enabled" => 2,
        "Ready" => 1,
        "Online" => 0,
        _ => 0
    };

    private static LineUpMonitorNavigationNodeDto Node(
        string nodeId,
        string nodeType,
        string? parentNodeId,
        long entityId,
        string code,
        string name,
        int sortOrder,
        int descendantCount,
        int issueCount,
        string runtimeStatus,
        int publishedBindingCount) => new(
            nodeId,
            nodeType,
            parentNodeId,
            entityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            code ?? string.Empty,
            name ?? string.Empty,
            sortOrder,
            descendantCount,
            issueCount,
            runtimeStatus,
            publishedBindingCount);
}
