using HSJM.AI.Core;
using HSJM.AI.IDbService;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

/// <summary>
/// GRAPH 节点成品图快照仓储（AI_LineUp_GraphNodeImage）。
/// 与 LineUpGraphNodeBomRepository 同一套约束：PG 大小写保留列名一律手写 SQL。
/// </summary>
public sealed class LineUpGraphNodeImageRepository(ISqlSugarClient db)
    : ILineUpGraphNodeImageRepository, IAutoInject
{
    private const string RowSelect = @"SELECT ""Id"", ""BatchId"", ""LineId"", ""DeviceId"", ""BlockName"",
                         ""SequenceSourceKey"", ""NodeSourceKey"", ""NodeKind"",
                         ""SubStationId"", ""SourceImageId"", ""StorageKey"", ""PixelWidth"", ""PixelHeight""
                    FROM ""AI_LineUp_GraphNodeImage""";

    public async Task ReplaceNodeRowsAsync(
        long batchId,
        string nodeSourceKey,
        IReadOnlyList<LineUpGraphNodeImageEntity> rows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            db.Ado.BeginTran();
            await db.Ado.ExecuteCommandAsync(
                @"DELETE FROM ""AI_LineUp_GraphNodeImage"" WHERE ""BatchId"" = @BatchId AND ""NodeSourceKey"" = @NodeSourceKey",
                new SugarParameter("@BatchId", batchId),
                new SugarParameter("@NodeSourceKey", nodeSourceKey));
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await db.Ado.ExecuteCommandAsync(
                    """
                    INSERT INTO "AI_LineUp_GraphNodeImage"
                    ("Id", "BatchId", "LineId", "DeviceId", "BlockName", "SequenceSourceKey",
                     "NodeSourceKey", "NodeKind", "SubStationId", "SourceImageId", "StorageKey",
                     "PixelWidth", "PixelHeight", "IsDeleted", "CreateTime")
                    VALUES (@Id, @BatchId, @LineId, @DeviceId, @BlockName, @SequenceSourceKey,
                            @NodeSourceKey, @NodeKind, @SubStationId, @SourceImageId, @StorageKey,
                            @PixelWidth, @PixelHeight, FALSE, @CreateTime)
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
                    new SugarParameter("@SourceImageId", row.SourceImageId),
                    new SugarParameter("@StorageKey", row.StorageKey),
                    new SugarParameter("@PixelWidth", row.PixelWidth),
                    new SugarParameter("@PixelHeight", row.PixelHeight),
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

    public async Task<List<LineUpGraphNodeImageEntity>> ListByNodeAsync(
        long lineId,
        string blockName,
        string nodeSourceKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batches = await db.Ado.SqlQueryAsync<long>(
            """
            SELECT r."BatchId"
            FROM "AI_LineUp_GraphNodeImage" r
            JOIN "AI_LineUp_ImportBatch" b ON b."Id" = r."BatchId"
            WHERE r."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
              AND r."LineId" = @LineId AND r."BlockName" = @BlockName AND r."NodeSourceKey" = @NodeSourceKey
            ORDER BY b."CreateTime" DESC, r."BatchId" DESC
            LIMIT 1
            """,
            new SugarParameter("@LineId", lineId),
            new SugarParameter("@BlockName", blockName),
            new SugarParameter("@NodeSourceKey", nodeSourceKey));
        if (batches.Count == 0)
        {
            return [];
        }

        return await db.Ado.SqlQueryAsync<LineUpGraphNodeImageEntity>(
            $@"{RowSelect}
               WHERE ""IsDeleted"" = FALSE AND ""BatchId"" = @BatchId AND ""NodeSourceKey"" = @NodeSourceKey
               ORDER BY ""SourceImageId""",
            new SugarParameter("@BatchId", batches[0]),
            new SugarParameter("@NodeSourceKey", nodeSourceKey));
    }
}