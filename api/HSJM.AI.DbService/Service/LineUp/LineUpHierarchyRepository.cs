using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using NpgsqlTypes;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpHierarchyRepository(ISqlSugarClient db)
    : ILineUpHierarchyRepository, IAutoInject
{
    public async Task<LineUpHierarchyLookupContext> LoadContextAsync(
        long lineId,
        long deviceId,
        IReadOnlyCollection<string> sequenceSourceKeys,
        CancellationToken cancellationToken,
        string? graphBlockName = null)
    {
        var sourceKeys = sequenceSourceKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // SqlSugar 对 record 投影的 DataReader 绑定不稳定（无参构造缺失 → ArgumentNullException），
        // 全部改手写 SQL + class 行绑定；保留表（Basic_* / AI_LineUp_*）列名为大小写保留需带引号
        var lineRows = await db.Ado.SqlQueryAsync<LineRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_ProductionLine"
                WHERE "IsDeleted" = FALSE AND "Id" = @Id AND ("IsEnable" IS NULL OR "IsEnable" = TRUE)
                """,
                new SugarParameter("@Id", lineId))
            .WaitAsync(cancellationToken);
        var lines = lineRows.Select(x => new LineUpLineLookup(x.Id, x.Code, x.Name)).ToArray();

        var deviceRows = await db.Ado.SqlQueryAsync<DeviceRow>(
                """
                SELECT "Id", "LineID" AS "LineId", "DeviceName", "ProjectName"
                FROM "Basic_Device"
                WHERE "IsDeleted" = FALSE AND "Id" = @Id
                """,
                new SugarParameter("@Id", deviceId))
            .WaitAsync(cancellationToken);
        var devices = deviceRows
            .Select(x => new LineUpDeviceLookup(x.Id, x.LineId, x.DeviceName, x.ProjectName))
            .ToArray();

        var processRows = await db.Ado.SqlQueryAsync<ProcessRow>(
                """
                SELECT "Id", "ProductionLineId" AS "LineId", "Code", "Name"
                FROM "Basic_Process"
                WHERE "IsDeleted" = FALSE AND "ProductionLineId" = @LineId
                  AND ("IsEnable" IS NULL OR "IsEnable" = TRUE)
                """,
                new SugarParameter("@LineId", lineId))
            .WaitAsync(cancellationToken);
        var processes = processRows
            .Select(x => new LineUpProcessLookup(x.Id, x.LineId, x.Code, x.Name))
            .ToArray();

        var stationRows = await db.Ado.SqlQueryAsync<StationRow>(
                """
                SELECT "Id", "ProductionLineId" AS "LineId", "ProcessId", "Code", "Name"
                FROM "Basic_WorkStation"
                WHERE "IsDeleted" = FALSE AND "ProductionLineId" = @LineId
                  AND ("IsEnable" IS NULL OR "IsEnable" = TRUE)
                """,
                new SugarParameter("@LineId", lineId))
            .WaitAsync(cancellationToken);
        var stations = stationRows
            .Select(x => new LineUpStationLookup(x.Id, x.LineId, x.ProcessId, x.Code, x.Name))
            .ToArray();

        var subStationRows = await db.Ado.SqlQueryAsync<SubStationRow>(
                """
                SELECT "Id", "ProductionLineID" AS "LineId", "ProcessID" AS "ProcessId",
                       "WorkStationID" AS "StationId", "PlcID" AS "PlcId",
                       "SubStationCode" AS "Code", "SubStationName" AS "Name", "SortNum"
                FROM "Basic_SubStation"
                WHERE "IsDeleted" = FALSE AND "ProductionLineID" = @LineId AND "PlcID" = @DeviceId
                """,
                new SugarParameter("@LineId", lineId),
                new SugarParameter("@DeviceId", deviceId))
            .WaitAsync(cancellationToken);
        var substations = subStationRows
            .Select(x => new LineUpSubStationLookup(
                x.Id, x.LineId, x.ProcessId, x.StationId, x.PlcId, x.Code, x.Name, x.SortNum))
            .ToArray();

        IReadOnlyList<LineUpExistingBindingLookup> bindings = [];
        if (sourceKeys.Length > 0)
        {
            var keyParams = sourceKeys
                .Select((value, index) => new SugarParameter($"@sk{index}", value))
                .ToArray();
            var keyList = string.Join(", ", keyParams.Select(p => p.ParameterName));
            var bindingRows = await db.Ado.SqlQueryAsync<BindingRow>(
                    $"""
                     SELECT s."SourceKey" AS "SequenceSourceKey",
                            s."PlcProjectId" AS "SequencePlcProjectId",
                            b."PlcProjectId" AS "BindingPlcProjectId",
                            b."ProcessId", b."StationId", b."SubStationId", b."BindingStatus"
                     FROM "AI_LineUp_SubStationGraphBinding" b
                     INNER JOIN "AI_LineUp_GraphSequence" s ON b."SequenceId" = s."Id"
                     WHERE b."IsDeleted" = FALSE AND s."IsDeleted" = FALSE
                       AND b."LineId" = @LineId AND b."DeviceId" = @DeviceId
                       AND s."SourceKey" IN ({keyList})
                     """,
                    keyParams.Concat(
                    [
                        new SugarParameter("@LineId", lineId),
                        new SugarParameter("@DeviceId", deviceId)
                    ]).ToArray())
                .WaitAsync(cancellationToken);
            bindings = bindingRows
                .Select(x => new LineUpExistingBindingLookup(
                    x.SequenceSourceKey, x.SequencePlcProjectId, x.BindingPlcProjectId,
                    x.ProcessId, x.StationId, x.SubStationId, x.BindingStatus))
                .ToArray();
        }

        // GRAPH 块的工站绑定（ai_plc_stationblockbinding）：块级人工绑定证据，
        // 供层级解析器把该块的顺控直接归属到绑定工站（「批量绑定工站」弹框的人工操作）。
        var blockBindings = Array.Empty<LineUpBlockStationBindingLookup>();
        if (!string.IsNullOrWhiteSpace(graphBlockName))
        {
            var blockBindingRows = await db.Ado.SqlQueryAsync<BlockStationBindingRow>(
                    """
                    SELECT "projectid" AS "ProjectId", "stationcode" AS "StationCode", "blockname" AS "BlockName"
                    FROM ai_plc_stationblockbinding
                    WHERE "isdeleted" = FALSE AND "blockname" = @BlockName
                    """,
                    new SugarParameter("@BlockName", graphBlockName))
                .WaitAsync(cancellationToken);
            blockBindings = blockBindingRows
                .Select(x => new LineUpBlockStationBindingLookup(x.ProjectId, x.StationCode, x.BlockName))
                .ToArray();
        }

        return new(
            lines.Length == 1 ? lines[0] : null,
            devices.Length == 1 ? devices[0] : null,
            processes,
            stations,
            substations,
            bindings,
            blockBindings);
    }

    public async Task<long> EnsureProcessAsync(
        LineUpLineLookup line,
        string code,
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var matches = await db.Ado.SqlQueryAsync<LineRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_Process"
                WHERE "IsDeleted" = FALSE AND "ProductionLineId" = @LineId
                  AND "Code" IS NOT NULL AND LOWER(TRIM("Code")) = @Code
                """,
                new SugarParameter("@LineId", line.Id),
                new SugarParameter("@Code", normalized))
            .WaitAsync(cancellationToken);
        if (matches.Count > 1)
        {
            return -1;
        }
        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var inserted = await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "Basic_Process"
                    ("Id", "Code", "Name", "ProductionLineId", "ProductionLineCode", "ProductionLineName",
                     "IsEnable", "IsDeleted", "CreateTime")
                VALUES (@Id, @Code, @Name, @LineId, @LineCode, @LineName, TRUE, FALSE, @Now)
                """,
                new SugarParameter("@Id", id),
                new SugarParameter("@Code", code.Trim()),
                new SugarParameter("@Name", string.IsNullOrWhiteSpace(name) ? code.Trim() : name.Trim()),
                new SugarParameter("@LineId", line.Id),
                new SugarParameter("@LineCode", (object?)line.Code ?? DBNull.Value),
                new SugarParameter("@LineName", (object?)line.Name ?? DBNull.Value),
                new SugarParameter("@Now", DateTime.Now))
            .WaitAsync(cancellationToken);
        return inserted == 1 ? id : -1;
    }

    public async Task<long> EnsureWorkStationAsync(
        LineUpLineLookup line,
        LineUpProcessLookup process,
        string code,
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var matches = await db.Ado.SqlQueryAsync<LineRow>(
                """
                SELECT "Id", "Code", "Name"
                FROM "Basic_WorkStation"
                WHERE "IsDeleted" = FALSE AND "ProductionLineId" = @LineId AND "ProcessId" = @ProcessId
                  AND "Code" IS NOT NULL AND LOWER(TRIM("Code")) = @Code
                """,
                new SugarParameter("@LineId", line.Id),
                new SugarParameter("@ProcessId", process.Id),
                new SugarParameter("@Code", normalized))
            .WaitAsync(cancellationToken);
        if (matches.Count > 1)
        {
            return -1;
        }
        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var inserted = await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "Basic_WorkStation"
                    ("Id", "Code", "Name", "ProcessId", "ProcessCode", "ProcessName",
                     "ProductionLineId", "ProductionLineCode", "ProductionLineName",
                     "IsEnable", "IsDeleted", "CreateTime")
                VALUES (@Id, @Code, @Name, @ProcessId, @ProcessCode, @ProcessName,
                        @LineId, @LineCode, @LineName, TRUE, FALSE, @Now)
                """,
                new SugarParameter("@Id", id),
                new SugarParameter("@Code", code.Trim()),
                new SugarParameter("@Name", string.IsNullOrWhiteSpace(name) ? code.Trim() : name.Trim()),
                new SugarParameter("@ProcessId", process.Id),
                new SugarParameter("@ProcessCode", (object?)process.Code ?? DBNull.Value),
                new SugarParameter("@ProcessName", (object?)process.Name ?? DBNull.Value),
                new SugarParameter("@LineId", line.Id),
                new SugarParameter("@LineCode", (object?)line.Code ?? DBNull.Value),
                new SugarParameter("@LineName", (object?)line.Name ?? DBNull.Value),
                new SugarParameter("@Now", DateTime.Now))
            .WaitAsync(cancellationToken);
        return inserted == 1 ? id : -1;
    }

    public async Task<long> EnsureSubStationAsync(
        LineUpLineLookup line,
        LineUpProcessLookup process,
        LineUpStationLookup station,
        LineUpDeviceLookup device,
        string code,
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var matches = await db.Ado.SqlQueryAsync<LineRow>(
                """
                SELECT "Id", "SubStationCode" AS "Code", "SubStationName" AS "Name"
                FROM "Basic_SubStation"
                WHERE "IsDeleted" = FALSE AND "ProductionLineID" = @LineId AND "WorkStationID" = @StationId
                  AND "PlcID" = @DeviceId AND "SubStationCode" IS NOT NULL
                  AND LOWER(TRIM("SubStationCode")) = @Code
                """,
                new SugarParameter("@LineId", line.Id),
                new SugarParameter("@StationId", station.Id),
                new SugarParameter("@DeviceId", device.Id),
                new SugarParameter("@Code", normalized))
            .WaitAsync(cancellationToken);
        if (matches.Count > 1)
        {
            return -1;
        }
        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var inserted = await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "Basic_SubStation"
                    ("Id", "ProcessID", "ProcessCode", "ProcessName", "WorkStationID", "WorkStationCode",
                     "WorkStationName", "ProductionLineID", "ProductionLineCode", "ProductionLineName",
                     "PlcID", "PlcName", "SubStationCode", "SubStationName", "SubStationMode", "SortNum",
                     "IsDeleted", "CreateTime")
                VALUES (@Id, @ProcessID, @ProcessCode, @ProcessName, @StationID, @StationCode,
                        @StationName, @LineID, @LineCode, @LineName,
                        @PlcID, @PlcName, @SubStationCode, @SubStationName, 'GRAPH', 0,
                        FALSE, @Now)
                """,
                new SugarParameter("@Id", id),
                new SugarParameter("@ProcessID", process.Id),
                new SugarParameter("@ProcessCode", (object?)process.Code ?? DBNull.Value),
                new SugarParameter("@ProcessName", (object?)process.Name ?? DBNull.Value),
                new SugarParameter("@StationID", station.Id),
                new SugarParameter("@StationCode", (object?)station.Code ?? DBNull.Value),
                new SugarParameter("@StationName", (object?)station.Name ?? DBNull.Value),
                new SugarParameter("@LineID", line.Id),
                new SugarParameter("@LineCode", (object?)line.Code ?? DBNull.Value),
                new SugarParameter("@LineName", (object?)line.Name ?? DBNull.Value),
                new SugarParameter("@PlcID", device.Id),
                new SugarParameter("@PlcName", (object?)device.DeviceName ?? DBNull.Value),
                new SugarParameter("@SubStationCode", code.Trim()),
                new SugarParameter("@SubStationName", string.IsNullOrWhiteSpace(name) ? code.Trim() : name.Trim()),
                new SugarParameter("@Now", DateTime.Now))
            .WaitAsync(cancellationToken);
        return inserted == 1 ? id : -1;
    }

    public async Task<long> EnsureGraphSequenceAsync(
        string sourceKey,
        string sourceName,
        string displayName,
        long plcProjectId,
        long graphBlockId,
        long? instanceDbBlockId,
        string? sourceHash,
        CancellationToken cancellationToken)
    {
        var key = sourceKey.Trim();
        var existing = await db.Ado.SqlQueryAsync<long>(
                """
                SELECT "Id" FROM "AI_LineUp_GraphSequence"
                WHERE "IsDeleted" = FALSE AND "SourceKey" = @SourceKey
                ORDER BY "Id" LIMIT 2
                """,
                new SugarParameter("@SourceKey", key))
            .WaitAsync(cancellationToken);
        if (existing.Count > 1)
        {
            return -1;
        }
        if (existing.Count == 1)
        {
            return existing[0];
        }

        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var inserted = await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "AI_LineUp_GraphSequence"
                    ("Id", "SourceKey", "SourceName", "DisplayName", "PlcProjectId", "GraphBlockId",
                     "InstanceDbBlockId", "SourceHash", "IsDeleted", "CreateTime")
                VALUES (@Id, @SourceKey, @SourceName, @DisplayName, @PlcProjectId, @GraphBlockId,
                        @InstanceDbBlockId, @SourceHash, FALSE, @Now)
                """,
                new SugarParameter("@Id", id),
                new SugarParameter("@SourceKey", key),
                new SugarParameter("@SourceName", (object?)sourceName?.Trim() ?? DBNull.Value),
                new SugarParameter("@DisplayName", (object?)displayName?.Trim() ?? DBNull.Value),
                new SugarParameter("@PlcProjectId", plcProjectId),
                new SugarParameter("@GraphBlockId", graphBlockId),
                new SugarParameter("@InstanceDbBlockId", (object?)instanceDbBlockId ?? DBNull.Value),
                new SugarParameter("@SourceHash", (object?)sourceHash ?? DBNull.Value),
                new SugarParameter("@Now", DateTime.Now))
            .WaitAsync(cancellationToken);
        return inserted == 1 ? id : -1;
    }

    public async Task<long> InsertSubStationBindingAsync(
        long lineId,
        long processId,
        long stationId,
        long subStationId,
        long deviceId,
        long plcProjectId,
        long sequenceId,
        long graphBlockId,
        long? instanceDbBlockId,
        CancellationToken cancellationToken)
    {
        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var inserted = await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "AI_LineUp_SubStationGraphBinding"
                    ("Id", "LineId", "ProcessId", "StationId", "SubStationId", "DeviceId",
                     "PlcProjectId", "SequenceId", "GraphBlockId", "InstanceDbBlockId",
                     "BindingStatus", "IsDeleted", "CreateTime")
                VALUES (@Id, @LineId, @ProcessId, @StationId, @SubStationId, @DeviceId,
                        @PlcProjectId, @SequenceId, @GraphBlockId, @InstanceDbBlockId,
                        'Unresolved', FALSE, @Now)
                """,
                new SugarParameter("@Id", id),
                new SugarParameter("@LineId", lineId),
                new SugarParameter("@ProcessId", processId),
                new SugarParameter("@StationId", stationId),
                new SugarParameter("@SubStationId", subStationId > 0 ? (object)subStationId : DBNull.Value)
                {
                    CustomDbType = NpgsqlDbType.Bigint,
                },
                new SugarParameter("@DeviceId", deviceId),
                new SugarParameter("@PlcProjectId", plcProjectId),
                new SugarParameter("@SequenceId", sequenceId),
                new SugarParameter("@GraphBlockId", graphBlockId),
                new SugarParameter("@InstanceDbBlockId", (object?)instanceDbBlockId ?? DBNull.Value)
                {
                    CustomDbType = NpgsqlDbType.Bigint,
                },
                new SugarParameter("@Now", DateTime.Now))
            .WaitAsync(cancellationToken);
        return inserted == 1 ? id : -1;
    }

    private sealed class LineRow
    {
        public long Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private sealed class DeviceRow
    {
        public long Id { get; set; }
        public long? LineId { get; set; }
        public string? DeviceName { get; set; }
        public string? ProjectName { get; set; }
    }

    private sealed class ProcessRow
    {
        public long Id { get; set; }
        public long LineId { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private sealed class StationRow
    {
        public long Id { get; set; }
        public long LineId { get; set; }
        public long ProcessId { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private sealed class SubStationRow
    {
        public long Id { get; set; }
        public long LineId { get; set; }
        public long ProcessId { get; set; }
        public long StationId { get; set; }
        public long PlcId { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public int SortNum { get; set; }
    }

    private sealed class BindingRow
    {
        public string SequenceSourceKey { get; set; } = string.Empty;
        public long SequencePlcProjectId { get; set; }
        public long BindingPlcProjectId { get; set; }
        public long? ProcessId { get; set; }
        public long? StationId { get; set; }
        public long? SubStationId { get; set; }
        public string? BindingStatus { get; set; }
    }

    private sealed class BlockStationBindingRow
    {
        public long? ProjectId { get; set; }
        public string? StationCode { get; set; }
        public string? BlockName { get; set; }
    }
}
