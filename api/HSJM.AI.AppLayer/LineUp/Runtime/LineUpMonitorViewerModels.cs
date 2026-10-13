#nullable enable

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpMonitorViewerPreviewDto(
    int ViewerSchemaVersion,
    ImmutableArray<string> ViewerCapabilities,
    ImmutableArray<string> ViewerWarnings,
    string BlockName,
    int BlockNumber,
    string ProgrammingLanguage,
    ImmutableArray<LineUpMonitorViewerDefinition> Sequences);

public sealed record LineUpMonitorViewerDefinition(
    int ViewerSchemaVersion,
    ImmutableArray<string> ViewerCapabilities,
    ImmutableArray<string> ViewerWarnings,
    [property: JsonProperty("sourceKey")]
    [property: JsonPropertyName("sourceKey")] string SequenceSourceKey,
    [property: JsonProperty("sourceName")]
    [property: JsonPropertyName("sourceName")] string SequenceSourceName,
    [property: JsonProperty("displayName")]
    [property: JsonPropertyName("displayName")] string SequenceDisplayName,
    ImmutableArray<LineUpMonitorViewerNodeDto> Nodes,
    ImmutableArray<LineUpMonitorViewerActionDto> Actions,
    ImmutableArray<LineUpMonitorViewerEdgeDto> Edges);

public sealed record LineUpMonitorViewerNodeDto(
    string NodeKind,
    string SourceKey,
    string SourceName,
    string DisplayName,
    int Number,
    bool IsInitial,
    bool IsFinal,
    string MaximumStepTime,
    string WarningTime,
    string Comment,
    string ActionGroupTitle,
    string ProgrammingLanguage,
    bool IsMissing,
    bool IsJump,
    ImmutableArray<LineUpMonitorViewerLogicNetworkDto> LogicNetworks);

public sealed record LineUpMonitorViewerActionDto(
    string SourceKey,
    string StepSourceKey,
    string SourceName,
    string DisplayName,
    int Ordinal,
    int StepNumber,
    string Qualifier,
    string? Event,
    bool? Interlock,
    ImmutableArray<string> Tokens,
    string ActionText);

public sealed record LineUpMonitorViewerEdgeDto(
    string SourceKey,
    string FromSourceKey,
    string ToSourceKey,
    string FromNodeKind,
    string ToNodeKind,
    string EdgeKind,
    int? BranchOrder);

public sealed record LineUpMonitorViewerLogicNetworkDto(
    string SourceKey,
    string OwnerSourceKey,
    string Role,
    int Ordinal,
    string ProgrammingLanguage,
    string Title,
    ImmutableArray<LineUpMonitorViewerLogicPartDto> Parts,
    ImmutableArray<LineUpMonitorViewerLogicWireDto> Wires);

public sealed record LineUpMonitorViewerLogicPartDto(
    string SourceKey,
    int UId,
    string PartName,
    string Scope,
    ImmutableArray<string> SymbolPath,
    string ConstantValue,
    bool Negated,
    ImmutableArray<LineUpMonitorViewerAttributeDto> Attributes,
    ImmutableArray<LineUpMonitorViewerTemplateValueDto> TemplateValues,
    ImmutableArray<string> PinNames);

public sealed record LineUpMonitorViewerAttributeDto(string Name, string Value);

public sealed record LineUpMonitorViewerTemplateValueDto(string Name, string Value);

public sealed record LineUpMonitorViewerLogicWireDto(
    string SourceKey,
    int UId,
    ImmutableArray<LineUpMonitorViewerLogicEndpointDto> Endpoints);

public sealed record LineUpMonitorViewerLogicEndpointDto(
    string Kind,
    int? UId,
    string Name);
