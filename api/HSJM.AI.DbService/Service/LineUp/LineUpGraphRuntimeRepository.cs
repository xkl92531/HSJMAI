#nullable enable

using System.Data;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using NpgsqlTypes;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpGraphRuntimeRepository(ISqlSugarClient db)
    : ILineUpGraphRuntimeRepository, IAutoInject
{
    public async Task<LineUpGraphRuntimeBindingStatusData?> LoadBindingAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0)
        {
            return null;
        }

        var rows = await db.Ado.SqlQueryAsync<RuntimeBindingStatusRow>(
                """
                SELECT "Id", "PublishedRevisionId", "BindingStatus", "RuntimeBindingJson",
                       "RuntimeValidationJson", "ValidationRevision", "MonitoringEnabled", "LastValidatedAt"
                FROM "AI_LineUp_GraphStationBinding"
                WHERE "Id" = @BindingId AND "IsDeleted" = FALSE
                """,
                new SugarParameter("@BindingId", bindingId))
            .WaitAsync(cancellationToken);
        if (rows.Count != 1)
        {
            return null;
        }

        var row = rows[0];
        return new(
            row.Id,
            row.PublishedRevisionId,
            row.BindingStatus ?? "Unresolved",
            row.RuntimeBindingJson,
            row.RuntimeValidationJson,
            row.ValidationRevision,
            row.MonitoringEnabled,
            row.LastValidatedAt);
    }

    public async Task<LineUpGraphRuntimeContext?> LoadContextAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rows = await db.Ado.SqlQueryAsync<RuntimeContextRow>(
                """
                SELECT b."Id" AS "BindingId",
                       b."GraphAssetId", b."LineId", b."ProcessId", b."StationId", b."DeviceId",
                       b."InstanceDbBlockId", b."PublishedRevisionId", b."RuntimeBindingJson",
                       b."RuntimeValidationJson", b."ValidationRevision",
                       b."BindingStatus", b."MonitoringEnabled", b."LastValidatedAt",
                       a."PlcProjectId", a."CurrentPlcBlockId", a."StableKey", a."SourceBlockName",
                       a."BlockType" AS "AssetBlockType", a."BlockNumber" AS "AssetBlockNumber",
                       a."ProgrammingLanguage" AS "AssetProgrammingLanguage",
                       r."RevisionNo", r."SourceHash", r."ParserSchemaVersion", r."PreviewJson",
                       d.blockname AS "InstanceDbName", d.blocktype AS "InstanceDbType",
                       d.blocknumber AS "InstanceDbNumber", d.programminglanguage AS "InstanceDbLanguage"
                FROM "AI_LineUp_GraphStationBinding" b
                INNER JOIN "AI_LineUp_GraphAsset" a
                  ON a."Id" = b."GraphAssetId" AND a."IsDeleted" = FALSE
                INNER JOIN "AI_LineUp_GraphRevision" r
                  ON r."Id" = b."PublishedRevisionId"
                 AND r."GraphAssetId" = a."Id"
                 AND r."IsDeleted" = FALSE
                 AND r."Status" <> 'ParseFailed'
                INNER JOIN ai_plc_block_new d
                  ON d.id = b."InstanceDbBlockId"
                 AND d.projectid = a."PlcProjectId"
                 AND d.isdeleted = FALSE
                 AND d.programminglanguage = 'DB'
                 AND d.blocktype = 'InstanceDB'
                WHERE b."Id" = @BindingId
                  AND b."IsDeleted" = FALSE
                  AND b."PublishedRevisionId" IS NOT NULL
                  AND b."InstanceDbBlockId" IS NOT NULL
                """,
                new SugarParameter("@BindingId", bindingId))
            .WaitAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }
        if (rows.Count != 1)
        {
            throw new InvalidOperationException("The GRAPH station runtime binding identity is not unique.");
        }

        var row = rows[0];
        var aliases = await db.Ado.SqlQueryAsync<AliasRow>(
                """
                SELECT "ElementSourceKey", "DisplayName"
                FROM "AI_LineUp_GraphAlias"
                WHERE "BindingId" = @BindingId AND "IsDeleted" = FALSE
                ORDER BY "ElementSourceKey", "Id"
                """,
                new SugarParameter("@BindingId", bindingId))
            .WaitAsync(cancellationToken);
        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias.ElementSourceKey) ||
                !aliasMap.TryAdd(alias.ElementSourceKey, alias.DisplayName ?? string.Empty))
            {
                throw new InvalidOperationException("The GRAPH runtime alias identity is invalid.");
            }
        }

        return new(
            new(
                row.BindingId,
                row.GraphAssetId,
                row.LineId,
                row.ProcessId,
                row.StationId,
                row.DeviceId,
                row.InstanceDbBlockId,
                row.PublishedRevisionId,
                row.RuntimeBindingJson,
                row.RuntimeValidationJson,
                row.ValidationRevision,
                row.BindingStatus ?? "Unresolved",
                row.MonitoringEnabled,
                row.LastValidatedAt),
            new(
                row.GraphAssetId,
                row.PlcProjectId,
                row.CurrentPlcBlockId,
                row.StableKey ?? string.Empty,
                row.SourceBlockName ?? string.Empty,
                row.AssetBlockType ?? string.Empty,
                row.AssetBlockNumber,
                row.AssetProgrammingLanguage ?? string.Empty),
            new(
                row.PublishedRevisionId,
                row.GraphAssetId,
                row.RevisionNo,
                row.SourceHash ?? string.Empty,
                row.ParserSchemaVersion,
                row.PreviewJson ?? string.Empty),
            new(
                row.InstanceDbBlockId!.Value,
                row.PlcProjectId,
                row.InstanceDbName,
                row.InstanceDbType,
                row.InstanceDbNumber,
                row.InstanceDbLanguage),
            aliasMap);
    }

    public async Task<bool> SavePreparedAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string runtimeBindingJson,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || expectedPublishedRevisionId <= 0 || string.IsNullOrWhiteSpace(runtimeBindingJson))
        {
            return false;
        }

        var now = UtcNow();
        var affected = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphStationBinding"
                SET "RuntimeBindingJson" = @RuntimeBindingJson,
                    "RuntimeValidationJson" = NULL,
                    "ValidationRevision" = NULL,
                    "BindingStatus" = 'NeedsReview',
                    "MonitoringEnabled" = FALSE,
                    "LastValidatedAt" = NULL,
                    "ModifyTime" = @Now
                WHERE "Id" = @BindingId
                  AND "IsDeleted" = FALSE
                  AND "PublishedRevisionId" = @PublishedRevisionId
                  AND "InstanceDbBlockId" IS NOT NULL
                """,
                JsonParameter("@RuntimeBindingJson", runtimeBindingJson),
                new SugarParameter("@BindingId", bindingId),
                new SugarParameter("@PublishedRevisionId", expectedPublishedRevisionId),
                TimestampParameter("@Now", now))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> SaveValidatedAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string expectedRuntimeBindingJson,
        string runtimeValidationJson,
        string validationRevision,
        DateTime validatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || expectedPublishedRevisionId <= 0 ||
            string.IsNullOrWhiteSpace(expectedRuntimeBindingJson) ||
            string.IsNullOrWhiteSpace(runtimeValidationJson) ||
            string.IsNullOrWhiteSpace(validationRevision) || validationRevision.Length > 64)
        {
            return false;
        }

        var validatedAt = AsTimestamp(validatedAtUtc);
        var affected = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphStationBinding"
                SET "BindingStatus" = 'Resolved',
                    "RuntimeValidationJson" = @RuntimeValidationJson,
                    "ValidationRevision" = @ValidationRevision,
                    "MonitoringEnabled" = FALSE,
                    "LastValidatedAt" = @ValidatedAt,
                    "ModifyTime" = @ValidatedAt
                WHERE "Id" = @BindingId
                  AND "IsDeleted" = FALSE
                  AND "PublishedRevisionId" = @PublishedRevisionId
                  AND "BindingStatus" = 'NeedsReview'
                  AND "MonitoringEnabled" = FALSE
                  AND "RuntimeBindingJson" = @RuntimeBindingJson
                """,
                new SugarParameter("@BindingId", bindingId),
                new SugarParameter("@PublishedRevisionId", expectedPublishedRevisionId),
                JsonParameter("@RuntimeBindingJson", expectedRuntimeBindingJson),
                JsonParameter("@RuntimeValidationJson", runtimeValidationJson),
                new SugarParameter("@ValidationRevision", validationRevision, global::System.Data.DbType.String),
                TimestampParameter("@ValidatedAt", validatedAt))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> ResetAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || expectedPublishedRevisionId <= 0)
        {
            return false;
        }

        var affected = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphStationBinding"
                SET "RuntimeBindingJson" = NULL,
                    "RuntimeValidationJson" = NULL,
                    "ValidationRevision" = NULL,
                    "BindingStatus" = 'NeedsReview',
                    "MonitoringEnabled" = FALSE,
                    "LastValidatedAt" = NULL,
                    "ModifyTime" = @Now
                WHERE "Id" = @BindingId
                  AND "IsDeleted" = FALSE
                  AND "PublishedRevisionId" = @PublishedRevisionId
                """,
                new SugarParameter("@BindingId", bindingId),
                new SugarParameter("@PublishedRevisionId", expectedPublishedRevisionId),
                TimestampParameter("@Now", UtcNow()))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> SetMonitoringAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string expectedDefinitionRevision,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || (enabled && string.IsNullOrWhiteSpace(expectedDefinitionRevision)))
        {
            return false;
        }

        var now = UtcNow();
        var sql = enabled
            ? """
              UPDATE "AI_LineUp_GraphStationBinding"
              SET "MonitoringEnabled" = TRUE, "ModifyTime" = @Now
              WHERE "Id" = @BindingId
                AND "IsDeleted" = FALSE
                AND "PublishedRevisionId" = @PublishedRevisionId
                AND "BindingStatus" = 'Resolved'
                AND "RuntimeBindingJson" IS NOT NULL
                AND "RuntimeValidationJson" IS NOT NULL
                AND "ValidationRevision" = @ExpectedDefinitionRevision
                AND "LastValidatedAt" IS NOT NULL
              """
            : """
              UPDATE "AI_LineUp_GraphStationBinding"
              SET "MonitoringEnabled" = FALSE, "ModifyTime" = @Now
              WHERE "Id" = @BindingId AND "IsDeleted" = FALSE
              """;
        var affected = await db.Ado.ExecuteCommandAsync(
                sql,
                new SugarParameter("@BindingId", bindingId),
                new SugarParameter("@PublishedRevisionId", expectedPublishedRevisionId),
                new SugarParameter("@ExpectedDefinitionRevision", expectedDefinitionRevision ?? string.Empty, global::System.Data.DbType.String),
                TimestampParameter("@Now", now))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> InvalidateAsync(
        long bindingId,
        long expectedPublishedRevisionId,
        string expectedDefinitionRevision,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || expectedPublishedRevisionId <= 0 || string.IsNullOrWhiteSpace(expectedDefinitionRevision))
        {
            return false;
        }

        var affected = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphStationBinding"
                SET "RuntimeBindingJson" = NULL,
                    "RuntimeValidationJson" = NULL,
                    "ValidationRevision" = NULL,
                    "BindingStatus" = 'NeedsReview',
                    "MonitoringEnabled" = FALSE,
                    "LastValidatedAt" = NULL,
                    "ModifyTime" = @Now
                WHERE "Id" = @BindingId
                  AND "IsDeleted" = FALSE
                  AND "PublishedRevisionId" = @PublishedRevisionId
                  AND "ValidationRevision" = @ExpectedDefinitionRevision
                """,
                new SugarParameter("@BindingId", bindingId),
                new SugarParameter("@PublishedRevisionId", expectedPublishedRevisionId),
                new SugarParameter("@ExpectedDefinitionRevision", expectedDefinitionRevision, global::System.Data.DbType.String),
                TimestampParameter("@Now", UtcNow()))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    private static DateTime UtcNow() => AsTimestamp(DateTime.UtcNow);

    private static DateTime AsTimestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static SugarParameter JsonParameter(string name, string value) => new(name, value)
    {
        CustomDbType = NpgsqlDbType.Jsonb
    };

    private static SugarParameter TimestampParameter(string name, DateTime value) => new(name, AsTimestamp(value))
    {
        CustomDbType = NpgsqlDbType.Timestamp
    };

    private sealed class RuntimeContextRow
    {
        public long BindingId { get; set; }
        public long GraphAssetId { get; set; }
        public long LineId { get; set; }
        public long ProcessId { get; set; }
        public long StationId { get; set; }
        public long DeviceId { get; set; }
        public long? InstanceDbBlockId { get; set; }
        public long PublishedRevisionId { get; set; }
        public string? RuntimeBindingJson { get; set; }
        public string? RuntimeValidationJson { get; set; }
        public string? ValidationRevision { get; set; }
        public string? BindingStatus { get; set; }
        public bool MonitoringEnabled { get; set; }
        public DateTime? LastValidatedAt { get; set; }
        public long PlcProjectId { get; set; }
        public long CurrentPlcBlockId { get; set; }
        public string? StableKey { get; set; }
        public string? SourceBlockName { get; set; }
        public string? AssetBlockType { get; set; }
        public int AssetBlockNumber { get; set; }
        public string? AssetProgrammingLanguage { get; set; }
        public int RevisionNo { get; set; }
        public string? SourceHash { get; set; }
        public int ParserSchemaVersion { get; set; }
        public string? PreviewJson { get; set; }
        public string? InstanceDbName { get; set; }
        public string? InstanceDbType { get; set; }
        public int? InstanceDbNumber { get; set; }
        public string? InstanceDbLanguage { get; set; }
    }

    private sealed class RuntimeBindingStatusRow
    {
        public long Id { get; set; }
        public long? PublishedRevisionId { get; set; }
        public string? BindingStatus { get; set; }
        public string? RuntimeBindingJson { get; set; }
        public string? RuntimeValidationJson { get; set; }
        public string? ValidationRevision { get; set; }
        public bool MonitoringEnabled { get; set; }
        public DateTime? LastValidatedAt { get; set; }
    }

    private sealed class AliasRow
    {
        public string? ElementSourceKey { get; set; }
        public string? DisplayName { get; set; }
    }
}
