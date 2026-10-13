#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

/// <summary>
/// Definition 降级数据源（2026-09-10）：当运行时采样管线（requireEnabled 全链路校验，
/// 含 IsCurrentAsync 元数据时效）未就绪时，用发布版本投影静态构造 Definition。
///
/// 动机：Definition 是纯静态数据（步/转移/拓扑 + 前端渲染投影），与 Bindings 目录同源；
/// 此前它复用 LoadByBindingAsync，导致采样侧任何"本轮跳过"（瞬态 JOIN 失败、编译对齐
/// 未过、元数据时效不一致）都连带监控页面 404，用户无法区分"监控未开"与"暂不可用"。
///
/// 与管线版差异：转移的 ConditionCanonicalKeys 为空（管线版来自 runtime doc 编译网络）。
/// 宽松准入：只要求绑定未删 + 已发布 + 四表 JOIN 可加载 + 投影成功 + 顺控 key 存在；
/// 不要求 Resolved / MonitoringEnabled / RuntimeBindingJson 自洽 / 元数据时效。
/// </summary>
public sealed class LineUpMonitorPublishedDefinitionSource(
    ILineUpGraphRuntimeRepository runtimeRepository,
    LineUpPublishedGraphProjector projector) : ISelfScopedAutoInject
{
    public async Task<LineUpMonitorDefinitionBundle?> LoadAsync(
        LineUpMonitorKey key,
        CancellationToken cancellationToken)
    {
        if (!key.IsValid) return null;

        var status = await runtimeRepository.LoadBindingAsync(key.BindingId, cancellationToken);
        if (status is null || status.PublishedRevisionId is not > 0) return null;

        var context = await runtimeRepository.LoadContextAsync(key.BindingId, cancellationToken);
        if (context is null) return null;

        LineUpPublishedGraphDefinition published;
        try
        {
            published = projector.Project(context);
        }
        catch (LineUpPublishedGraphProjectionException)
        {
            return null;
        }

        var sequence = published.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.SequenceSourceKey, key.SequenceSourceKey, StringComparison.Ordinal));
        if (sequence is null) return null;

        var viewer = LineUpMonitorViewerProjector.Project(published).Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.SequenceSourceKey, key.SequenceSourceKey, StringComparison.Ordinal));
        if (viewer is null) return null;

        // 优先用行的 ValidationRevision（与历史事件/快照里的 DefinitionRevision 同源），
        // 行没有时退回发布版本 SourceHash。
        var definitionRevision = !string.IsNullOrWhiteSpace(status.ValidationRevision)
            ? status.ValidationRevision!
            : context.PublishedRevision.SourceHash;

        var runtime = new LineUpRuntimeStateDefinition(
            context.Binding.Id,
            context.PublishedRevision.Id,
            context.Binding.StationId,
            sequence.SequenceSourceKey,
            sequence.DisplayName,
            definitionRevision,
            sequence.Steps
                .Select(step => new LineUpRuntimeStepDefinition(
                    step.SourceKey, step.DisplayName, step.IsInitial, step.WarningTime, step.MaximumStepTime))
                .ToImmutableArray(),
            sequence.Transitions
                .Select(transition => new LineUpRuntimeTransitionDefinition(
                    transition.SourceKey, transition.DisplayName, ImmutableArray<string>.Empty))
                .ToImmutableArray(),
            sequence.Edges
                .Select(edge => new LineUpRuntimeEdgeDefinition(
                    edge.SourceKey, edge.FromSourceKey, edge.ToSourceKey, edge.EdgeKind))
                .ToImmutableArray(),
            ImmutableArray<ImmutableArray<string>>.Empty);
        return new(runtime, viewer);
    }
}
