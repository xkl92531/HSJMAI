using HSJM.AI.Core;
using HSJM.AI.IDbService;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

/// <summary>
/// Agent 只读查询仓储实现。全部手写 SQL：
/// 引号列名（Basic_*/AI_LineUp_*）下 SqlSugar Queryable/Updateable 生成非法 SQL（42601），
/// 且 record 投影 DataReader 绑定不可靠 —— 与 LineUpImportRepository 同一已验证路径。
/// </summary>
public sealed class LineUpAgentQueryRepository(ISqlSugarClient db)
    : ILineUpAgentQueryRepository, IAutoInject
{
    public async Task<List<LineUpAgentSubStationRow>> FindSubStationsAsync(
        string workStationCode,
        string subStationCode,
        long? lineId,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT s."Id" AS "SubStationId", s."SubStationCode" AS "SubStationCode", s."SubStationName" AS "SubStationName",
                   w."Id" AS "WorkStationId", w."Code" AS "WorkStationCode", w."Name" AS "WorkStationName",
                   s."ProductionLineID" AS "ProductionLineId",
                   l."Code" AS "ProductionLineCode", l."Name" AS "ProductionLineName",
                   COALESCE(s."PlcID", 0) AS "PlcId"
            FROM "Basic_SubStation" s
            JOIN "Basic_WorkStation" w ON w."Id" = s."WorkStationID" AND w."IsDeleted" = FALSE
            LEFT JOIN "Basic_ProductionLine" l ON l."Id" = s."ProductionLineID" AND l."IsDeleted" = FALSE
            WHERE s."IsDeleted" = FALSE
              AND LOWER(TRIM(w."Code")) = @WorkStationCode
              AND LOWER(TRIM(s."SubStationCode")) = @SubStationCode
            """;
        var parameters = new List<SugarParameter>
        {
            new("@WorkStationCode", Normalize(workStationCode)),
            new("@SubStationCode", Normalize(subStationCode)),
        };
        if (lineId is > 0)
        {
            sql += @" AND s.""ProductionLineID"" = @LineId";
            parameters.Add(new("@LineId", lineId.Value));
        }
        sql += @" ORDER BY s.""ProductionLineID"", s.""Id""";
        return await db.Ado.SqlQueryAsync<LineUpAgentSubStationRow>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentLineRow>> FindLinesAsync(
        string lineKey,
        CancellationToken cancellationToken)
    {
        return await db.Ado.SqlQueryAsync<LineUpAgentLineRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_ProductionLine"
                WHERE "IsDeleted" = FALSE
                  AND (LOWER(TRIM("Code")) = @Key OR LOWER(TRIM("Name")) = @Key)
                ORDER BY "Id"
                """,
                new SugarParameter("@Key", Normalize(lineKey)))
            .WaitAsync(cancellationToken);
    }

    public async Task<LineUpAgentLineRow?> FindLineByIdAsync(
        long lineId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Ado.SqlQueryAsync<LineUpAgentLineRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_ProductionLine"
                WHERE "IsDeleted" = FALSE AND "Id" = @Id
                """,
                new SugarParameter("@Id", lineId))
            .WaitAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    public async Task<List<LineUpAgentWorkStationRow>> FindWorkStationsAsync(
        string workStationCode,
        long? lineId,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT w."Id", w."Code", w."Name", COALESCE(w."ProductionLineId", 0) AS "ProductionLineId",
                   l."Code" AS "ProductionLineCode", l."Name" AS "ProductionLineName"
            FROM "Basic_WorkStation" w
            LEFT JOIN "Basic_ProductionLine" l ON l."Id" = w."ProductionLineId" AND l."IsDeleted" = FALSE
            WHERE w."IsDeleted" = FALSE AND LOWER(TRIM(w."Code")) = @Code
            """;
        var parameters = new List<SugarParameter> { new("@Code", Normalize(workStationCode)) };
        if (lineId is > 0)
        {
            sql += @" AND w.""ProductionLineId"" = @LineId";
            parameters.Add(new("@LineId", lineId.Value));
        }
        sql += @" ORDER BY w.""Id""";
        return await db.Ado.SqlQueryAsync<LineUpAgentWorkStationRow>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentSubStationBriefRow>> ListSubStationsByWorkStationAsync(
        long workStationId,
        CancellationToken cancellationToken)
    {
        return await db.Ado.SqlQueryAsync<LineUpAgentSubStationBriefRow>(
                """
                SELECT "Id" AS "SubStationId", "SubStationCode" AS "SubStationCode", COALESCE("PlcID", 0) AS "PlcId"
                FROM "Basic_SubStation"
                WHERE "IsDeleted" = FALSE AND "WorkStationID" = @WorkStationId
                ORDER BY "SortNum", "Id"
                """,
                new SugarParameter("@WorkStationId", workStationId))
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentDeviceRow>> ListDevicesAsync(
        IReadOnlyCollection<long> deviceIds,
        CancellationToken cancellationToken)
    {
        var ids = deviceIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        var (sql, parameters) = InBigint(@"SELECT ""Id"", ""DeviceName"", ""ProjectName"" FROM ""Basic_Device"" WHERE ""IsDeleted"" = FALSE", "Id", ids);
        return await db.Ado.SqlQueryAsync<LineUpAgentDeviceRow>(sql, parameters)
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentProjectRow>> FindProjectsByNameAsync(
        IReadOnlyCollection<string> projectNames,
        CancellationToken cancellationToken)
    {
        var names = projectNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
        {
            return [];
        }
        var parameters = new List<SugarParameter>();
        var placeholders = new List<string>();
        for (var i = 0; i < names.Length; i++)
        {
            placeholders.Add($"@pn{i}");
            parameters.Add(new($"@pn{i}", names[i]));
        }
        var inList = string.Join(", ", placeholders);
        var sql = $"""
            SELECT id AS "Id", projectname AS "ProjectName"
            FROM ai_plc_project_new
            WHERE isdeleted = FALSE AND LOWER(TRIM(projectname)) IN ({inList})
            """;
        return await db.Ado.SqlQueryAsync<LineUpAgentProjectRow>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentProjectRow>> FindProjectsByIdAsync(
        IReadOnlyCollection<long> projectIds,
        CancellationToken cancellationToken)
    {
        var ids = projectIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        var (inList, idParams) = BuildInList("pv", ids);
        var sql = $"""
            SELECT id AS "Id", projectname AS "ProjectName"
            FROM ai_plc_project_new
            WHERE isdeleted = FALSE AND id IN ({inList})
            """;
        return await db.Ado.SqlQueryAsync<LineUpAgentProjectRow>(sql, idParams)
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentBindingRow>> ListBindingsBySubStationsAsync(
        IReadOnlyCollection<long> subStationIds,
        CancellationToken cancellationToken)
    {
        var ids = subStationIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        var (sql, parameters) = InBigint(
            @"SELECT ""Id"", ""LineId"", COALESCE(""SubStationId"", 0) AS ""SubStationId"", ""DeviceId"",
                      ""PlcProjectId"", ""SequenceId"", ""BindingStatus""
               FROM ""AI_LineUp_SubStationGraphBinding"" WHERE ""IsDeleted"" = FALSE", "SubStationId", ids);
        sql += @" ORDER BY ""Id"" DESC";
        return await db.Ado.SqlQueryAsync<LineUpAgentBindingRow>(sql, parameters)
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentStepRow>> FindStepsAsync(
        IReadOnlyCollection<long> sequenceIds,
        int stepNumber,
        CancellationToken cancellationToken)
    {
        var ids = sequenceIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        var sql = BuildStepSql(ids, out var parameters);
        parameters.Add(new("@StepNumber", stepNumber));
        return await db.Ado.SqlQueryAsync<LineUpAgentStepRow>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentNodeBomRow>> ListNodeBomsAsync(
        long subStationId,
        IReadOnlyCollection<string> nodeSourceKeys,
        CancellationToken cancellationToken)
    {
        var keys = nodeSourceKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (subStationId <= 0 || keys.Length == 0)
        {
            return [];
        }
        var parameters = new List<SugarParameter> { new("@SubStationId", subStationId) };
        var placeholders = new List<string>();
        for (var i = 0; i < keys.Length; i++)
        {
            placeholders.Add($"@nk{i}");
            parameters.Add(new($"@nk{i}", keys[i]));
        }
        var sql = $"""
            SELECT "Id", "BatchId", "LineId", "BlockName", "SequenceSourceKey", "NodeSourceKey", "NodeKind",
                   "SubStationId", "BomId", "BomRefNo", "TagCode", "ImageId", "MarkId"
            FROM "AI_LineUp_GraphNodeBom"
            WHERE "IsDeleted" = FALSE AND "SubStationId" = @SubStationId
              AND "NodeSourceKey" IN ({string.Join(", ", placeholders)})
            ORDER BY "BatchId" DESC, "Id"
            """;
        return await db.Ado.SqlQueryAsync<LineUpAgentNodeBomRow>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    public async Task<List<LineUpAgentBomRow>> FindBomsByCodeAsync(
        IReadOnlyCollection<long> subStationIds,
        string code,
        CancellationToken cancellationToken)
    {
        var ids = subStationIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(code))
        {
            return [];
        }
        var parameters = new List<SugarParameter> { new("@Code", Normalize(code)) };
        var placeholders = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders.Add($"@ss{i}");
            parameters.Add(new($"@ss{i}", ids[i]));
        }
        var sql = $"""
            SELECT "Id", COALESCE("StationId", 0) AS "StationId", "RefNo", "TagCode"
            FROM "Lup_BomDirectory"
            WHERE "IsDeleted" = FALSE
              AND (LOWER(TRIM("RefNo")) = @Code OR LOWER(TRIM("TagCode")) = @Code)
              AND "StationId" IN ({string.Join(", ", placeholders)})
            ORDER BY "Id"
            """;
        return await db.Ado.SqlQueryAsync<LineUpAgentBomRow>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    private static string BuildStepSql(long[] ids, out List<SugarParameter> parameters)
    {
        parameters = [];
        var placeholders = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders.Add($"@sq{i}");
            parameters.Add(new($"@sq{i}", ids[i]));
        }
        return $"""
            SELECT "Id", "SequenceId", "StepNumber", "SourceKey", "DisplayName"
            FROM "AI_LineUp_GraphStep"
            WHERE "IsDeleted" = FALSE AND "StepNumber" = @StepNumber
              AND "SequenceId" IN ({string.Join(", ", placeholders)})
            ORDER BY "SequenceId", "Id"
            """;
    }

    private static (string InList, SugarParameter[] Parameters) BuildInList(
        string prefix,
        long[] ids)
    {
        var parameters = new List<SugarParameter>();
        var placeholders = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders.Add($"@{prefix}{i}");
            parameters.Add(new($"@{prefix}{i}", ids[i]));
        }
        return (string.Join(", ", placeholders), parameters.ToArray());
    }

    private static (string Sql, SugarParameter[] Parameters) InBigint(
        string prefixSql,
        string columnName,
        long[] ids)
    {
        var parameters = new List<SugarParameter>();
        var placeholders = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            placeholders.Add($"@v{i}");
            parameters.Add(new($"@v{i}", ids[i]));
        }
        var sql = $"{prefixSql} AND {columnName} IN ({string.Join(", ", placeholders)})";
        return (sql, parameters.ToArray());
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
