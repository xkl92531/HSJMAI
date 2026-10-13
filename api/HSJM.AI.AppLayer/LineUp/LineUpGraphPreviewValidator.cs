#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.LineUp.Models;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpGraphPreviewValidationException : Exception
{
    public LineUpGraphPreviewValidationException(string code, string safeSummary)
        : base(safeSummary)
    {
        Code = code;
        SafeSummary = safeSummary;
    }

    public string Code { get; }
    public string SafeSummary { get; }
}

public static class LineUpGraphPreviewValidator
{
    public const int MaximumPreviewJsonBytes = 32 * 1024 * 1024;
    public const int MaximumSequences = 256;
    public const int MaximumNodes = 32_768;
    public const int MaximumActions = 65_536;
    public const int MaximumEdges = 65_536;
    public const int MaximumLogicNetworks = 32_768;
    public const int MaximumLogicPartsPerNetwork = 4_096;
    public const int MaximumLogicWiresPerNetwork = 8_192;
    public const int MaximumLogicEndpointsPerWire = 64;
    public const int MaximumPartAttributes = 64;
    public const int MaximumPartAttributeNameLength = 256;
    public const int MaximumPartAttributeValueLength = 4_096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 128
    };

    public static LineUpGraphDraftPreview Deserialize(string json)
    {
        ValidateJsonSize(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw Error("GraphPreviewInvalid", "The GRAPH preview is empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<LineUpGraphDraftPreview>(json, JsonOptions)
                   ?? throw Error("GraphPreviewInvalid", "The GRAPH preview is empty.");
        }
        catch (JsonException)
        {
            throw Error("GraphPreviewInvalid", "The GRAPH preview cannot be read.");
        }
        catch (NotSupportedException)
        {
            throw Error("GraphPreviewInvalid", "The GRAPH preview cannot be read.");
        }
    }

    public static void ValidateJsonSize(string json)
    {
        if (json is not null && Encoding.UTF8.GetByteCount(json) > MaximumPreviewJsonBytes)
        {
            throw Error("GraphPreviewTooLarge", "The GRAPH preview exceeds the 32 MiB limit.");
        }
    }

    public static void ValidateForDraft(LineUpGraphDraftPreview preview) =>
        Validate(preview, rejectBlockingIssues: false);

    public static void ValidateForPublication(LineUpGraphDraftPreview preview) =>
        Validate(preview, rejectBlockingIssues: true);

    public static void ValidateSchema(
        LineUpGraphDraftPreview preview,
        int parserSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var embeddedSchema = preview.Schema ?? string.Empty;
        var matches = parserSchemaVersion switch
        {
            1 => string.IsNullOrEmpty(embeddedSchema),
            >= 2 => string.Equals(
                embeddedSchema,
                LineUpGraphDraftBuilder.PreviewSchema,
                StringComparison.Ordinal),
            _ => false
        };
        if (!matches)
        {
            throw Error(
                "GraphPreviewSchemaMismatch",
                "The GRAPH preview schema does not match its immutable revision metadata.");
        }
    }

    private static void Validate(LineUpGraphDraftPreview preview, bool rejectBlockingIssues)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateIssues(preview.Issues, rejectBlockingIssues);
        ValidateStructure(preview);
    }

    private static void ValidateIssues(
        IReadOnlyList<LineUpGraphPreviewIssue> issues,
        bool rejectBlockingIssues)
    {
        if (issues is null || issues.Any(issue => issue is null))
        {
            throw Error("GraphPreviewInvalid", "The GRAPH issue collection is missing or invalid.");
        }
        if (rejectBlockingIssues && issues.Any(issue =>
                string.Equals(issue.Severity, "Blocking", StringComparison.OrdinalIgnoreCase)))
        {
            throw Error("GraphPreviewBlockingIssue", "The GRAPH preview contains blocking issues.");
        }
    }

    private static void ValidateStructure(LineUpGraphDraftPreview preview)
    {
        if (preview.Sequences is null || preview.Sequences.Count == 0)
        {
            throw Error("GraphPreviewEmpty", "The GRAPH preview contains no Sequence.");
        }
        if (preview.Sequences.Count > MaximumSequences)
        {
            throw Error("GraphPreviewTooLarge", "The GRAPH preview contains too many Sequences.");
        }

        var allSourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var nodeCount = 0;
        var actionCount = 0;
        var edgeCount = 0;
        var networkCount = 0;
        foreach (var sequence in preview.Sequences)
        {
            if (sequence is null || sequence.Nodes is null ||
                sequence.Actions is null || sequence.Edges is null)
            {
                throw Error("GraphPreviewInvalid", "The GRAPH Sequence is incomplete.");
            }
            AddSourceKey(allSourceKeys, sequence.SourceKey);
            nodeCount = CheckedCount(nodeCount, sequence.Nodes.Count, MaximumNodes);
            actionCount = CheckedCount(actionCount, sequence.Actions.Count, MaximumActions);
            edgeCount = CheckedCount(edgeCount, sequence.Edges.Count, MaximumEdges);

            var nodes = new Dictionary<string, LineUpGraphPreviewNode>(StringComparer.Ordinal);
            var stepNumbers = new HashSet<int>();
            foreach (var node in sequence.Nodes)
            {
                if (node is null)
                {
                    throw Error("GraphPreviewInvalid", "The GRAPH node is missing.");
                }
                AddSourceKey(allSourceKeys, node.SourceKey);
                nodes.Add(node.SourceKey, node);
                if (string.Equals(node.NodeKind, "Step", StringComparison.Ordinal))
                {
                    if (node.Number <= 0 || !stepNumbers.Add(node.Number))
                    {
                        throw Error("GraphStepNumberInvalid", "A GRAPH Step number is non-positive or duplicated within its Sequence.");
                    }
                }
                else if (!string.Equals(node.NodeKind, "Transition", StringComparison.Ordinal))
                {
                    throw Error("GraphNodeKindInvalid", "The GRAPH preview contains an unsupported node kind.");
                }

                if (node.LogicNetworks is null)
                {
                    throw Error("GraphPreviewInvalid", "A GRAPH node logic network collection is missing.");
                }
                networkCount = CheckedCount(networkCount, node.LogicNetworks.Count, MaximumLogicNetworks);
                ValidateLogicNetworks(node, allSourceKeys);
            }
            if (stepNumbers.Count == 0)
            {
                throw Error("GraphPreviewEmpty", "A GRAPH Sequence contains no Step.");
            }

            foreach (var action in sequence.Actions)
            {
                if (action is null)
                {
                    throw Error("GraphPreviewInvalid", "The GRAPH action is missing.");
                }
                AddSourceKey(allSourceKeys, action.SourceKey);
                if (string.IsNullOrWhiteSpace(action.StepSourceKey) ||
                    !nodes.TryGetValue(action.StepSourceKey, out var owner) ||
                    !string.Equals(owner.NodeKind, "Step", StringComparison.Ordinal) ||
                    action.StepNumber != owner.Number)
                {
                    throw Error("GraphActionOwnerInvalid", "A GRAPH action does not belong to its exact Step.");
                }
            }

            foreach (var edge in sequence.Edges)
            {
                if (edge is null)
                {
                    throw Error("GraphPreviewInvalid", "The GRAPH edge is missing.");
                }
                AddSourceKey(allSourceKeys, edge.SourceKey);
                if (string.IsNullOrWhiteSpace(edge.FromSourceKey) ||
                    string.IsNullOrWhiteSpace(edge.ToSourceKey) ||
                    !nodes.TryGetValue(edge.FromSourceKey, out var from) ||
                    !nodes.TryGetValue(edge.ToSourceKey, out var to) ||
                    !string.Equals(from.NodeKind, edge.FromNodeKind, StringComparison.Ordinal) ||
                    !string.Equals(to.NodeKind, edge.ToNodeKind, StringComparison.Ordinal))
                {
                    throw Error("GraphEdgeEndpointInvalid", "A GRAPH edge does not reference its exact Sequence endpoints.");
                }
            }
        }
    }

    private static int CheckedCount(int current, int added, int maximum)
    {
        if (added < 0 || current > maximum - added)
        {
            throw Error("GraphPreviewTooLarge", "The GRAPH preview exceeds a structural count limit.");
        }
        return current + added;
    }

    private static void ValidateLogicNetworks(
        LineUpGraphPreviewNode owner,
        ISet<string> allSourceKeys)
    {
        foreach (var network in owner.LogicNetworks)
        {
            if (network is null || network.Parts is null || network.Wires is null ||
                !string.Equals(network.OwnerSourceKey, owner.SourceKey, StringComparison.Ordinal) ||
                network.Ordinal < 0 || !ValidRole(owner.NodeKind, network.Role))
            {
                throw Error("GraphLogicNetworkOwnerInvalid", "A GRAPH logic network has an invalid owner or role.");
            }
            AddSourceKey(allSourceKeys, network.SourceKey);
            if (network.Parts.Count > MaximumLogicPartsPerNetwork ||
                network.Wires.Count > MaximumLogicWiresPerNetwork)
            {
                throw Error("GraphPreviewTooLarge", "A GRAPH logic network exceeds its structural count limit.");
            }

            var partUIds = new HashSet<int>();
            foreach (var part in network.Parts)
            {
                if (part is null || part.UId <= 0 || !partUIds.Add(part.UId) ||
                    part.SymbolPath is null || part.Attributes is null ||
                    part.TemplateValues is null || part.PinNames is null ||
                    part.Attributes.Count > MaximumPartAttributes ||
                    part.Attributes.Any(attribute =>
                        string.IsNullOrEmpty(attribute.Key) ||
                        attribute.Key.Length > MaximumPartAttributeNameLength ||
                        attribute.Value is null ||
                        attribute.Value.Length > MaximumPartAttributeValueLength))
                {
                    throw Error("GraphLogicPartInvalid", "A GRAPH logic Part is invalid or duplicated.");
                }
                AddSourceKey(allSourceKeys, part.SourceKey);
            }

            var wireUIds = new HashSet<int>();
            foreach (var wire in network.Wires)
            {
                if (wire is null || wire.UId <= 0 || !wireUIds.Add(wire.UId) ||
                    wire.Endpoints is null || wire.Endpoints.Count > MaximumLogicEndpointsPerWire)
                {
                    throw Error("GraphLogicWireInvalid", "A GRAPH logic Wire is invalid or duplicated.");
                }
                AddSourceKey(allSourceKeys, wire.SourceKey);
                foreach (var endpoint in wire.Endpoints)
                {
                    if (endpoint is null || endpoint.Kind is not ("Powerrail" or "IdentCon" or "NameCon" or "OpenCon") ||
                        endpoint.UId is <= 0 ||
                        endpoint.Kind is "IdentCon" or "NameCon" &&
                        (endpoint.UId is null || !partUIds.Contains(endpoint.UId.Value)))
                    {
                        throw Error("GraphLogicEndpointInvalid", "A GRAPH logic Wire endpoint is invalid or ambiguous.");
                    }
                }
            }
        }
    }

    private static bool ValidRole(string nodeKind, string role) =>
        string.Equals(nodeKind, "Transition", StringComparison.Ordinal)
            ? string.Equals(role, "TransitionCondition", StringComparison.Ordinal)
            : string.Equals(role, "StepInterlock", StringComparison.Ordinal) ||
              string.Equals(role, "StepSupervision", StringComparison.Ordinal);

    private static void AddSourceKey(ISet<string> sourceKeys, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || !sourceKeys.Add(sourceKey))
        {
            throw Error("GraphSourceKeyInvalid", "A GRAPH SourceKey is empty or duplicated.");
        }
    }

    private static LineUpGraphPreviewValidationException Error(string code, string summary) =>
        new(code, summary);
}
