namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpGraphDraftWrite(
    long PlcProjectId,
    long PlcBlockId,
    string PlcParseTaskId,
    string StableKey,
    string SourceBlockName,
    string BlockType,
    int BlockNumber,
    string ProgrammingLanguage,
    string SourceHash,
    int ParserSchemaVersion,
    string PreviewJson,
    string SummaryJson);

public sealed record LineUpGraphDraftPersistenceResult(
    long AssetId,
    long RevisionId,
    int RevisionNo,
    bool WasReused);
