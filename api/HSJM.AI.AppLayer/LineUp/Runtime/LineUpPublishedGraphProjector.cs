#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed class LineUpPublishedGraphProjector : ISelfScopedAutoInject
{
    public const int MaximumPreviewJsonBytes = LineUpGraphPreviewValidator.MaximumPreviewJsonBytes;
    public const string DefinitionSchema = "lineup.graph-published-definition.v1";
    public const int MaximumSequences = LineUpGraphPreviewValidator.MaximumSequences;
    public const int MaximumNodes = LineUpGraphPreviewValidator.MaximumNodes;
    public const int MaximumActions = LineUpGraphPreviewValidator.MaximumActions;
    public const int MaximumEdges = LineUpGraphPreviewValidator.MaximumEdges;
    public const int MaximumLogicNetworks = LineUpGraphPreviewValidator.MaximumLogicNetworks;
    public const int MaximumLogicPartsPerNetwork = LineUpGraphPreviewValidator.MaximumLogicPartsPerNetwork;
    public const int MaximumLogicWiresPerNetwork = LineUpGraphPreviewValidator.MaximumLogicWiresPerNetwork;
    public const int MaximumLogicEndpointsPerWire = LineUpGraphPreviewValidator.MaximumLogicEndpointsPerWire;

    public LineUpPublishedGraphDefinition Project(LineUpGraphRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateContextIdentity(context);
        var preview = Deserialize(context.PublishedRevision.PreviewJson);
        ValidateSchema(preview, context.PublishedRevision.ParserSchemaVersion);
        ValidatePreviewIdentity(context, preview);
        ValidateForPublication(preview);
        var aliased = ApplyAliases(preview, context.Aliases);

        return ProjectDefinition(context, aliased);
    }

    internal static LineUpMonitorViewerPreviewDto ProjectViewer(
        string previewJson,
        int parserSchemaVersion,
        IReadOnlyDictionary<string, string> aliases)
    {
        var preview = Deserialize(previewJson);
        ValidateSchema(preview, parserSchemaVersion);
        ValidateForPublication(preview);
        var aliased = ApplyAliases(preview, aliases);
        var published = ProjectDefinition(
            DefinitionSchema,
            0,
            0,
            0,
            0,
            string.Empty,
            parserSchemaVersion,
            aliased);
        return LineUpMonitorViewerProjector.Project(published);
    }

    private static LineUpPublishedGraphDefinition ProjectDefinition(
        LineUpGraphRuntimeContext context,
        LineUpGraphDraftPreview aliased) =>
        ProjectDefinition(
            DefinitionSchema,
            context.Binding.Id,
            context.Asset.Id,
            context.PublishedRevision.Id,
            context.PublishedRevision.RevisionNo,
            context.PublishedRevision.SourceHash,
            context.PublishedRevision.ParserSchemaVersion,
            aliased);

    private static LineUpPublishedGraphDefinition ProjectDefinition(
        string schema,
        long bindingId,
        long graphAssetId,
        long publishedRevisionId,
        int publishedRevisionNo,
        string sourceHash,
        int parserSchemaVersion,
        LineUpGraphDraftPreview aliased) =>
        new(
            schema,
            bindingId,
            graphAssetId,
            publishedRevisionId,
            publishedRevisionNo,
            sourceHash,
            parserSchemaVersion,
            aliased.BlockName,
            aliased.BlockNumber,
            aliased.ProgrammingLanguage,
            aliased.Sequences
                .OrderBy(sequence => sequence.SourceKey, StringComparer.Ordinal)
                .Select(sequence => ProjectSequence(sequence, parserSchemaVersion >= 2))
                .ToArray(),
            aliased.Issues
                .OrderBy(issue => issue.Severity, StringComparer.Ordinal)
                .ThenBy(issue => issue.IssueType, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceKey, StringComparer.Ordinal)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                .Select(issue => new LineUpPublishedIssueDefinition(
                    issue.Severity,
                    issue.IssueType,
                    issue.SourceKey,
                    issue.Message))
                .ToArray());

    private static void ValidateContextIdentity(LineUpGraphRuntimeContext context)
    {
        if (context.Binding.Id <= 0 ||
            context.Asset.Id <= 0 ||
            context.PublishedRevision.Id <= 0 ||
            context.Binding.GraphAssetId != context.Asset.Id ||
            context.Binding.PublishedRevisionId != context.PublishedRevision.Id ||
            context.PublishedRevision.GraphAssetId != context.Asset.Id ||
            context.PublishedRevision.RevisionNo <= 0 ||
            context.PublishedRevision.ParserSchemaVersion <= 0 ||
            string.IsNullOrWhiteSpace(context.PublishedRevision.SourceHash))
        {
            throw Error("GraphRevisionStale", "The published GRAPH revision identity is stale or inconsistent.");
        }
    }

    internal static LineUpGraphDraftPreview Deserialize(string json)
    {
        try
        {
            return LineUpGraphPreviewValidator.Deserialize(json);
        }
        catch (LineUpGraphPreviewValidationException error)
        {
            throw Error(error.Code, error.SafeSummary);
        }
    }

    internal static void ValidateForPublication(LineUpGraphDraftPreview preview)
    {
        try
        {
            LineUpGraphPreviewValidator.ValidateForPublication(preview);
        }
        catch (LineUpGraphPreviewValidationException error)
        {
            throw Error(error.Code, error.SafeSummary);
        }
    }

    internal static void ValidateSchema(
        LineUpGraphDraftPreview preview,
        int parserSchemaVersion)
    {
        try
        {
            LineUpGraphPreviewValidator.ValidateSchema(preview, parserSchemaVersion);
        }
        catch (LineUpGraphPreviewValidationException error)
        {
            throw Error(error.Code, error.SafeSummary);
        }
    }

    private static void ValidatePreviewIdentity(
        LineUpGraphRuntimeContext context,
        LineUpGraphDraftPreview preview)
    {
        if (!string.Equals(preview.BlockName?.Trim(), context.Asset.SourceBlockName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            preview.BlockNumber != context.Asset.BlockNumber ||
            !string.Equals(preview.ProgrammingLanguage?.Trim(), context.Asset.ProgrammingLanguage.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(context.Asset.BlockType?.Trim(), "FB", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(context.Asset.ProgrammingLanguage?.Trim(), "GRAPH", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("GraphPreviewIdentityInvalid", "The published GRAPH preview does not match its asset.");
        }
    }

    private static LineUpGraphDraftPreview ApplyAliases(
        LineUpGraphDraftPreview preview,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases is null)
        {
            throw Error("GraphAliasInvalid", "The GRAPH alias collection is missing.");
        }

        try
        {
            return LineUpGraphAliasProjector.Apply(preview, aliases);
        }
        catch (LineUpImportException exception) when (
            exception.Code is "GraphAliasTargetInvalid" or "GraphAliasInvalid" or "GraphPreviewInvalid")
        {
            throw Error(exception.Code, exception.SafeSummary);
        }
    }

    private static LineUpPublishedSequenceDefinition ProjectSequence(
        LineUpGraphPreviewSequence sequence,
        bool includeV2Detail) => new(
        sequence.SourceKey,
        sequence.SourceName,
        sequence.DisplayName,
        sequence.Nodes
            .Where(node => string.Equals(node.NodeKind, "Step", StringComparison.Ordinal))
            .OrderBy(node => node.Number)
            .ThenBy(node => node.SourceKey, StringComparer.Ordinal)
            .Select(node => ProjectNode(node, includeV2Detail))
            .ToArray(),
        sequence.Nodes
            .Where(node => string.Equals(node.NodeKind, "Transition", StringComparison.Ordinal))
            .OrderBy(node => node.Number)
            .ThenBy(node => node.SourceKey, StringComparer.Ordinal)
            .Select(node => ProjectNode(node, includeV2Detail))
            .ToArray(),
        sequence.Actions
            .OrderBy(action => action.StepNumber)
            .ThenBy(action => action.Ordinal)
            .ThenBy(action => action.SourceKey, StringComparer.Ordinal)
            .Select(action => new LineUpPublishedActionDefinition(
                action.SourceKey,
                action.StepSourceKey,
                action.SourceName,
                action.DisplayName,
                action.Ordinal,
                action.StepNumber,
                action.Qualifier,
                includeV2Detail ? action.Event : null,
                includeV2Detail ? action.Interlock : null,
                action.Tokens?.ToArray() ?? Array.Empty<string>(),
                action.ActionText))
            .ToArray(),
        sequence.Edges
            .OrderBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .Select(edge => new LineUpPublishedEdgeDefinition(
                edge.SourceKey,
                edge.FromSourceKey,
                edge.ToSourceKey,
                edge.FromNodeKind,
                edge.ToNodeKind,
                edge.EdgeKind,
                edge.BranchOrder))
            .ToArray());

    private static LineUpPublishedNodeDefinition ProjectNode(
        LineUpGraphPreviewNode node,
        bool includeV2Detail) => new(
        node.NodeKind,
        node.SourceKey,
        node.SourceName,
        node.DisplayName,
        node.Number,
        string.Equals(node.NodeKind, "Step", StringComparison.Ordinal)
            ? $"RT_DATA.EXEC_BITS.EXEC_BITS[{node.Number - 1}]"
            : null,
        node.IsInitial,
        node.IsFinal,
        node.MaximumStepTime,
        node.WarningTime,
        node.Comment,
        includeV2Detail ? node.ActionGroupTitle : string.Empty,
        includeV2Detail
            ? node.LogicNetworks
                .OrderBy(network => network.Role, StringComparer.Ordinal)
                .ThenBy(network => network.Ordinal)
                .ThenBy(network => network.SourceKey, StringComparer.Ordinal)
                .Select(ProjectLogicNetwork)
                .ToArray()
            : Array.Empty<LineUpPublishedLogicNetworkDefinition>(),
        node.ProgrammingLanguage,
        node.IsMissing,
        node.RawXml,
        node.IsJump);

    private static LineUpPublishedLogicNetworkDefinition ProjectLogicNetwork(
        LineUpGraphLogicNetwork network) => new(
        network.SourceKey,
        network.OwnerSourceKey,
        network.Role,
        network.Ordinal,
        network.ProgrammingLanguage ?? string.Empty,
        network.Title ?? string.Empty,
        network.Parts
            .OrderBy(part => part.UId)
            .ThenBy(part => part.SourceKey, StringComparer.Ordinal)
            .Select(part => new LineUpPublishedLogicPartDefinition(
                part.SourceKey,
                part.UId,
                part.PartName ?? string.Empty,
                part.Scope ?? string.Empty,
                part.SymbolPath.Select(value => value ?? string.Empty).ToArray(),
                part.ConstantValue ?? string.Empty,
                part.Negated,
                part.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                part.TemplateValues.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                part.PinNames.Select(value => value ?? string.Empty)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray()))
            .ToArray(),
        network.Wires
            .OrderBy(wire => wire.UId)
            .ThenBy(wire => wire.SourceKey, StringComparer.Ordinal)
            .Select(wire => new LineUpPublishedLogicWireDefinition(
                wire.SourceKey,
                wire.UId,
                wire.Endpoints.Select(endpoint => new LineUpPublishedLogicEndpointDefinition(
                    endpoint.Kind ?? string.Empty,
                    endpoint.UId,
                    endpoint.Name ?? string.Empty)).ToArray()))
            .ToArray());

    private static LineUpPublishedGraphProjectionException Error(string code, string summary) => new(code, summary);
}

internal static class LineUpMonitorViewerProjector
{
    private static readonly ImmutableArray<string> V2Capabilities =
    [
        "GraphTopology",
        "ActionTable",
        "TransitionNetwork",
        "StepInterlock",
        "StepSupervision",
        "RuntimeOverlay"
    ];

    private static readonly ImmutableArray<string> V1Capabilities = ["GraphTopology"];
    private static readonly ImmutableArray<string> V1Warnings = ["GraphViewerDetailUnavailable"];

    public static LineUpMonitorViewerPreviewDto Project(LineUpPublishedGraphDefinition published)
    {
        ArgumentNullException.ThrowIfNull(published);
        var v2 = published.ParserSchemaVersion >= 2;
        var capabilities = v2 ? V2Capabilities : V1Capabilities;
        var warnings = v2 ? ImmutableArray<string>.Empty : V1Warnings;
        return new(
            v2 ? 2 : 1,
            capabilities,
            warnings,
            published.BlockName ?? string.Empty,
            published.BlockNumber,
            published.ProgrammingLanguage ?? string.Empty,
            published.Sequences
                .OrderBy(sequence => sequence.SequenceSourceKey, StringComparer.Ordinal)
                .Select(sequence => ProjectSequence(sequence, v2, capabilities, warnings))
                .ToImmutableArray());
    }

    private static LineUpMonitorViewerDefinition ProjectSequence(
        LineUpPublishedSequenceDefinition sequence,
        bool v2,
        ImmutableArray<string> capabilities,
        ImmutableArray<string> warnings) => new(
        v2 ? 2 : 1,
        capabilities,
        warnings,
        sequence.SequenceSourceKey,
        sequence.SourceName ?? string.Empty,
        sequence.DisplayName ?? string.Empty,
        sequence.Steps.Concat(sequence.Transitions)
            .OrderBy(node => node.NodeKind, StringComparer.Ordinal)
            .ThenBy(node => node.Number)
            .ThenBy(node => node.SourceKey, StringComparer.Ordinal)
            .Select(node => new LineUpMonitorViewerNodeDto(
                node.NodeKind,
                node.SourceKey,
                node.SourceName ?? string.Empty,
                node.DisplayName ?? string.Empty,
                node.Number,
                node.IsInitial,
                node.IsFinal,
                node.MaximumStepTime ?? string.Empty,
                node.WarningTime ?? string.Empty,
                node.Comment ?? string.Empty,
                v2 ? node.ActionGroupTitle ?? string.Empty : string.Empty,
                node.ProgrammingLanguage ?? string.Empty,
                node.IsMissing,
                node.IsJump,
                v2 ? node.LogicNetworks.Select(ProjectNetwork).ToImmutableArray() : []))
            .ToImmutableArray(),
        sequence.Actions
            .OrderBy(action => action.StepNumber)
            .ThenBy(action => action.Ordinal)
            .ThenBy(action => action.SourceKey, StringComparer.Ordinal)
            .Select(action => new LineUpMonitorViewerActionDto(
                action.SourceKey,
                action.StepSourceKey,
                action.SourceName ?? string.Empty,
                action.DisplayName ?? string.Empty,
                action.Ordinal,
                action.StepNumber,
                action.Qualifier ?? string.Empty,
                v2 ? action.Event : null,
                v2 ? action.Interlock : null,
                action.Tokens.Select(value => value ?? string.Empty).ToImmutableArray(),
                action.ActionText ?? string.Empty))
            .ToImmutableArray(),
        sequence.Edges
            .OrderBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .Select(edge => new LineUpMonitorViewerEdgeDto(
                edge.SourceKey,
                edge.FromSourceKey,
                edge.ToSourceKey,
                edge.FromNodeKind,
                edge.ToNodeKind,
                edge.EdgeKind,
                edge.BranchOrder))
            .ToImmutableArray());

    private static LineUpMonitorViewerLogicNetworkDto ProjectNetwork(
        LineUpPublishedLogicNetworkDefinition network) => new(
        network.SourceKey,
        network.OwnerSourceKey,
        network.Role,
        network.Ordinal,
        network.ProgrammingLanguage ?? string.Empty,
        network.Title ?? string.Empty,
        network.Parts
            .OrderBy(part => part.UId)
            .ThenBy(part => part.SourceKey, StringComparer.Ordinal)
            .Select(part => new LineUpMonitorViewerLogicPartDto(
                part.SourceKey,
                part.UId,
                part.PartName ?? string.Empty,
                part.Scope ?? string.Empty,
                part.SymbolPath.Select(value => value ?? string.Empty).ToImmutableArray(),
                part.ConstantValue ?? string.Empty,
                part.Negated,
                part.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new LineUpMonitorViewerAttributeDto(
                        item.Key ?? string.Empty,
                        item.Value ?? string.Empty)).ToImmutableArray(),
                part.TemplateValues.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new LineUpMonitorViewerTemplateValueDto(
                        item.Key ?? string.Empty,
                        item.Value ?? string.Empty)).ToImmutableArray(),
                part.PinNames.Select(value => value ?? string.Empty).ToImmutableArray()))
            .ToImmutableArray(),
        network.Wires
            .OrderBy(wire => wire.UId)
            .ThenBy(wire => wire.SourceKey, StringComparer.Ordinal)
            .Select(wire => new LineUpMonitorViewerLogicWireDto(
                wire.SourceKey,
                wire.UId,
                wire.Endpoints.Select(endpoint => new LineUpMonitorViewerLogicEndpointDto(
                    endpoint.Kind ?? string.Empty,
                    endpoint.UId,
                    endpoint.Name ?? string.Empty)).ToImmutableArray()))
            .ToImmutableArray());
}
