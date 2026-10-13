#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using NpgsqlTypes;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpGraphCatalogRepository(ISqlSugarClient db)
    : ILineUpGraphCatalogRepository, IAutoInject
{
    public async Task<LineUpGraphCatalogLookupData> LoadLookupsAsync(
        long? plcProjectId,
        long? lineId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectRows = await db.Ado.SqlQueryAsync<ProjectRow>(
                """
                SELECT DISTINCT p.id AS "Id", p.projectname AS "Name", p.plcdeviceid AS "PlcDeviceId"
                FROM ai_plc_project_new p
                INNER JOIN "AI_LineUp_GraphAsset" a ON a."PlcProjectId" = p.id AND a."IsDeleted" = FALSE
                WHERE p.isdeleted = FALSE AND (@ProjectId = 0 OR p.id = @ProjectId)
                ORDER BY p.projectname, p.id
                """,
                new SugarParameter("@ProjectId", plcProjectId ?? 0))
            .WaitAsync(cancellationToken);

        var lines = await db.Ado.SqlQueryAsync<LineLookupRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_ProductionLine"
                WHERE "IsDeleted" = FALSE AND "IsEnable" = TRUE
                ORDER BY "Code", "Id"
                """)
            .WaitAsync(cancellationToken);

        var processes = await db.Ado.SqlQueryAsync<ProcessLookupRow>(
                """
                SELECT "Id", "ProductionLineId", "Code", "Name"
                FROM "Basic_Process"
                WHERE "IsDeleted" = FALSE AND "IsEnable" = TRUE
                  AND (@LineId = 0 OR "ProductionLineId" = @LineId)
                ORDER BY "Code", "Id"
                """,
                new SugarParameter("@LineId", lineId ?? 0))
            .WaitAsync(cancellationToken);

        var stations = await db.Ado.SqlQueryAsync<StationLookupRow>(
                """
                SELECT "Id", "ProductionLineId", "ProcessId", "Code", "Name"
                FROM "Basic_WorkStation"
                WHERE "IsDeleted" = FALSE AND "IsEnable" = TRUE
                  AND (@LineId = 0 OR "ProductionLineId" = @LineId)
                ORDER BY "Code", "Id"
                """,
                new SugarParameter("@LineId", lineId ?? 0))
            .WaitAsync(cancellationToken);

        var subStations = await db.Ado.SqlQueryAsync<SubStationLookupRow>(
                """
                SELECT "Id", "ProductionLineID", "ProcessID", "WorkStationID",
                       "SubStationCode", "SubStationName", NULLIF("PlcID", 0) AS "PlcId", "PlcName"
                FROM "Basic_SubStation"
                WHERE "IsDeleted" = FALSE
                  AND (@LineId = 0 OR "ProductionLineID" = @LineId)
                ORDER BY "SortNum", "SubStationCode", "Id"
                """,
                new SugarParameter("@LineId", lineId ?? 0))
            .WaitAsync(cancellationToken);

        var devices = await db.Ado.SqlQueryAsync<DeviceLookupRow>(
                """
                SELECT "Id", "LineID", "DeviceName", "ProjectName", "DeviceType"
                FROM "Basic_Device"
                WHERE "IsDeleted" = FALSE AND "LineID" IS NOT NULL
                  AND (@LineId = 0 OR "LineID" = @LineId)
                ORDER BY "DeviceName", "Id"
                """,
                new SugarParameter("@LineId", lineId ?? 0))
            .WaitAsync(cancellationToken);

        return new(
            projectRows.Select(item => new LineUpGraphCatalogProjectLookup(item.Id, item.Name ?? string.Empty, item.PlcDeviceId)).ToArray(),
            lines.Select(item => new LineUpGraphCatalogLineLookup(item.Id, item.Code ?? string.Empty, item.Name ?? string.Empty)).ToArray(),
            processes.Select(item => new LineUpGraphCatalogProcessLookup(item.Id, item.ProductionLineId, item.Code ?? string.Empty, item.Name ?? string.Empty)).ToArray(),
            stations.Select(item => new LineUpGraphCatalogStationLookup(item.Id, item.ProductionLineId, item.ProcessId, item.Code ?? string.Empty, item.Name ?? string.Empty)).ToArray(),
            subStations.Select(item => new LineUpGraphCatalogSubStationLookup(
                item.Id,
                item.ProductionLineID,
                item.ProcessID,
                item.WorkStationID,
                item.SubStationCode ?? string.Empty,
                item.SubStationName ?? string.Empty,
                item.PlcId,
                item.PlcName)).ToArray(),
            devices.Select(item => new LineUpGraphCatalogDeviceLookup(item.Id, item.LineID, item.DeviceName ?? string.Empty, item.ProjectName, item.DeviceType)).ToArray());
    }

    public async Task<IReadOnlyList<LineUpGraphCatalogAssetData>> QueryAssetsAsync(
        LineUpGraphCatalogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await db.Ado.SqlQueryAsync<AssetRow>(
                """
                SELECT a."Id" AS "AssetId",
                       a."PlcProjectId",
                       COALESCE(p.projectname, '') AS "PlcProjectName",
                       a."SourceBlockName",
                       a."BlockType",
                       a."BlockNumber",
                       a."CurrentPlcBlockId",
                       a."LatestRevisionId",
                       COALESCE(latest."RevisionNo", 0) AS "LatestRevisionNo",
                       COALESCE(latest."Status", 'Draft') AS "LatestRevisionStatus",
                       COALESCE(latest."SummaryJson", '{}'::jsonb)::text AS "SummaryJson",
                       b."Id" AS "BindingId",
                       b."LineId",
                       b."ProcessId",
                       b."StationId",
                       b."SubStationId",
                       b."DeviceId",
                       b."PublishedRevisionId",
                       published."RevisionNo" AS "PublishedRevisionNo",
                       b."BindingStatus",
                       COALESCE(b."MonitoringEnabled", FALSE) AS "MonitoringEnabled",
                       EXISTS (
                           SELECT 1 FROM "AI_LineUp_ImportBatch" ib
                           WHERE ib."IsDeleted" = FALSE AND ib."Status" IN ('PreviewReady', 'Committed')
                             AND ib."PreviewJson" #>> '{Documents,0,BlockName}' = a."SourceBlockName"
                       ) AS "HasPreviewBatch",
                       COUNT(*) OVER()::int AS "TotalCount"
                FROM "AI_LineUp_GraphAsset" a
                LEFT JOIN ai_plc_project_new p ON p.id = a."PlcProjectId" AND p.isdeleted = FALSE
                LEFT JOIN "AI_LineUp_GraphRevision" latest
                  ON latest."Id" = a."LatestRevisionId" AND latest."IsDeleted" = FALSE
                LEFT JOIN "AI_LineUp_GraphStationBinding" b
                  ON b."GraphAssetId" = a."Id" AND b."IsDeleted" = FALSE
                LEFT JOIN "AI_LineUp_GraphRevision" published
                  ON published."Id" = b."PublishedRevisionId" AND published."IsDeleted" = FALSE
                WHERE a."IsDeleted" = FALSE
                  AND (@ProjectId = 0 OR a."PlcProjectId" = @ProjectId)
                  AND (@LineId = 0 OR b."LineId" = @LineId)
                  AND (@ProcessId = 0 OR b."ProcessId" = @ProcessId)
                  AND (@StationId = 0 OR b."StationId" = @StationId)
                  AND (@SubStationId = 0 OR b."SubStationId" = @SubStationId)
                  AND (@DeviceId = 0 OR b."DeviceId" = @DeviceId)
                  AND (@Keyword = '' OR a."SourceBlockName" ILIKE '%' || @Keyword || '%')
                  AND (@Status = ''
                       OR (@Status = 'Unbound' AND b."Id" IS NULL)
                       OR (@Status = 'Published' AND b."PublishedRevisionId" IS NOT NULL
                           AND b."PublishedRevisionId" = a."LatestRevisionId")
                       OR (@Status = 'DraftPending' AND a."LatestRevisionId" IS NOT NULL
                           AND b."PublishedRevisionId" IS DISTINCT FROM a."LatestRevisionId"))
                ORDER BY p.projectname, a."BlockNumber", a."SourceBlockName",
                         b."StationId", b."SubStationId", b."DeviceId", a."Id"
                OFFSET @Offset LIMIT @Limit
                """,
                new SugarParameter("@ProjectId", query.PlcProjectId ?? 0),
                new SugarParameter("@LineId", query.LineId ?? 0),
                new SugarParameter("@ProcessId", query.ProcessId ?? 0),
                new SugarParameter("@StationId", query.StationId ?? 0),
                new SugarParameter("@SubStationId", query.SubStationId ?? 0),
                new SugarParameter("@DeviceId", query.DeviceId ?? 0),
                new SugarParameter("@Keyword", query.Keyword ?? string.Empty),
                new SugarParameter("@Status", query.Status ?? string.Empty),
                new SugarParameter("@Offset", query.Offset),
                new SugarParameter("@Limit", query.Limit))
            .WaitAsync(cancellationToken);
        return rows.Select(MapAsset).ToArray();
    }

    public async Task<LineUpGraphCatalogDetailData?> LoadDetailAsync(
        long assetId,
        long? bindingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assetRows = await db.Ado.SqlQueryAsync<AssetRow>(
                """
                SELECT a."Id" AS "AssetId",
                       a."PlcProjectId",
                       COALESCE(p.projectname, '') AS "PlcProjectName",
                       a."SourceBlockName",
                       a."BlockType",
                       a."BlockNumber",
                       a."CurrentPlcBlockId",
                       a."LatestRevisionId",
                       COALESCE(latest."RevisionNo", 0) AS "LatestRevisionNo",
                       COALESCE(latest."Status", 'Draft') AS "LatestRevisionStatus",
                       COALESCE(latest."SummaryJson", '{}'::jsonb)::text AS "SummaryJson",
                       b."Id" AS "BindingId",
                       b."LineId",
                       b."ProcessId",
                       b."StationId",
                       b."SubStationId",
                       b."DeviceId",
                       b."PublishedRevisionId",
                       published."RevisionNo" AS "PublishedRevisionNo",
                       b."BindingStatus",
                       COALESCE(b."MonitoringEnabled", FALSE) AS "MonitoringEnabled",
                       EXISTS (
                           SELECT 1 FROM "AI_LineUp_ImportBatch" ib
                           WHERE ib."IsDeleted" = FALSE AND ib."Status" IN ('PreviewReady', 'Committed')
                             AND ib."PreviewJson" #>> '{Documents,0,BlockName}' = a."SourceBlockName"
                       ) AS "HasPreviewBatch",
                       1 AS "TotalCount"
                FROM "AI_LineUp_GraphAsset" a
                LEFT JOIN ai_plc_project_new p ON p.id = a."PlcProjectId" AND p.isdeleted = FALSE
                LEFT JOIN "AI_LineUp_GraphRevision" latest
                  ON latest."Id" = a."LatestRevisionId" AND latest."IsDeleted" = FALSE
                LEFT JOIN "AI_LineUp_GraphStationBinding" b
                  ON b."GraphAssetId" = a."Id" AND b."IsDeleted" = FALSE AND b."Id" = @BindingId
                LEFT JOIN "AI_LineUp_GraphRevision" published
                  ON published."Id" = b."PublishedRevisionId" AND published."IsDeleted" = FALSE
                WHERE a."Id" = @AssetId AND a."IsDeleted" = FALSE
                """,
                new SugarParameter("@AssetId", assetId),
                new SugarParameter("@BindingId", bindingId ?? 0))
            .WaitAsync(cancellationToken);
        if (assetRows.Count == 0)
        {
            return null;
        }
        if (assetRows.Count != 1)
        {
            throw new InvalidOperationException("The GRAPH asset identity is not unique.");
        }

        var asset = MapAsset(assetRows[0]);
        var revisionRows = await db.Ado.SqlQueryAsync<RevisionRow>(
                """
                SELECT "Id", "GraphAssetId", "RevisionNo", "Status", "SourceHash", "ParserSchemaVersion",
                       "PreviewJson"::text AS "PreviewJson", "SummaryJson"::text AS "SummaryJson",
                       "PublishedAt", "PublishedBy"
                FROM "AI_LineUp_GraphRevision"
                WHERE "IsDeleted" = FALSE AND "GraphAssetId" = @AssetId
                ORDER BY "RevisionNo" DESC
                """,
                new SugarParameter("@AssetId", assetId))
            .WaitAsync(cancellationToken);
        var revisions = revisionRows.Select(item => new LineUpGraphRevisionData(
            item.Id,
            item.GraphAssetId,
            item.RevisionNo,
            item.Status ?? "Draft",
            item.SourceHash ?? string.Empty,
            item.ParserSchemaVersion,
            item.PreviewJson ?? "{}",
            item.SummaryJson ?? "{}",
            item.PublishedAt,
            item.PublishedBy)).ToArray();

        var binding = bindingId is > 0
            ? await LoadBindingAsync(bindingId.Value, assetId, cancellationToken)
            : null;
        var aliases = binding is null
            ? []
            : (await db.Ado.SqlQueryAsync<AliasRow>(
                    """
                    SELECT "Id", "BindingId", "ElementSourceKey", "DisplayName"
                    FROM "AI_LineUp_GraphAlias"
                    WHERE "IsDeleted" = FALSE AND "BindingId" = @BindingId
                    ORDER BY "ElementSourceKey"
                    """,
                    new SugarParameter("@BindingId", binding.Id))
                .WaitAsync(cancellationToken))
                .Select(item => new LineUpGraphAliasData(
                    item.Id,
                    item.BindingId,
                    item.ElementSourceKey ?? string.Empty,
                    item.DisplayName ?? string.Empty))
                .ToArray();
        return new(asset, revisions, binding, aliases);
    }

    public async Task<LineUpGraphCatalogDetailData?> LoadBindingDetailAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await db.Ado.SqlQueryAsync<long>(
                """
                SELECT "GraphAssetId"
                FROM "AI_LineUp_GraphStationBinding"
                WHERE "IsDeleted" = FALSE AND "Id" = @BindingId
                """,
                new SugarParameter("@BindingId", bindingId))
            .WaitAsync(cancellationToken);
        return rows.Count switch
        {
            0 => null,
            1 => await LoadDetailAsync(rows[0], bindingId, cancellationToken),
            _ => throw new InvalidOperationException("The GRAPH binding identity is not unique.")
        };
    }

    public async Task<LineUpGraphBindingData> SaveBindingAsync(
        LineUpGraphBindingWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.SubStationId is null or <= 0)
        {
            throw new ArgumentException("The GRAPH binding requires an exact work-position identity.", nameof(write));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var now = UtcNow();
        var proposedId = new LineUpGraphStationBindingEntity().Id;
        var rows = await db.Ado.SqlQueryAsync<BindingRow>(
                """
                INSERT INTO "AI_LineUp_GraphStationBinding"
                    ("Id", "GraphAssetId", "LineId", "ProcessId", "StationId", "SubStationId", "DeviceId",
                     "InstanceDbBlockId", "PublishedRevisionId", "BindingStatus", "RuntimeBindingJson",
                     "RuntimeValidationJson", "ValidationRevision", "MonitoringEnabled",
                     "IsDeleted", "CreateTime", "ModifyTime")
                VALUES
                    (@Id, @GraphAssetId, @LineId, @ProcessId, @StationId, @SubStationId, @DeviceId,
                     @InstanceDbBlockId, NULL, @BindingStatus, NULL, NULL, NULL, FALSE, FALSE, @Now, @Now)
                ON CONFLICT ("GraphAssetId", "SubStationId", "DeviceId")
                WHERE "IsDeleted" = FALSE AND "SubStationId" IS NOT NULL
                DO UPDATE SET
                    "LineId" = EXCLUDED."LineId",
                    "ProcessId" = EXCLUDED."ProcessId",
                    "InstanceDbBlockId" = EXCLUDED."InstanceDbBlockId",
                    "BindingStatus" = EXCLUDED."BindingStatus",
                    "RuntimeBindingJson" = NULL,
                    "RuntimeValidationJson" = NULL,
                    "ValidationRevision" = NULL,
                    "MonitoringEnabled" = FALSE,
                    "LastValidatedAt" = NULL,
                    "ModifyTime" = EXCLUDED."ModifyTime"
                RETURNING "Id", "GraphAssetId", "LineId", "ProcessId", "StationId", "SubStationId", "DeviceId",
                          "InstanceDbBlockId", "PublishedRevisionId", "BindingStatus",
                          "MonitoringEnabled", "ModifyTime"
                """,
                new SugarParameter("@Id", proposedId),
                new SugarParameter("@GraphAssetId", write.GraphAssetId),
                new SugarParameter("@LineId", write.LineId),
                new SugarParameter("@ProcessId", write.ProcessId),
                new SugarParameter("@StationId", write.StationId),
                new SugarParameter("@SubStationId", write.SubStationId.Value),
                new SugarParameter("@DeviceId", write.DeviceId),
                new SugarParameter("@InstanceDbBlockId", write.InstanceDbBlockId, global::System.Data.DbType.Int64),
                new SugarParameter("@BindingStatus", write.BindingStatus),
                TimestampParameter("@Now", now))
            .WaitAsync(cancellationToken);
        return MapBinding(rows.Single());
    }

    public async Task<IReadOnlyList<LineUpGraphAliasData>> ReplaceAliasesAsync(
        LineUpGraphAliasWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        db.Ado.BeginTran();
        try
        {
            var ownership = await LockBindingAsync(write.BindingId, cancellationToken);
            if (ownership.LatestRevisionId != write.ExpectedLatestRevisionId)
            {
                throw new LineUpGraphCatalogPersistenceException("GraphRevisionStale");
            }

            var now = UtcNow();
            await db.Ado.ExecuteCommandAsync(
                    """
                    UPDATE "AI_LineUp_GraphAlias"
                    SET "IsDeleted" = TRUE, "ModifyTime" = @Now
                    WHERE "BindingId" = @BindingId AND "IsDeleted" = FALSE
                    """,
                    TimestampParameter("@Now", now),
                    new SugarParameter("@BindingId", write.BindingId))
                .WaitAsync(cancellationToken);

            var entities = write.Aliases.Select(item => new LineUpGraphAliasEntity
            {
                BindingId = write.BindingId,
                ElementSourceKey = item.ElementSourceKey,
                DisplayName = item.DisplayName,
                CreateTime = now,
                ModifyTime = now
            }).ToArray();
            foreach (var entity in entities)
            {
                var inserted = await db.Ado.ExecuteCommandAsync(
                        """
                        INSERT INTO "AI_LineUp_GraphAlias"
                            ("Id", "BindingId", "ElementSourceKey", "DisplayName", "IsDeleted", "CreateTime", "ModifyTime")
                        VALUES
                            (@Id, @BindingId, @ElementSourceKey, @DisplayName, FALSE, @Now, @Now)
                        """,
                        new SugarParameter("@Id", entity.Id),
                        new SugarParameter("@BindingId", entity.BindingId),
                        new SugarParameter("@ElementSourceKey", entity.ElementSourceKey),
                        new SugarParameter("@DisplayName", entity.DisplayName),
                        TimestampParameter("@Now", now))
                    .WaitAsync(cancellationToken);
                if (inserted != 1)
                {
                    throw new InvalidOperationException("The GRAPH alias could not be created.");
                }
            }

            db.Ado.CommitTran();
            return entities.Select(item => new LineUpGraphAliasData(item.Id, item.BindingId, item.ElementSourceKey, item.DisplayName)).ToArray();
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public async Task<LineUpGraphPublishData> PublishAsync(
        LineUpGraphPublishWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        db.Ado.BeginTran();
        try
        {
            var ownership = await LockBindingAsync(write.BindingId, cancellationToken);
            if (ownership.LatestRevisionId != write.ExpectedLatestRevisionId)
            {
                throw new LineUpGraphCatalogPersistenceException("GraphRevisionStale");
            }
            if (ownership.PublishedRevisionId != write.ExpectedPublishedRevisionId)
            {
                throw new LineUpGraphCatalogPersistenceException("GraphPublishConflict");
            }

            var targets = await db.Ado.SqlQueryAsync<TargetRevisionRow>(
                    """
                    SELECT "Id", "RevisionNo"
                    FROM "AI_LineUp_GraphRevision"
                    WHERE "IsDeleted" = FALSE
                      AND "Id" = @TargetRevisionId
                      AND "GraphAssetId" = @GraphAssetId
                      AND "Status" <> 'ParseFailed'
                    """,
                    new SugarParameter("@TargetRevisionId", write.TargetRevisionId),
                    new SugarParameter("@GraphAssetId", ownership.GraphAssetId))
                .WaitAsync(cancellationToken);
            if (targets.Count != 1)
            {
                throw new LineUpGraphCatalogPersistenceException("GraphRevisionMissing");
            }

            var affected = await db.Ado.ExecuteCommandAsync(
                    """
                    UPDATE "AI_LineUp_GraphStationBinding"
                    SET "PublishedRevisionId" = @TargetRevisionId,
                        "BindingStatus" = 'Unresolved',
                        "RuntimeBindingJson" = NULL,
                        "RuntimeValidationJson" = NULL,
                        "ValidationRevision" = NULL,
                        "MonitoringEnabled" = FALSE,
                        "LastValidatedAt" = NULL,
                        "ModifyTime" = @PublishedAt,
                        "ModifierId" = @PublishedBy
                    WHERE "Id" = @BindingId AND "IsDeleted" = FALSE
                      AND "PublishedRevisionId" IS NOT DISTINCT FROM @ExpectedPublishedRevisionId
                    """,
                    new SugarParameter("@TargetRevisionId", write.TargetRevisionId),
                    TimestampParameter("@PublishedAt", write.PublishedAt),
                    new SugarParameter("@PublishedBy", write.PublishedBy, global::System.Data.DbType.Int64),
                    new SugarParameter("@BindingId", write.BindingId),
                    new SugarParameter("@ExpectedPublishedRevisionId", write.ExpectedPublishedRevisionId, global::System.Data.DbType.Int64))
                .WaitAsync(cancellationToken);
            if (affected != 1)
            {
                throw new LineUpGraphCatalogPersistenceException("GraphPublishConflict");
            }

            await db.Ado.ExecuteCommandAsync(
                    """
                    UPDATE "AI_LineUp_GraphRevision" r
                    SET "Status" = CASE
                            WHEN EXISTS (
                                SELECT 1 FROM "AI_LineUp_GraphStationBinding" b
                                WHERE b."PublishedRevisionId" = r."Id" AND b."IsDeleted" = FALSE
                            ) THEN 'Published'
                            WHEN r."Status" = 'Published' THEN 'Archived'
                            ELSE r."Status"
                        END,
                        "PublishedAt" = CASE WHEN r."Id" = @TargetRevisionId THEN @PublishedAt ELSE r."PublishedAt" END,
                        "PublishedBy" = CASE WHEN r."Id" = @TargetRevisionId THEN @PublishedBy ELSE r."PublishedBy" END,
                        "ModifyTime" = @PublishedAt
                    WHERE r."GraphAssetId" = @GraphAssetId AND r."IsDeleted" = FALSE
                    """,
                    new SugarParameter("@TargetRevisionId", write.TargetRevisionId),
                    TimestampParameter("@PublishedAt", write.PublishedAt),
                    new SugarParameter("@PublishedBy", write.PublishedBy, global::System.Data.DbType.Int64),
                    new SugarParameter("@GraphAssetId", ownership.GraphAssetId))
                .WaitAsync(cancellationToken);

            db.Ado.CommitTran();
            var target = targets[0];
            return new(
                write.BindingId,
                target.Id,
                target.RevisionNo,
                ownership.PublishedRevisionId,
                write.PublishedAt,
                false);
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    private async Task<LineUpGraphBindingData?> LoadBindingAsync(
        long bindingId,
        long assetId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Ado.SqlQueryAsync<BindingRow>(
                """
                SELECT "Id", "GraphAssetId", "LineId", "ProcessId", "StationId", "SubStationId", "DeviceId",
                       "InstanceDbBlockId", "PublishedRevisionId", "BindingStatus",
                       "MonitoringEnabled", "ModifyTime"
                FROM "AI_LineUp_GraphStationBinding"
                WHERE "IsDeleted" = FALSE AND "Id" = @BindingId AND "GraphAssetId" = @AssetId
                """,
                new SugarParameter("@BindingId", bindingId),
                new SugarParameter("@AssetId", assetId))
            .WaitAsync(cancellationToken);
        return rows.Count switch
        {
            0 => null,
            1 => MapBinding(rows[0]),
            _ => throw new InvalidOperationException("The GRAPH binding identity is not unique.")
        };
    }

    private async Task<BindingOwnershipRow> LockBindingAsync(long bindingId, CancellationToken cancellationToken)
    {
        var rows = await db.Ado.SqlQueryAsync<BindingOwnershipRow>(
                """
                SELECT b."GraphAssetId", b."PublishedRevisionId", a."LatestRevisionId"
                FROM "AI_LineUp_GraphStationBinding" b
                INNER JOIN "AI_LineUp_GraphAsset" a
                  ON a."Id" = b."GraphAssetId" AND a."IsDeleted" = FALSE
                WHERE b."Id" = @BindingId AND b."IsDeleted" = FALSE
                FOR UPDATE OF b, a
                """,
                new SugarParameter("@BindingId", bindingId))
            .WaitAsync(cancellationToken);
        if (rows.Count != 1)
        {
            throw new LineUpGraphCatalogPersistenceException("GraphBindingMissing");
        }
        return rows[0];
    }

    private static LineUpGraphCatalogAssetData MapAsset(AssetRow item) => new(
        item.AssetId,
        item.PlcProjectId,
        item.PlcProjectName ?? string.Empty,
        item.SourceBlockName ?? string.Empty,
        item.BlockType ?? string.Empty,
        item.BlockNumber,
        item.CurrentPlcBlockId,
        item.LatestRevisionId,
        item.LatestRevisionNo,
        item.LatestRevisionStatus ?? "Draft",
        item.SummaryJson ?? "{}",
        item.BindingId,
        item.LineId,
        item.ProcessId,
        item.StationId,
        item.SubStationId,
        item.DeviceId,
        item.PublishedRevisionId,
        item.PublishedRevisionNo,
        item.BindingStatus,
        item.MonitoringEnabled,
        item.HasPreviewBatch,
        item.TotalCount);

    private static LineUpGraphBindingData MapBinding(BindingRow item) => new(
        item.Id,
        item.GraphAssetId,
        item.LineId,
        item.ProcessId,
        item.StationId,
        item.SubStationId,
        item.DeviceId,
        item.InstanceDbBlockId,
        item.PublishedRevisionId,
        item.BindingStatus ?? "Unresolved",
        item.MonitoringEnabled,
        item.ModifyTime);

    private static DateTime UtcNow() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static SugarParameter TimestampParameter(string name, DateTime value) =>
        new(name, DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
    {
        CustomDbType = NpgsqlDbType.Timestamp
    };

    private sealed class ProjectRow
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public long? PlcDeviceId { get; set; }
    }

    private class LineLookupRow
    {
        public long Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private class ProcessLookupRow : LineLookupRow
    {
        public long ProductionLineId { get; set; }
    }

    private sealed class StationLookupRow : ProcessLookupRow
    {
        public long ProcessId { get; set; }
    }

    private sealed class SubStationLookupRow
    {
        public long Id { get; set; }
        public long ProductionLineID { get; set; }
        public long ProcessID { get; set; }
        public long WorkStationID { get; set; }
        public string? SubStationCode { get; set; }
        public string? SubStationName { get; set; }
        public long? PlcId { get; set; }
        public string? PlcName { get; set; }
    }

    private sealed class DeviceLookupRow
    {
        public long Id { get; set; }
        public long LineID { get; set; }
        public string? DeviceName { get; set; }
        public string? ProjectName { get; set; }
        public string? DeviceType { get; set; }
    }

    private sealed class RevisionRow
    {
        public long Id { get; set; }
        public long GraphAssetId { get; set; }
        public int RevisionNo { get; set; }
        public string? Status { get; set; }
        public string? SourceHash { get; set; }
        public int ParserSchemaVersion { get; set; }
        public string? PreviewJson { get; set; }
        public string? SummaryJson { get; set; }
        public DateTime? PublishedAt { get; set; }
        public long? PublishedBy { get; set; }
    }

    private sealed class AliasRow
    {
        public long Id { get; set; }
        public long BindingId { get; set; }
        public string? ElementSourceKey { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class TargetRevisionRow
    {
        public long Id { get; set; }
        public int RevisionNo { get; set; }
    }

    private sealed class AssetRow
    {
        public long AssetId { get; set; }
        public long PlcProjectId { get; set; }
        public string? PlcProjectName { get; set; }
        public string? SourceBlockName { get; set; }
        public string? BlockType { get; set; }
        public int BlockNumber { get; set; }
        public long CurrentPlcBlockId { get; set; }
        public long? LatestRevisionId { get; set; }
        public int LatestRevisionNo { get; set; }
        public string? LatestRevisionStatus { get; set; }
        public string? SummaryJson { get; set; }
        public long? BindingId { get; set; }
        public long? LineId { get; set; }
        public long? ProcessId { get; set; }
        public long? StationId { get; set; }
        public long? SubStationId { get; set; }
        public long? DeviceId { get; set; }
        public long? PublishedRevisionId { get; set; }
        public int? PublishedRevisionNo { get; set; }
        public string? BindingStatus { get; set; }
        public bool MonitoringEnabled { get; set; }
        public bool HasPreviewBatch { get; set; }
        public int TotalCount { get; set; }
    }

    private sealed class BindingRow
    {
        public long Id { get; set; }
        public long GraphAssetId { get; set; }
        public long LineId { get; set; }
        public long ProcessId { get; set; }
        public long StationId { get; set; }
        public long? SubStationId { get; set; }
        public long DeviceId { get; set; }
        public long? InstanceDbBlockId { get; set; }
        public long? PublishedRevisionId { get; set; }
        public string? BindingStatus { get; set; }
        public bool MonitoringEnabled { get; set; }
        public DateTime? ModifyTime { get; set; }
    }

    private sealed class BindingOwnershipRow
    {
        public long GraphAssetId { get; set; }
        public long? PublishedRevisionId { get; set; }
        public long? LatestRevisionId { get; set; }
    }
}
