#nullable enable
using System.Data;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpTagCollectionRuleRepository(ISqlSugarClient db)
    : ILineUpTagCollectionRuleRepository, IAutoInject
{
    public async Task<IReadOnlyList<string>> ListDisabledAsync(
        long plcProjectId,
        CancellationToken cancellationToken)
    {
        if (plcProjectId <= 0)
        {
            return [];
        }

        var rows = await db.Ado.SqlQueryAsync<string>(
                """
                SELECT "VariablePath"
                FROM "AI_LineUp_TagCollectionRule"
                WHERE "PlcProjectId" = @PlcProjectId AND "IsDeleted" = FALSE
                ORDER BY "VariablePath"
                """,
                new SugarParameter("@PlcProjectId", plcProjectId, global::System.Data.DbType.Int64))
            .WaitAsync(cancellationToken);
        return rows;
    }

    public async Task DisableAsync(
        long plcProjectId,
        string variablePath,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variablePath);
        if (plcProjectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plcProjectId));
        }

        var normalized = variablePath.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The variable path is empty.", nameof(variablePath));
        }

        var truncatedReason = reason is null ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 256)];
        var now = DateTime.UtcNow;
        var updated = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_TagCollectionRule"
                SET "Reason" = @Reason, "ModifyTime" = @Now, "IsDeleted" = FALSE
                WHERE "PlcProjectId" = @PlcProjectId AND "VariablePath" = @VariablePath
                """,
                new SugarParameter[]
                {
                    new("@PlcProjectId", plcProjectId, global::System.Data.DbType.Int64),
                    new("@VariablePath", normalized),
                    new("@Reason", truncatedReason),
                    new("@Now", now)
                })
            .WaitAsync(cancellationToken);
        if (updated > 0)
        {
            return;
        }

        // 幂等插入：并发下唯一索引冲突视为已存在（捕获后忽略）。
        try
        {
            await db.Ado.ExecuteCommandAsync(
                    """
                    INSERT INTO "AI_LineUp_TagCollectionRule"
                        ("Id", "PlcProjectId", "VariablePath", "Reason", "IsDeleted", "CreateTime", "ModifyTime")
                    VALUES
                        (@Id, @PlcProjectId, @VariablePath, @Reason, FALSE, @Now, @Now)
                    """,
                    new SugarParameter[]
                    {
                        new("@Id", GeneratePrimaryKeyIdHelper.GetPrimaryKeyId()),
                        new("@PlcProjectId", plcProjectId, global::System.Data.DbType.Int64),
                        new("@VariablePath", normalized),
                        new("@Reason", truncatedReason),
                        new("@Now", now)
                    })
                .WaitAsync(cancellationToken);
        }
        catch (Exception error) when (error.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            // 并发插入竞争：另一请求已建行，视为成功。
        }
    }

    public async Task EnableAsync(
        long plcProjectId,
        string variablePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variablePath);
        if (plcProjectId <= 0)
        {
            return;
        }

        await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_TagCollectionRule"
                SET "IsDeleted" = TRUE, "ModifyTime" = @Now
                WHERE "PlcProjectId" = @PlcProjectId AND "VariablePath" = @VariablePath AND "IsDeleted" = FALSE
                """,
                new SugarParameter[]
                {
                    new("@PlcProjectId", plcProjectId, global::System.Data.DbType.Int64),
                    new("@VariablePath", variablePath.Trim()),
                    new("@Now", DateTime.UtcNow)
                })
            .WaitAsync(cancellationToken);
    }
}
