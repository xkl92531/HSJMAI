#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

[method: JsonConstructor]
internal sealed record LineUpRuntimeBindingDocument(
    string? Schema,
    long BindingId,
    long GraphAssetId,
    long PublishedRevisionId,
    string? SourceHash,
    int ParserSchemaVersion,
    string? DefinitionRevision,
    long PlcProjectId,
    long InstanceDbBlockId,
    string? InstanceDbName,
    bool PersistSamples,
    IReadOnlyList<LineUpRuntimeBindingSequence> Sequences,
    IReadOnlyList<LineUpRuntimeBindingTag> Tags,
    IReadOnlyList<LineUpRuntimeBindingStaticOperand> StaticOperands,
    IReadOnlyList<LineUpRuntimeBindingStaticIssue> StaticIssues,
    IReadOnlyList<LineUpRuntimeLogicNetworkDefinition>? CompiledNetworks)
{
    public const string SchemaVersion = "lineup.graph-runtime-binding.v2";

    public LineUpRuntimeBindingDocument(
        string? schema,
        long bindingId,
        long graphAssetId,
        long publishedRevisionId,
        string? sourceHash,
        int parserSchemaVersion,
        string? definitionRevision,
        long plcProjectId,
        long instanceDbBlockId,
        string? instanceDbName,
        bool persistSamples,
        IReadOnlyList<LineUpRuntimeBindingSequence> sequences,
        IReadOnlyList<LineUpRuntimeBindingTag> tags)
        : this(schema, bindingId, graphAssetId, publishedRevisionId, sourceHash, parserSchemaVersion,
            definitionRevision, plcProjectId, instanceDbBlockId, instanceDbName, persistSamples,
            sequences, tags, [], [], null)
    {
    }
}

internal sealed record LineUpRuntimeBindingSequence(
    string? SequenceSourceKey,
    IReadOnlyList<string> StepSourceKeys);

[method: JsonConstructor]
internal sealed record LineUpRuntimeBindingTag(
    string? SequenceSourceKey,
    string? Role,
    string? OwnerSourceKey,
    string? NetworkSourceKey,
    string? PartSourceKey,
    string? Scope,
    long ResolvedBlockId,
    string? ResolvedBlockName,
    long ResolvedBlockInterfaceId,
    string? VariablePath,
    string? CanonicalPlcPath,
    string? DataType,
    string? NumericAddress,
    int ExpectedScanMs)
{
    public LineUpRuntimeBindingTag(
        string? sequenceSourceKey,
        string? role,
        string? ownerSourceKey,
        string? networkSourceKey,
        string? partSourceKey,
        long resolvedBlockId,
        string? resolvedBlockName,
        long resolvedBlockInterfaceId,
        string? variablePath,
        string? canonicalPlcPath,
        string? dataType,
        string? numericAddress,
        int expectedScanMs)
        : this(sequenceSourceKey, role, ownerSourceKey, networkSourceKey, partSourceKey, "InstanceDb",
            resolvedBlockId, resolvedBlockName, resolvedBlockInterfaceId, variablePath, canonicalPlcPath,
            dataType, numericAddress, expectedScanMs)
    {
    }
}

internal sealed record LineUpRuntimeBindingStaticOperand(
    string? SequenceSourceKey,
    string? Role,
    string? OwnerSourceKey,
    string? NetworkSourceKey,
    string? PartSourceKey,
    string? Scope,
    string? ConstantValue);

internal sealed record LineUpRuntimeBindingStaticIssue(
    string? Severity,
    string? Code,
    string? OwnerSourceKey,
    string? NetworkSourceKey,
    string? PartSourceKey);
