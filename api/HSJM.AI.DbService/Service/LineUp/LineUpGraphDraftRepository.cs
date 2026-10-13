#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using NpgsqlTypes;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpGraphDraftRepository(ISqlSugarClient db)
    : ILineUpGraphDraftRepository, IAutoInject
{
    public async Task<LineUpGraphDraftPersistenceResult> SaveDraftAsync(
        LineUpGraphDraftWrite draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        cancellationToken.ThrowIfCancellationRequested();

        db.Ado.BeginTran();
        try
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            var proposedAssetId = new LineUpGraphAssetEntity().Id;
            var assetIds = await db.Ado.SqlQueryAsync<long>(
                    @"INSERT INTO ""AI_LineUp_GraphAsset""
                        (""Id"", ""PlcProjectId"", ""StableKey"", ""SourceBlockName"",
                         ""BlockType"", ""BlockNumber"", ""ProgrammingLanguage"",
                         ""CurrentPlcBlockId"", ""LatestRevisionId"", ""IsDeleted"",
                         ""CreateTime"", ""ModifyTime"")
                      VALUES
                        (@Id, @PlcProjectId, @StableKey, @SourceBlockName,
                         @BlockType, @BlockNumber, @ProgrammingLanguage,
                         @CurrentPlcBlockId, NULL, FALSE, @Now, @Now)
                      ON CONFLICT (""PlcProjectId"", ""StableKey"") WHERE ""IsDeleted"" = FALSE
                      DO UPDATE SET
                         ""SourceBlockName"" = EXCLUDED.""SourceBlockName"",
                         ""BlockType"" = EXCLUDED.""BlockType"",
                         ""BlockNumber"" = EXCLUDED.""BlockNumber"",
                         ""ProgrammingLanguage"" = EXCLUDED.""ProgrammingLanguage"",
                         ""CurrentPlcBlockId"" = EXCLUDED.""CurrentPlcBlockId"",
                         ""ModifyTime"" = EXCLUDED.""ModifyTime""
                      RETURNING ""Id""",
                    new SugarParameter("@Id", proposedAssetId),
                    new SugarParameter("@PlcProjectId", draft.PlcProjectId),
                    new SugarParameter("@StableKey", draft.StableKey),
                    new SugarParameter("@SourceBlockName", draft.SourceBlockName),
                    new SugarParameter("@BlockType", draft.BlockType),
                    new SugarParameter("@BlockNumber", draft.BlockNumber),
                    new SugarParameter("@ProgrammingLanguage", draft.ProgrammingLanguage),
                    new SugarParameter("@CurrentPlcBlockId", draft.PlcBlockId),
                    TimestampParameter("@Now", now))
                .WaitAsync(cancellationToken);
            var assetId = assetIds.Single();

            // INSERT ... ON CONFLICT DO UPDATE holds a row lock on the durable asset
            // until this transaction ends, serializing revision-number allocation.
            var matchingRevisions = await db.Ado.SqlQueryAsync<DraftRevisionIdentityRow>(
                    """
                    SELECT "Id", "RevisionNo"
                    FROM "AI_LineUp_GraphRevision"
                    WHERE "IsDeleted" = FALSE
                      AND "GraphAssetId" = @GraphAssetId
                      AND "SourceHash" = @SourceHash
                      AND "ParserSchemaVersion" = @ParserSchemaVersion
                      AND "Status" <> 'ParseFailed'
                    """,
                    new SugarParameter("@GraphAssetId", assetId),
                    new SugarParameter("@SourceHash", draft.SourceHash),
                    new SugarParameter("@ParserSchemaVersion", draft.ParserSchemaVersion))
                .WaitAsync(cancellationToken);
            if (matchingRevisions.Count > 1)
            {
                throw new InvalidOperationException("The GRAPH revision identity is not unique.");
            }

            if (matchingRevisions.Count == 1)
            {
                var existing = matchingRevisions[0];
                await UpdateAssetRevisionAsync(assetId, draft.PlcBlockId, existing.Id, now, cancellationToken);
                db.Ado.CommitTran();
                return new(assetId, existing.Id, existing.RevisionNo, true);
            }

            var revisionNo = await db.Ado.SqlQuerySingleAsync<int>(
                    @"SELECT COALESCE(MAX(""RevisionNo""), 0) + 1
                      FROM ""AI_LineUp_GraphRevision""
                      WHERE ""GraphAssetId"" = @GraphAssetId",
                    new SugarParameter("@GraphAssetId", assetId))
                .WaitAsync(cancellationToken);
            var revisionId = new LineUpGraphRevisionEntity().Id;
            var inserted = await db.Ado.ExecuteCommandAsync(
                    @"INSERT INTO ""AI_LineUp_GraphRevision""
                        (""Id"", ""GraphAssetId"", ""PlcBlockId"", ""PlcParseTaskId"",
                         ""SourceHash"", ""ParserSchemaVersion"", ""RevisionNo"", ""Status"",
                         ""PreviewJson"", ""SummaryJson"", ""ParseErrorCode"",
                         ""PublishedAt"", ""PublishedBy"", ""IsDeleted"",
                         ""CreateTime"", ""ModifyTime"")
                      VALUES
                        (@Id, @GraphAssetId, @PlcBlockId, @PlcParseTaskId,
                         @SourceHash, @ParserSchemaVersion, @RevisionNo, 'Draft',
                         CAST(@PreviewJson AS jsonb), CAST(@SummaryJson AS jsonb), NULL,
                         NULL, NULL, FALSE, @Now, @Now)",
                    new SugarParameter("@Id", revisionId),
                    new SugarParameter("@GraphAssetId", assetId),
                    new SugarParameter("@PlcBlockId", draft.PlcBlockId),
                    new SugarParameter("@PlcParseTaskId", draft.PlcParseTaskId),
                    new SugarParameter("@SourceHash", draft.SourceHash),
                    new SugarParameter("@ParserSchemaVersion", draft.ParserSchemaVersion),
                    new SugarParameter("@RevisionNo", revisionNo),
                    new SugarParameter("@PreviewJson", draft.PreviewJson),
                    new SugarParameter("@SummaryJson", draft.SummaryJson),
                    TimestampParameter("@Now", now))
                .WaitAsync(cancellationToken);
            if (inserted != 1)
            {
                throw new InvalidOperationException("The GRAPH draft revision could not be created.");
            }

            await UpdateAssetRevisionAsync(assetId, draft.PlcBlockId, revisionId, now, cancellationToken);
            db.Ado.CommitTran();
            return new(assetId, revisionId, revisionNo, false);
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    private async Task UpdateAssetRevisionAsync(
        long assetId,
        long plcBlockId,
        long revisionId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var affected = await db.Ado.ExecuteCommandAsync(
                @"UPDATE ""AI_LineUp_GraphAsset""
                  SET ""CurrentPlcBlockId"" = @PlcBlockId,
                      ""LatestRevisionId"" = @RevisionId,
                      ""ModifyTime"" = @Now
                  WHERE ""Id"" = @AssetId AND ""IsDeleted"" = FALSE",
                new SugarParameter("@PlcBlockId", plcBlockId),
                new SugarParameter("@RevisionId", revisionId),
                TimestampParameter("@Now", now),
                new SugarParameter("@AssetId", assetId))
            .WaitAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException("The GRAPH asset changed during draft creation.");
        }
    }

    private static void Validate(LineUpGraphDraftWrite draft)
    {
        if (draft.PlcProjectId <= 0 || draft.PlcBlockId <= 0 ||
            string.IsNullOrWhiteSpace(draft.PlcParseTaskId) ||
            string.IsNullOrWhiteSpace(draft.StableKey) ||
            string.IsNullOrWhiteSpace(draft.SourceBlockName) ||
            !string.Equals(draft.BlockType?.Trim(), "FB", StringComparison.OrdinalIgnoreCase) ||
            draft.BlockNumber < 0 ||
            !string.Equals(draft.ProgrammingLanguage?.Trim(), "GRAPH", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(draft.SourceHash) || draft.ParserSchemaVersion <= 0 ||
            string.IsNullOrWhiteSpace(draft.PreviewJson) || string.IsNullOrWhiteSpace(draft.SummaryJson))
        {
            throw new ArgumentException("The GRAPH draft write is incomplete.", nameof(draft));
        }
    }

    private static SugarParameter TimestampParameter(string name, DateTime value) =>
        new(name, DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
    {
        CustomDbType = NpgsqlDbType.Timestamp
    };

    private sealed class DraftRevisionIdentityRow
    {
        public long Id { get; set; }
        public int RevisionNo { get; set; }
    }
}
