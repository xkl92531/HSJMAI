using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpWorkspaceRepository(ISqlSugarClient db)
    : ILineUpWorkspaceRepository, IAutoInject
{
    // 注意：兼容层映射的 Basic_* / AI_LineUp_* 表在 PostgreSQL 中以带引号的大小写保留名存在，
    // SqlSugar Queryable 会生成无引号小写标识符导致 42P01，因此统一使用带引号的原生 SQL。
    private sealed class LineRow
    {
        public long Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private sealed class DeviceRow
    {
        public long Id { get; set; }
        public long? LineID { get; set; }
        public string? DeviceName { get; set; }
        public string? ProjectName { get; set; }
        public string? DeviceType { get; set; }
        public int RegistStatus { get; set; }
    }

    private sealed class ImportRow
    {
        public long Id { get; set; }
        public long LineId { get; set; }
        public long DeviceId { get; set; }
        public string SourceFileName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ErrorSummary { get; set; }
        public int PreviewRevision { get; set; }
        public string? SummaryJson { get; set; }
        public DateTime? CreateTime { get; set; }
        public DateTime? CommittedAt { get; set; }
    }

    public async Task<LineUpWorkspaceLookupData> LoadAsync(
        long? requestedLineId,
        int recentImportLimit,
        CancellationToken cancellationToken)
    {
        if (recentImportLimit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(recentImportLimit));
        }

        var lineRows = await db.Ado.SqlQueryAsync<LineRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_ProductionLine"
                WHERE "IsDeleted" = FALSE AND "IsEnable" = TRUE
                ORDER BY "Code", "Name", "Id"
                """)
            .WaitAsync(cancellationToken);
        var orderedLines = lineRows
            .Select(x => new LineUpWorkspaceLineLookup(x.Id, x.Code, x.Name))
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .ToArray();
        var selectedLineId = requestedLineId is > 0
            ? orderedLines.Where(x => x.Id == requestedLineId.Value).Select(x => (long?)x.Id).SingleOrDefault()
            : orderedLines.Select(x => (long?)x.Id).FirstOrDefault();
        if (selectedLineId is null)
        {
            return new(orderedLines, null, [], []);
        }

        var deviceRows = await db.Ado.SqlQueryAsync<DeviceRow>(
                """
                SELECT "Id", "LineID", "DeviceName", "ProjectName", "DeviceType", "RegistStatus"
                FROM "Basic_Device"
                WHERE "IsDeleted" = FALSE AND "LineID" = @LineId
                ORDER BY "DeviceName", "Id"
                """,
                new SugarParameter("@LineId", selectedLineId.Value))
            .WaitAsync(cancellationToken);

        var importRows = await db.Ado.SqlQueryAsync<ImportRow>(
                """
                SELECT "Id", "LineId", "DeviceId", "SourceFileName", "Status", "ErrorSummary",
                       "PreviewRevision", "SummaryJson", "CreateTime", "CommittedAt"
                FROM "AI_LineUp_ImportBatch"
                WHERE "IsDeleted" = FALSE AND "LineId" = @LineId
                ORDER BY "CreateTime" DESC, "Id" DESC
                LIMIT @Limit
                """,
                new SugarParameter("@LineId", selectedLineId.Value),
                new SugarParameter("@Limit", recentImportLimit))
            .WaitAsync(cancellationToken);

        return new(
            orderedLines,
            selectedLineId,
            deviceRows.Select(x => new LineUpWorkspaceDeviceLookup(
                    x.Id, x.LineID ?? 0, x.DeviceName, x.ProjectName, x.DeviceType, x.RegistStatus))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id)
                .ToArray(),
            importRows.Select(x => new LineUpWorkspaceImportLookup(
                    x.Id, x.LineId, x.DeviceId, x.SourceFileName, x.Status, x.ErrorSummary,
                    x.PreviewRevision, x.SummaryJson, x.CreateTime, x.CommittedAt))
                .ToArray());
    }
}
