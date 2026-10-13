using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpImageMarkRepository(ISqlSugarClient db)
    : ILineUpImageMarkRepository, IAutoInject
{
    private const string ImageSelect = @"SELECT ""Id"", ""StationId"", ""PictureName"",
                         ""StorageKey"", ""CurrentStorageKey"", ""ContentSha256"",
                         ""ByteLength"", ""PixelWidth"", ""PixelHeight"",
                         ""DetectedMimeType"", ""RowVersion""
                  FROM ""Lup_StationImage""";
    private const string BomSelect = @"SELECT ""Id"", ""StationId"", ""RefNo"", ""Describe"",
                         ""TagCode"", ""DBAddress"", ""ModelNo"", ""Brand"",
                         ""IOAddress"", ""MESPN"", ""PlcId"", ""PlcName"", ""PLCIP"",
                         ""IsAssociated""
                  FROM ""Lup_BomDirectory""";
    private const string MarkerSelect = @"SELECT ""Id"", ""StationImageId"", ""MarkType"", ""Mark"",
                         ""MCPConfig"", ""MarkTag"", ""MarkTagConfig"", ""MarkTagLine"",
                         ""MarkTagLineConfig"", ""MarkTagBom"", ""IsSingleMark"",
                         ""MarkerSchemaVersion"", ""ParsedMarkerJson""
                  FROM ""Lup_StationImageMark""";

    public async Task<LineUpImageWorkspaceData?> GetStationWorkspaceAsync(
        long subStationId,
        CancellationToken cancellationToken)
    {
        var hierarchy = await GetHierarchyAsync(subStationId, cancellationToken);
        if (hierarchy is null) return null;

        var images = await LoadImagesAsync([subStationId], cancellationToken);
        // 产线级池：本子工站已绑点位 ∪ 产线直挂（无工站）点位，二者都进清单供勾选上图
        var boms = (await GetBomsByLineAsync(hierarchy.ProductionLineId, cancellationToken))
            .Where(bom => bom.StationId is null || bom.StationId == subStationId)
            .ToArray();
        var imageIds = images.Select(row => row.Id).ToList();
        var markers = await LoadMarkersAsync(imageIds, cancellationToken);

        return new(
            hierarchy,
            images.Select(ToImage).ToArray(),
            boms,
            markers);
    }

    public async Task<LineUpImageHierarchyData?> GetHierarchyAsync(
        long subStationId,
        CancellationToken cancellationToken)
    {
        if (subStationId <= 0) return null;
        var rows = await db.Ado.SqlQueryAsync<HierarchyProjection>(
                @"SELECT substation.""Id"" AS ""SubStationId"",
                         substation.""SubStationCode"", substation.""SubStationName"",
                         workstation.""Id"" AS ""WorkStationId"",
                         workstation.""Code"" AS ""WorkStationCode"",
                         workstation.""Name"" AS ""WorkStationName"",
                         process.""Id"" AS ""ProcessId"",
                         process.""Code"" AS ""ProcessCode"",
                         process.""Name"" AS ""ProcessName"",
                         line.""Id"" AS ""ProductionLineId"",
                         line.""Code"" AS ""ProductionLineCode"",
                         line.""Name"" AS ""ProductionLineName""
                  FROM ""Basic_SubStation"" substation
                  INNER JOIN ""Basic_WorkStation"" workstation
                    ON workstation.""Id"" = substation.""WorkStationID""
                   AND workstation.""ProcessId"" = substation.""ProcessID""
                   AND workstation.""ProductionLineId"" = substation.""ProductionLineID""
                   AND workstation.""IsDeleted"" = FALSE
                  INNER JOIN ""Basic_Process"" process
                    ON process.""Id"" = substation.""ProcessID""
                   AND process.""ProductionLineId"" = substation.""ProductionLineID""
                   AND process.""IsDeleted"" = FALSE
                  INNER JOIN ""Basic_ProductionLine"" line
                    ON line.""Id"" = substation.""ProductionLineID""
                   AND line.""IsDeleted"" = FALSE
                  WHERE substation.""Id"" = @SubStationId
                    AND substation.""IsDeleted"" = FALSE
                  LIMIT 1",
                new SugarParameter("@SubStationId", subStationId))
            .WaitAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        return row is null
            ? null
            : new(
                row.SubStationId,
                row.SubStationCode,
                row.SubStationName,
                row.WorkStationId,
                row.WorkStationCode,
                row.WorkStationName,
                row.ProcessId,
                row.ProcessCode,
                row.ProcessName,
                row.ProductionLineId,
                row.ProductionLineCode,
                row.ProductionLineName);
    }

    public async Task<LineUpImageData?> GetImageAsync(
        long imageId,
        CancellationToken cancellationToken)
    {
        if (imageId <= 0) return null;
        var rows = await db.Ado.SqlQueryAsync<LineUpStationImageEntity>(
                ImageSelect + @"
                  WHERE ""IsDeleted"" = FALSE
                    AND ""Id"" = @ImageId
                    AND ""StationId"" IS NOT NULL
                  LIMIT 1",
                new SugarParameter("@ImageId", imageId))
            .WaitAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        return row is null ? null : ToImage(row);
    }

    public async Task<IReadOnlyList<LineUpImageBomData>> GetBomsByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        var exactIds = ids.Where(id => id > 0).Distinct().ToList();
        if (exactIds.Count == 0) return [];
        var parameters = new List<SugarParameter>();
        var idsClause = AddInParameters(parameters, "BomId", exactIds);
        var rows = await db.Ado.SqlQueryAsync<LineUpBomDirectoryEntity>(
                BomSelect + $@"
                  WHERE ""IsDeleted"" = FALSE
                    AND ""Id"" IN {idsClause}
                  ORDER BY ""Id""",
                parameters.ToArray())
            .WaitAsync(cancellationToken);
        return rows.Select(ToBom).ToArray();
    }

    public async Task<long> CreateImageAsync(
        LineUpImageCreateData data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var affected = await db.Ado.ExecuteCommandAsync(@"
                INSERT INTO ""Lup_StationImage""
                    (""Id"", ""StationId"", ""StationCode"", ""StationName"",
                     ""PictureName"", ""PictureSize"", ""HasSubitem"",
                     ""ProductionLineId"", ""ProductionLineCode"", ""ProductionLineName"",
                     ""ProcessId"", ""ProcessCode"", ""ProcessName"",
                     ""StorageKey"", ""ContentSha256"", ""ByteLength"",
                     ""PixelWidth"", ""PixelHeight"", ""DetectedMimeType"",
                     ""RowVersion"", ""IsDeleted"", ""CreateTime"", ""ModifyTime"")
                VALUES
                    (@Id, @StationId, @StationCode, @StationName,
                     @PictureName, @PictureSize, @HasSubitem,
                     @ProductionLineId, @ProductionLineCode, @ProductionLineName,
                     @ProcessId, @ProcessCode, @ProcessName,
                     @StorageKey, @ContentSha256, @ByteLength,
                     @PixelWidth, @PixelHeight, @DetectedMimeType,
                     @RowVersion, FALSE, @CreateTime, @ModifyTime)",
                new SugarParameter("@Id", data.ImageId),
                new SugarParameter("@StationId", DbValue(data.Hierarchy.SubStationId)),
                new SugarParameter("@StationCode", DbValue(data.Hierarchy.SubStationCode)),
                new SugarParameter("@StationName", DbValue(data.Hierarchy.SubStationName)),
                new SugarParameter("@PictureName", DbValue(data.PictureName)),
                new SugarParameter("@PictureSize", $"{data.PixelWidth}x{data.PixelHeight}"),
                new SugarParameter("@HasSubitem", false),
                new SugarParameter("@ProductionLineId", DbValue(data.Hierarchy.ProductionLineId)),
                new SugarParameter("@ProductionLineCode", DbValue(data.Hierarchy.ProductionLineCode)),
                new SugarParameter("@ProductionLineName", DbValue(data.Hierarchy.ProductionLineName)),
                new SugarParameter("@ProcessId", DbValue(data.Hierarchy.ProcessId)),
                new SugarParameter("@ProcessCode", DbValue(data.Hierarchy.ProcessCode)),
                new SugarParameter("@ProcessName", DbValue(data.Hierarchy.ProcessName)),
                new SugarParameter("@StorageKey", DbValue(data.StorageKey)),
                new SugarParameter("@ContentSha256", DbValue(data.ContentSha256)),
                new SugarParameter("@ByteLength", DbValue(data.ByteLength)),
                new SugarParameter("@PixelWidth", DbValue(data.PixelWidth)),
                new SugarParameter("@PixelHeight", DbValue(data.PixelHeight)),
                new SugarParameter("@DetectedMimeType", DbValue(data.DetectedMimeType)),
                new SugarParameter("@RowVersion", data.RowVersion),
                new SugarParameter("@CreateTime", now),
                new SugarParameter("@ModifyTime", now))
            .WaitAsync(cancellationToken);
        return affected == 1 ? data.ImageId : 0;
    }

    public async Task<LineUpImageBomData?> GetBomByIdAsync(
        long bomId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Ado.SqlQueryAsync<LineUpBomDirectoryEntity>(
                BomSelect + @" WHERE ""IsDeleted"" = FALSE AND ""Id"" = @BomId LIMIT 1",
                new SugarParameter("@BomId", bomId))
            .WaitAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        return row is null ? null : ToBom(row);
    }

    public async Task<long> CreateBomAsync(
        LineUpBomDirectoryEntity entity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var affected = await db.Ado.ExecuteCommandAsync(@"
                INSERT INTO ""Lup_BomDirectory""
                    (""Id"", ""RefNo"", ""Describe"", ""TagCode"", ""DBAddress"",
                     ""ModelNo"", ""Brand"", ""IOAddress"", ""MESPN"",
                     ""PlcId"", ""PlcCode"", ""PlcName"", ""PLCIP"",
                     ""StationId"", ""StationCode"", ""StationName"",
                     ""ProductionLineId"", ""ProductionLineCode"", ""ProductionLineName"",
                     ""ProcessId"", ""ProcessCode"", ""ProcessName"",
                     ""SourceFrom"", ""IsDeleted"", ""CreateTime"", ""ModifyTime"")
                VALUES
                    (@Id, @RefNo, @Describe, @TagCode, @DbAddress,
                     @ModelNo, @Brand, @IoAddress, @PartNo,
                     CAST(@PlcId AS bigint), @PlcCode, @PlcName, @PlcIp,
                     CAST(@StationId AS bigint), @StationCode, @StationName,
                     @ProductionLineId, @ProductionLineCode, @ProductionLineName,
                     CAST(@ProcessId AS bigint), @ProcessCode, @ProcessName,
                     @SourceFrom, FALSE, @CreateTime, @ModifyTime)",
                new SugarParameter("@Id", entity.Id),
                new SugarParameter("@RefNo", DbValue(entity.RefNo)),
                new SugarParameter("@Describe", DbValue(entity.Describe)),
                new SugarParameter("@TagCode", DbValue(entity.TagCode)),
                new SugarParameter("@DbAddress", DbValue(entity.DBAddress)),
                new SugarParameter("@ModelNo", DbValue(entity.ModelNo)),
                new SugarParameter("@Brand", DbValue(entity.Brand)),
                new SugarParameter("@IoAddress", DbValue(entity.IOAddress)),
                new SugarParameter("@PartNo", DbValue(entity.MESPN)),
                new SugarParameter("@PlcId", DbValue(entity.PlcId)),
                new SugarParameter("@PlcCode", DbValue(entity.PlcCode)),
                new SugarParameter("@PlcName", DbValue(entity.PlcName)),
                new SugarParameter("@PlcIp", DbValue(entity.PLCIP)),
                new SugarParameter("@StationId", DbValue(entity.StationId)),
                new SugarParameter("@StationCode", DbValue(entity.StationCode)),
                new SugarParameter("@StationName", DbValue(entity.StationName)),
                new SugarParameter("@ProductionLineId", DbValue(entity.ProductionLineId)),
                new SugarParameter("@ProductionLineCode", DbValue(entity.ProductionLineCode)),
                new SugarParameter("@ProductionLineName", DbValue(entity.ProductionLineName)),
                new SugarParameter("@ProcessId", DbValue(entity.ProcessId)),
                new SugarParameter("@ProcessCode", DbValue(entity.ProcessCode)),
                new SugarParameter("@ProcessName", DbValue(entity.ProcessName)),
                new SugarParameter("@SourceFrom", DbValue(entity.SourceFrom)),
                new SugarParameter("@CreateTime", now),
                new SugarParameter("@ModifyTime", now))
            .WaitAsync(cancellationToken);
        return affected == 1 ? entity.Id : 0;
    }

    public async Task<bool> UpdateBomFieldsAsync(
        long bomId,
        string? refNo,
        string? tagCode,
        string? describe,
        string? dbAddress,
        string? modelNo,
        string? brand,
        string? ioAddress,
        string? partNo,
        long? plcId,
        string? plcCode,
        string? plcName,
        string? plcIp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var affected = await db.Ado.ExecuteCommandAsync(@"
                UPDATE ""Lup_BomDirectory""
                   SET ""RefNo"" = @RefNo,
                       ""TagCode"" = @TagCode,
                       ""Describe"" = @Describe,
                       ""DBAddress"" = @DbAddress,
                       ""ModelNo"" = @ModelNo,
                       ""Brand"" = @Brand,
                       ""IOAddress"" = @IoAddress,
                       ""MESPN"" = @PartNo,
                       ""PlcId"" = CAST(@PlcId AS bigint),
                       ""PlcCode"" = @PlcCode,
                       ""PlcName"" = @PlcName,
                       ""PLCIP"" = @PlcIp,
                       ""ModifyTime"" = NOW()
                 WHERE ""Id"" = @BomId
                   AND ""IsDeleted"" = FALSE",
                new SugarParameter("@RefNo", refNo),
                new SugarParameter("@TagCode", tagCode),
                new SugarParameter("@Describe", describe),
                new SugarParameter("@DbAddress", dbAddress),
                new SugarParameter("@ModelNo", modelNo),
                new SugarParameter("@Brand", brand),
                new SugarParameter("@IoAddress", ioAddress),
                new SugarParameter("@PartNo", partNo),
                new SugarParameter("@PlcId", plcId),
                new SugarParameter("@PlcCode", plcCode),
                new SugarParameter("@PlcName", plcName),
                new SugarParameter("@PlcIp", plcIp),
                new SugarParameter("@BomId", bomId))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<IReadOnlyList<LineUpPlcDeviceData>> ListPlcDevicesAsync(
        long? productionLineId,
        CancellationToken cancellationToken)
    {
        var sql = @"SELECT ""Id"", ""DeviceName"", ""IpAddress"", ""LineID"" FROM ""Basic_Device"" WHERE ""IsDeleted"" = FALSE";
        var parameters = new List<SugarParameter>();
        if (productionLineId is > 0)
        {
            sql += @" AND ""LineID"" = @LineId";
            parameters.Add(new("@LineId", productionLineId.Value));
        }
        sql += @" ORDER BY ""DeviceName""";
        var rows = await db.Ado.SqlQueryAsync<PlcDeviceProjection>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
        return rows.Select(row => new LineUpPlcDeviceData(
            row.Id, row.DeviceName, row.DeviceName, row.IpAddress, row.LineID)).ToArray();
    }

    public async Task<LineUpPlcDeviceData?> GetPlcDeviceAsync(long plcId, CancellationToken cancellationToken)
    {
        var rows = await db.Ado.SqlQueryAsync<PlcDeviceProjection>(
                @"SELECT ""Id"", ""DeviceName"", ""IpAddress"", ""LineID"" FROM ""Basic_Device"" WHERE ""IsDeleted"" = FALSE AND ""Id"" = @PlcId LIMIT 1",
                new SugarParameter("@PlcId", plcId))
            .WaitAsync(cancellationToken);
        var row = rows.SingleOrDefault();
        return row is null ? null : new LineUpPlcDeviceData(
            row.Id, row.DeviceName, row.DeviceName, row.IpAddress, row.LineID);
    }

    private sealed class PlcDeviceProjection
    {
        public long Id { get; set; }
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public long? LineID { get; set; }
    }

    public async Task<bool> UpdateBomAssociationAsync(
        long bomId,
        bool isAssociated,
        CancellationToken cancellationToken)
    {
        var affected = await db.Ado.ExecuteCommandAsync(
                @"UPDATE ""Lup_BomDirectory""
                      SET ""IsAssociated"" = @IsAssociated, ""ModifyTime"" = @ModifyTime
                    WHERE ""IsDeleted"" = FALSE AND ""Id"" = @BomId",
                new SugarParameter("@BomId", bomId),
                new SugarParameter("@IsAssociated", isAssociated),
                new SugarParameter("@ModifyTime", DateTime.UtcNow))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> SoftDeleteBomAsync(
        long bomId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var affected = await db.Ado.ExecuteCommandAsync(
                @"UPDATE ""Lup_BomDirectory"" SET ""IsDeleted"" = TRUE, ""ModifyTime"" = NOW() WHERE ""Id"" = @BomId AND ""IsDeleted"" = FALSE",
                new SugarParameter("@BomId", bomId))
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<LineUpImageSaveResult> SaveImageAsync(
        LineUpImageSaveData data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.Ado.BeginTran();
        try
        {
            var now = DateTime.UtcNow;
            var imageUpdateSql = @"
                    UPDATE ""Lup_StationImage""
                       SET ""RowVersion"" = @NewRowVersion,
                           ""ModifyTime"" = @ModifyTime";
            var imageUpdateParams = new List<SugarParameter>
            {
                new("@NewRowVersion", data.ExpectedRowVersion + 1),
                new("@ModifyTime", now),
                new("@ImageId", data.ImageId),
                new("@ExpectedRowVersion", data.ExpectedRowVersion),
            };
            if (!string.IsNullOrWhiteSpace(data.CurrentStorageKey))
            {
                imageUpdateSql += @",
                           ""CurrentStorageKey"" = @CurrentStorageKey";
                imageUpdateParams.Add(new("@CurrentStorageKey", data.CurrentStorageKey));
            }
            imageUpdateSql += @"
                     WHERE ""Id"" = @ImageId
                       AND ""IsDeleted"" = FALSE
                       AND ""RowVersion"" = @ExpectedRowVersion";

            var affected = await db.Ado.ExecuteCommandAsync(imageUpdateSql, imageUpdateParams.ToArray())
                .WaitAsync(cancellationToken);
            if (affected != 1)
            {
                db.Ado.RollbackTran();
                var existsRows = await db.Ado.SqlQueryAsync<long>(
                        @"SELECT COUNT(1) FROM ""Lup_StationImage""
                          WHERE ""IsDeleted"" = FALSE AND ""Id"" = @ImageId",
                        new SugarParameter("@ImageId", data.ImageId))
                    .WaitAsync(cancellationToken);
                return existsRows.FirstOrDefault() > 0
                    ? LineUpImageSaveResult.VersionConflict
                    : LineUpImageSaveResult.Missing;
            }

            var requestedIds = data.Markers.Select(marker => marker.MarkerId).Distinct().ToList();
            var requestedRows = requestedIds.Count == 0
                ? []
                : await QueryMarkerOwnershipAsync(requestedIds, cancellationToken);
            if (requestedRows.Any(row => row.StationImageId != data.ImageId))
            {
                db.Ado.RollbackTran();
                return LineUpImageSaveResult.MarkerOwnershipConflict;
            }

            var activeIds = await db.Ado.SqlQueryAsync<long>(
                    @"SELECT ""Id"" FROM ""Lup_StationImageMark""
                      WHERE ""IsDeleted"" = FALSE AND ""StationImageId"" = @ImageId",
                    new SugarParameter("@ImageId", data.ImageId))
                .WaitAsync(cancellationToken);
            var removedIds = activeIds.Except(requestedIds).ToList();
            if (removedIds.Count > 0)
            {
                var removeParams = new List<SugarParameter>
                {
                    new("@ModifyTime", now),
                    new("@ImageId", data.ImageId),
                };
                var removeClause = AddInParameters(removeParams, "RemovedId", removedIds);
                await db.Ado.ExecuteCommandAsync(
                        $@"UPDATE ""Lup_StationImageMark""
                              SET ""IsDeleted"" = TRUE, ""ModifyTime"" = @ModifyTime
                            WHERE ""Id"" IN {removeClause}
                              AND ""StationImageId"" = @ImageId",
                        removeParams.ToArray())
                    .WaitAsync(cancellationToken);
            }

            var existingIds = requestedRows.Select(row => row.Id).ToHashSet();
            foreach (var marker in data.Markers.OrderBy(marker => marker.MarkerId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingIds.Contains(marker.MarkerId))
                {
                    var markerAffected = await db.Ado.ExecuteCommandAsync(
                            @"UPDATE ""Lup_StationImageMark""
                              SET ""MarkType"" = @MarkType,
                                  ""MCPConfig"" = @MCPConfig,
                                  ""MarkTagConfig"" = @MarkTagConfig,
                                  ""MarkTagLineConfig"" = @MarkTagLineConfig,
                                  ""MarkTagBom"" = @MarkTagBom,
                                  ""IsSingleMark"" = @IsSingleMark,
                                  ""MarkerSchemaVersion"" = @MarkerSchemaVersion,
                                  ""ParsedMarkerJson"" = CAST(@ParsedMarkerJson AS jsonb),
                                  ""IsDeleted"" = FALSE,
                                  ""ModifyTime"" = @ModifyTime
                              WHERE ""Id"" = @MarkerId AND ""StationImageId"" = @ImageId",
                            MarkerParameters(marker, data.ImageId, now))
                        .WaitAsync(cancellationToken);
                    if (markerAffected != 1)
                    {
                        throw new InvalidOperationException("The image marker changed during the transaction.");
                    }
                }
                else
                {
                    var markerAffected = await db.Ado.ExecuteCommandAsync(
                            @"INSERT INTO ""Lup_StationImageMark""
                                (""Id"", ""StationImageId"", ""MarkType"", ""MCPConfig"",
                                 ""MarkTagConfig"", ""MarkTagLineConfig"", ""MarkTagBom"",
                                 ""IsSingleMark"", ""MarkerSchemaVersion"", ""ParsedMarkerJson"",
                                 ""IsDeleted"", ""CreateTime"", ""ModifyTime"")
                              VALUES
                                (@MarkerId, @ImageId, @MarkType, @MCPConfig,
                                 @MarkTagConfig, @MarkTagLineConfig, @MarkTagBom,
                                 @IsSingleMark, @MarkerSchemaVersion, CAST(@ParsedMarkerJson AS jsonb),
                                 FALSE, @ModifyTime, @ModifyTime)",
                            MarkerParameters(marker, data.ImageId, now))
                        .WaitAsync(cancellationToken);
                    if (markerAffected != 1)
                    {
                        throw new InvalidOperationException("The image marker could not be created.");
                    }
                }
            }

            // 上图即绑定（2026-09-29 v2.1）：无工站池点位绑定为当前子工站，仅更新 StationId 为空的行（幂等）
            if (data.PoolBinding is { BomIds.Count: > 0 } binding)
            {
                var bindingParameters = new List<SugarParameter>
                {
                    new("@StationId", binding.Hierarchy.SubStationId),
                    new("@StationCode", DbValue(binding.Hierarchy.SubStationCode)),
                    new("@StationName", DbValue(binding.Hierarchy.SubStationName)),
                    new("@ProcessId", DbValue(binding.Hierarchy.ProcessId)),
                    new("@ProcessCode", DbValue(binding.Hierarchy.ProcessCode)),
                    new("@ProcessName", DbValue(binding.Hierarchy.ProcessName)),
                };
                var poolIdsClause = AddInParameters(bindingParameters, "PoolBomId", binding.BomIds);
                var bound = await db.Ado.ExecuteCommandAsync($@"
                        UPDATE ""Lup_BomDirectory""
                           SET ""StationId"" = @StationId,
                               ""StationCode"" = @StationCode,
                               ""StationName"" = @StationName,
                               ""ProcessId"" = @ProcessId,
                               ""ProcessCode"" = @ProcessCode,
                               ""ProcessName"" = @ProcessName,
                               ""ModifyTime"" = NOW()
                         WHERE ""IsDeleted"" = FALSE
                           AND ""StationId"" IS NULL
                           AND ""Id"" IN {poolIdsClause}",
                        bindingParameters.ToArray())
                    .WaitAsync(cancellationToken);
                if (bound != binding.BomIds.Count)
                    throw new InvalidOperationException(
                        "A line-pool BOM changed binding during the transaction.");
            }

            db.Ado.CommitTran();
            return LineUpImageSaveResult.Saved;
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    private static SugarParameter[] MarkerParameters(
        LineUpImageMarkerSaveData marker,
        long imageId,
        DateTime modifyTime) =>
    [
        new("@MarkerId", marker.MarkerId),
        new("@ImageId", imageId),
        new("@MarkType", DbValue(marker.MarkType)),
        new("@MCPConfig", DbValue(marker.McpConfig)),
        new("@MarkTagConfig", DbValue(marker.MarkTagConfig)),
        new("@MarkTagLineConfig", DbValue(marker.MarkTagLineConfig)),
        new("@MarkTagBom", DbValue(marker.MarkTagBom)),
        new("@IsSingleMark", marker.IsSingleMark),
        new("@MarkerSchemaVersion", marker.MarkerSchemaVersion),
        new("@ParsedMarkerJson", marker.ParsedMarkerJson),
        new("@ModifyTime", DateTime.SpecifyKind(modifyTime, DateTimeKind.Utc))
    ];

    private static object DbValue(object? value) => value ?? DBNull.Value;

    public async Task<bool> SoftDeleteImageAsync(
        long imageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.Ado.BeginTran();
        try
        {
            var now = DateTime.UtcNow;
            var affected = await db.Ado.ExecuteCommandAsync(
                    @"UPDATE ""Lup_StationImage""
                          SET ""IsDeleted"" = TRUE, ""ModifyTime"" = @ModifyTime
                        WHERE ""IsDeleted"" = FALSE AND ""Id"" = @ImageId",
                    new SugarParameter("@ImageId", imageId),
                    new SugarParameter("@ModifyTime", now))
                .WaitAsync(cancellationToken);
            if (affected != 1)
            {
                db.Ado.RollbackTran();
                return false;
            }

            await db.Ado.ExecuteCommandAsync(
                    @"UPDATE ""Lup_StationImageMark""
                          SET ""IsDeleted"" = TRUE, ""ModifyTime"" = @ModifyTime
                        WHERE ""IsDeleted"" = FALSE AND ""StationImageId"" = @ImageId",
                    new SugarParameter("@ImageId", imageId),
                    new SugarParameter("@ModifyTime", now))
                .WaitAsync(cancellationToken);
            db.Ado.CommitTran();
            return true;
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public async Task<LineUpImageWorkspaceData?> FindFaultCandidatesAsync(
        string? tagCode,
        long? bomId,
        CancellationToken cancellationToken)
    {
        var normalizedTag = string.IsNullOrWhiteSpace(tagCode) ? null : tagCode.Trim().ToLower();
        var boms = await LoadBomsAsync(null, bomId, normalizedTag, cancellationToken);
        if (boms.Count == 0) return null;

        var stationIds = boms.Select(row => row.StationId!.Value).Distinct().ToArray();
        var images = await LoadImagesAsync(stationIds, cancellationToken);
        var imageIds = images.Select(row => row.Id).ToArray();
        var markers = await LoadMarkersAsync(imageIds, cancellationToken);
        var hierarchy = await GetHierarchyAsync(stationIds[0], cancellationToken);
        if (hierarchy is null) return null;
        return new(
            hierarchy,
            images.Select(ToImage).ToArray(),
            boms.Select(ToBom).ToArray(),
            markers);
    }

    /// <summary>产线级 BOM 池：产线直挂行（含导入的无工站点位）∪ 该产线全部子工站名下已绑行。</summary>
    public async Task<IReadOnlyList<LineUpImageBomData>> GetBomsByLineAsync(
        long productionLineId,
        CancellationToken cancellationToken)
    {
        if (productionLineId <= 0) return [];
        var rows = await db.Ado.SqlQueryAsync<LineUpBomDirectoryEntity>(
                BomSelect + @"
                   WHERE ""IsDeleted"" = FALSE
                     AND (""ProductionLineId"" = @LineId
                          OR ""StationId"" IN (SELECT ""Id"" FROM ""Basic_SubStation""
                                                WHERE ""IsDeleted"" = FALSE
                                                  AND ""ProductionLineID"" = @LineId))
                   ORDER BY ""Id""",
                new SugarParameter("@LineId", productionLineId))
            .WaitAsync(cancellationToken);
        return rows.Select(ToBom).ToArray();
    }

    public async Task<LineUpBomImportLineInfo?> GetProductionLineAsync(
        long productionLineId,
        CancellationToken cancellationToken)
    {
        if (productionLineId <= 0) return null;
        // 注意：SqlSugar 映射目标必须是无参构造的投影类（positional record 会导致 IL Emit ArgumentNullException）
        var rows = await db.Ado.SqlQueryAsync<ProductionLineProjection>(
                @"SELECT ""Id"" AS ""LineId"", ""Code"", ""Name""
                    FROM ""Basic_ProductionLine""
                   WHERE ""IsDeleted"" = FALSE AND ""Id"" = @LineId
                   LIMIT 1",
                new SugarParameter("@LineId", productionLineId))
            .WaitAsync(cancellationToken);
        var row = rows.FirstOrDefault();
        return row is null ? null : new LineUpBomImportLineInfo(row.LineId, row.Code, row.Name);
    }

    private sealed class ProductionLineProjection
    {
        public long LineId { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }
    /// <summary>整批原子导入：任一行失败全部回滚（BeginTran/Commit/RollbackTran 惯例同 SaveImageAsync）。</summary>
    public async Task<int> ImportBomsAsync(
        IReadOnlyList<LineUpBomImportUpsert> upserts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (upserts.Count == 0) return 0;
        var affected = 0;
        db.Ado.BeginTran();
        try
        {
            foreach (var upsert in upserts)
            {
                if (upsert.IsUpdate)
                {
                    var updated = await UpdateBomFieldsAsync(
                        upsert.BomId,
                        upsert.RefNo,
                        upsert.TagCode,
                        upsert.Describe,
                        upsert.DbAddress,
                        upsert.ModelNo,
                        upsert.Brand,
                        upsert.IoAddress,
                        upsert.PartNo,
                        upsert.PlcId,
                        upsert.PlcCode,
                        upsert.PlcName,
                        upsert.PlcIp,
                        cancellationToken);
                    if (!updated)
                        throw new InvalidOperationException(
                            $"LineUp BOM import update affected 0 rows for bom {upsert.BomId}.");
                    affected++;
                }
                else
                {
                    var line = upsert.Line
                        ?? throw new InvalidOperationException(
                            "LineUp BOM import create requires a production line.");
                    var entity = new LineUpBomDirectoryEntity
                    {
                        RefNo = upsert.RefNo,
                        Describe = upsert.Describe,
                        TagCode = upsert.TagCode,
                        DBAddress = upsert.DbAddress,
                        ModelNo = upsert.ModelNo,
                        Brand = upsert.Brand,
                        MESPN = upsert.PartNo,
                        PlcId = upsert.PlcId,
                        PlcCode = upsert.PlcCode,
                        PlcName = upsert.PlcName,
                        PLCIP = upsert.PlcIp,
                        StationId = null,
                        StationCode = null,
                        StationName = null,
                        ProductionLineId = line.LineId,
                        ProductionLineCode = line.Code,
                        ProductionLineName = line.Name,
                        ProcessId = null,
                        ProcessCode = null,
                        ProcessName = null,
                        SourceFrom = "ImageMarksImport",
                    };
                    var createdId = await CreateBomAsync(entity, cancellationToken);
                    if (createdId <= 0)
                        throw new InvalidOperationException("LineUp BOM import create failed.");
                    affected++;
                }
            }

            db.Ado.CommitTran();
            return affected;
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    private async Task<List<LineUpStationImageEntity>> LoadImagesAsync(
        IReadOnlyCollection<long> stationIds,
        CancellationToken cancellationToken)
    {
        if (stationIds.Count == 0) return [];
        var parameters = new List<SugarParameter>();
        var stationIdsClause = AddInParameters(parameters, "StationId", stationIds);
        return await db.Ado.SqlQueryAsync<LineUpStationImageEntity>(
                ImageSelect + $@"
                  WHERE ""IsDeleted"" = FALSE
                    AND ""StationId"" IN {stationIdsClause}
                  ORDER BY ""Id""",
                parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    private async Task<List<LineUpBomDirectoryEntity>> LoadBomsAsync(
        IReadOnlyCollection<long>? stationIds,
        long? bomId,
        string? normalizedTag,
        CancellationToken cancellationToken)
    {
        if (stationIds is { Count: 0 }) return [];
        var sql = BomSelect + @"
                  WHERE ""IsDeleted"" = FALSE
                    AND ""StationId"" IS NOT NULL";
        var parameters = new List<SugarParameter>();
        if (stationIds is not null)
        {
            var stationIdsClause = AddInParameters(parameters, "StationId", stationIds);
            sql += $@" AND ""StationId"" IN {stationIdsClause}";
        }
        if (bomId is > 0)
        {
            sql += @" AND ""Id"" = @BomId";
            parameters.Add(new("@BomId", bomId.Value));
        }
        if (normalizedTag is not null)
        {
            sql += @" AND lower(""TagCode"") = @TagCode";
            parameters.Add(new("@TagCode", normalizedTag));
        }
        sql += @" ORDER BY ""Id""";
        return await db.Ado.SqlQueryAsync<LineUpBomDirectoryEntity>(sql, parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    private async Task<List<LineUpStationImageMarkEntity>> LoadMarkersAsync(
        IReadOnlyCollection<long> imageIds,
        CancellationToken cancellationToken)
    {
        if (imageIds.Count == 0) return [];
        var parameters = new List<SugarParameter>();
        var imageIdsClause = AddInParameters(parameters, "ImageId", imageIds);
        return await db.Ado.SqlQueryAsync<LineUpStationImageMarkEntity>(
                MarkerSelect + $@"
                  WHERE ""IsDeleted"" = FALSE
                    AND ""StationImageId"" IS NOT NULL
                    AND ""StationImageId"" IN {imageIdsClause}
                  ORDER BY ""Id""",
                parameters.ToArray())
            .WaitAsync(cancellationToken);
    }

    private async Task<List<MarkerOwnershipRow>> QueryMarkerOwnershipAsync(
        List<long> markerIds,
        CancellationToken cancellationToken)
    {
        var parameters = new List<SugarParameter>();
        var clause = AddInParameters(parameters, "MarkerId", markerIds);
        var rows = await db.Ado.SqlQueryAsync<MarkerOwnershipRow>(
                $@"SELECT ""Id"", ""StationImageId"" FROM ""Lup_StationImageMark""
                    WHERE ""Id"" IN {clause}",
                parameters.ToArray())
            .WaitAsync(cancellationToken);
        return rows.ToList();
    }

    private sealed class MarkerOwnershipRow
    {
        public long Id { get; set; }
        public long? StationImageId { get; set; }
    }

    private static string AddInParameters(
        ICollection<SugarParameter> parameters,
        string prefix,
        IEnumerable<long> values)
    {
        var names = values.Distinct().Select((value, index) =>
        {
            var name = $"@{prefix}{index}";
            parameters.Add(new(name, value));
            return name;
        }).ToArray();
        return $"({string.Join(", ", names)})";
    }

    private sealed class HierarchyProjection
    {
        public long SubStationId { get; set; }
        public string? SubStationCode { get; set; }
        public string? SubStationName { get; set; }
        public long WorkStationId { get; set; }
        public string? WorkStationCode { get; set; }
        public string? WorkStationName { get; set; }
        public long ProcessId { get; set; }
        public string? ProcessCode { get; set; }
        public string? ProcessName { get; set; }
        public long ProductionLineId { get; set; }
        public string? ProductionLineCode { get; set; }
        public string? ProductionLineName { get; set; }
    }

    private static LineUpImageData ToImage(LineUpStationImageEntity row) => new(
        row.Id,
        row.StationId!.Value,
        row.PictureName,
        row.StorageKey,
        row.CurrentStorageKey,
        row.ContentSha256,
        row.ByteLength,
        row.PixelWidth,
        row.PixelHeight,
        row.DetectedMimeType,
        row.RowVersion);

    private static LineUpImageBomData ToBom(LineUpBomDirectoryEntity row) => new(
        row.Id,
        row.StationId,
        row.RefNo,
        row.Describe,
        row.TagCode,
        row.DBAddress,
        row.ModelNo,
        row.Brand,
        row.IOAddress,
        row.MESPN,
        row.PlcId,
        row.PlcName,
        row.PLCIP,
        row.IsAssociated);
}
