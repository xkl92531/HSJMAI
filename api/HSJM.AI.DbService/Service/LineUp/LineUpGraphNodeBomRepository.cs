using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

/// <summary>
/// GRAPH 节点 BOM 点位图关联仓储（AI_LineUp_GraphNodeBom）。
/// 与 LineUpImageMarkRepository 同一套约束：PG 大小写保留列名一律手写 SQL，record 投影不可靠。
/// </summary>
public sealed class LineUpGraphNodeBomRepository(ISqlSugarClient db)
    : ILineUpGraphNodeBomRepository, IAutoInject
{
    private const string RowSelect = @"SELECT ""Id"", ""BatchId"", ""LineId"", ""DeviceId"", ""BlockName"",
                         ""SequenceSourceKey"", ""NodeSourceKey"", ""NodeKind"",
                         ""SubStationId"", ""BomId"", ""BomRefNo"", ""TagCode"",
                         ""ImageId"", ""MarkId"", ""ImageStorageKey""
                   FROM ""AI_LineUp_GraphNodeBom""";

    public async Task ReplaceBatchRowsAsync(
        long batchId,
        IReadOnlyList<LineUpGraphNodeBomEntity> rows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            db.Ado.BeginTran();
            await db.Ado.ExecuteCommandAsync(
                @"DELETE FROM ""AI_LineUp_GraphNodeBom"" WHERE ""BatchId"" = @BatchId",
                new SugarParameter("@BatchId", batchId));
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await db.Ado.ExecuteCommandAsync(
                    """
                    INSERT INTO "AI_LineUp_GraphNodeBom"
                    ("Id", "BatchId", "LineId", "DeviceId", "BlockName", "SequenceSourceKey",
                     "NodeSourceKey", "NodeKind", "SubStationId", "BomId", "BomRefNo", "TagCode",
                     "ImageId", "MarkId", "ImageStorageKey", "IsDeleted", "CreateTime")
                    VALUES (@Id, @BatchId, @LineId, @DeviceId, @BlockName, @SequenceSourceKey,
                            @NodeSourceKey, @NodeKind, @SubStationId, @BomId, @BomRefNo, @TagCode,
                            @ImageId, @MarkId, @ImageStorageKey, FALSE, @CreateTime)
                    """,
                    new SugarParameter("@Id", row.Id),
                    new SugarParameter("@BatchId", row.BatchId),
                    new SugarParameter("@LineId", row.LineId),
                    new SugarParameter("@DeviceId", row.DeviceId),
                    new SugarParameter("@BlockName", row.BlockName),
                    new SugarParameter("@SequenceSourceKey", row.SequenceSourceKey),
                    new SugarParameter("@NodeSourceKey", row.NodeSourceKey),
                    new SugarParameter("@NodeKind", row.NodeKind),
                    new SugarParameter("@SubStationId", row.SubStationId),
                    new SugarParameter("@BomId", row.BomId),
                    new SugarParameter("@BomRefNo", (object?)row.BomRefNo ?? DBNull.Value),
                    new SugarParameter("@TagCode", (object?)row.TagCode ?? DBNull.Value),
                    new SugarParameter("@ImageId", row.ImageId),
                    new SugarParameter("@MarkId", row.MarkId),
                    new SugarParameter("@ImageStorageKey", (object?)row.ImageStorageKey ?? DBNull.Value),
                    new SugarParameter("@CreateTime", DateTime.Now));
            }

            db.Ado.CommitTran();
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public async Task<List<LineUpGraphNodeBomEntity>> ListByBatchAsync(
        long batchId,
        string? nodeSourceKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sql = $@"{RowSelect}
                   WHERE ""IsDeleted"" = FALSE AND ""BatchId"" = @BatchId";
        var parameters = new List<SugarParameter> { new("@BatchId", batchId) };
        if (!string.IsNullOrWhiteSpace(nodeSourceKey))
        {
            sql += @" AND ""NodeSourceKey"" = @NodeSourceKey";
            parameters.Add(new SugarParameter("@NodeSourceKey", nodeSourceKey));
        }

        sql += @" ORDER BY ""NodeSourceKey"", ""BomId"", ""MarkId""";
        return await db.Ado.SqlQueryAsync<LineUpGraphNodeBomEntity>(sql, parameters.ToArray());
    }

    public async Task<long?> FindLatestMaterializedBatchAsync(
        long lineId,
        string blockName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await db.Ado.SqlQueryAsync<long>(
            """
            SELECT r."BatchId"
            FROM "AI_LineUp_GraphNodeBom" r
            JOIN "AI_LineUp_ImportBatch" b ON b."Id" = r."BatchId"
            WHERE r."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
              AND b."LineId" = @LineId AND r."BlockName" = @BlockName
            ORDER BY b."CreateTime" DESC, r."BatchId" DESC
            LIMIT 1
            """,
            new SugarParameter("@LineId", lineId),
            new SugarParameter("@BlockName", blockName));
        return rows.Count == 1 ? rows[0] : null;
    }

    public async Task<LineUpGraphNodeBomStationData> LoadStationDataAsync(
        long subStationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = new LineUpGraphNodeBomStationData();
        data.Boms.AddRange(await db.Ado.SqlQueryAsync<LineUpGraphNodeBomBomRow>(
            @"SELECT ""Id"", ""RefNo"", ""TagCode""
              FROM ""Lup_BomDirectory""
              WHERE ""IsDeleted"" = FALSE AND ""StationId"" = @SubStationId",
            new SugarParameter("@SubStationId", subStationId)));
        data.Images.AddRange(await db.Ado.SqlQueryAsync<LineUpGraphNodeBomImageRow>(
            @"SELECT ""Id"", ""CurrentStorageKey""
              FROM ""Lup_StationImage""
              WHERE ""IsDeleted"" = FALSE AND ""StationId"" = @SubStationId",
            new SugarParameter("@SubStationId", subStationId)));
        data.Marks.AddRange(await db.Ado.SqlQueryAsync<LineUpGraphNodeBomMarkRow>(
            @"SELECT m.""Id"", m.""StationImageId"", m.""MarkTagBom""
              FROM ""Lup_StationImageMark"" m
              JOIN ""Lup_StationImage"" i ON i.""Id"" = m.""StationImageId""
              WHERE m.""IsDeleted"" = FALSE AND i.""IsDeleted"" = FALSE AND i.""StationId"" = @SubStationId",
            new SugarParameter("@SubStationId", subStationId)));
        return data;
    }
}
