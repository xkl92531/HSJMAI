#nullable enable

using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpPublishedGraphDefinition(
    string Schema,
    long BindingId,
    long GraphAssetId,
    long PublishedRevisionId,
    int PublishedRevisionNo,
    string SourceHash,
    int ParserSchemaVersion,
    string BlockName,
    int BlockNumber,
    string ProgrammingLanguage,
    IReadOnlyList<LineUpPublishedSequenceDefinition> Sequences,
    IReadOnlyList<LineUpPublishedIssueDefinition> Issues);

public sealed record LineUpPublishedSequenceDefinition(
    string SequenceSourceKey,
    string SourceName,
    string DisplayName,
    IReadOnlyList<LineUpPublishedNodeDefinition> Steps,
    IReadOnlyList<LineUpPublishedNodeDefinition> Transitions,
    IReadOnlyList<LineUpPublishedActionDefinition> Actions,
    IReadOnlyList<LineUpPublishedEdgeDefinition> Edges);

public sealed record LineUpPublishedNodeDefinition(
    string NodeKind,
    string SourceKey,
    string SourceName,
    string DisplayName,
    int Number,
    string? ExecutionPath,
    bool IsInitial,
    bool IsFinal,
    string MaximumStepTime,
    string WarningTime,
    string Comment,
    string ActionGroupTitle,
    IReadOnlyList<LineUpPublishedLogicNetworkDefinition> LogicNetworks,
    string ProgrammingLanguage,
    bool IsMissing,
    string RawXml,
    bool IsJump);

public sealed record LineUpPublishedActionDefinition(
    string SourceKey,
    string StepSourceKey,
    string SourceName,
    string DisplayName,
    int Ordinal,
    int StepNumber,
    string Qualifier,
    string? Event,
    bool? Interlock,
    IReadOnlyList<string> Tokens,
    string ActionText);

public sealed record LineUpPublishedLogicNetworkDefinition(
    string SourceKey,
    string OwnerSourceKey,
    string Role,
    int Ordinal,
    string ProgrammingLanguage,
    string Title,
    IReadOnlyList<LineUpPublishedLogicPartDefinition> Parts,
    IReadOnlyList<LineUpPublishedLogicWireDefinition> Wires);

public sealed record LineUpPublishedLogicPartDefinition(
    string SourceKey,
    int UId,
    string PartName,
    string Scope,
    IReadOnlyList<string> SymbolPath,
    string ConstantValue,
    bool Negated,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, string> TemplateValues,
    IReadOnlyList<string> PinNames);

public sealed record LineUpPublishedLogicWireDefinition(
    string SourceKey,
    int UId,
    IReadOnlyList<LineUpPublishedLogicEndpointDefinition> Endpoints);

public sealed record LineUpPublishedLogicEndpointDefinition(
    string Kind,
    int? UId,
    string Name);

public sealed record LineUpPublishedEdgeDefinition(
    string SourceKey,
    string FromSourceKey,
    string ToSourceKey,
    string FromNodeKind,
    string ToNodeKind,
    string EdgeKind,
    int? BranchOrder);

public sealed record LineUpPublishedIssueDefinition(
    string Severity,
    string IssueType,
    string? SourceKey,
    string Message);

public sealed class LineUpPublishedGraphProjectionException : Exception
{
    public LineUpPublishedGraphProjectionException(string code, string safeSummary)
        : base($"{code}: {safeSummary}")
    {
        Code = code;
        SafeSummary = safeSummary;
    }

    public string Code { get; }
    public string SafeSummary { get; }
}
