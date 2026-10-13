#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;

namespace HSJM.AI.AppLayer.LineUp;

public static class LineUpGraphRevisionComparer
{
    public static LineUpGraphRevisionDiffDto Compare(
        LineUpGraphDraftPreview? published,
        LineUpGraphDraftPreview target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var previous = published is null
            ? new Dictionary<string, ElementSnapshot>(StringComparer.Ordinal)
            : Flatten(published);
        var current = Flatten(target);
        var items = new List<LineUpGraphDiffItemDto>();

        foreach (var item in current.Values)
        {
            if (!previous.TryGetValue(item.Identity, out var old))
            {
                items.Add(new("Added", item.Kind, item.SourceKey, item.SourceName));
            }
            else if (!string.Equals(old.Signature, item.Signature, StringComparison.Ordinal))
            {
                items.Add(new("Changed", item.Kind, item.SourceKey, item.SourceName));
            }
        }

        foreach (var item in previous.Values)
        {
            if (!current.ContainsKey(item.Identity))
            {
                items.Add(new("Removed", item.Kind, item.SourceKey, item.SourceName));
            }
        }

        var ordered = items
            .OrderBy(item => item.ChangeKind, StringComparer.Ordinal)
            .ThenBy(item => item.ElementKind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ToArray();

        return new(
            published is null,
            Count(ordered, "Added"),
            Count(ordered, "Removed"),
            Count(ordered, "Changed"),
            ordered);
    }

    private static Dictionary<string, ElementSnapshot> Flatten(LineUpGraphDraftPreview preview)
    {
        var result = new Dictionary<string, ElementSnapshot>(StringComparer.Ordinal);
        foreach (var sequence in preview.Sequences)
        {
            Add(result, new(
                "Sequence",
                sequence.SourceKey,
                sequence.SourceName,
                Join(preview.Schema, sequence.SourceName)));
            foreach (var node in sequence.Nodes)
            {
                Add(result, new(
                    "Node",
                    node.SourceKey,
                    node.SourceName,
                    Join(
                        node.NodeKind,
                        node.SourceName,
                        node.Number,
                        node.IsInitial,
                        node.IsFinal,
                        node.MaximumStepTime,
                        node.WarningTime,
                        node.Comment,
                        node.ActionGroupTitle,
                        LogicNetworksFingerprint(node.LogicNetworks),
                        node.ProgrammingLanguage,
                        node.IsMissing,
                        node.RawXml,
                        node.IsJump)));
            }
            foreach (var action in sequence.Actions)
            {
                Add(result, new(
                    "Action",
                    action.SourceKey,
                    action.SourceName,
                    Join(
                        action.StepSourceKey,
                        action.SourceName,
                        action.Ordinal,
                        action.StepNumber,
                        action.Qualifier,
                        action.Event,
                        action.Interlock,
                        string.Join("\u001e", action.Tokens),
                        action.ActionText)));
            }
            foreach (var edge in sequence.Edges)
            {
                Add(result, new(
                    "Edge",
                    edge.SourceKey,
                    edge.SourceKey,
                    Join(
                        edge.FromSourceKey,
                        edge.ToSourceKey,
                        edge.FromNodeKind,
                        edge.ToNodeKind,
                        edge.EdgeKind,
                        edge.BranchOrder)));
            }
        }

        return result;
    }

    private static string LogicNetworksFingerprint(IReadOnlyList<LineUpGraphLogicNetwork> networks) =>
        string.Join("\u001d", networks
            .OrderBy(network => network.SourceKey, StringComparer.Ordinal)
            .ThenBy(network => network.Ordinal)
            .Select(network => Join(
                network.SourceKey,
                network.OwnerSourceKey,
                network.Role,
                network.Ordinal,
                network.ProgrammingLanguage,
                network.Title,
                string.Join("\u001c", network.Parts
                    .OrderBy(part => part.SourceKey, StringComparer.Ordinal)
                    .ThenBy(part => part.UId)
                    .Select(PartFingerprint)),
                string.Join("\u001c", network.Wires
                    .OrderBy(wire => wire.SourceKey, StringComparer.Ordinal)
                    .ThenBy(wire => wire.UId)
                    .Select(WireFingerprint)))));

    private static string PartFingerprint(LineUpGraphLogicPart part) => Join(
        part.SourceKey,
        part.UId,
        part.PartName,
        part.Scope,
        string.Join("\u001b", part.SymbolPath),
        part.ConstantValue,
        part.Negated,
        DictionaryFingerprint(part.Attributes),
        DictionaryFingerprint(part.TemplateValues),
        string.Join("\u001b", part.PinNames));

    private static string WireFingerprint(LineUpGraphLogicWire wire) => Join(
        wire.SourceKey,
        wire.UId,
        string.Join("\u001b", wire.Endpoints
            .OrderBy(endpoint => endpoint.Kind, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.UId)
            .ThenBy(endpoint => endpoint.Name, StringComparer.Ordinal)
            .Select(endpoint => Join(endpoint.Kind, endpoint.UId, endpoint.Name))));

    private static string DictionaryFingerprint(IReadOnlyDictionary<string, string> values) =>
        string.Join("\u001b", values
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Join(item.Key, item.Value)));

    private static void Add(Dictionary<string, ElementSnapshot> target, ElementSnapshot item)
    {
        if (string.IsNullOrWhiteSpace(item.SourceKey) || !target.TryAdd(item.Identity, item))
        {
            throw new LineUpImportException(
                "GraphPreviewInvalid",
                "GRAPH",
                "The GRAPH preview contains an empty or duplicate source key.");
        }
    }

    private static LineUpGraphDiffCounts Count(
        IReadOnlyList<LineUpGraphDiffItemDto> items,
        string changeKind) => new(
        items.Count(item => item.ChangeKind == changeKind && item.ElementKind == "Sequence"),
        items.Count(item => item.ChangeKind == changeKind && item.ElementKind == "Node"),
        items.Count(item => item.ChangeKind == changeKind && item.ElementKind == "Action"),
        items.Count(item => item.ChangeKind == changeKind && item.ElementKind == "Edge"));

    private static string Join(params object?[] values) => string.Join(
        "\u001f",
        values.Select(value => value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        }));

    private sealed record ElementSnapshot(
        string Kind,
        string SourceKey,
        string SourceName,
        string Signature)
    {
        internal string Identity => $"{Kind}:{SourceKey}";
    }
}
