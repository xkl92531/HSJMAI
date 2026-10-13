#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpMonitorNavigationRepository(ISqlSugarClient db)
    : ILineUpMonitorNavigationRepository, IAutoInject
{
    public async Task<IReadOnlyList<LineUpMonitorNavigationRow>> LoadAsync(
        bool includeUnconfigured,
        string? keyword,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedKeyword = string.IsNullOrWhiteSpace(keyword) ? string.Empty : keyword.Trim();
        var parameters = new SugarParameter[]
        {
            new("@IncludeUnconfigured", includeUnconfigured, global::System.Data.DbType.Boolean),
            new("@Keyword", normalizedKeyword, global::System.Data.DbType.String)
        };
        var rows = await db.Ado.SqlQueryAsync<NavigationRow>(
            """
            SELECT l."Id" AS "LineId",
                   COALESCE(l."Code", '') AS "LineCode",
                   COALESCE(l."Name", '') AS "LineName",
                   p."Id" AS "ProcessId",
                   COALESCE(p."Code", '') AS "ProcessCode",
                   COALESCE(p."Name", '') AS "ProcessName",
                   w."Id" AS "StationId",
                   COALESCE(w."Code", '') AS "StationCode",
                   COALESCE(w."Name", '') AS "StationName",
                   s."Id" AS "SubStationId",
                   COALESCE(s."SubStationCode", '') AS "SubStationCode",
                   COALESCE(s."SubStationName", '') AS "SubStationName",
                   COALESCE(s."SortNum", 0) AS "SubStationSortOrder",
                   b."Id" AS "BindingId",
                   b."PublishedRevisionId",
                   b."BindingStatus",
                   ga."SourceBlockName" AS "GraphSourceName",
                   COALESCE(b."MonitoringEnabled", FALSE) AS "MonitoringEnabled",
                   CASE
                       WHEN b."Id" IS NULL THEN 0
                       WHEN b."BindingStatus" IN ('Unresolved', 'NeedsReview', 'Resolved') THEN 0
                       ELSE 1
                   END AS "BindingIssueCount"
            FROM "Basic_SubStation" s
            INNER JOIN "Basic_WorkStation" w
              ON w."Id" = s."WorkStationID"
             AND w."ProcessId" = s."ProcessID"
             AND w."ProductionLineId" = s."ProductionLineID"
             AND w."IsDeleted" = FALSE
            INNER JOIN "Basic_Process" p
              ON p."Id" = s."ProcessID"
             AND p."ProductionLineId" = s."ProductionLineID"
             AND p."IsDeleted" = FALSE
            INNER JOIN "Basic_ProductionLine" l
              ON l."Id" = s."ProductionLineID"
             AND l."IsDeleted" = FALSE
            LEFT JOIN "AI_LineUp_GraphStationBinding" b
              ON b."SubStationId" = s."Id"
             AND b."PublishedRevisionId" IS NOT NULL
             AND b."IsDeleted" = FALSE
            LEFT JOIN "AI_LineUp_GraphAsset" ga
              ON ga."Id" = b."GraphAssetId"
             AND ga."IsDeleted" = FALSE
            WHERE s."IsDeleted" = FALSE
              AND (@IncludeUnconfigured = TRUE OR b."Id" IS NOT NULL)
              AND (@Keyword = '' OR
                   COALESCE(l."Code", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(l."Name", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(p."Code", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(p."Name", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(w."Code", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(w."Name", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(s."SubStationCode", '') ILIKE '%' || @Keyword || '%' OR
                   COALESCE(s."SubStationName", '') ILIKE '%' || @Keyword || '%')
            ORDER BY l."Code", l."Id", p."Code", p."Id", w."Code", w."Id",
                     s."SortNum", s."SubStationCode", s."Id", b."Id"
            """,
            (object)parameters,
            cancellationToken);
        return rows.Select(row => new LineUpMonitorNavigationRow(
                row.LineId,
                row.LineCode ?? string.Empty,
                row.LineName ?? string.Empty,
                row.ProcessId,
                row.ProcessCode ?? string.Empty,
                row.ProcessName ?? string.Empty,
                row.StationId,
                row.StationCode ?? string.Empty,
                row.StationName ?? string.Empty,
                row.SubStationId,
                row.SubStationCode ?? string.Empty,
                row.SubStationName ?? string.Empty,
                row.SubStationSortOrder,
                row.BindingId,
                row.PublishedRevisionId,
                row.BindingStatus,
                row.GraphSourceName,
                row.MonitoringEnabled,
                Math.Max(0, row.BindingIssueCount)))
            .ToArray();
    }

    private sealed class NavigationRow
    {
        public long LineId { get; set; }
        public string? LineCode { get; set; }
        public string? LineName { get; set; }
        public long ProcessId { get; set; }
        public string? ProcessCode { get; set; }
        public string? ProcessName { get; set; }
        public long StationId { get; set; }
        public string? StationCode { get; set; }
        public string? StationName { get; set; }
        public long SubStationId { get; set; }
        public string? SubStationCode { get; set; }
        public string? SubStationName { get; set; }
        public int SubStationSortOrder { get; set; }
        public long? BindingId { get; set; }
        public long? PublishedRevisionId { get; set; }
        public string? BindingStatus { get; set; }
        public string? GraphSourceName { get; set; }
        public bool MonitoringEnabled { get; set; }
        public int BindingIssueCount { get; set; }
    }
}
