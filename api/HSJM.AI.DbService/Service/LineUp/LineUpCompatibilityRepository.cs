using System;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpCompatibilityRepository(ISqlSugarClient db)
    : ILineUpCompatibilityRepository, IAutoInject
{
    public Task<LineUpMasterPageData> QueryMasterAsync(LineUpMasterQuery query, CancellationToken cancellationToken) =>
        query.Level switch
        {
            "ProductionLine" => QueryLinesAsync(query, cancellationToken),
            "Process" => QueryProcessesAsync(query, cancellationToken),
            "WorkStation" => QueryWorkStationsAsync(query, cancellationToken),
            "SubStation" => QuerySubStationsAsync(query, cancellationToken),
            "Device" => QueryDevicesAsync(query, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query.Level)),
        };

    // Basic_* 是 PG 大小写保留表：SqlSugar Queryable/Insertable 会把列名二次引号（42601 空标识符），
    // 全部改手写 SQL + class 绑定（同 LineUpImportRepository 的已验证路径）。
    private sealed class MasterRow
    {
        public long Id { get; set; }
        public long ProductionLineId { get; set; }
        public long ProcessId { get; set; }
        public long WorkStationId { get; set; }
        public long LineId { get; set; }
        public long PlcId { get; set; }
        public string? PlcName { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? IpAddress { get; set; }
        public bool? IsEnable { get; set; }
        public string? Remark { get; set; }
    }

    public async Task<LineUpMasterParentLookup?> GetMasterParentAsync(
        string level,
        long parentId,
        CancellationToken cancellationToken)
    {
        if (level == "ProductionLine")
        {
            var rows = await db.Ado.SqlQueryAsync<MasterRow>(
                "SELECT \"Id\" FROM \"Basic_ProductionLine\" WHERE \"IsDeleted\" = FALSE AND \"Id\" = @Id",
                new SugarParameter("@Id", parentId)).WaitAsync(cancellationToken);
            return rows.Count == 1 ? new(rows[0].Id, "ProductionLine", rows[0].Id, null, null) : null;
        }

        if (level == "Device")
        {
            var rows = await db.Ado.SqlQueryAsync<MasterRow>(
                "SELECT \"Id\", \"DeviceName\" FROM \"Basic_Device\" WHERE \"IsDeleted\" = FALSE AND \"Id\" = @Id",
                new SugarParameter("@Id", parentId)).WaitAsync(cancellationToken);
            return rows.Count == 1 ? new(rows[0].Id, "Device", 0, null, null) : null;
        }

        if (level == "Process")
        {
            var rows = await db.Ado.SqlQueryAsync<MasterRow>(
                "SELECT \"Id\" FROM \"Basic_ProductionLine\" WHERE \"IsDeleted\" = FALSE AND \"Id\" = @Id",
                new SugarParameter("@Id", parentId)).WaitAsync(cancellationToken);
            return rows.Count == 1 ? new(rows[0].Id, "ProductionLine", rows[0].Id, null, null) : null;
        }

        if (level == "WorkStation")
        {
            var rows = await db.Ado.SqlQueryAsync<MasterRow>(
                "SELECT \"Id\", \"ProductionLineId\" FROM \"Basic_Process\" WHERE \"IsDeleted\" = FALSE AND \"Id\" = @Id",
                new SugarParameter("@Id", parentId)).WaitAsync(cancellationToken);
            return rows.Count == 1 ? new(rows[0].Id, "Process", rows[0].ProductionLineId, rows[0].Id, null) : null;
        }

        if (level == "SubStation")
        {
            var rows = await db.Ado.SqlQueryAsync<MasterRow>(
                "SELECT \"Id\", \"ProductionLineId\", \"ProcessId\" FROM \"Basic_WorkStation\" WHERE \"IsDeleted\" = FALSE AND \"Id\" = @Id",
                new SugarParameter("@Id", parentId)).WaitAsync(cancellationToken);
            return rows.Count == 1
                ? new(rows[0].Id, "WorkStation", rows[0].ProductionLineId, rows[0].ProcessId, rows[0].Id)
                : null;
        }

        return null;
    }

    public async Task<bool> MasterCodeExistsAsync(
        string level,
        long parentId,
        string code,
        long? excludeId,
        CancellationToken cancellationToken)
    {
        var normalized = code.ToLower();
        var (table, column, parentColumn) = level switch
        {
            "ProductionLine" => ("Basic_ProductionLine", "Code", (string?)null),
            "Process" => ("Basic_Process", "Code", "ProductionLineId"),
            "WorkStation" => ("Basic_WorkStation", "Code", "ProcessId"),
            "SubStation" => ("Basic_SubStation", "SubStationCode", "WorkStationID"),
            // DeviceName 全局唯一（PLC 绑定/工位白名单按 DeviceName 匹配，不允许重名）
            "Device" => ("Basic_Device", "DeviceName", (string?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        var sql = $"SELECT COUNT(1) FROM \"{table}\" WHERE \"IsDeleted\" = FALSE AND LOWER(\"{column}\") = @Code" +
                  (parentColumn is null ? string.Empty : $" AND \"{parentColumn}\" = @ParentId") +
                  (excludeId.HasValue ? " AND \"Id\" <> @ExcludeId" : string.Empty);
        var parameters = new List<SugarParameter> { new("@Code", normalized) };
        if (parentColumn is not null)
        {
            parameters.Add(new SugarParameter("@ParentId", parentId));
        }
        if (excludeId.HasValue)
        {
            parameters.Add(new SugarParameter("@ExcludeId", excludeId.Value));
        }
        var count = await db.Ado.SqlQuerySingleAsync<long>(sql, parameters.ToArray()).WaitAsync(cancellationToken);
        return count > 0;
    }

    public async Task<long> CreateMasterAsync(LineUpMasterWrite write, CancellationToken cancellationToken)
    {
        // 仅 Process/WorkStation/SubStation 必须有已校验父级；
        // ProductionLine 是顶层、Device 的所属产线可选，二者允许无父级（否则创建顶层记录会 500）
        var parent = write.Level is "Process" or "WorkStation" or "SubStation"
            ? RequireParent(write)
            : write.Parent;
        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var now = DateTime.Now;
        switch (write.Level)
        {
            case "ProductionLine":
                await db.Ado.ExecuteCommandAsync(
                    "INSERT INTO \"Basic_ProductionLine\" (\"Id\",\"Code\",\"Name\",\"Remark\",\"IsEnable\",\"IsDeleted\",\"CreaterId\",\"CreateTime\") " +
                    "VALUES (@Id,@Code,@Name,@Remark,@Enable,FALSE,0,@Now)",
                    new SugarParameter("@Id", id),
                    new SugarParameter("@Code", write.Code),
                    new SugarParameter("@Name", write.Name),
                    new SugarParameter("@Remark", write.Remark ?? (object)DBNull.Value),
                    new SugarParameter("@Enable", write.IsEnabled),
                    new SugarParameter("@Now", now)).WaitAsync(cancellationToken);
                break;
            case "Process":
                await db.Ado.ExecuteCommandAsync(
                    "INSERT INTO \"Basic_Process\" (\"Id\",\"Code\",\"Name\",\"ProductionLineId\",\"Remark\",\"IsEnable\",\"IsDeleted\",\"CreaterId\",\"CreateTime\") " +
                    "VALUES (@Id,@Code,@Name,@LineId,@Remark,@Enable,FALSE,0,@Now)",
                    new SugarParameter("@Id", id),
                    new SugarParameter("@Code", write.Code),
                    new SugarParameter("@Name", write.Name),
                    new SugarParameter("@LineId", parent.LineId),
                    new SugarParameter("@Remark", write.Remark ?? (object)DBNull.Value),
                    new SugarParameter("@Enable", write.IsEnabled),
                    new SugarParameter("@Now", now)).WaitAsync(cancellationToken);
                break;
            case "WorkStation":
                await db.Ado.ExecuteCommandAsync(
                    "INSERT INTO \"Basic_WorkStation\" (\"Id\",\"Code\",\"Name\",\"ProcessId\",\"ProductionLineId\",\"Remark\",\"IsEnable\",\"IsDeleted\",\"CreaterId\",\"CreateTime\") " +
                    "VALUES (@Id,@Code,@Name,@ProcessId,@LineId,@Remark,@Enable,FALSE,0,@Now)",
                    new SugarParameter("@Id", id),
                    new SugarParameter("@Code", write.Code),
                    new SugarParameter("@Name", write.Name),
                    new SugarParameter("@ProcessId", parent.ProcessId!.Value),
                    new SugarParameter("@LineId", parent.LineId),
                    new SugarParameter("@Remark", write.Remark ?? (object)DBNull.Value),
                    new SugarParameter("@Enable", write.IsEnabled),
                    new SugarParameter("@Now", now)).WaitAsync(cancellationToken);
                break;
            case "SubStation":
            {
                // PlcName 由 PlcID 反查 DeviceName 回填（StationPlcScopeService 工位白名单按 PlcName 匹配）
                var plcName = await GetDeviceNameAsync(write.PlcId, cancellationToken);
                await db.Ado.ExecuteCommandAsync(
                    "INSERT INTO \"Basic_SubStation\" (\"Id\",\"ProcessID\",\"WorkStationID\",\"ProductionLineID\",\"PlcID\",\"PlcName\",\"SubStationCode\",\"SubStationName\",\"IsDeleted\",\"CreaterId\",\"CreateTime\") " +
                    "VALUES (@Id,@ProcessId,@StationId,@LineId,@PlcId,@PlcName,@Code,@Name,FALSE,0,@Now)",
                    new SugarParameter("@Id", id),
                    new SugarParameter("@ProcessId", parent.ProcessId!.Value),
                    new SugarParameter("@StationId", parent.WorkStationId!.Value),
                    new SugarParameter("@LineId", parent.LineId),
                    new SugarParameter("@PlcId", write.PlcId ?? 0),
                    new SugarParameter("@PlcName", plcName ?? (object)DBNull.Value),
                    new SugarParameter("@Code", write.Code),
                    new SugarParameter("@Name", write.Name),
                    new SugarParameter("@Now", now)).WaitAsync(cancellationToken);
                break;
            }
            case "Device":
            {
                // LineID 为空时不能传 DBNull（SqlSugar 会按 text 发送，PG bigint 列报 42804），直接省略该列
                var columns = "\"Id\",\"DeviceName\",\"ProjectName\",\"IpAddress\",\"LineID\",\"Remark\",\"IsDeleted\",\"CreaterId\",\"CreateTime\"";
                var values = "@Id,@Code,@Name,@IpAddress,@LineId,@Remark,FALSE,0,@Now";
                var deviceParams = new List<SugarParameter>
                {
                    new("@Id", id),
                    new("@Code", write.Code),
                    new("@Name", write.Name.Length == 0 ? (object)DBNull.Value : write.Name),
                    new("@IpAddress", write.IpAddress ?? (object)DBNull.Value),
                    new("@Remark", write.Remark ?? (object)DBNull.Value),
                    new("@Now", now),
                };
                if (write.LineId is > 0)
                {
                    deviceParams.Add(new SugarParameter("@LineId", write.LineId.Value));
                }
                else
                {
                    columns = columns.Replace(",\"LineID\"", string.Empty);
                    values = values.Replace(",@LineId", string.Empty);
                }
                await db.Ado.ExecuteCommandAsync(
                    $"INSERT INTO \"Basic_Device\" ({columns}) VALUES ({values})",
                    deviceParams.ToArray()).WaitAsync(cancellationToken);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(write.Level));
        }
        return id;
    }

    public async Task<bool> UpdateMasterAsync(LineUpMasterWrite write, CancellationToken cancellationToken)
    {
        // 写路径全部手写 SQL：这些 PascalCase 表上 SqlSugar Updateable 的 GetDiffTable 会生成
        // 空引号标识符（PG 42601 zero-length delimited identifier），且 bigint 列不能收 DBNull 参数
        var id = write.Id!.Value;
        var now = DateTime.Now;
        var affected = write.Level switch
        {
            "ProductionLine" => await db.Ado.ExecuteCommandAsync(
                "UPDATE \"Basic_ProductionLine\" SET \"Code\"=@Code,\"Name\"=@Name,\"Remark\"=@Remark,\"IsEnable\"=@Enable,\"ModifyTime\"=@Now " +
                "WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
                new SugarParameter("@Code", write.Code),
                new SugarParameter("@Name", write.Name),
                new SugarParameter("@Remark", write.Remark ?? (object)DBNull.Value),
                new SugarParameter("@Enable", write.IsEnabled),
                new SugarParameter("@Now", now),
                new SugarParameter("@Id", id)).WaitAsync(cancellationToken),
            "Process" => await UpdateProcessAsync(write, id, now, cancellationToken),
            "WorkStation" => await UpdateWorkStationAsync(write, id, now, cancellationToken),
            "SubStation" => await UpdateSubStationAsync(write, id, now, cancellationToken),
            "Device" => await UpdateDeviceAsync(write, id, now, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Level)),
        };
        return affected == 1;
    }

    public async Task<bool> SoftDeleteMasterAsync(string level, long id, CancellationToken cancellationToken)
    {
        var hasChildren = level switch
        {
            "ProductionLine" => await db.Queryable<LineUpProcessEntity>().Where(x => !x.IsDeleted && x.ProductionLineId == id).AnyAsync().WaitAsync(cancellationToken),
            "Process" => await db.Queryable<LineUpWorkStationEntity>().Where(x => !x.IsDeleted && x.ProcessId == id).AnyAsync().WaitAsync(cancellationToken),
            "WorkStation" => await db.Queryable<LineUpSubStationEntity>().Where(x => !x.IsDeleted && x.WorkStationID == id).AnyAsync().WaitAsync(cancellationToken),
            "SubStation" => await db.Queryable<LineUpActionEntity>().Where(x => !x.IsDeleted && x.SubStationId == id).AnyAsync().WaitAsync(cancellationToken),
            // PLC 设备被子工位（PlcID）或 PLC 项目绑定（ai_plc_project_new.plcdeviceid）引用时禁止删除
            "Device" => await db.Queryable<LineUpSubStationEntity>().Where(x => !x.IsDeleted && x.PlcID == id).AnyAsync().WaitAsync(cancellationToken)
                || await db.Queryable<PLC_ProjectEntity>().Where(x => !x.IsDeleted && x.PlcDeviceId == id).AnyAsync().WaitAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        if (hasChildren) return false;

        var table = level switch
        {
            "ProductionLine" => "Basic_ProductionLine",
            "Process" => "Basic_Process",
            "WorkStation" => "Basic_WorkStation",
            "SubStation" => "Basic_SubStation",
            "Device" => "Basic_Device",
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        var affected = await db.Ado.ExecuteCommandAsync(
            $"UPDATE \"{table}\" SET \"IsDeleted\"=TRUE,\"ModifyTime\"=@Now WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
            new SugarParameter("@Now", DateTime.Now),
            new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// 删除前置检查：存在性 + 下级/关联引用计数。
    /// App 层据此把「有下级」(LineUpMasterHasChildren/409) 与「不存在」(LineUpRecordNotFound/404) 区分。
    /// </summary>
    public async Task<LineUpMasterDeleteCheck> GetMasterDeleteCheckAsync(string level, long id, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case "ProductionLine":
            {
                var exists = await db.Queryable<LineUpProductionLineEntity>().Where(x => !x.IsDeleted && x.Id == id).AnyAsync().WaitAsync(cancellationToken);
                var count = await db.Queryable<LineUpProcessEntity>().Where(x => !x.IsDeleted && x.ProductionLineId == id).CountAsync().WaitAsync(cancellationToken);
                return new(exists, count, "工序");
            }
            case "Process":
            {
                var exists = await db.Queryable<LineUpProcessEntity>().Where(x => !x.IsDeleted && x.Id == id).AnyAsync().WaitAsync(cancellationToken);
                var count = await db.Queryable<LineUpWorkStationEntity>().Where(x => !x.IsDeleted && x.ProcessId == id).CountAsync().WaitAsync(cancellationToken);
                return new(exists, count, "工站");
            }
            case "WorkStation":
            {
                var exists = await db.Queryable<LineUpWorkStationEntity>().Where(x => !x.IsDeleted && x.Id == id).AnyAsync().WaitAsync(cancellationToken);
                var count = await db.Queryable<LineUpSubStationEntity>().Where(x => !x.IsDeleted && x.WorkStationID == id).CountAsync().WaitAsync(cancellationToken);
                return new(exists, count, "子工站");
            }
            case "SubStation":
            {
                var exists = await db.Queryable<LineUpSubStationEntity>().Where(x => !x.IsDeleted && x.Id == id).AnyAsync().WaitAsync(cancellationToken);
                var count = await db.Queryable<LineUpActionEntity>().Where(x => !x.IsDeleted && x.SubStationId == id).CountAsync().WaitAsync(cancellationToken);
                return new(exists, count, "动作");
            }
            case "Device":
            {
                var exists = await db.Queryable<LineUpDeviceEntity>().Where(x => !x.IsDeleted && x.Id == id).AnyAsync().WaitAsync(cancellationToken);
                var stationCount = await db.Queryable<LineUpSubStationEntity>().Where(x => !x.IsDeleted && x.PlcID == id).CountAsync().WaitAsync(cancellationToken);
                var projectCount = await db.Queryable<PLC_ProjectEntity>().Where(x => !x.IsDeleted && x.PlcDeviceId == id).CountAsync().WaitAsync(cancellationToken);
                return new(exists, stationCount + projectCount, "子工站/PLC项目绑定");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(level));
        }
    }

    /// <summary>
    /// 级联软删层级基础信息（自身 + 全部下级 + 动作业务配置）。Device 不走此路径（引用保护，由 App 层拦截）。
    /// 全部走 Updateable/Queryable 泛型（本仓储对 Basic_* 表的已验证路径）。
    /// </summary>
    public async Task<int> CascadeSoftDeleteMasterAsync(string level, long id, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        switch (level)
        {
            case "ProductionLine":
            {
                var processIds = (await db.Queryable<LineUpProcessEntity>()
                    .Where(x => !x.IsDeleted && x.ProductionLineId == id).Select(x => x.Id)
                    .ToListAsync(cancellationToken)).ToArray();
                var affected = await CascadeDeleteProcessesAsync(processIds, now, cancellationToken);
                affected += await db.Ado.ExecuteCommandAsync(
                    "UPDATE \"Basic_ProductionLine\" SET \"IsDeleted\"=TRUE,\"ModifyTime\"=@Now WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
                    new SugarParameter("@Now", now), new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
                return affected;
            }
            case "Process":
            {
                var wsIds = (await db.Queryable<LineUpWorkStationEntity>()
                    .Where(x => !x.IsDeleted && x.ProcessId == id).Select(x => x.Id)
                    .ToListAsync(cancellationToken)).ToArray();
                var affected = await CascadeDeleteWorkStationsAsync(wsIds, now, cancellationToken);
                affected += await db.Ado.ExecuteCommandAsync(
                    "UPDATE \"Basic_Process\" SET \"IsDeleted\"=TRUE,\"ModifyTime\"=@Now WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
                    new SugarParameter("@Now", now), new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
                return affected;
            }
            case "WorkStation":
            {
                var ssIds = (await db.Queryable<LineUpSubStationEntity>()
                    .Where(x => !x.IsDeleted && x.WorkStationID == id).Select(x => x.Id)
                    .ToListAsync(cancellationToken)).ToArray();
                var affected = await CascadeDeleteSubStationsAsync(ssIds, now, cancellationToken);
                affected += await db.Ado.ExecuteCommandAsync(
                    "UPDATE \"Basic_WorkStation\" SET \"IsDeleted\"=TRUE,\"ModifyTime\"=@Now WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
                    new SugarParameter("@Now", now), new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
                return affected;
            }
            case "SubStation":
            {
                var ssIds = new[] { id };
                return await CascadeDeleteSubStationsAsync(ssIds, now, cancellationToken);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(level));
        }
    }

    private async Task<int> CascadeDeleteProcessesAsync(long[] processIds, DateTime now, CancellationToken cancellationToken)
    {
        if (processIds.Length == 0) return 0;
        // id 收集走手写 SQL（SqlSugar lambda 对 long[].Contains 解析会抛 op_Implicit 不支持）
        var wsIds = await SelectAliveIdsAsync("Basic_WorkStation", "ProcessId", processIds, cancellationToken);
        var affected = await CascadeDeleteWorkStationsAsync(wsIds, now, cancellationToken);
        affected += await SoftDeleteRowsByIdsAsync("Basic_Process", "Id", processIds, now, cancellationToken);
        return affected;
    }

    private async Task<int> CascadeDeleteWorkStationsAsync(long[] wsIds, DateTime now, CancellationToken cancellationToken)
    {
        if (wsIds.Length == 0) return 0;
        var ssIds = await SelectAliveIdsAsync("Basic_SubStation", "WorkStationID", wsIds, cancellationToken);
        var affected = await CascadeDeleteSubStationsAsync(ssIds, now, cancellationToken);
        affected += await SoftDeleteRowsByIdsAsync("Basic_WorkStation", "Id", wsIds, now, cancellationToken);
        return affected;
    }

    private async Task<int> CascadeDeleteSubStationsAsync(long[] ssIds, DateTime now, CancellationToken cancellationToken)
    {
        if (ssIds.Length == 0) return 0;
        var actionIds = await SelectAliveIdsAsync("SubStation_Actions", "SubStationId", ssIds, cancellationToken);
        var affected = await CascadeDeleteActionsAsync(actionIds, now, cancellationToken);
        affected += await SoftDeleteRowsByIdsAsync("Basic_SubStation", "Id", ssIds, now, cancellationToken);
        return affected;
    }

    private async Task<int> CascadeDeleteActionsAsync(long[] actionIds, DateTime now, CancellationToken cancellationToken)
    {
        if (actionIds.Length == 0) return 0;
        var affected = 0;
        // 执行条件（SubStation_OtherTagInfos.StationActionId）
        affected += await SoftDeleteRowsByIdsAsync("SubStation_OtherTagInfos", "StationActionId", actionIds, now, cancellationToken);
        // 异常解决方案（SubStation_ActionErrSolutions.ActionId）
        affected += await SoftDeleteRowsByIdsAsync("SubStation_ActionErrSolutions", "ActionId", actionIds, now, cancellationToken);
        // 动作步骤（SubStation_ActionSteps.StationActionId）
        affected += await SoftDeleteRowsByIdsAsync("SubStation_ActionSteps", "StationActionId", actionIds, now, cancellationToken);
        // 动作自身
        affected += await SoftDeleteRowsByIdsAsync("SubStation_Actions", "Id", actionIds, now, cancellationToken);
        return affected;
    }

    /// <summary>按 id 集合软删（手写 SQL，动态 IN 参数；table/column 仅来自本类白名单常量）。</summary>
    private async Task<int> SoftDeleteRowsByIdsAsync(string table, string column, long[] ids, DateTime now, CancellationToken cancellationToken)
    {
        if (ids.Length == 0) return 0;
        var parameters = ids.Select((v, i) => new SugarParameter($"@Id{i}", v)).ToList();
        parameters.Add(new SugarParameter("@Now", now));
        var inList = string.Join(",", ids.Select((_, i) => $"@Id{i}"));
        return await db.Ado.ExecuteCommandAsync(
            $"UPDATE \"{table}\" SET \"IsDeleted\"=TRUE,\"ModifyTime\"=@Now WHERE \"IsDeleted\"=FALSE AND \"{column}\" IN ({inList})",
            parameters.ToArray()).WaitAsync(cancellationToken);
    }

    /// <summary>查询未删除的 id 集合（手写 SQL 动态 IN；table/column 仅来自本类白名单常量）。</summary>
    private async Task<long[]> SelectAliveIdsAsync(string table, string column, long[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0) return Array.Empty<long>();
        var parameters = ids.Select((v, i) => new SugarParameter($"@Id{i}", v)).ToArray();
        var inList = string.Join(",", ids.Select((_, i) => $"@Id{i}"));
        var rows = await db.Ado.SqlQueryAsync<long>(
            $"SELECT \"Id\" FROM \"{table}\" WHERE \"IsDeleted\"=FALSE AND \"{column}\" IN ({inList})",
            parameters).WaitAsync(cancellationToken);
        return rows.ToArray();
    }

    public Task<LineUpBusinessPageData> QueryBusinessAsync(LineUpBusinessQuery query, CancellationToken cancellationToken) =>
        query.Kind switch
        {
            "Action" => QueryActionsAsync(query, cancellationToken),
            "Condition" => QueryConditionsAsync(query, cancellationToken),
            "Solution" => QuerySolutionsAsync(query, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query.Kind)),
        };

    public async Task<LineUpBusinessParentLookup?> GetBusinessParentAsync(
        string kind,
        long parentId,
        CancellationToken cancellationToken)
    {
        if (kind == "Action")
        {
            var rows = await db.Queryable<LineUpSubStationEntity>()
                .Where(x => !x.IsDeleted && x.Id == parentId)
                .Select(x => new { x.Id }).ToListAsync(cancellationToken);
            return rows.Count == 1 ? new(rows[0].Id, "SubStation", rows[0].Id, null) : null;
        }

        var actions = await db.Queryable<LineUpActionEntity>()
            .Where(x => !x.IsDeleted && x.Id == parentId && x.SubStationId != null)
            .Select(x => new { x.Id, x.SubStationId }).ToListAsync(cancellationToken);
        return actions.Count == 1
            ? new(actions[0].Id, "Action", actions[0].SubStationId!.Value, actions[0].Id)
            : null;
    }

    public async Task<bool> BusinessCodeExistsAsync(
        string kind,
        long parentId,
        string code,
        long? excludeId,
        CancellationToken cancellationToken)
    {
        var normalized = code.ToLower();
        return kind switch
        {
            "Action" => await db.Queryable<LineUpActionEntity>()
                .Where(x => !x.IsDeleted && x.SubStationId == parentId && x.ActionCode != null && x.ActionCode.ToLower() == normalized && (!excludeId.HasValue || x.Id != excludeId.Value))
                .AnyAsync().WaitAsync(cancellationToken),
            "Condition" => await db.Queryable<LineUpOtherTagInfoEntity>()
                .Where(x => !x.IsDeleted && x.StationActionId == parentId && x.TagCode != null && x.TagCode.ToLower() == normalized && (!excludeId.HasValue || x.Id != excludeId.Value))
                .AnyAsync().WaitAsync(cancellationToken),
            "Solution" => await db.Queryable<LineUpActionErrSolutionEntity>()
                .Where(x => !x.IsDeleted && x.ActionId == parentId && x.ActionName != null && x.ActionName.ToLower() == normalized && (!excludeId.HasValue || x.Id != excludeId.Value))
                .AnyAsync().WaitAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public async Task<long> CreateBusinessAsync(LineUpBusinessWrite write, CancellationToken cancellationToken)
    {
        switch (write.Kind)
        {
            case "Action":
            {
                var entity = new LineUpActionEntity
                {
                    SubStationId = write.SubStationId, ActionCode = write.Code, ActionName = write.Name,
                    Remark = write.Value, CreateTime = DateTime.Now,
                };
                await db.Insertable(entity).ExecuteCommandAsync().WaitAsync(cancellationToken);
                return entity.Id;
            }
            case "Condition":
            {
                var entity = new LineUpOtherTagInfoEntity
                {
                    SubStationId = write.SubStationId, StationActionId = write.ParentId,
                    TagCode = write.Code, Details = write.Name, TagDBAdress = write.Value,
                    Status = write.IsEnabled, CreateTime = DateTime.Now,
                };
                await db.Insertable(entity).ExecuteCommandAsync().WaitAsync(cancellationToken);
                return entity.Id;
            }
            case "Solution":
            {
                var entity = new LineUpActionErrSolutionEntity
                {
                    ActionId = write.ParentId, ActionName = write.Code,
                    ErrSolutions = write.Name, Remark = write.Value, CreateTime = DateTime.Now,
                };
                await db.Insertable(entity).ExecuteCommandAsync().WaitAsync(cancellationToken);
                return entity.Id;
            }
            default: throw new ArgumentOutOfRangeException(nameof(write.Kind));
        }
    }

    public async Task<bool> UpdateBusinessAsync(LineUpBusinessWrite write, CancellationToken cancellationToken)
    {
        var id = write.Id!.Value;
        var affected = write.Kind switch
        {
            "Action" => await db.Updateable<LineUpActionEntity>()
                .SetColumns(x => new LineUpActionEntity { SubStationId = write.SubStationId, ActionCode = write.Code, ActionName = write.Name, Remark = write.Value, ModifyTime = DateTime.Now })
                .Where(x => x.Id == id && !x.IsDeleted).ExecuteCommandAsync().WaitAsync(cancellationToken),
            "Condition" => await db.Updateable<LineUpOtherTagInfoEntity>()
                .SetColumns(x => new LineUpOtherTagInfoEntity { SubStationId = write.SubStationId, StationActionId = write.ParentId, TagCode = write.Code, Details = write.Name, TagDBAdress = write.Value, Status = write.IsEnabled, ModifyTime = DateTime.Now })
                .Where(x => x.Id == id && !x.IsDeleted).ExecuteCommandAsync().WaitAsync(cancellationToken),
            "Solution" => await db.Updateable<LineUpActionErrSolutionEntity>()
                .SetColumns(x => new LineUpActionErrSolutionEntity { ActionId = write.ParentId, ActionName = write.Code, ErrSolutions = write.Name, Remark = write.Value, ModifyTime = DateTime.Now })
                .Where(x => x.Id == id && !x.IsDeleted).ExecuteCommandAsync().WaitAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Kind)),
        };
        return affected == 1;
    }

    public async Task<bool> SoftDeleteBusinessAsync(string kind, long id, CancellationToken cancellationToken)
    {
        if (kind == "Action")
        {
            var hasChildren = await db.Queryable<LineUpOtherTagInfoEntity>()
                .Where(x => !x.IsDeleted && x.StationActionId == id).AnyAsync().WaitAsync(cancellationToken) ||
                await db.Queryable<LineUpActionErrSolutionEntity>()
                    .Where(x => !x.IsDeleted && x.ActionId == id).AnyAsync().WaitAsync(cancellationToken);
            if (hasChildren) return false;
        }
        var affected = kind switch
        {
            "Action" => await db.Updateable<LineUpActionEntity>()
                .SetColumns(x => new LineUpActionEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                .Where(x => x.Id == id && !x.IsDeleted).ExecuteCommandAsync().WaitAsync(cancellationToken),
            "Condition" => await db.Updateable<LineUpOtherTagInfoEntity>()
                .SetColumns(x => new LineUpOtherTagInfoEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                .Where(x => x.Id == id && !x.IsDeleted).ExecuteCommandAsync().WaitAsync(cancellationToken),
            "Solution" => await db.Updateable<LineUpActionErrSolutionEntity>()
                .SetColumns(x => new LineUpActionErrSolutionEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                .Where(x => x.Id == id && !x.IsDeleted).ExecuteCommandAsync().WaitAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return affected == 1;
    }

    private async Task<(long Total, MasterRow[] Rows)> QueryMasterRowsAsync(
        string table,
        string selectList,
        string where,
        string orderBy,
        LineUpMasterQuery query,
        SugarParameter[] args,
        CancellationToken cancellationToken)
    {
        var total = await db.Ado.SqlQuerySingleAsync<long>(
            $"SELECT COUNT(1) FROM \"{table}\" WHERE {where}",
            args).WaitAsync(cancellationToken);
        var pageArgs = args
            .Select(x => new SugarParameter(x.ParameterName, x.Value))
            .ToList();
        pageArgs.Add(new SugarParameter("@__lim", query.PageSize));
        pageArgs.Add(new SugarParameter("@__off", Offset(query)));
        var rows = await db.Ado.SqlQueryAsync<MasterRow>(
            $"SELECT {selectList} FROM \"{table}\" WHERE {where} ORDER BY {orderBy} LIMIT @__lim OFFSET @__off",
            pageArgs.ToArray()).WaitAsync(cancellationToken);
        return new(total, rows.ToArray());
    }

    private static SugarParameter[] KeywordArgs(LineUpMasterQuery query, string codeColumn, string nameColumn)
    {
        return query.Keyword is null
            ? Array.Empty<SugarParameter>()
            : new SugarParameter[] { new("@__kw", "%" + query.Keyword + "%") };
    }

    private static string KeywordWhere(LineUpMasterQuery query, string codeColumn, string nameColumn) =>
        query.Keyword is null
            ? string.Empty
            : $" AND (\"{codeColumn}\" LIKE @__kw OR \"{nameColumn}\" LIKE @__kw)";

    private async Task<LineUpMasterPageData> QueryLinesAsync(LineUpMasterQuery query, CancellationToken cancellationToken)
    {
        var where = "\"IsDeleted\" = FALSE" + KeywordWhere(query, "Code", "Name");
        var (total, rows) = await QueryMasterRowsAsync(
            "Basic_ProductionLine",
            "\"Id\", \"Code\" AS \"Code\", \"Name\" AS \"Name\", \"IsEnable\", \"Remark\"",
            where, "\"Code\", \"Id\"", query, KeywordArgs(query, "Code", "Name"), cancellationToken);
        return new(total, rows.Select(x => new LineUpMasterItemData(x.Id, "ProductionLine", null, x.Id, x.Code, x.Name, x.IsEnable == true, null, null, null, x.Remark)).ToArray());
    }

    private async Task<LineUpMasterPageData> QueryProcessesAsync(LineUpMasterQuery query, CancellationToken cancellationToken)
    {
        var where = "\"IsDeleted\" = FALSE AND \"ProductionLineId\" = @__parent" + KeywordWhere(query, "Code", "Name");
        var args = new List<SugarParameter>(KeywordArgs(query, "Code", "Name")) { new("@__parent", query.ParentId!.Value) };
        var (total, rows) = await QueryMasterRowsAsync(
            "Basic_Process",
            "\"Id\", \"ProductionLineId\", \"Code\" AS \"Code\", \"Name\" AS \"Name\", \"IsEnable\", \"Remark\"",
            where, "\"Code\", \"Id\"", query, args.ToArray(), cancellationToken);
        return new(total, rows.Select(x => new LineUpMasterItemData(x.Id, "Process", x.ProductionLineId, x.ProductionLineId, x.Code, x.Name, x.IsEnable == true, null, null, null, x.Remark)).ToArray());
    }

    private async Task<LineUpMasterPageData> QueryWorkStationsAsync(LineUpMasterQuery query, CancellationToken cancellationToken)
    {
        var where = "\"IsDeleted\" = FALSE AND \"ProcessId\" = @__parent" + KeywordWhere(query, "Code", "Name");
        var args = new List<SugarParameter>(KeywordArgs(query, "Code", "Name")) { new("@__parent", query.ParentId!.Value) };
        var (total, rows) = await QueryMasterRowsAsync(
            "Basic_WorkStation",
            "\"Id\", \"ProcessId\", \"ProductionLineId\", \"Code\" AS \"Code\", \"Name\" AS \"Name\", \"IsEnable\", \"Remark\"",
            where, "\"Code\", \"Id\"", query, args.ToArray(), cancellationToken);
        return new(total, rows.Select(x => new LineUpMasterItemData(x.Id, "WorkStation", x.ProcessId, x.ProductionLineId, x.Code, x.Name, x.IsEnable == true, null, null, null, x.Remark)).ToArray());
    }

    private async Task<LineUpMasterPageData> QuerySubStationsAsync(LineUpMasterQuery query, CancellationToken cancellationToken)
    {
        var where = "\"IsDeleted\" = FALSE AND \"WorkStationID\" = @__parent" + KeywordWhere(query, "SubStationCode", "SubStationName");
        var args = new List<SugarParameter>(KeywordArgs(query, "SubStationCode", "SubStationName")) { new("@__parent", query.ParentId!.Value) };
        var (total, rows) = await QueryMasterRowsAsync(
            "Basic_SubStation",
            "\"Id\", \"WorkStationID\" AS \"WorkStationId\", \"ProductionLineID\" AS \"ProductionLineId\", \"PlcID\" AS \"PlcId\", \"PlcName\", \"SubStationCode\" AS \"Code\", \"SubStationName\" AS \"Name\"",
            where, "\"SortNum\", \"SubStationCode\", \"Id\"", query, args.ToArray(), cancellationToken);
        return new(total, rows.Select(x => new LineUpMasterItemData(x.Id, "SubStation", x.WorkStationId, x.ProductionLineId, x.Code, x.Name, true, x.PlcId, x.PlcName, null, null)).ToArray());
    }

    private async Task<LineUpMasterPageData> QueryDevicesAsync(LineUpMasterQuery query, CancellationToken cancellationToken)
    {
        // parentId 语义为产线过滤（Basic_Device.LineID），可选；keyword 匹配设备名/项目名/IP
        var where = "\"IsDeleted\" = FALSE"
            + (query.ParentId is > 0 ? " AND \"LineID\" = @__parent" : string.Empty)
            + DeviceKeywordWhere(query);
        var args = new List<SugarParameter>();
        if (query.ParentId is > 0)
        {
            args.Add(new SugarParameter("@__parent", query.ParentId.Value));
        }
        if (query.Keyword is not null)
        {
            args.Add(new SugarParameter("@__kw", "%" + query.Keyword + "%"));
        }
        var (total, rows) = await QueryMasterRowsAsync(
            "Basic_Device",
            "\"Id\", \"LineID\" AS \"LineId\", \"DeviceName\" AS \"Code\", \"ProjectName\" AS \"Name\", \"IpAddress\" AS \"IpAddress\", \"Remark\"",
            where, "\"DeviceName\", \"Id\"", query, args.ToArray(), cancellationToken);
        return new(total, rows.Select(x => new LineUpMasterItemData(x.Id, "Device", null, x.LineId, x.Code, x.Name, true, null, null, x.Name, x.Remark, x.IpAddress)).ToArray());
    }

    private static string DeviceKeywordWhere(LineUpMasterQuery query) =>
        query.Keyword is null
            ? string.Empty
            : " AND (\"DeviceName\" LIKE @__kw OR \"ProjectName\" LIKE @__kw OR \"IpAddress\" LIKE @__kw)";

    private async Task<LineUpBusinessPageData> QueryActionsAsync(LineUpBusinessQuery query, CancellationToken cancellationToken)
    {
        var q = db.Queryable<LineUpActionEntity>().Where(x => !x.IsDeleted && x.SubStationId == query.ParentId)
            .WhereIF(query.Keyword != null, x => (x.ActionCode != null && x.ActionCode.Contains(query.Keyword!)) || (x.ActionName != null && x.ActionName.Contains(query.Keyword!)));
        var total = await q.CountAsync().WaitAsync(cancellationToken);
        var rows = await q.OrderBy(x => x.ActionNum).OrderBy(x => x.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(x => new LineUpBusinessItemData(x.Id, "Action", query.ParentId, x.SubStationId ?? 0, x.ActionCode, x.ActionName, x.Remark, true))
            .ToListAsync(cancellationToken);
        return new(total, rows);
    }

    private async Task<LineUpBusinessPageData> QueryConditionsAsync(LineUpBusinessQuery query, CancellationToken cancellationToken)
    {
        var q = db.Queryable<LineUpOtherTagInfoEntity>().Where(x => !x.IsDeleted && x.StationActionId == query.ParentId)
            .WhereIF(query.Keyword != null, x => (x.TagCode != null && x.TagCode.Contains(query.Keyword!)) || (x.Details != null && x.Details.Contains(query.Keyword!)));
        var total = await q.CountAsync().WaitAsync(cancellationToken);
        var rows = await q.OrderBy(x => x.TagCode).OrderBy(x => x.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(x => new LineUpBusinessItemData(x.Id, "Condition", query.ParentId, x.SubStationId ?? 0, x.TagCode, x.Details, x.TagDBAdress, x.Status))
            .ToListAsync(cancellationToken);
        return new(total, rows);
    }

    private async Task<LineUpBusinessPageData> QuerySolutionsAsync(LineUpBusinessQuery query, CancellationToken cancellationToken)
    {
        var parent = await GetBusinessParentAsync("Solution", query.ParentId, cancellationToken);
        var subStationId = parent?.SubStationId ?? 0;
        var q = db.Queryable<LineUpActionErrSolutionEntity>().Where(x => !x.IsDeleted && x.ActionId == query.ParentId)
            .WhereIF(query.Keyword != null, x => (x.ActionName != null && x.ActionName.Contains(query.Keyword!)) || (x.ErrSolutions != null && x.ErrSolutions.Contains(query.Keyword!)));
        var total = await q.CountAsync().WaitAsync(cancellationToken);
        var rows = await q.OrderBy(x => x.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(x => new LineUpBusinessItemData(x.Id, "Solution", query.ParentId, subStationId, x.ActionName, x.ErrSolutions, x.Remark, true))
            .ToListAsync(cancellationToken);
        return new(total, rows);
    }

    private async Task<int> UpdateProcessAsync(LineUpMasterWrite write, long id, DateTime now, CancellationToken cancellationToken)
    {
        var parent = RequireParent(write);
        return await db.Ado.ExecuteCommandAsync(
            "UPDATE \"Basic_Process\" SET \"Code\"=@Code,\"Name\"=@Name,\"ProductionLineId\"=@LineId,\"Remark\"=@Remark,\"IsEnable\"=@Enable,\"ModifyTime\"=@Now " +
            "WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
            new SugarParameter("@Code", write.Code),
            new SugarParameter("@Name", write.Name),
            new SugarParameter("@LineId", parent.LineId),
            new SugarParameter("@Remark", write.Remark ?? (object)DBNull.Value),
            new SugarParameter("@Enable", write.IsEnabled),
            new SugarParameter("@Now", now),
            new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
    }

    private async Task<int> UpdateWorkStationAsync(LineUpMasterWrite write, long id, DateTime now, CancellationToken cancellationToken)
    {
        var parent = RequireParent(write);
        return await db.Ado.ExecuteCommandAsync(
            "UPDATE \"Basic_WorkStation\" SET \"Code\"=@Code,\"Name\"=@Name,\"ProcessId\"=@ProcessId,\"ProductionLineId\"=@LineId,\"Remark\"=@Remark,\"IsEnable\"=@Enable,\"ModifyTime\"=@Now " +
            "WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
            new SugarParameter("@Code", write.Code),
            new SugarParameter("@Name", write.Name),
            new SugarParameter("@ProcessId", parent.ProcessId!.Value),
            new SugarParameter("@LineId", parent.LineId),
            new SugarParameter("@Remark", write.Remark ?? (object)DBNull.Value),
            new SugarParameter("@Enable", write.IsEnabled),
            new SugarParameter("@Now", now),
            new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
    }

    private async Task<int> UpdateSubStationAsync(LineUpMasterWrite write, long id, DateTime now, CancellationToken cancellationToken)
    {
        var parent = RequireParent(write);
        // PlcName 随 PlcID 同步回填，保证工位白名单（StationPlcScopeService 按 PlcName 过滤）数据一致
        var plcName = await GetDeviceNameAsync(write.PlcId, cancellationToken);
        return await db.Ado.ExecuteCommandAsync(
            "UPDATE \"Basic_SubStation\" SET \"SubStationCode\"=@Code,\"SubStationName\"=@Name,\"WorkStationID\"=@StationId,\"ProcessID\"=@ProcessId,\"ProductionLineID\"=@LineId,\"PlcID\"=@PlcId,\"PlcName\"=@PlcName,\"ModifyTime\"=@Now " +
            "WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
            new SugarParameter("@Code", write.Code),
            new SugarParameter("@Name", write.Name),
            new SugarParameter("@StationId", parent.WorkStationId!.Value),
            new SugarParameter("@ProcessId", parent.ProcessId!.Value),
            new SugarParameter("@LineId", parent.LineId),
            new SugarParameter("@PlcId", write.PlcId ?? 0),
            new SugarParameter("@PlcName", plcName ?? (object)DBNull.Value),
            new SugarParameter("@Now", now),
            new SugarParameter("@Id", id)).WaitAsync(cancellationToken);
    }

    private async Task<int> UpdateDeviceAsync(LineUpMasterWrite write, long id, DateTime now, CancellationToken cancellationToken)
    {
        // LineID 为空时置 NULL 字面量（不能传 DBNull：SqlSugar 按 text 发送，PG bigint 列 42804）
        var lineIdSet = write.LineId is > 0
            ? "\"LineID\"=@LineId"
            : "\"LineID\"=NULL";
        var parameters = new List<SugarParameter>
        {
            new("@Code", write.Code),
            new("@Name", write.Name.Length == 0 ? (object)DBNull.Value : write.Name),
            new("@IpAddress", write.IpAddress ?? (object)DBNull.Value),
            new("@Remark", write.Remark ?? (object)DBNull.Value),
            new("@Now", now),
            new("@Id", id),
        };
        if (write.LineId is > 0)
        {
            parameters.Add(new SugarParameter("@LineId", write.LineId.Value));
        }
        return await db.Ado.ExecuteCommandAsync(
            $"UPDATE \"Basic_Device\" SET \"DeviceName\"=@Code,\"ProjectName\"=@Name,\"IpAddress\"=@IpAddress,{lineIdSet},\"Remark\"=@Remark,\"ModifyTime\"=@Now " +
            "WHERE \"Id\"=@Id AND \"IsDeleted\"=FALSE",
            parameters.ToArray()).WaitAsync(cancellationToken);
    }

    /// <summary>按 PlcID 反查 Basic_Device.DeviceName；PlcID 空/无效返回 null。</summary>
    private async Task<string?> GetDeviceNameAsync(long? plcId, CancellationToken cancellationToken)
    {
        if (plcId is not > 0)
        {
            return null;
        }
        var rows = await db.Ado.SqlQueryAsync<string>(
            "SELECT \"DeviceName\" FROM \"Basic_Device\" WHERE \"IsDeleted\" = FALSE AND \"Id\" = @Id",
            new SugarParameter("@Id", plcId.Value)).WaitAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    private static LineUpMasterParentLookup RequireParent(LineUpMasterWrite write) =>
        write.Parent ?? throw new InvalidOperationException("A validated parent is required.");

    private static int Offset(LineUpMasterQuery query) => (query.Page - 1) * query.PageSize;
    private static int Offset(LineUpBusinessQuery query) => (query.Page - 1) * query.PageSize;
}
