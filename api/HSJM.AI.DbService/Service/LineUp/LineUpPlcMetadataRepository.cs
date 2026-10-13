using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpPlcMetadataRepository(ISqlSugarClient db)
    : ILineUpPlcMetadataRepository, IAutoInject
{
    private readonly Func<long, IReadOnlyCollection<string>, CancellationToken,
        Task<IReadOnlyList<PLC_BlockEntity>>> _graphBlockCandidateLoader =
        (projectId, blockNames, cancellationToken) =>
            LoadGraphBlockCandidatesAsync(db, projectId, blockNames, cancellationToken);

    private readonly Func<IReadOnlyCollection<long>, CancellationToken,
        Task<IReadOnlyList<PLC_BlockInterfaceEntity>>> _graphInterfaceCandidateLoader =
        (blockIds, cancellationToken) =>
            LoadGraphInterfaceCandidatesAsync(db, blockIds, cancellationToken);

    internal LineUpPlcMetadataRepository(
        Func<long, IReadOnlyCollection<string>, CancellationToken,
            Task<IReadOnlyList<PLC_BlockEntity>>> graphBlockCandidateLoader,
        Func<IReadOnlyCollection<long>, CancellationToken,
            Task<IReadOnlyList<PLC_BlockInterfaceEntity>>> graphInterfaceCandidateLoader)
        : this(null!)
    {
        _graphBlockCandidateLoader = graphBlockCandidateLoader
            ?? throw new ArgumentNullException(nameof(graphBlockCandidateLoader));
        _graphInterfaceCandidateLoader = graphInterfaceCandidateLoader
            ?? throw new ArgumentNullException(nameof(graphInterfaceCandidateLoader));
    }

    private sealed class GraphStepLedgerRow
    {
        public int TotalCount { get; set; }
        public int? MaxStepNumber { get; set; }
        public string? ArrayDbAddress { get; set; }
    }

    public async Task<LineUpGraphStepLedgerLookup> LoadGraphStepLedgerAsync(
        long plcProjectId,
        string graphBlockName,
        CancellationToken cancellationToken)
    {
        if (plcProjectId <= 0 || string.IsNullOrWhiteSpace(graphBlockName))
        {
            return new(false, 0, 0, null);
        }

        var rows = await db.Ado.SqlQueryAsync<GraphStepLedgerRow>(
            """
            SELECT COUNT(1)::int AS "TotalCount",
                   MAX(stepnumber)::int AS "MaxStepNumber",
                   MIN(CASE WHEN dbaddress ILIKE '%.exec_bits.exec_bits[%' THEN dbaddress END) AS "ArrayDbAddress"
            FROM ai_plc_graphsteptag
            WHERE isdeleted = FALSE AND projectid = @ProjectId
              AND LOWER(TRIM(fbname)) = LOWER(TRIM(@FbName))
            """,
            new SugarParameter("@ProjectId", plcProjectId),
            new SugarParameter("@FbName", graphBlockName.Trim())).WaitAsync(cancellationToken);
        var row = rows.FirstOrDefault();
        if (row is null || row.TotalCount <= 0)
        {
            return new(false, 0, 0, null);
        }

        // 台账逐位行形如 DB160_MainGraph.RT_DATA.EXEC_BITS.EXEC_BITS[0]，去掉 [0] 即数组容器路径
        var execBitsArray = row.ArrayDbAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(execBitsArray) && execBitsArray.EndsWith("[0]", StringComparison.Ordinal))
        {
            execBitsArray = execBitsArray[..^3];
        }

        return new(
            !string.IsNullOrWhiteSpace(execBitsArray),
            row.TotalCount,
            row.MaxStepNumber ?? 0,
            string.IsNullOrWhiteSpace(execBitsArray) ? null : execBitsArray);
    }

    public async Task<LineUpGraphSymbolLookupData> LoadGraphSymbolDataAsync(
        long plcProjectId,
        IReadOnlyCollection<LineUpGraphSymbolLookupRequest> symbols,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbols = symbols
            .Where(symbol => symbol is not null &&
                             !string.IsNullOrWhiteSpace(symbol.BlockName) &&
                             !string.IsNullOrWhiteSpace(symbol.VariablePath))
            .Select(symbol => new LineUpGraphSymbolLookupRequest(
                symbol.BlockName.Trim(),
                symbol.VariablePath.Trim()))
            .Distinct()
            .OrderBy(symbol => symbol.BlockName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.VariablePath, StringComparer.Ordinal)
            .ToArray();
        if (normalizedSymbols.Length == 0)
        {
            return new([]);
        }

        var requestedBlockNames = normalizedSymbols
            .Select(symbol => symbol.BlockName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var requestedBlockNameSet = requestedBlockNames.ToHashSet(StringComparer.Ordinal);
        var rawBlocks = await _graphBlockCandidateLoader(
            plcProjectId,
            requestedBlockNames,
            cancellationToken);
        var blocks = (rawBlocks ?? [])
            .Where(block => !block.IsDeleted &&
                            block.ProjectId == plcProjectId &&
                            !string.IsNullOrWhiteSpace(block.BlockName) &&
                            requestedBlockNameSet.Contains(block.BlockName.Trim()))
            .Select(block => new LineUpBlockLookup(
                block.Id,
                block.ProjectId ?? 0,
                block.BlockName,
                block.BlockType,
                block.BlockNumber,
                block.ProgrammingLanguage))
            .OrderBy(block => block.Id)
            .ToArray();

        var blockIds = blocks
            .Where(block => block.Id > 0)
            .Select(block => block.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        IReadOnlyList<LineUpBlockInterfaceLookup> interfaces = [];
        if (blockIds.Length > 0)
        {
            var blockIdSet = blockIds.ToHashSet();
            var rawInterfaces = await _graphInterfaceCandidateLoader(blockIds, cancellationToken);
            interfaces = (rawInterfaces ?? [])
                .Where(item => !item.IsDeleted &&
                               item.BlockId is not null &&
                               blockIdSet.Contains(item.BlockId.Value) &&
                               !string.IsNullOrWhiteSpace(item.VariablePath))
                .Select(item => new LineUpBlockInterfaceLookup(
                    item.Id,
                    item.BlockId ?? 0,
                    item.VariableName,
                    item.VariablePath,
                    item.DataType,
                    item.OffsetByte,
                    item.OffsetBit,
                    item.SizeBits,
                    item.Offset))
                .OrderBy(item => item.Id)
                .ToArray();
        }

        var results = normalizedSymbols.Select(symbol =>
        {
            var matchingBlocks = blocks
                .Where(block => string.Equals(
                    block.BlockName?.Trim(),
                    symbol.BlockName,
                    StringComparison.Ordinal))
                .ToArray();
            var matchingBlockIds = matchingBlocks.Select(block => block.Id).ToHashSet();
            var matchingInterfaces = interfaces
                .Where(item => matchingBlockIds.Contains(item.BlockId) &&
                               string.Equals(
                                   item.VariablePath?.Trim(),
                                   symbol.VariablePath,
                                   StringComparison.Ordinal))
                .ToArray();
            return new LineUpGraphSymbolLookupResult(
                symbol.BlockName,
                symbol.VariablePath,
                matchingBlocks,
                matchingInterfaces);
        }).ToArray();

        return new(results);
    }

    public async Task<LineUpVariableLookupData> LoadVariableDataAsync(
        long plcProjectId,
        long instanceDbBlockId,
        IReadOnlyCollection<string> canonicalVariablePaths,
        CancellationToken cancellationToken)
    {
        var normalizedPaths = canonicalVariablePaths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var blockRows = await db.Ado.SqlQueryAsync<VariableBlockRow>(
                """
                SELECT id AS "Id", projectid AS "ProjectId", blockname AS "BlockName",
                       blocktype AS "BlockType", blocknumber AS "BlockNumber",
                       programminglanguage AS "ProgrammingLanguage"
                FROM ai_plc_block_new
                WHERE isdeleted = FALSE AND id = @BlockId AND projectid = @ProjectId
                """,
                new SugarParameter("@BlockId", instanceDbBlockId),
                new SugarParameter("@ProjectId", plcProjectId))
            .WaitAsync(cancellationToken);
        var blocks = blockRows
            .Select(x => new LineUpBlockLookup(
                x.Id,
                x.ProjectId,
                x.BlockName,
                x.BlockType,
                x.BlockNumber,
                x.ProgrammingLanguage))
            .ToArray();

        IReadOnlyList<LineUpBlockInterfaceLookup> interfaces = [];
        if (normalizedPaths.Length > 0)
        {
            var pathSet = normalizedPaths.ToHashSet(StringComparer.Ordinal);
            var interfaceRows = await db.Ado.SqlQueryAsync<VariableInterfaceRow>(
                    """
                    SELECT id AS "Id", blockid AS "BlockId", variablename AS "VariableName",
                           variablepath AS "VariablePath", datatype AS "DataType",
                           offsetbyte AS "OffsetByte", offsetbit AS "OffsetBit",
                           sizebits AS "SizeBits", "offset" AS "Offset"
                    FROM ai_plc_blockinterface
                    WHERE isdeleted = FALSE AND blockid = @BlockId
                    ORDER BY id
                    """,
                    new SugarParameter("@BlockId", instanceDbBlockId))
                .WaitAsync(cancellationToken);
            interfaces = interfaceRows
                .Where(x => !string.IsNullOrWhiteSpace(x.VariablePath) &&
                            pathSet.Contains(x.VariablePath.Trim().ToLowerInvariant()))
                .Select(x => new LineUpBlockInterfaceLookup(
                    x.Id,
                    x.BlockId,
                    x.VariableName,
                    x.VariablePath,
                    x.DataType,
                    x.OffsetByte,
                    x.OffsetBit,
                    x.SizeBits,
                    x.Offset))
                .ToArray();
        }

        return new(
            blocks.OrderBy(x => x.Id).ToArray(),
            interfaces.OrderBy(x => x.Id).ToArray());
    }

    public async Task<LineUpPlcResolutionData> LoadResolutionDataAsync(
        string projectName,
        string graphBlockName,
        int graphBlockNumber,
        CancellationToken cancellationToken)
    {
        var normalizedProjectName = projectName.Trim().ToLowerInvariant();
        // SqlSugar 对 record 投影的 DataReader 绑定不稳定（无参构造缺失 → ArgumentNullException），
        // 改手写 SQL + class 行绑定（同 LoadVariableDataAsync 模式）
        var projectRows = await db.Ado.SqlQueryAsync<ProjectLookupRow>(
                """
                SELECT id AS "Id", projectname AS "ProjectName"
                FROM ai_plc_project_new
                WHERE isdeleted = FALSE AND projectname IS NOT NULL
                  AND LOWER(TRIM(projectname)) = @Name
                """,
                new SugarParameter("@Name", normalizedProjectName))
            .WaitAsync(cancellationToken);
        var projects = projectRows
            .Select(x => new LineUpProjectLookup(x.Id, x.ProjectName))
            .ToArray();
        return await LoadResolutionCoreAsync(projects, graphBlockName, graphBlockNumber, cancellationToken);
    }

    public async Task<LineUpPlcResolutionData> LoadResolutionDataByDeviceIdAsync(
        long deviceId,
        string graphBlockName,
        int graphBlockNumber,
        CancellationToken cancellationToken)
    {
        var projectRows = await db.Ado.SqlQueryAsync<ProjectLookupRow>(
                """
                SELECT id AS "Id", projectname AS "ProjectName"
                FROM ai_plc_project_new
                WHERE isdeleted = FALSE AND plcdeviceid = @DeviceId
                ORDER BY id
                """,
                new SugarParameter("@DeviceId", deviceId))
            .WaitAsync(cancellationToken);
        var projects = projectRows
            .Select(x => new LineUpProjectLookup(x.Id, x.ProjectName))
            .ToArray();
        return await LoadResolutionCoreAsync(projects, graphBlockName, graphBlockNumber, cancellationToken);
    }

    /// <summary>
    /// 解析数据装载核心（按名/按设备 ID 两个入口共用）：GRAPH/DB 块 + 调用点，全部按 projectIds 过滤。
    /// </summary>
    private async Task<LineUpPlcResolutionData> LoadResolutionCoreAsync(
        LineUpProjectLookup[] projects,
        string graphBlockName,
        int graphBlockNumber,
        CancellationToken cancellationToken)
    {
        var normalizedGraphName = graphBlockName.Trim().ToLowerInvariant();
        var projectIds = projects.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (projectIds.Length == 0)
        {
            return new(projects, [], []);
        }

        var projectIdParams = projectIds
            .Select((value, index) => new SugarParameter($"@pid{index}", value))
            .ToArray();
        var projectIdList = string.Join(", ", projectIdParams.Select(p => p.ParameterName));
        var graphBlockRows = await db.Ado.SqlQueryAsync<BlockLookupRow>(
                $"""
                 SELECT id AS "Id", COALESCE(projectid, 0) AS "ProjectId", blockname AS "BlockName",
                        blocktype AS "BlockType", blocknumber AS "BlockNumber",
                        programminglanguage AS "ProgrammingLanguage"
                 FROM ai_plc_block_new
                 WHERE isdeleted = FALSE AND projectid IN ({projectIdList})
                   AND ((blockname IS NOT NULL AND LOWER(TRIM(blockname)) = @Name) OR blocknumber = @Number)
                 """,
                projectIdParams.Concat(
                [
                    new SugarParameter("@Name", normalizedGraphName),
                    new SugarParameter("@Number", graphBlockNumber)
                ]).ToArray())
            .WaitAsync(cancellationToken);
        var graphBlocks = graphBlockRows
            .Select(x => new LineUpBlockLookup(
                x.Id, x.ProjectId, x.BlockName, x.BlockType, x.BlockNumber, x.ProgrammingLanguage))
            .ToArray();

        // SqlSugar 翻译不了此处的可空转换表达式（op_Implicit 异常），改手写 SQL
        var rawCalls = await db.Ado.SqlQueryAsync<RawCall>(
                $"""
                 SELECT id AS "CallId", COALESCE(projectid, 0) AS "ProjectId",
                        COALESCE(callerblockid, 0) AS "CallerBlockId",
                        COALESCE(logicunitid, 0) AS "LogicUnitId",
                        calleeblockname AS "CalleeBlockName", calleeblocktype AS "CalleeBlockType"
                 FROM ai_plc_blockcall
                 WHERE isdeleted = FALSE AND projectid IN ({projectIdList})
                   AND calleeblockname IS NOT NULL AND LOWER(TRIM(calleeblockname)) = @Name
                   AND calleeblocktype IS NOT NULL AND LOWER(TRIM(calleeblocktype)) = 'fb'
                 ORDER BY id
                 """,
                projectIdParams.Concat([new SugarParameter("@Name", normalizedGraphName)]).ToArray())
            .WaitAsync(cancellationToken);
        var callerIds = rawCalls.Where(x => x.CallerBlockId > 0).Select(x => x.CallerBlockId).Distinct().ToArray();
        var logicUnitIds = rawCalls.Where(x => x.LogicUnitId > 0).Select(x => x.LogicUnitId).Distinct().ToArray();
        IReadOnlyList<CallerBlock> callerBlocks;
        if (callerIds.Length == 0)
        {
            callerBlocks = [];
        }
        else
        {
            var callerParams = callerIds
                .Select((value, index) => new SugarParameter($"@cb{index}", value))
                .ToArray();
            var callerList = string.Join(", ", callerParams.Select(p => p.ParameterName));
            callerBlocks = await db.Ado.SqlQueryAsync<CallerBlock>(
                    $"""
                     SELECT id AS "Id", COALESCE(projectid, 0) AS "ProjectId"
                     FROM ai_plc_block_new
                     WHERE isdeleted = FALSE AND id IN ({callerList})
                       AND projectid IN ({projectIdList})
                     ORDER BY id
                     """,
                    callerParams.Concat(projectIdParams).ToArray())
                .WaitAsync(cancellationToken);
        }

        IReadOnlyList<CallerLogicUnit> logicUnits;
        if (logicUnitIds.Length == 0)
        {
            logicUnits = [];
        }
        else
        {
            var luParams = logicUnitIds
                .Select((value, index) => new SugarParameter($"@lu{index}", value))
                .ToArray();
            var luList = string.Join(", ", luParams.Select(p => p.ParameterName));
            logicUnits = await db.Ado.SqlQueryAsync<CallerLogicUnit>(
                    $"""
                     SELECT id AS "Id", COALESCE(blockid, 0) AS "BlockId", callpinbindingsjson AS "CallPinBindingsJson"
                     FROM ai_plc_logicunit
                     WHERE isdeleted = FALSE AND id IN ({luList})
                     ORDER BY id
                     """,
                    luParams)
                .WaitAsync(cancellationToken);
        }

        var safeCalls = rawCalls
            .OrderBy(x => x.CallId)
            .Select(call => ProjectCall(call, callerBlocks, logicUnits, graphBlockName))
            .ToArray();
        var dbBlockRows = await db.Ado.SqlQueryAsync<BlockLookupRow>(
                $"""
                 SELECT id AS "Id", COALESCE(projectid, 0) AS "ProjectId", blockname AS "BlockName",
                        blocktype AS "BlockType", blocknumber AS "BlockNumber",
                        programminglanguage AS "ProgrammingLanguage"
                 FROM ai_plc_block_new
                 WHERE isdeleted = FALSE AND projectid IN ({projectIdList})
                   AND programminglanguage IS NOT NULL AND LOWER(TRIM(programminglanguage)) = 'db'
                   AND blocktype IS NOT NULL AND LOWER(TRIM(blocktype)) = 'instancedb'
                 """,
                projectIdParams)
            .WaitAsync(cancellationToken);
        var dbBlocks = dbBlockRows
            .Select(x => new LineUpBlockLookup(
                x.Id, x.ProjectId, x.BlockName, x.BlockType, x.BlockNumber, x.ProgrammingLanguage))
            .ToArray();
        var blocks = graphBlocks.Concat(dbBlocks)
            .GroupBy(x => x.Id)
            .Select(x => x.OrderBy(y => y.ProjectId).First())
            .OrderBy(x => x.Id)
            .ToArray();

        return new(
            projects.OrderBy(x => x.Id).ToArray(),
            blocks,
            safeCalls);
    }

    private static LineUpCallSiteLookup ProjectCall(
        RawCall call,
        IReadOnlyList<CallerBlock> callerBlocks,
        IReadOnlyList<CallerLogicUnit> logicUnits,
        string graphBlockName)
    {
        if (call.CallerBlockId <= 0)
        {
            return SafeCall(call, LineUpCallBindingParseStatus.CallerBlockMissing);
        }

        var callers = callerBlocks.Where(x => x.Id == call.CallerBlockId).ToArray();
        if (callers.Length != 1)
        {
            return SafeCall(call, LineUpCallBindingParseStatus.CallerBlockMissing);
        }

        if (callers[0].ProjectId != call.ProjectId)
        {
            return SafeCall(call, LineUpCallBindingParseStatus.CallerBlockProjectMismatch);
        }

        if (call.LogicUnitId <= 0)
        {
            return SafeCall(call, LineUpCallBindingParseStatus.LogicUnitMissing);
        }

        var units = logicUnits.Where(x => x.Id == call.LogicUnitId).ToArray();
        if (units.Length != 1)
        {
            return SafeCall(call, LineUpCallBindingParseStatus.LogicUnitMissing);
        }

        if (units[0].BlockId <= 0 || units[0].BlockId != call.CallerBlockId)
        {
            return SafeCall(call, LineUpCallBindingParseStatus.LogicUnitBlockMismatch);
        }

        var projection = LineUpCallPinBindingProjector.Project(
            units[0].CallPinBindingsJson,
            graphBlockName,
            "FB");
        return new(
            call.CallId,
            call.ProjectId,
            call.CallerBlockId,
            call.LogicUnitId,
            call.CalleeBlockName,
            call.CalleeBlockType,
            projection.InstanceDbName,
            projection.Status,
            projection.HasEquivalentDuplicates);
    }

    private static LineUpCallSiteLookup SafeCall(RawCall call, LineUpCallBindingParseStatus status) => new(
        call.CallId,
        call.ProjectId,
        call.CallerBlockId,
        call.LogicUnitId,
        call.CalleeBlockName,
        call.CalleeBlockType,
        null,
        status);

    private sealed class ProjectLookupRow
    {
        public long Id { get; set; }
        public string? ProjectName { get; set; }
    }

    private sealed class BlockLookupRow
    {
        public long Id { get; set; }
        public long ProjectId { get; set; }
        public string? BlockName { get; set; }
        public string? BlockType { get; set; }
        public int BlockNumber { get; set; }
        public string? ProgrammingLanguage { get; set; }
    }

    private sealed class RawCall
    {
        public long CallId { get; set; }
        public long ProjectId { get; set; }
        public long CallerBlockId { get; set; }
        public long LogicUnitId { get; set; }
        public string? CalleeBlockName { get; set; }
        public string? CalleeBlockType { get; set; }
    }

    private sealed class CallerBlock
    {
        public long Id { get; set; }
        public long ProjectId { get; set; }
    }

    private sealed class CallerLogicUnit
    {
        public long Id { get; set; }
        public long BlockId { get; set; }
        public string? CallPinBindingsJson { get; set; }
    }

    private sealed class VariableBlockRow
    {
        public long Id { get; set; }
        public long ProjectId { get; set; }
        public string? BlockName { get; set; }
        public string? BlockType { get; set; }
        public int? BlockNumber { get; set; }
        public string? ProgrammingLanguage { get; set; }
    }

    private sealed class VariableInterfaceRow
    {
        public long Id { get; set; }
        public long BlockId { get; set; }
        public string? VariableName { get; set; }
        public string? VariablePath { get; set; }
        public string? DataType { get; set; }
        public decimal? OffsetByte { get; set; }
        public decimal? OffsetBit { get; set; }
        public decimal? SizeBits { get; set; }
        public string? Offset { get; set; }
    }

    private static async Task<IReadOnlyList<PLC_BlockEntity>> LoadGraphBlockCandidatesAsync(
        ISqlSugarClient database,
        long plcProjectId,
        IReadOnlyCollection<string> requestedBlockNames,
        CancellationToken cancellationToken)
    {
        var blockNames = requestedBlockNames.ToArray();
        if (blockNames.Length == 0)
        {
            return [];
        }

        var query = CreateGraphBlockCandidateQuery(plcProjectId, blockNames);
        var rows = await database.Ado.SqlQueryAsync<VariableBlockRow>(
            query.Sql,
            (object)query.Parameters,
            cancellationToken);
        return rows.Select(row => new PLC_BlockEntity
        {
            Id = row.Id,
            ProjectId = row.ProjectId,
            BlockName = row.BlockName ?? string.Empty,
            BlockType = row.BlockType ?? string.Empty,
            BlockNumber = row.BlockNumber,
            ProgrammingLanguage = row.ProgrammingLanguage ?? string.Empty,
            IsDeleted = false
        }).ToArray();
    }

    private static async Task<IReadOnlyList<PLC_BlockInterfaceEntity>> LoadGraphInterfaceCandidatesAsync(
        ISqlSugarClient database,
        IReadOnlyCollection<long> requestedBlockIds,
        CancellationToken cancellationToken)
    {
        var blockIds = requestedBlockIds.ToArray();
        if (blockIds.Length == 0)
        {
            return [];
        }

        var query = CreateGraphInterfaceCandidateQuery(blockIds);
        var rows = await database.Ado.SqlQueryAsync<VariableInterfaceRow>(
            query.Sql,
            (object)query.Parameters,
            cancellationToken);
        return rows.Select(row => new PLC_BlockInterfaceEntity
        {
            Id = row.Id,
            BlockId = row.BlockId,
            VariableName = row.VariableName ?? string.Empty,
            VariablePath = row.VariablePath ?? string.Empty,
            DataType = row.DataType ?? string.Empty,
            OffsetByte = row.OffsetByte,
            OffsetBit = row.OffsetBit,
            SizeBits = row.SizeBits,
            Offset = row.Offset ?? string.Empty,
            IsDeleted = false
        }).ToArray();
    }

    internal static (string Sql, SugarParameter[] Parameters) CreateGraphBlockCandidateQuery(
        long plcProjectId,
        IReadOnlyList<string> blockNames)
    {
        var names = Enumerable.Range(0, blockNames.Count)
            .Select(index => $"@BlockName{index}")
            .ToArray();
        var parameters = new SugarParameter[blockNames.Count + 1];
        parameters[0] = new("@ProjectId", plcProjectId, global::System.Data.DbType.Int64);
        for (var index = 0; index < blockNames.Count; index++)
        {
            parameters[index + 1] = new(
                names[index],
                blockNames[index],
                global::System.Data.DbType.String);
        }

        return (
            $"""
             SELECT id AS "Id", projectid AS "ProjectId", blockname AS "BlockName",
                    blocktype AS "BlockType", blocknumber AS "BlockNumber",
                    programminglanguage AS "ProgrammingLanguage"
             FROM ai_plc_block_new
             WHERE isdeleted = FALSE
               AND projectid = @ProjectId
               AND blockname IN ({string.Join(", ", names)})
             ORDER BY id
             """,
            parameters);
    }

    internal static (string Sql, SugarParameter[] Parameters) CreateGraphInterfaceCandidateQuery(
        IReadOnlyList<long> blockIds)
    {
        var ids = Enumerable.Range(0, blockIds.Count)
            .Select(index => $"@BlockId{index}")
            .ToArray();
        var parameters = new SugarParameter[blockIds.Count];
        for (var index = 0; index < blockIds.Count; index++)
        {
            parameters[index] = new(
                ids[index],
                blockIds[index],
                global::System.Data.DbType.Int64);
        }

        return (
            $"""
             SELECT id AS "Id", blockid AS "BlockId", variablename AS "VariableName",
                    variablepath AS "VariablePath", datatype AS "DataType",
                    offsetbyte AS "OffsetByte", offsetbit AS "OffsetBit",
                    sizebits AS "SizeBits", "offset" AS "Offset"
             FROM ai_plc_blockinterface
             WHERE isdeleted = FALSE
               AND blockid IN ({string.Join(", ", ids)})
             ORDER BY id
             """,
            parameters);
    }
}
