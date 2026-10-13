#nullable enable

using HSJM.AI.Core;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpExistingMetadataAuditRepository(ISqlSugarClient db)
    : ILineUpExistingMetadataAuditRepository, IAutoInject
{
    public async Task<LineUpExistingGraphMetadataAudit> LoadGraphMetadataAsync(
        long plcProjectId,
        CancellationToken cancellationToken)
    {
        if (plcProjectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plcProjectId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var projects = await db.Ado.SqlQueryAsync<LineUpExistingPlcProject>(
                """
                SELECT id AS "PlcProjectId",
                       projectname AS "ProjectName",
                       stationid AS "StationCode"
                FROM ai_plc_project_new
                WHERE id = @ProjectId AND isdeleted = FALSE
                """,
                new SugarParameter("@ProjectId", plcProjectId))
            .WaitAsync(cancellationToken);
        if (projects.Count == 0)
        {
            return Empty();
        }

        if (projects.Count != 1)
        {
            throw new InvalidOperationException("The PLC project identity is not unique.");
        }

        var project = projects[0];
        var blocks = await db.Ado.SqlQueryAsync<LineUpExistingGraphBlock>(
                """
                SELECT id AS "PlcBlockId",
                       projectid AS "PlcProjectId",
                       blockname AS "SourceBlockName",
                       blocktype AS "BlockType",
                       blocknumber AS "BlockNumber",
                       programminglanguage AS "ProgrammingLanguage",
                       stationcode AS "StationCode"
                FROM ai_plc_block_new
                WHERE projectid = @ProjectId
                  AND isdeleted = FALSE
                  AND programminglanguage = 'GRAPH'
                ORDER BY blocknumber, blockname, id
                """,
                new SugarParameter("@ProjectId", plcProjectId))
            .WaitAsync(cancellationToken);

        var blockIds = blocks.Select(row => row.PlcBlockId).ToArray();
        var interfaces = blockIds.Length == 0
            ? []
            : await db.Ado.SqlQueryAsync<LineUpExistingBlockInterface>(
                    """
                    SELECT id AS "InterfaceId",
                           blockid AS "PlcBlockId",
                           parentid AS "ParentInterfaceId",
                           variablename AS "VariableName",
                           variablepath AS "VariablePath",
                           datatype AS "DataType",
                           scope AS "Scope",
                           offsetbyte AS "OffsetByte",
                           offsetbit AS "OffsetBit",
                           sizebits AS "SizeBits",
                           "offset" AS "Offset"
                    FROM ai_plc_blockinterface
                    WHERE blockid IN (@BlockIds) AND isdeleted = FALSE
                    ORDER BY blockid, nestinglevel, variablepath, id
                    """,
                    new SugarParameter("@BlockIds", blockIds))
                .WaitAsync(cancellationToken);

        var devices = string.IsNullOrWhiteSpace(project.ProjectName)
            ? []
            : await db.Ado.SqlQueryAsync<LineUpExistingDeviceEvidence>(
                    """
                    SELECT "Id" AS "DeviceId",
                           "DeviceName",
                           "ProjectName",
                           "LineID" AS "ProductionLineId",
                           "DicCode" AS "DeviceCode",
                           "Ss7DeviceName" AS "S7DeviceName"
                    FROM "Basic_Device"
                    WHERE "IsDeleted" = FALSE AND "ProjectName" = @ProjectName
                    ORDER BY "Id"
                    """,
                    new SugarParameter("@ProjectName", project.ProjectName))
                .WaitAsync(cancellationToken);

        var stationCodes = blocks
            .Select(row => row.StationCode)
            .Append(project.StationCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stations = stationCodes.Length == 0
            ? []
            : await db.Ado.SqlQueryAsync<LineUpExistingStationEvidence>(
                    """
                    SELECT "Id" AS "WorkStationId",
                           "ProcessId",
                           "ProductionLineId",
                           "Code",
                           "Name"
                    FROM "Basic_WorkStation"
                    WHERE "IsDeleted" = FALSE
                      AND ("IsEnable" IS NULL OR "IsEnable" = TRUE)
                      AND "Code" IN (@StationCodes)
                    ORDER BY "Id"
                    """,
                    new SugarParameter("@StationCodes", stationCodes))
                .WaitAsync(cancellationToken);

        var processIds = stations.Select(row => row.ProcessId).Distinct().ToArray();
        var processes = processIds.Length == 0
            ? []
            : await db.Ado.SqlQueryAsync<LineUpExistingProcessEvidence>(
                    """
                    SELECT "Id" AS "ProcessId",
                           "ProductionLineId",
                           "Code",
                           "Name"
                    FROM "Basic_Process"
                    WHERE "IsDeleted" = FALSE
                      AND ("IsEnable" IS NULL OR "IsEnable" = TRUE)
                      AND "Id" IN (@ProcessIds)
                    ORDER BY "Id"
                    """,
                    new SugarParameter("@ProcessIds", processIds))
                .WaitAsync(cancellationToken);

        var lineIds = devices
            .Where(row => row.ProductionLineId.HasValue)
            .Select(row => row.ProductionLineId!.Value)
            .Concat(stations.Select(row => row.ProductionLineId))
            .Concat(processes.Select(row => row.ProductionLineId))
            .Distinct()
            .ToArray();
        var lines = lineIds.Length == 0
            ? []
            : await db.Ado.SqlQueryAsync<LineUpExistingLineEvidence>(
                    """
                    SELECT "Id" AS "ProductionLineId", "Code", "Name"
                    FROM "Basic_ProductionLine"
                    WHERE "IsDeleted" = FALSE
                      AND ("IsEnable" IS NULL OR "IsEnable" = TRUE)
                      AND "Id" IN (@LineIds)
                    ORDER BY "Id"
                    """,
                    new SugarParameter("@LineIds", lineIds))
                .WaitAsync(cancellationToken);

        return new(project, blocks, interfaces, devices, stations, processes, lines, stationCodes);
    }

    private static LineUpExistingGraphMetadataAudit Empty() => new(
        null,
        [],
        [],
        [],
        [],
        [],
        [],
        []);
}
