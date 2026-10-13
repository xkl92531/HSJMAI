#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Models;

public sealed record LineUpGraphDraftPreview(
    string BlockName,
    int BlockNumber,
    string ProgrammingLanguage,
    string? InstanceDbName,
    IReadOnlyList<LineUpGraphPreviewSequence> Sequences,
    IReadOnlyList<LineUpGraphPreviewIssue> Issues)
{
    public string Schema { get; init; } = string.Empty;
}

public sealed record LineUpPlcGraphBlockIdentity(
    long PlcProjectId,
    long PlcBlockId,
    string BlockName,
    string BlockType,
    int BlockNumber,
    string ProgrammingLanguage);

public sealed record LineUpGraphDraftCandidate(
    long PlcProjectId,
    long PlcBlockId,
    string PlcParseTaskId,
    string SourceHash,
    string StableKey,
    string SourceBlockName,
    string BlockType,
    int BlockNumber,
    string ProgrammingLanguage,
    LineUpGraphDraftPreview Preview,
    LineUpImportSummaryDto Summary,
    IReadOnlyList<LineUpGraphPreviewIssue> Issues)
{
    public LineUpGraphDraftWrite ToWrite()
    {
        try
        {
            LineUpGraphPreviewValidator.ValidateSchema(
                Preview,
                LineUpGraphDraftBuilder.ParserSchemaVersion);
            LineUpGraphPreviewValidator.ValidateForDraft(Preview);
            var previewJson = JsonSerializer.Serialize(Preview);
            LineUpGraphPreviewValidator.ValidateJsonSize(previewJson);
            return new(
                PlcProjectId,
                PlcBlockId,
                PlcParseTaskId,
                StableKey,
                SourceBlockName,
                BlockType,
                BlockNumber,
                ProgrammingLanguage,
                SourceHash,
                LineUpGraphDraftBuilder.ParserSchemaVersion,
                previewJson,
                JsonSerializer.Serialize(Summary));
        }
        catch (LineUpGraphPreviewValidationException error)
        {
            throw new LineUpImportException(error.Code, "GRAPH", error.SafeSummary);
        }
    }
}
