#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;

namespace HSJM.AI.AppLayer.LineUp;

public static class LineUpGraphAliasProjector
{
    public const int MaximumDisplayNameLength = 200;

    public static LineUpGraphDraftPreview Apply(
        LineUpGraphDraftPreview source,
        IReadOnlyDictionary<string, string> aliases)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(aliases);

        var validKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sequence in source.Sequences)
        {
            AddUnique(validKeys, sequence.SourceKey);
            foreach (var node in sequence.Nodes)
            {
                AddUnique(validKeys, node.SourceKey);
            }
            foreach (var action in sequence.Actions)
            {
                AddUnique(validKeys, action.SourceKey);
            }
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sourceKey, displayName) in aliases)
        {
            if (!validKeys.Contains(sourceKey))
            {
                throw new LineUpImportException(
                    "GraphAliasTargetInvalid",
                    source.BlockName,
                    "The alias target does not exist in the selected GRAPH revision.");
            }

            var value = displayName?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumDisplayNameLength)
            {
                throw new LineUpImportException(
                    "GraphAliasInvalid",
                    source.BlockName,
                    "The alias display name is invalid.");
            }

            normalized[sourceKey] = value;
        }

        return new LineUpGraphDraftPreview(
            source.BlockName,
            source.BlockNumber,
            source.ProgrammingLanguage,
            source.InstanceDbName,
            source.Sequences.Select(sequence => Clone(sequence, normalized)).ToArray(),
            source.Issues.Select(Clone).ToArray())
        {
            Schema = source.Schema
        };
    }

    private static void AddUnique(HashSet<string> keys, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || !keys.Add(sourceKey))
        {
            throw new LineUpImportException(
                "GraphPreviewInvalid",
                "GRAPH",
                "The GRAPH preview contains an empty or duplicate source key.");
        }
    }

    private static LineUpGraphPreviewSequence Clone(
        LineUpGraphPreviewSequence source,
        IReadOnlyDictionary<string, string> aliases) => new()
    {
        SourceKey = source.SourceKey,
        SourceName = source.SourceName,
        DisplayName = AliasOrSource(source.SourceKey, source.DisplayName, aliases),
        Nodes = source.Nodes.Select(node => Clone(node, aliases)).ToList(),
        Actions = source.Actions.Select(action => Clone(action, aliases)).ToList(),
        Edges = source.Edges.Select(Clone).ToList()
    };

    private static LineUpGraphPreviewNode Clone(
        LineUpGraphPreviewNode source,
        IReadOnlyDictionary<string, string> aliases) => new()
    {
        NodeKind = source.NodeKind,
        SourceKey = source.SourceKey,
        SourceName = source.SourceName,
        DisplayName = AliasOrSource(source.SourceKey, source.DisplayName, aliases),
        Number = source.Number,
        IsInitial = source.IsInitial,
        IsFinal = source.IsFinal,
        MaximumStepTime = source.MaximumStepTime,
        WarningTime = source.WarningTime,
        Comment = source.Comment,
        ActionGroupTitle = source.ActionGroupTitle,
        LogicNetworks = source.LogicNetworks.Select(Clone).ToList(),
        ProgrammingLanguage = source.ProgrammingLanguage,
        IsMissing = source.IsMissing,
        RawXml = source.RawXml,
        IsJump = source.IsJump
    };

    private static LineUpGraphPreviewAction Clone(
        LineUpGraphPreviewAction source,
        IReadOnlyDictionary<string, string> aliases) => new()
    {
        SourceKey = source.SourceKey,
        StepSourceKey = source.StepSourceKey,
        SourceName = source.SourceName,
        DisplayName = AliasOrSource(source.SourceKey, source.DisplayName, aliases),
        Ordinal = source.Ordinal,
        StepNumber = source.StepNumber,
        Qualifier = source.Qualifier,
        Event = source.Event,
        Interlock = source.Interlock,
        Tokens = source.Tokens.ToList(),
        ActionText = source.ActionText
    };

    private static LineUpGraphPreviewEdge Clone(LineUpGraphPreviewEdge source) => new()
    {
        SourceKey = source.SourceKey,
        FromSourceKey = source.FromSourceKey,
        ToSourceKey = source.ToSourceKey,
        FromNodeKind = source.FromNodeKind,
        ToNodeKind = source.ToNodeKind,
        EdgeKind = source.EdgeKind,
        BranchOrder = source.BranchOrder
    };

    private static LineUpGraphLogicNetwork Clone(LineUpGraphLogicNetwork source) => new()
    {
        SourceKey = source.SourceKey,
        OwnerSourceKey = source.OwnerSourceKey,
        Role = source.Role,
        Ordinal = source.Ordinal,
        ProgrammingLanguage = source.ProgrammingLanguage,
        Title = source.Title,
        Parts = source.Parts.Select(Clone).ToList(),
        Wires = source.Wires.Select(Clone).ToList()
    };

    private static LineUpGraphLogicPart Clone(LineUpGraphLogicPart source) => new()
    {
        SourceKey = source.SourceKey,
        UId = source.UId,
        PartName = source.PartName,
        Scope = source.Scope,
        SymbolPath = source.SymbolPath.ToList(),
        ConstantValue = source.ConstantValue,
        Negated = source.Negated,
        Attributes = new Dictionary<string, string>(source.Attributes, StringComparer.Ordinal),
        TemplateValues = new Dictionary<string, string>(source.TemplateValues, StringComparer.Ordinal),
        PinNames = source.PinNames.ToList()
    };

    private static LineUpGraphLogicWire Clone(LineUpGraphLogicWire source) => new()
    {
        SourceKey = source.SourceKey,
        UId = source.UId,
        Endpoints = source.Endpoints.Select(Clone).ToList()
    };

    private static LineUpGraphLogicEndpoint Clone(LineUpGraphLogicEndpoint source) => new()
    {
        Kind = source.Kind,
        UId = source.UId,
        Name = source.Name
    };

    private static LineUpGraphPreviewIssue Clone(LineUpGraphPreviewIssue source) => new()
    {
        Severity = source.Severity,
        IssueType = source.IssueType,
        SourceKey = source.SourceKey,
        Message = source.Message
    };

    private static string AliasOrSource(
        string sourceKey,
        string displayName,
        IReadOnlyDictionary<string, string> aliases) =>
        aliases.TryGetValue(sourceKey, out var value) ? value : displayName;
}
