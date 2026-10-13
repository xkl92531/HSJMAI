using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.AppLayer.LineUp.Runtime;
using HSJM.AI.AppLayer.LineUp.Storage;
using HSJM.AI.AppLayer.LineUp.Resolution;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using Microsoft.AspNetCore.Http;
using SqlSugar;
using System.IO;
using System.Text;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpImportApp(
    ILineUpImportRepository repository,
    LineUpImportStorage storage,
    ILineUpImportJobScheduler scheduler,
    ISqlSugarClient db,
    LineUpRuntimeBindingApp runtimeBindingApp = null,
    LineUpAddressResolver addressResolver = null,
    LineUpHierarchyResolver hierarchyResolver = null,
    ILineUpHierarchyRepository hierarchyRepository = null,
    LineUpGraphDraftBuilder graphDraftBuilder = null,
    LineUpGraphDraftApp graphDraftApp = null) : ISelfScopedAutoInject
{
    /// <summary>
    /// 按产线 + 设备 + GRAPH 块名找最新一条可查看（PreviewReady 或已提交 Committed）批次，供「查看流程图」跳转直达预览。
    /// 块名存于 PreviewJson.Documents[0].BlockName（jsonb），用 PG jsonb 取路径精确匹配。
    /// </summary>
    public async Task<LineUpLatestPreviewDto?> FindLatestPreviewBatchAsync(
        long lineId,
        long deviceId,
        string blockName,
        CancellationToken cancellationToken)
    {
        if (lineId <= 0 || deviceId <= 0 || string.IsNullOrWhiteSpace(blockName))
        {
            return null;
        }

        // 批次表是 PG 大小写保留表：SqlSugar Queryable/record 投影都会炸（见 LineUpImportRepository 注释），
        // 照仓储已验证路径走手写 SQL + 无参构造 class 绑定；块名匹配用 PG jsonb 取路径。
        var rows = await db.Ado.SqlQueryAsync<LineUpLatestPreviewRow>(
            """
            SELECT "Id" AS "BatchId"
            FROM "AI_LineUp_ImportBatch"
            WHERE "IsDeleted" = FALSE AND "Status" IN ('PreviewReady', 'Committed')
              AND "LineId" = @LineId AND "DeviceId" = @DeviceId
              AND "PreviewJson" #>> '{Documents,0,BlockName}' = @BlockName
            ORDER BY "Id" DESC
            LIMIT 1
            """,
            new SugarParameter[] {
                new("@LineId", lineId),
                new("@DeviceId", deviceId),
                new("@BlockName", blockName),
            });
        return rows.Count == 1 ? new LineUpLatestPreviewDto(rows[0].BatchId) : null;
    }

    /// <summary>
    /// 层级改派：校验目标工序/工站/子工站在同产线同 PLC 下关系成立后，
    /// 更新既有结构绑定行（AI_LineUp_SubStationGraphBinding）的 ProcessId/StationId/SubStationId。
    /// 目标层级可传已有 Id，也可传新编码（Id&lt;=0 + New*Code）——新编码先按编码幂等复用既有行，
    /// 工序/工站无匹配时按编码新建（不修改既有基础层级数据）；子工站不再新建——
    /// 新编码仅在既有子工站中匹配，匹配不到直接报错（请先在主数据维护页维护子工站）。
    /// </summary>
    public async Task<LineUpHierarchyReassignReceipt> ReassignHierarchyBindingsAsync(
        LineUpHierarchyReassignRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LineId <= 0 || request.DeviceId <= 0)
        {
            throw ImportError("InvalidSelection", "The production line and PLC device are required.");
        }
        if (hierarchyRepository is null)
        {
            throw ImportError("ResolutionUnavailable", "The hierarchy lookup repository is unavailable.");
        }

        var updates = (request.Updates ?? []).Where(x => x is not null).ToArray();
        if (updates.Length == 0)
        {
            throw ImportError("HierarchyReassignEmpty", "At least one hierarchy reassignment is required.");
        }
        if (updates.GroupBy(x => x.SequenceSourceKey?.Trim(), StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            throw ImportError("HierarchyReassignDuplicate", "A Sequence appears more than once in the reassignment request.");
        }

        var context = await hierarchyRepository.LoadContextAsync(
            request.LineId, request.DeviceId, Array.Empty<string>(), cancellationToken);
        if (context.Line is null || context.Line.Id != request.LineId ||
            context.Device is null || context.Device.Id != request.DeviceId || context.Device.LineId != request.LineId)
        {
            throw ImportError("InvalidSelection", "The selected production line or PLC device is unavailable.");
        }

        var processes = context.Processes
            .Where(x => x.Id > 0 && x.LineId == request.LineId)
            .GroupBy(x => x.Id).Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single());
        var stations = context.Stations
            .Where(x => x.Id > 0 && x.LineId == request.LineId && processes.ContainsKey(x.ProcessId))
            .GroupBy(x => x.Id).Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single());
        var subStations = context.SubStations
            .Where(x => x.Id > 0 && x.LineId == request.LineId && x.PlcId == request.DeviceId &&
                        stations.TryGetValue(x.StationId, out var subStationRow) && subStationRow.ProcessId == x.ProcessId)
            .GroupBy(x => x.Id).Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single());

        // 每级解析：优先用已有 Id；Id<=0 时必须带新编码（1-64 字符）。
        // 新编码先与既有数据按编码（忽略大小写）匹配可幂等复用；无匹配则标记新建（Id=0 占位，Code 携带新编码）。
        // 层级约束：选择既有工站时其必须挂在目标工序下（工序新建时不可能）；子工站同理必须挂在目标工站下。
        var plans = new List<(LineUpHierarchyReassignItem Item, LineUpProcessLookup Process,
            LineUpStationLookup Station, long SubStationId, string? SubStationCode)>();

        foreach (var item in updates)
        {
            if (string.IsNullOrWhiteSpace(item.SequenceSourceKey))
            {
                throw ImportError("HierarchyReassignInvalid", "Each reassignment needs a Sequence key.");
            }

            // 改派深度由请求字段推断：只带工序=深度1（批量改工序，清空工站/子工站）；
            // 带工序+工站=深度2（批量改工站，清空子工站）；三级齐=深度3（改单顺控子工站）。
            var hasProcess = item.ProcessId > 0 || !string.IsNullOrWhiteSpace(item.NewProcessCode);
            var hasStation = item.StationId > 0 || !string.IsNullOrWhiteSpace(item.NewStationCode);
            var hasSubStation = item.SubStationId > 0 || !string.IsNullOrWhiteSpace(item.NewSubStationCode);
            if (!hasProcess)
            {
                throw ImportError("HierarchyReassignInvalid",
                    $"Sequence '{item.SequenceSourceKey}' needs a process selection or a new process code.");
            }

            if (hasSubStation && !hasStation)
            {
                throw ImportError("HierarchyReassignInvalid",
                    $"Sequence '{item.SequenceSourceKey}' cannot reassign a sub-station without a station.");
            }

            LineUpProcessLookup process;
            if (item.ProcessId > 0)
            {
                if (!processes.TryGetValue(item.ProcessId, out var existingProcess))
                {
                    throw ImportError("HierarchyRelationshipInvalid",
                        $"The target hierarchy for Sequence '{item.SequenceSourceKey}' is invalid for the selected line and PLC device.");
                }
                process = existingProcess;
            }
            else
            {
                var code = (item.NewProcessCode ?? string.Empty).Trim();
                if (code.Length == 0 || code.Length > 64)
                {
                    throw ImportError("HierarchyReassignInvalid",
                        $"Sequence '{item.SequenceSourceKey}' needs a valid new process code (1-64 chars).");
                }
                var matches = context.Processes
                    .Where(x => x.LineId == request.LineId && string.Equals(x.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length > 1)
                {
                    throw ImportError("HierarchyEnsureAmbiguous", $"The process code '{code}' matches more than one process.");
                }
                process = matches.Length == 1 ? matches[0] : new LineUpProcessLookup(0, request.LineId, code, code);
            }

            LineUpStationLookup? station = null;
            if (hasStation)
            {
                if (item.StationId > 0)
                {
                    if (!stations.TryGetValue(item.StationId, out var existingStation) ||
                        process.Id == 0 || existingStation.ProcessId != process.Id)
                    {
                        throw ImportError("HierarchyRelationshipInvalid",
                            $"The target hierarchy for Sequence '{item.SequenceSourceKey}' is invalid for the selected line and PLC device.");
                    }
                    station = existingStation;
                }
                else
                {
                    var code = (item.NewStationCode ?? string.Empty).Trim();
                    if (code.Length == 0 || code.Length > 64)
                    {
                        throw ImportError("HierarchyReassignInvalid",
                            $"Sequence '{item.SequenceSourceKey}' needs a valid new station code (1-64 chars).");
                    }
                    var matches = process.Id == 0
                        ? []
                        : context.Stations
                            .Where(x => x.LineId == request.LineId && x.ProcessId == process.Id &&
                                        string.Equals(x.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    if (matches.Length > 1)
                    {
                        throw ImportError("HierarchyEnsureAmbiguous", $"The station code '{code}' matches more than one station.");
                    }
                    station = matches.Length == 1
                        ? matches[0]
                        : new LineUpStationLookup(0, request.LineId, process.Id, code, code);
                }
            }

            long subStationId = 0;
            string? subStationCode = null;
            if (hasSubStation)
            {
                if (item.SubStationId > 0)
                {
                    if (!subStations.TryGetValue(item.SubStationId, out var existingSubStation) ||
                        station is null || station.Id == 0 || existingSubStation.StationId != station.Id)
                    {
                        throw ImportError("HierarchyRelationshipInvalid",
                            $"The target hierarchy for Sequence '{item.SequenceSourceKey}' is invalid for the selected line and PLC device.");
                    }
                    subStationId = item.SubStationId;
                }
                else
                {
                    subStationCode = (item.NewSubStationCode ?? string.Empty).Trim();
                    if (subStationCode.Length == 0 || subStationCode.Length > 64)
                    {
                        throw ImportError("HierarchyReassignInvalid",
                            $"Sequence '{item.SequenceSourceKey}' needs a valid new sub-station code (1-64 chars).");
                    }
                    var matches = station is null || station.Id == 0
                        ? []
                        : context.SubStations
                            .Where(x => x.LineId == request.LineId && x.StationId == station.Id && x.PlcId == request.DeviceId &&
                                        string.Equals(x.Code?.Trim(), subStationCode, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    if (matches.Length > 1)
                    {
                        throw ImportError("HierarchyEnsureAmbiguous", $"The sub-station code '{subStationCode}' matches more than one sub-station.");
                    }
                    if (matches.Length == 0)
                    {
                        // 子工站不再自动新建：新编码仅在既有子工站中匹配，匹配不到提示先补主数据
                        throw ImportError("HierarchySubStationMissing",
                            $"The sub-station '{subStationCode}' does not exist under the target station for the selected PLC; create it in master data first.");
                    }
                    subStationId = matches[0].Id;
                }
            }

            plans.Add((item, process, station, subStationId, subStationId == 0 ? subStationCode : null));
        }

        // 新建阶段：工序 → 工站（后一级引用前一级的真实 Id；同请求内同编码去重只建一次）。
        // 子工站不参与新建：上面的 NewSubStationCode 分支只在既有行中匹配，匹配不到已提前报错。
        var createdProcessCount = 0;
        var createdStationCount = 0;
        var createdSubStationCount = 0;
        var resolvedProcessIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var resolvedStationIds = new Dictionary<(long ProcessId, string Code), long>();

        foreach (var code in plans.Where(p => p.Process.Id == 0).Select(p => p.Process.Code!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ensured = await hierarchyRepository.EnsureProcessAsync(context.Line, code, code, cancellationToken);
            if (ensured <= 0)
            {
                throw ImportError("HierarchyEnsureAmbiguous", $"The process code '{code}' could not be ensured.");
            }
            resolvedProcessIds[code] = ensured;
            createdProcessCount++;
        }

        foreach (var key in plans.Where(p => p.Station is not null && p.Station.Id == 0)
                     .Select(p => (
                         Process: p.Process.Id > 0 ? p.Process : p.Process with { Id = resolvedProcessIds[p.Process.Code!] },
                         Code: p.Station!.Code!))
                     .Distinct())
        {
            var ensured = await hierarchyRepository.EnsureWorkStationAsync(
                context.Line, key.Process, key.Code, key.Code, cancellationToken);
            if (ensured <= 0)
            {
                throw ImportError("HierarchyEnsureAmbiguous", $"The station code '{key.Code}' could not be ensured.");
            }
            resolvedStationIds[(key.Process.Id, key.Code)] = ensured;
            createdStationCount++;
        }

        var bindings = await repository.LoadActiveSubStationBindingsAsync(
            request.LineId,
            request.DeviceId,
            updates.Select(x => x.SequenceSourceKey.Trim()).ToArray(),
            cancellationToken);
        var bindingByKey = bindings
            .GroupBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray());

        // 无绑定顺控：带批次 Id 时从预览取顺控元数据直接新建绑定（弹框内免保存结构直接绑定），
        // 否则维持"先保存结构"的错误提示。
        var createdBindingCount = 0;
        var newBindings = new List<(string SequenceSourceKey, long BindingId)>();
        var missingKeys = updates
            .Select(x => x.SequenceSourceKey.Trim())
            .Where(key => !bindingByKey.ContainsKey(key) || bindingByKey[key].Length == 0)
            .ToArray();
        if (missingKeys.Length > 0)
        {
            if (request.BatchId <= 0)
            {
                throw ImportError("HierarchyBindingMissing",
                    $"No saved structure binding exists for Sequence '{missingKeys[0]}'; save the GRAPH structure first or retry with the preview batch.");
            }
            var batch = await repository.GetBatchAsync(request.BatchId, cancellationToken) ??
                        throw ImportError("HierarchyBindingMissing",
                            $"The preview batch {request.BatchId} is unavailable; the Sequences '{string.Join("', '", missingKeys)}' have no saved structure binding.");
            if (batch.LineId != request.LineId || batch.DeviceId != request.DeviceId ||
                string.IsNullOrWhiteSpace(batch.PreviewJson))
            {
                throw ImportError("HierarchyBindingMissing",
                    $"The preview batch {request.BatchId} does not match the selected line/PLC; cannot create bindings for its Sequences.");
            }
            var previewContent = LineUpImportPreviewCodec.Deserialize(batch.PreviewJson, batch.SourceFileName);
            var document = previewContent.Documents.FirstOrDefault() ??
                           throw ImportError("HierarchyBindingMissing",
                               $"The preview batch {request.BatchId} has no GRAPH document for binding creation.");
            var plcProjectId = document.Address.PlcProjectId is > 0 ? document.Address.PlcProjectId.Value : 0;
            var graphBlockId = document.Address.GraphBlockId is > 0 ? document.Address.GraphBlockId.Value : 0;
            if (plcProjectId <= 0 || graphBlockId <= 0)
            {
                throw ImportError("HierarchyBindingMissing",
                    $"The preview batch {request.BatchId} lacks PLC project/block identity; cannot create bindings.");
            }
            foreach (var key in missingKeys)
            {
                var sequenceMeta = document.Sequences.FirstOrDefault(x =>
                    string.Equals(x.SourceKey?.Trim(), key, StringComparison.Ordinal));
                if (sequenceMeta is null)
                {
                    throw ImportError("HierarchyBindingMissing",
                        $"Sequence '{key}' is not part of preview batch {request.BatchId}; cannot create its binding.");
                }
                var sequenceId = await hierarchyRepository.EnsureGraphSequenceAsync(
                    key,
                    sequenceMeta.SourceName ?? key,
                    string.IsNullOrWhiteSpace(sequenceMeta.DisplayName) ? sequenceMeta.SourceName ?? key : sequenceMeta.DisplayName,
                    plcProjectId,
                    graphBlockId,
                    document.Address.InstanceDbBlockId is > 0 ? document.Address.InstanceDbBlockId : null,
                    document.SourceSha256,
                    cancellationToken);
                if (sequenceId <= 0)
                {
                    throw ImportError("HierarchyEnsureAmbiguous",
                        $"The GRAPH sequence row for '{key}' could not be ensured.");
                }
                // 绑定行的层级列由下方统一 update 循环写入（hierarchyDepth 语义不变），这里先占位 Unresolved
                var bindingId = await hierarchyRepository.InsertSubStationBindingAsync(
                    request.LineId, 0, 0, 0, request.DeviceId,
                    plcProjectId, sequenceId, graphBlockId,
                    document.Address.InstanceDbBlockId is > 0 ? document.Address.InstanceDbBlockId : null,
                    cancellationToken);
                if (bindingId <= 0)
                {
                    throw ImportError("HierarchyBindingCreateFailed",
                        $"The structure binding for Sequence '{key}' could not be created.");
                }
                newBindings.Add((key, bindingId));
                createdBindingCount++;
                bindingByKey[key] = new[] { new LineUpActiveSubStationBindingLookup(bindingId, key, 0, 0, 0) };
            }
        }
        foreach (var item in updates)
        {
            if (!bindingByKey.TryGetValue(item.SequenceSourceKey.Trim(), out var rows) || rows.Length == 0)
            {
                throw ImportError("HierarchyBindingMissing",
                    $"No saved structure binding exists for Sequence '{item.SequenceSourceKey}'; save the GRAPH structure first.");
            }
            if (rows.Length > 1)
            {
                throw ImportError("HierarchyBindingAmbiguous",
                    $"More than one saved structure binding matches Sequence '{item.SequenceSourceKey}'.");
            }
        }

        var updatedCount = 0;
        foreach (var plan in plans)
        {
            var processId = plan.Process.Id > 0 ? plan.Process.Id : resolvedProcessIds[plan.Process.Code!];
            // 深度1=只改工序；深度2=工序+工站；深度3=三级全写。
            // 未覆盖的层级保留数据库原值（改上级不清空下级绑定）。
            var hierarchyDepth = 1;
            var stationId = 0L;
            if (plan.Station is not null)
            {
                hierarchyDepth = 2;
                stationId = plan.Station.Id > 0
                    ? plan.Station.Id
                    : resolvedStationIds[(processId, plan.Station.Code!)];
            }

            var subStationId = 0L;
            if (plan.SubStationId > 0)
            {
                hierarchyDepth = 3;
                subStationId = plan.SubStationId;
            }

            var row = bindingByKey[plan.Item.SequenceSourceKey.Trim()][0];
            if (!await repository.ReassignSubStationBindingHierarchyAsync(
                    row.BindingId, processId, stationId, subStationId, hierarchyDepth, cancellationToken))
            {
                throw ImportError("HierarchyBindingStale",
                    $"The saved structure binding for Sequence '{plan.Item.SequenceSourceKey}' changed during the update.");
            }
            updatedCount++;
        }

        return new LineUpHierarchyReassignReceipt(updatedCount, createdProcessCount, createdStationCount, createdSubStationCount, createdBindingCount);
    }

    public async Task<LineUpImportCommitReceiptDto> CommitAsync(
        long batchId,
        LineUpImportCommitRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CommitCoreAsync(batchId, request, cancellationToken);
        }
        catch (LineUpImportException error)
        {
            // 拒绝原因落库：前端 toast 文案泛化，排查时直接查批次 ErrorSummary 即可拿到确切守卫与漂移明细
            try
            {
                await repository.WriteCommitRejectionAsync(batchId, error.Code, error.SafeSummary, CancellationToken.None);
            }
            catch
            {
                // 诊断落库失败不影响原始错误上抛
            }
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // EventLog logger 无权限时 controller 的 LogError 会炸出 AggregateException 掩盖真实异常，
            // 这里在最内层转成业务错误，保证前端能看到真实原因
            throw ImportError("CommitInternalError", $"{error.GetType().Name}: {error.Message}");
        }
    }

    private async Task<LineUpImportCommitReceiptDto> CommitCoreAsync(
        long batchId,
        LineUpImportCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var batch = await repository.GetBatchAsync(batchId, cancellationToken) ??
                    throw ImportError("BatchMissing", "The import batch is unavailable.");
        var requestHash = HashCommitRequest(batchId, request);
        if (string.Equals(batch.Status, "Committed", StringComparison.Ordinal))
        {
            if (string.Equals(batch.CommitRequestHash, requestHash, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(batch.CommitReceiptJson))
            {
                return DeserializeReceipt(batch.CommitReceiptJson);
            }

            throw ImportError("BatchAlreadyCommitted", "The import batch was committed with a different request.");
        }

        if (!string.Equals(batch.Status, "PreviewReady", StringComparison.Ordinal))
        {
            throw ImportError("PreviewNotReady", "The import preview is not ready for commit.");
        }

        var preview = await GetPreviewAsync(batchId, cancellationToken);
        if (request.PreviewRevision != preview.PreviewRevision ||
            !string.Equals(request.PreviewHash, preview.PreviewHash, StringComparison.Ordinal) ||
            !string.Equals(request.DiffToken, preview.DiffToken, StringComparison.Ordinal))
        {
            throw ImportError("PreviewStale", "The submitted preview revision, hash, or diff token is stale.");
        }

        var mode = request.CommitMode?.Trim();
        if (!string.Equals(mode, "StructureOnly", StringComparison.Ordinal) &&
            !string.Equals(mode, "ReadyForMonitoring", StringComparison.Ordinal))
        {
            throw ImportError("InvalidCommitMode", "The requested commit mode is invalid.");
        }

        if (!preview.CanCommitStructure ||
            preview.Issues.Any(x => string.Equals(x.Severity, "Blocking", StringComparison.Ordinal)))
        {
            throw ImportError("GraphStructureBlocked", "The preview contains a Blocking issue and cannot be committed.");
        }

        if (string.Equals(mode, "ReadyForMonitoring", StringComparison.Ordinal) &&
            preview.Documents.Any(x => x.Address?.InstanceDbBlockId is not > 0))
        {
            throw ImportError("RuntimeBindingUnresolved", "The InstanceDB of a GRAPH document is not proven; save the structure first and retry after the PLC metadata is complete.");
        }

        if (addressResolver is null || hierarchyResolver is null)
        {
            throw ImportError("ResolutionUnavailable", "The current hierarchy and PLC metadata resolvers are unavailable.");
        }

        var decisionMap = BuildDecisionMap(request.HierarchyDecisions);
        var displayMap = BuildDisplayMap(request.DisplayNameChanges);
        var knownDisplayKeys = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<LineUpCommitDocumentWrite>(preview.Documents.Count);
        foreach (var document in preview.Documents.OrderBy(x => x.SourceRelativePath, StringComparer.Ordinal))
        {
            var graph = RestoreGraph(document);
            var currentAddress = await addressResolver.ResolveAsync(
                preview.LineId, preview.DeviceId, graph, cancellationToken);
            if (!SameAddress(document.Address, currentAddress))
            {
                throw ImportError("PreviewStale", "The PLC project, GRAPH block, InstanceDB, or binding status changed after preview.");
            }

            var currentHierarchy = await hierarchyResolver.ResolveAsync(
                preview.LineId,
                preview.DeviceId,
                currentAddress.PlcProjectId,
                graph,
                cancellationToken);
            // GetPreviewAsync 返回前做过 MergeCurrentHierarchyBindingsAsync（已绑定顺控自动选
            // existing-{BindingId} 候选）；裸解析器只产 lhc:v1: 候选，不合并的话 SameHierarchy
            // 必然不等、existing- 决策也找不到候选 —— 已保存/已改派过的 GRAPH 重新提交会永远 409。
            // 预览加载后用户可能又做了改派（只写绑定行）：此时与合并结果的差异全部来自现有绑定，
            // 精确比对不等时按 SameHierarchySkeleton 只比骨架（顺控键集/置信度/项目归属），
            // 骨架未变即放行，并把当前绑定作为权威归属。
            currentHierarchy = await MergeCurrentBindingsIntoResolutionAsync(
                currentHierarchy, preview.LineId, preview.DeviceId, cancellationToken);
            // 层级守卫只比「骨架」：产线/设备/已解析项目号 + 顺控源键集 + 每顺控置信度。
            // 解析候选（lhc: 哈希内嵌层级 Id/ProposedSubStationCode）、existing- 绑定候选、
            // 自动选中键、Issues、解析器 CanCommitStructure 都随「改派写绑定行 / EnsureProcess·
            // EnsureWorkStation 建基础层级行」漂移 —— 那是预期内的绑定世界变化；真正的结构竞争由 SameAddress +
            // DiffToken + 事务内 StateHash 三道锁守护。骨架不等 = GRAPH 本身或项目归属变了，才拦。
            // 提交侧解析器此刻报 CanCommitStructure=false（如绑定二义）仍拦截，且漂移明细进错误文本。
            var hierarchyDrift = DescribeHierarchyDrift(document.Hierarchy, currentHierarchy, currentAddress.CanCommitStructure);
            if (!currentAddress.CanCommitStructure || !currentHierarchy.CanCommitStructure ||
                (!SameHierarchy(document.Hierarchy, currentHierarchy) &&
                 !SameHierarchySkeleton(document.Hierarchy, currentHierarchy)))
            {
                throw ImportError("PreviewStale",
                    $"The current line, device, project, GRAPH, or hierarchy candidates changed after preview. Drift: {hierarchyDrift}");
            }

            var sequenceWrites = new List<LineUpCommitSequenceWrite>(document.Sequences.Count);
            foreach (var sequence in document.Sequences.OrderBy(x => x.SourceKey, StringComparer.Ordinal))
            {
                var resolution = currentHierarchy.Sequences.SingleOrDefault(x =>
                    string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal));
                if (resolution is null)
                {
                    throw ImportError("HierarchyCandidateStale", "A Sequence no longer has a current hierarchy resolution.");
                }

                decisionMap.TryGetValue(sequence.SourceKey, out var requestedCandidateKey);
                // 预览加载后可能发生过改派/层级世界漂移（其它 GRAPH 块的提交、改派、建基础层级都会
                // 改变 lhc: 候选哈希）。权威顺序：
                //   1) 当前合并结果的 existing- 自动选中（改派刚写的绑定）；
                //   2) 用户决策键 —— 仅当它仍在当前候选集中；
                //   3) 当前解析器的自动选中 / 唯一候选（无歧义时直接采纳，旧预览不再强制重生成）。
                var selectedKey = resolution.SelectedCandidateKey is not null &&
                                  resolution.SelectedCandidateKey.StartsWith("existing-", StringComparison.Ordinal)
                    ? resolution.SelectedCandidateKey
                    : requestedCandidateKey;
                var candidate = resolution.Candidates.SingleOrDefault(x =>
                    !string.IsNullOrWhiteSpace(selectedKey) &&
                    string.Equals(x.CandidateKey, selectedKey, StringComparison.Ordinal) &&
                    string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal));
                if (candidate is null)
                {
                    var currentAuto = resolution.Candidates.Where(x =>
                        string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal)).ToArray();
                    if (currentAuto.Length == 1)
                    {
                        // 当前世界只剩一个候选：无歧义，直接采纳
                        candidate = currentAuto[0];
                    }
                    else
                    {
                        selectedKey = resolution.SelectedCandidateKey;
                        candidate = resolution.Candidates.SingleOrDefault(x =>
                            !string.IsNullOrWhiteSpace(selectedKey) &&
                            string.Equals(x.CandidateKey, selectedKey, StringComparison.Ordinal) &&
                            string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal));
                    }
                }
                if (candidate is null)
                {
                    throw ImportError("HierarchyCandidateStale",
                        $"The saved hierarchy decision for Sequence '{sequence.SourceKey}' no longer matches current candidates ({currentHierarchy.Sequences.First(x => string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal)).Candidates.Count} current). Regenerate the preview and re-pick.");
                }

                decisionMap.Remove(sequence.SourceKey);
                knownDisplayKeys.Add(sequence.SourceKey);
                foreach (var node in sequence.Nodes) knownDisplayKeys.Add(node.SourceKey);
                foreach (var action in sequence.Actions) knownDisplayKeys.Add(action.SourceKey);
                var bindingStatus = currentAddress.Sequences.Single(x =>
                    string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal)).BindingStatus.ToString();
                sequenceWrites.Add(new(
                    sequence.SourceKey,
                    sequence.SourceName,
                    sequence.DisplayName,
                    DisplayChange(displayMap, sequence.SourceKey),
                    bindingStatus,
                    candidate.ProcessId,
                    candidate.StationId,
                    candidate.SubStationId,
                    candidate.CandidateKind.ToString(),
                    candidate.ProposedSubStationCode,
                    candidate.ProposedSubStationName,
                    sequence.Nodes.OrderBy(x => x.SourceKey, StringComparer.Ordinal).Select(node => new LineUpCommitNodeWrite(
                        node.NodeKind,
                        node.SourceKey,
                        node.SourceName,
                        node.DisplayName,
                        DisplayChange(displayMap, node.SourceKey),
                        node.Number,
                        node.IsInitial,
                        node.IsFinal,
                        node.MaximumStepTime,
                        node.WarningTime,
                        node.Comment,
                        node.ProgrammingLanguage,
                        node.IsMissing,
                        node.IsJump)).ToArray(),
                    sequence.Actions.OrderBy(x => x.SourceKey, StringComparer.Ordinal).Select(action => new LineUpCommitActionWrite(
                        action.SourceKey,
                        action.StepSourceKey,
                        action.SourceName,
                        action.DisplayName,
                        DisplayChange(displayMap, action.SourceKey),
                        action.Ordinal,
                        action.Qualifier,
                        action.Tokens,
                        action.ActionText)).ToArray(),
                    sequence.Edges.OrderBy(x => x.SourceKey, StringComparer.Ordinal).Select(edge => new LineUpCommitEdgeWrite(
                        edge.SourceKey,
                        edge.FromSourceKey,
                        edge.ToSourceKey,
                        edge.FromNodeKind,
                        edge.ToNodeKind,
                        edge.EdgeKind,
                        edge.BranchOrder)).ToArray()));
            }

            documents.Add(new(
                currentAddress.PlcProjectId!.Value,
                currentAddress.GraphBlockId!.Value,
                currentAddress.InstanceDbBlockId,
                document.SourceSha256,
                sequenceWrites));
        }

        if (decisionMap.Count > 0)
        {
            throw ImportError("HierarchyCandidateStale", "A hierarchy decision references an unknown or duplicate Sequence.");
        }

        var unknownDisplayKey = displayMap.Keys.FirstOrDefault(x => !knownDisplayKeys.Contains(x));
        if (unknownDisplayKey is not null)
        {
            throw ImportError("DisplayNameTargetInvalid", "A display-name change references an unknown source key.");
        }

        var projectIds = documents.Select(x => x.PlcProjectId).Distinct().OrderBy(x => x).ToArray();
        var graphBlockIds = documents.Select(x => x.GraphBlockId).Distinct().OrderBy(x => x).ToArray();
        var sequenceKeys = documents.SelectMany(x => x.Sequences).Select(x => x.SourceKey)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var graphState = await repository.LoadExistingGraphStateAsync(
            projectIds, graphBlockIds, sequenceKeys, cancellationToken);
        var currentDiffToken = LineUpImportDiffToken.Compute(preview.Documents, graphState);
        if (!string.Equals(currentDiffToken, request.DiffToken, StringComparison.Ordinal))
        {
            throw ImportError("PreviewStale", "The persisted GRAPH changed after the preview was built.");
        }

        var committedAt = DateTime.UtcNow;
        var receipt = new LineUpImportCommitReceiptDto(
            batchId,
            "Committed",
            mode!,
            preview.PreviewRevision,
            preview.PreviewHash,
            committedAt);
        var receiptJson = JsonSerializer.Serialize(receipt);
        var plan = new LineUpCommitPersistencePlan(
            batchId,
            preview.LineId,
            preview.DeviceId,
            preview.PreviewSchemaVersion,
            preview.PreviewRevision,
            preview.PreviewHash,
            graphState.StateHash,
            preview.DiffToken,
            mode!,
            requestHash,
            receiptJson,
            committedAt,
            documents);
        LineUpImportCommitReceiptDto committedReceipt;
        try
        {
            var outcome = await repository.CommitAsync(plan, cancellationToken);
            committedReceipt = DeserializeReceipt(outcome.CommitReceiptJson);
        }
        catch (LineUpCommitPersistenceException error)
        {
            var safeCode = SafePersistenceCodes.Contains(error.Code)
                ? error.Code
                : "CommitConflict";
            throw ImportError(safeCode, $"The persisted import state changed during the atomic commit check. (origin={error.Code}: {error.Message})");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LineUpImportException)
        {
            throw;
        }
        catch (Exception error)
        {
            // EventLog logger 无权限会把真实异常吞成 500「出现异常」，这里转成业务错误让前端可见
            throw ImportError("CommitPersistenceFailed", $"{error.GetType().Name}: {error.Message}");
        }

        if (string.Equals(mode, "ReadyForMonitoring", StringComparison.Ordinal))
        {
            await EnableMonitoringAfterCommitAsync(preview, documents, cancellationToken);
        }

        return committedReceipt;
    }

    /// <summary>
    /// ReadyForMonitoring 编排：结构已落库后，为每个 GRAPH 资产确保运行时绑定（GraphStationBinding）
    /// 并指向刚发布的修订，然后 Prepare → Validate（IGW 读回）→ SetMonitoring(true)。
    /// 结构不回滚：任一环节失败抛 MonitoringEnableFailed，错误信息说明结构已保存。
    /// </summary>
    private async Task EnableMonitoringAfterCommitAsync(
        LineUpImportPreviewDto preview,
        IReadOnlyList<LineUpCommitDocumentWrite> documents,
        CancellationToken cancellationToken)
    {
        if (runtimeBindingApp is null)
        {
            throw ImportError("MonitoringEnableFailed", "The runtime binding service is unavailable.");
        }

        foreach (var document in documents)
        {
            var asset = await repository.LoadCommittedAssetRevisionAsync(
                document.PlcProjectId, document.GraphBlockId, cancellationToken);
            if (asset is null)
            {
                // 2026-09-10：图谱资产（AI_LineUp_GraphAsset/Revision）只在上传预览路径建立（SaveDraft），
                // 免上传（PreviewFromBlock）链路的块可能从未有过资产——结构/绑定已保存成功，
                // 监控绑定挂在图谱资产上无从建起，跳过该文档而不是把整批 commit 炸成 422。
                Console.WriteLine(
                    "[MonitorEnable] 跳过无图谱资产的块 project=" + document.PlcProjectId +
                    " block=" + document.GraphBlockId + "（需走上传预览建立资产后才能开监控）");
                continue;
            }

            // 层级决策按序列记录；同一 GRAPH 资产取第一个序列选中的层级建运行时绑定
            var first = document.Sequences.FirstOrDefault(x => x.SubStationId is > 0)
                        ?? document.Sequences.FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            var bindingId = await repository.EnsureGraphStationBindingAsync(
                new(
                    asset.GraphAssetId,
                    asset.LatestRevisionId,
                    preview.LineId,
                    first.ProcessId,
                    first.StationId,
                    first.SubStationId ?? 0,
                    preview.DeviceId,
                    document.InstanceDbBlockId),
                cancellationToken);
            // 错误消息自带定位上下文（项目/GRAPH 块/实例DB/绑定），并聚合 blocking issue 统计，
            // 让 422 一次就能看出病根（如 PlcExactTagMissing x18 = iotag 表没登记该 DB 的符号）。
            var scope = $"project {document.PlcProjectId}, GRAPH block {document.GraphBlockId}, " +
                        $"InstanceDB {document.InstanceDbBlockId}, binding {bindingId}";
            try
            {
                var preparation = await runtimeBindingApp.PrepareAsync(bindingId, cancellationToken);
                if (!preparation.CanValidate)
                {
                    throw ImportError(
                        "MonitoringEnableFailed",
                        $"The GRAPH structure was saved, but monitoring could not be enabled at {scope}: " +
                        $"binding preparation is blocked: {DescribeRuntimeIssues(preparation.Issues)}");
                }

                var validation = await runtimeBindingApp.ValidateAsync(bindingId, cancellationToken);
                if (!validation.CanEnable)
                {
                    throw ImportError(
                        "MonitoringEnableFailed",
                        $"The GRAPH structure was saved, but monitoring could not be enabled at {scope}: " +
                        $"runtime validation failed ({validation.GoodCount}/{validation.TotalCount} tags read good): " +
                        DescribeRuntimeIssues(validation.Issues));
                }

                await runtimeBindingApp.SetMonitoringAsync(bindingId, true, cancellationToken);
            }
            catch (LineUpImportException error) when (!error.Code.Contains("MonitoringEnableFailed", StringComparison.Ordinal))
            {
                throw ImportError(
                    "MonitoringEnableFailed",
                    $"The GRAPH structure was saved, but enabling monitoring failed at {scope}: " +
                    $"{error.Code}: {error.SafeSummary}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                throw ImportError(
                    "MonitoringEnableFailed",
                    $"The GRAPH structure was saved, but enabling monitoring failed at {scope}: " +
                    $"{error.GetType().Name}: {error.Message}");
            }
        }
    }

    /// <summary>
    /// 聚合运行时绑定 issue 供错误消息直读：blocking/total 计数 + 前 3 类 code 计数 + 首个 blocking 的信号定位。
    /// 例："18 blocking / 21 total: PlcExactTagMissing x18, ...; first blocking at 'DB462_HomeGraph.RT_DATA...' (PlcExactTagMissing)"
    /// </summary>
    private static string DescribeRuntimeIssues(IReadOnlyList<LineUpRuntimeBindingIssueDto> issues)
    {
        if (issues is null || issues.Count == 0)
        {
            return "no issues reported (binding returned no detail)";
        }

        var blockingCount = issues.Count(issue => string.Equals(issue.Severity, "Blocking", StringComparison.Ordinal));
        var topCodes = string.Join(", ", issues
            .GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(group => $"{group.Key} x{group.Count()}"));
        var first = issues.FirstOrDefault(issue => string.Equals(issue.Severity, "Blocking", StringComparison.Ordinal))
                    ?? issues[0];
        var firstAt = string.IsNullOrWhiteSpace(first.OwnerSourceKey)
            ? string.Empty
            : $"; first blocking at '{first.OwnerSourceKey}'";
        return $"{blockingCount} blocking / {issues.Count} total: {topCodes}{firstAt}";
    }

    public async Task<LineUpImportPreviewDto> GetPreviewAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        var batch = await repository.GetBatchAsync(batchId, cancellationToken);
        if (batch is null ||
            (!string.Equals(batch.Status, "PreviewReady", StringComparison.Ordinal) &&
             !string.Equals(batch.Status, "Committed", StringComparison.Ordinal)))
        {
            throw new LineUpImportException("PreviewNotReady", "source", "The import preview is not ready.");
        }

        if (string.IsNullOrWhiteSpace(batch.PreviewJson) || string.IsNullOrWhiteSpace(batch.PreviewHash))
        {
            throw new LineUpImportException("PreviewIntegrityFailed", batch.SourceFileName, "The stored preview is incomplete.");
        }

        var actualHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(batch.PreviewJson)))
            .ToLowerInvariant();
        if (!string.Equals(actualHash, batch.PreviewHash, StringComparison.Ordinal))
        {
            throw new LineUpImportException("PreviewIntegrityFailed", batch.SourceFileName, "The stored preview hash does not match.");
        }

        var content = LineUpImportPreviewCodec.Deserialize(batch.PreviewJson, batch.SourceFileName);
        if (content.SchemaVersion != batch.PreviewSchemaVersion ||
            content.LineId != batch.LineId || content.DeviceId != batch.DeviceId)
        {
            throw new LineUpImportException("PreviewIntegrityFailed", batch.SourceFileName, "The stored preview identity does not match the batch.");
        }

        // 层级改派只写绑定表，预览快照（PreviewJson）会滞后于最新归属；
        // 返回前用当前绑定动态合并层级归属，保证面板/改派对话框看到的是最新数据。
        content = await MergeCurrentHierarchyBindingsAsync(content, cancellationToken);

        return new()
        {
            BatchId = batch.BatchId,
            Status = batch.Status,
            PreviewSchemaVersion = batch.PreviewSchemaVersion,
            PreviewRevision = batch.PreviewRevision,
            PreviewHash = batch.PreviewHash,
            SourceFileName = batch.SourceFileName,
            SourceFileHash = batch.SourceFileHash,
            LineId = batch.LineId,
            DeviceId = batch.DeviceId,
            Documents = content.Documents,
            Issues = content.Issues,
            Summary = content.Summary,
            DiffToken = content.DiffToken,
            CanCommitStructure = content.CanCommitStructure,
            CanCommitForMonitoring = content.CanCommitForMonitoring
        };
    }

    /// <summary>
    /// 用绑定表当前值覆盖预览快照的层级归属：改派（HierarchyReassign）只写
    /// AI_LineUp_SubStationGraphBinding，不回写 PreviewJson；不合并的话重新打开预览
    /// 面板/改派对话框看到的还是保存结构时的旧归属。有绑定且唯一的顺控才会被覆盖，
    /// 无绑定（未保存结构）的顺控保持快照原样。已指派值为 0 表示待重新指派。
    /// </summary>
    private async Task<LineUpImportPreviewContentDto> MergeCurrentHierarchyBindingsAsync(
        LineUpImportPreviewContentDto content,
        CancellationToken cancellationToken)
    {
        var keys = content.Documents
            .SelectMany(x => x.Hierarchy.Sequences.Select(s => s.SequenceSourceKey))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
        {
            return content;
        }

        var bindings = await repository.LoadActiveSubStationBindingsAsync(
            content.LineId, content.DeviceId, keys, cancellationToken);
        var bindingByKey = bindings
            .GroupBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray());
        if (bindingByKey.Count == 0)
        {
            return content;
        }

        var context = await hierarchyRepository.LoadContextAsync(
            content.LineId, content.DeviceId, keys, cancellationToken);
        var processById = context.Processes.Where(x => x.Id > 0)
            .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var stationById = context.Stations.Where(x => x.Id > 0)
            .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var subStationById = context.SubStations.Where(x => x.Id > 0)
            .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());

        var documents = content.Documents.Select(doc =>
        {
            var sequences = doc.Hierarchy.Sequences.Select(seq =>
            {
                if (!bindingByKey.TryGetValue(seq.SequenceSourceKey, out var rows) || rows.Length != 1)
                {
                    return seq;
                }

                var row = rows[0];
                processById.TryGetValue(row.ProcessId, out var process);
                stationById.TryGetValue(row.StationId, out var station);
                LineUpSubStationLookup? subStation = null;
                var hasSubStation = row.SubStationId > 0 && subStationById.TryGetValue(row.SubStationId, out subStation);
                var subCode = hasSubStation ? subStation!.Code : null;
                var subName = hasSubStation ? subStation!.Name : null;
                var existing = seq.Candidates.FirstOrDefault(x =>
                    x.CandidateKind == LineUpHierarchyCandidateKind.ExistingBinding);
                var candidate = existing is null
                    ? new LineUpHierarchyCandidate(
                        $"existing-{row.BindingId}",
                        LineUpHierarchyCandidateKind.ExistingBinding,
                        seq.SequenceSourceKey,
                        row.ProcessId,
                        row.StationId,
                        hasSubStation ? row.SubStationId : null,
                        subCode,
                        subName,
                        LineUpConfidenceLevel.Automatic,
                        new[] { "SavedStructureBinding" })
                        {
                            ProcessCode = process?.Code,
                            ProcessName = process?.Name,
                            StationCode = station?.Code,
                            StationName = station?.Name,
                            SubStationCode = subCode,
                            SubStationName = subName,
                        }
                    : existing with
                    {
                        ProcessId = row.ProcessId,
                        StationId = row.StationId,
                        SubStationId = hasSubStation ? row.SubStationId : null,
                        ProposedSubStationCode = subCode,
                        ProposedSubStationName = subName,
                        ProcessCode = process?.Code,
                        ProcessName = process?.Name,
                        StationCode = station?.Code,
                        StationName = station?.Name,
                        SubStationCode = subCode,
                        SubStationName = subName,
                    };
                var candidates = existing is null
                    ? new[] { candidate }.Concat(seq.Candidates).ToArray()
                    : seq.Candidates.Select(x => ReferenceEquals(x, existing) ? candidate : x).ToArray();
                return seq with
                {
                    ProcessId = row.ProcessId,
                    StationId = row.StationId > 0 ? row.StationId : null,
                    SubStationId = hasSubStation ? row.SubStationId : null,
                    SelectedCandidateKey = candidate.CandidateKey,
                    Candidates = candidates,
                };
            }).ToArray();
            return doc with { Hierarchy = doc.Hierarchy with { Sequences = sequences } };
        }).ToList();

        return new LineUpImportPreviewContentDto
        {
            SchemaVersion = content.SchemaVersion,
            LineId = content.LineId,
            DeviceId = content.DeviceId,
            Documents = documents,
            Issues = content.Issues,
            Summary = content.Summary,
            DiffToken = content.DiffToken,
            CanCommitStructure = content.CanCommitStructure,
            CanCommitForMonitoring = content.CanCommitForMonitoring,
        };
    }


    /// <summary>
    /// 提交校验用的层级合并：GetPreviewAsync 返回前会做 MergeCurrentHierarchyBindingsAsync，
    /// 把已有活动绑定的顺控自动选为 existing-{BindingId} 候选；而提交时的裸解析器只产
    /// lhc:v1: 候选。若不对重解析结果施加同样合并，SameHierarchy 必然不等、用户按预览
    /// 选择的 existing- 决策也找不到候选 —— 已保存/已改派过的 GRAPH 重新提交会永远 409。
    /// 合并规则与预览侧完全一致，绑定在预览与提交之间未变时两边可比。
    /// </summary>
    private async Task<LineUpHierarchyResolution> MergeCurrentBindingsIntoResolutionAsync(
        LineUpHierarchyResolution resolution,
        long lineId,
        long deviceId,
        CancellationToken cancellationToken)
    {
        var keys = resolution.Sequences.Select(x => x.SequenceSourceKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
        {
            return resolution;
        }

        var bindings = await repository.LoadActiveSubStationBindingsAsync(lineId, deviceId, keys, cancellationToken);
        var bindingByKey = bindings
            .GroupBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray());
        if (bindingByKey.Count == 0)
        {
            return resolution;
        }

        var context = await hierarchyRepository.LoadContextAsync(lineId, deviceId, keys, cancellationToken);
        var processById = context.Processes.Where(x => x.Id > 0)
            .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var stationById = context.Stations.Where(x => x.Id > 0)
            .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var subStationById = context.SubStations.Where(x => x.Id > 0)
            .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());

        var changed = false;
        var sequences = resolution.Sequences.Select(seq =>
        {
            if (!bindingByKey.TryGetValue(seq.SequenceSourceKey, out var rows) || rows.Length != 1)
            {
                return seq;
            }

            var row = rows[0];
            processById.TryGetValue(row.ProcessId, out var process);
            stationById.TryGetValue(row.StationId, out var station);
            LineUpSubStationLookup? subStation = null;
            var hasSubStation = row.SubStationId > 0 && subStationById.TryGetValue(row.SubStationId, out subStation);
            var subCode = hasSubStation ? subStation!.Code : null;
            var subName = hasSubStation ? subStation!.Name : null;
            var existing = seq.Candidates.FirstOrDefault(x =>
                x.CandidateKind == LineUpHierarchyCandidateKind.ExistingBinding);
            var candidate = existing is null
                ? new LineUpHierarchyCandidate(
                    $"existing-{row.BindingId}",
                    LineUpHierarchyCandidateKind.ExistingBinding,
                    seq.SequenceSourceKey,
                    row.ProcessId,
                    row.StationId,
                    hasSubStation ? row.SubStationId : null,
                    subCode,
                    subName,
                    LineUpConfidenceLevel.Automatic,
                    new[] { "SavedStructureBinding" })
                    {
                        ProcessCode = process?.Code,
                        ProcessName = process?.Name,
                        StationCode = station?.Code,
                        StationName = station?.Name,
                        SubStationCode = subCode,
                        SubStationName = subName,
                    }
                : existing with
                {
                    ProcessId = row.ProcessId,
                    StationId = row.StationId,
                    SubStationId = hasSubStation ? row.SubStationId : null,
                    ProposedSubStationCode = subCode,
                    ProposedSubStationName = subName,
                    ProcessCode = process?.Code,
                    ProcessName = process?.Name,
                    StationCode = station?.Code,
                    StationName = station?.Name,
                    SubStationCode = subCode,
                    SubStationName = subName,
                };
            var candidates = existing is null
                ? new[] { candidate }.Concat(seq.Candidates).ToArray()
                : seq.Candidates.Select(x => ReferenceEquals(x, existing) ? candidate : x).ToArray();
            changed = true;
            return seq with
            {
                ProcessId = row.ProcessId,
                StationId = row.StationId > 0 ? row.StationId : null,
                SubStationId = hasSubStation ? row.SubStationId : null,
                SelectedCandidateKey = candidate.CandidateKey,
                Candidates = candidates,
            };
        }).ToArray();

        return changed ? resolution with { Sequences = sequences } : resolution;
    }

    public async Task<LineUpImportProgressDto> GetProgressAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        var batch = await repository.GetBatchAsync(batchId, cancellationToken) ??
                    throw ImportError("BatchMissing", "The import batch is unavailable.");
        var canLoadPreview = string.Equals(batch.Status, "PreviewReady", StringComparison.Ordinal) ||
                             string.Equals(batch.Status, "Committed", StringComparison.Ordinal);
        return new(
            batch.BatchId,
            batch.Status,
            string.IsNullOrWhiteSpace(batch.ErrorSummary) ? null : batch.ErrorSummary,
            batch.PreviewRevision,
            canLoadPreview);
    }

    public async Task<LineUpImportStartedDto> StartPreviewAsync(
        IFormFile file,
        long lineId,
        long deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (lineId <= 0 || deviceId <= 0)
        {
            throw new LineUpImportException(
                "InvalidSelection",
                "source",
                "A valid production line and PLC device selection is required.");
        }

        await using var source = file.OpenReadStream();
        return await StartPreviewCoreAsync(source, file.FileName, lineId, deviceId, cancellationToken);
    }

    /// <summary>
    /// 从已入库的 PLC GRAPH 块免上传生成预览（graph 结构管理页弹框入口）：
    /// ai_plc_block_new(未删 GRAPH) → SourceFileId → ai_plc_sourcefile.filecontent 归档原文
    /// → 复用 StartPreviewCoreAsync 同一套 storage/batch/scheduler 解析链。
    /// </summary>
    public async Task<LineUpImportStartedDto> StartPreviewFromBlockAsync(
        long blockId,
        long lineId,
        long deviceId,
        CancellationToken cancellationToken)
    {
        if (lineId <= 0 || deviceId <= 0)
        {
            throw new LineUpImportException(
                "InvalidSelection",
                "source",
                "A valid production line and PLC device selection is required.");
        }
        if (blockId <= 0)
        {
            throw new LineUpImportException(
                "InvalidSelection",
                "block",
                "A valid GRAPH block id is required.");
        }

        var block = await db.Queryable<PLC_BlockEntity>()
            .Where(b => b.Id == blockId && b.IsDeleted == false && b.ProgrammingLanguage == "GRAPH")
            .FirstAsync(cancellationToken);
        if (block is null)
        {
            throw new LineUpImportException(
                "SourceBlockMissing",
                "block:" + blockId,
                "The GRAPH block is unavailable.");
        }
        if (!block.SourceFileId.HasValue)
        {
            throw new LineUpImportException(
                "SourceFileMissing",
                block.BlockName,
                "The GRAPH block has no archived source file.");
        }

        var sourceFile = await db.Queryable<PLC_SourceFileEntity>()
            .Where(s => s.Id == block.SourceFileId.Value && s.IsDeleted == false)
            .FirstAsync(cancellationToken);
        if (sourceFile is null || string.IsNullOrWhiteSpace(sourceFile.FileContent))
        {
            // 历史上传没有归档原文（改造前上传的块）：引导重传补档
            throw new LineUpImportException(
                "SourceContentMissing",
                block.BlockName,
                "该 GRAPH 块没有归档原始 XML（历史上传未归档），请重新上传项目 zip 后再预览。");
        }

        var fileName = string.IsNullOrWhiteSpace(sourceFile.FileName)
            ? block.BlockName + ".xml"
            : sourceFile.FileName;

        // 2026-09-10 资产补链：9/4 PLC 自包含改造移除了「上传后自动写 LineUp GRAPH 草稿」的联动
        // （见 PLC_UploadApp 注释，设计意图=LineUp 图谱统一走导入工作台免上传链路），
        // 但免上传链路从未建图谱资产（AI_LineUp_GraphAsset 只由 SaveDraftAsync 写入）——
        // 监控绑定挂资产，无资产的块 commit 后开监控只能跳过。
        // 这里在预览入队前用归档原文幂等建/复用 Draft（SourceHash 相同自动复用，零副作用），
        // 使免上传链路成为图谱资产的完整入口；失败仅记日志不阻断预览。
        if (graphDraftBuilder is not null && graphDraftApp is not null && block.ProjectId is long draftProjectId)
        {
            try
            {
                var identity = new LineUpPlcGraphBlockIdentity(
                    draftProjectId,
                    block.Id,
                    block.BlockName,
                    block.BlockType,
                    block.BlockNumber ?? 0,
                    block.ProgrammingLanguage);
                using var draftStream = new MemoryStream(Encoding.UTF8.GetBytes(sourceFile.FileContent));
                var candidate = await graphDraftBuilder.BuildAsync(
                    draftStream,
                    fileName,
                    identity,
                    "preview-from-block:" + block.Id,
                    cancellationToken);
                await graphDraftApp.CreateDraftAsync(candidate, cancellationToken);
            }
            catch (Exception draftError)
            {
                Console.WriteLine(
                    "[PreviewFromBlock] 图谱资产创建跳过 block=" + block.BlockName +
                    " -> " + draftError.GetType().Name + ": " + draftError.Message);
            }
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sourceFile.FileContent));
        return await StartPreviewCoreAsync(stream, fileName, lineId, deviceId, cancellationToken);
    }

    /// <summary>
    /// 预览生成核心链（文件上传与库内归档原文共用）：storage 落盘 → 建 batch → 入队解析。
    /// </summary>
    private async Task<LineUpImportStartedDto> StartPreviewCoreAsync(
        Stream sourceStream,
        string sourceFileName,
        long lineId,
        long deviceId,
        CancellationToken cancellationToken)
    {
        var batchId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        LineUpStoredImportFile stored;
        await using (var source = sourceStream)
        {
            stored = await storage.SaveAsync(batchId, sourceFileName, source, cancellationToken);
        }

        try
        {
            await repository.CreateBatchAsync(
                new(
                    batchId,
                    stored.StorageKey,
                    stored.SourceFileName,
                    stored.SourceFileHash,
                    lineId,
                    deviceId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await storage.DeleteAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }
        catch (Exception error)
        {
            Console.WriteLine($"[LineUpImport] BatchCreateFailed: {error}");
            await storage.DeleteAsync(stored.StorageKey, CancellationToken.None);
            throw new LineUpImportException(
                "BatchCreateFailed",
                stored.SourceFileName,
                "The import batch could not be created.");
        }

        try
        {
            scheduler.Enqueue(batchId);
        }
        catch
        {
            await repository.FailUploadAsync(batchId, "JobScheduleFailed", CancellationToken.None);
            await storage.DeleteAsync(stored.StorageKey, CancellationToken.None);
            throw new LineUpImportException(
                "JobScheduleFailed",
                stored.SourceFileName,
                "The import parsing job could not be scheduled.");
        }

        return new(batchId, "Uploaded");
    }

    private static Dictionary<string, string> BuildDecisionMap(
        IReadOnlyList<LineUpHierarchyDecisionDto> decisions)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var decision in decisions ?? [])
        {
            var sourceKey = decision.SequenceSourceKey?.Trim() ?? string.Empty;
            var candidateKey = decision.CandidateKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(candidateKey) ||
                !result.TryAdd(sourceKey, candidateKey))
            {
                throw ImportError("HierarchyDecisionInvalid", "Hierarchy decisions must be unique, complete server-owned key pairs.");
            }
        }

        return result;
    }

    private static Dictionary<string, string> BuildDisplayMap(
        IReadOnlyList<LineUpDisplayNameChangeDto> changes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in changes ?? [])
        {
            var sourceKey = change.SourceKey?.Trim() ?? string.Empty;
            var displayName = change.DisplayName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(displayName) ||
                displayName.Length > 200 || !result.TryAdd(sourceKey, displayName))
            {
                throw ImportError("DisplayNameChangeInvalid", "Display-name changes must be unique, non-empty, and at most 200 characters.");
            }
        }

        return result;
    }

    private static string? DisplayChange(IReadOnlyDictionary<string, string> changes, string sourceKey) =>
        changes.TryGetValue(sourceKey, out var value) ? value : null;

    private static bool SameAddress(LineUpAddressResolution stored, LineUpAddressResolution current) =>
        stored.PlcProjectId == current.PlcProjectId &&
        stored.GraphBlockId == current.GraphBlockId &&
        stored.InstanceDbBlockId == current.InstanceDbBlockId &&
        stored.StructuralStatus == current.StructuralStatus &&
        stored.CanCommitStructure == current.CanCommitStructure &&
        stored.Sequences.OrderBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
            .Select(x => (x.SequenceSourceKey, x.BindingStatus))
            .SequenceEqual(current.Sequences.OrderBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
                .Select(x => (x.SequenceSourceKey, x.BindingStatus)));

    /// <summary>
    /// 层级「骨架」比对：只看产线/设备/已解析项目号 + 顺控源键集合。
    /// 置信度也不比 —— 解析器对同一顺控的置信判定随层级世界（其它 GRAPH 块的
    /// 提交/改派/建基础层级）漂移，属于建议强度而非结构身份。
    /// </summary>
    private static bool SameHierarchySkeleton(LineUpHierarchyResolution stored, LineUpHierarchyResolution current)
    {
        if (stored.LineId != current.LineId || stored.DeviceId != current.DeviceId ||
            stored.ResolvedPlcProjectId != current.ResolvedPlcProjectId)
        {
            return false;
        }
        static string CanonicalKeys(LineUpHierarchyResolution resolution) => string.Join("|",
            resolution.Sequences
                .OrderBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
                .Select(x => x.SequenceSourceKey));
        return string.Equals(CanonicalKeys(stored), CanonicalKeys(current), StringComparison.Ordinal);
    }

    /// <summary>
    /// 层级漂移明细：哪侧 CanCommitStructure、顺控键差异、候选键差异 —— 写进拒绝错误与批次
    /// ErrorSummary，让每次 409 都能直接读到「差在哪」，不再靠推断。
    /// </summary>
    private static string DescribeHierarchyDrift(
        LineUpHierarchyResolution stored,
        LineUpHierarchyResolution current,
        bool currentAddressCanCommit)
    {
        var storedKeys = new HashSet<string>(
            stored.Sequences.Select(x => x.SequenceSourceKey), StringComparer.Ordinal);
        var currentKeys = new HashSet<string>(
            current.Sequences.Select(x => x.SequenceSourceKey), StringComparer.Ordinal);
        var storedCandidates = new HashSet<string>(
            stored.Sequences.SelectMany(x => x.Candidates.Select(c => x.SequenceSourceKey + ">" + c.CandidateKey)),
            StringComparer.Ordinal);
        var currentCandidates = new HashSet<string>(
            current.Sequences.SelectMany(x => x.Candidates.Select(c => x.SequenceSourceKey + ">" + c.CandidateKey)),
            StringComparer.Ordinal);
        static string Join(IEnumerable<string> items)
        {
            var text = string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal).Take(8));
            return text.Length == 0 ? "-" : text;
        }
        var detail =
            $"addressCanCommit[current={currentAddressCanCommit}];" +
            $"canCommitStructure[stored={stored.CanCommitStructure},current={current.CanCommitStructure};" +
            $"seqOnlyStored={Join(storedKeys.Except(currentKeys))};seqOnlyCurrent={Join(currentKeys.Except(storedKeys))};" +
            $"candOnlyStored={Join(storedCandidates.Except(currentCandidates))};candOnlyCurrent={Join(currentCandidates.Except(storedCandidates))}]";
        return detail.Length > 1500 ? detail[..1500] : detail;
    }

    private static bool SameHierarchy(LineUpHierarchyResolution stored, LineUpHierarchyResolution current)
    {
        if (stored.LineId != current.LineId || stored.DeviceId != current.DeviceId ||
            stored.ResolvedPlcProjectId != current.ResolvedPlcProjectId ||
            stored.CanCommitStructure != current.CanCommitStructure)
        {
            return false;
        }

        static IEnumerable<string> Canonical(LineUpHierarchyResolution resolution) =>
            resolution.Sequences
                .OrderBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
                .SelectMany(sequence => new[]
                    {
                        $"S|{sequence.SequenceSourceKey}|{sequence.ConfidenceLevel}|{sequence.ProcessId}|" +
                        $"{sequence.StationId}|{sequence.SubStationId}|{sequence.SelectedCandidateKey}"
                    }
                    .Concat(sequence.Candidates
                        .OrderBy(x => x.CandidateKey, StringComparer.Ordinal)
                        .Select(candidate =>
                            $"C|{sequence.SequenceSourceKey}|{candidate.CandidateKey}|{candidate.CandidateKind}|" +
                            $"{candidate.ProcessId}|{candidate.StationId}|{candidate.SubStationId}|" +
                            $"{candidate.ProposedSubStationCode}|{candidate.ProposedSubStationName}|" +
                            $"{candidate.ConfidenceLevel}|{string.Join(',', candidate.EvidenceCodes.OrderBy(x => x, StringComparer.Ordinal))}")))
                .Concat(resolution.Issues
                    .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                    .ThenBy(x => x.IssueType, StringComparer.Ordinal)
                    .ThenBy(x => x.Message, StringComparer.Ordinal)
                    .Select(x => $"I|{x.SourceKey}|{x.Severity}|{x.IssueType}|{x.Message}"));

        return Canonical(stored).SequenceEqual(Canonical(current), StringComparer.Ordinal);
    }

    private static LineUpGraphPreview RestoreGraph(LineUpImportDocumentDto document) => new()
    {
        SourceFileName = document.SourceFileName,
        SourceRelativePath = document.SourceRelativePath,
        SourceSha256 = document.SourceSha256,
        BlockName = document.BlockName,
        BlockNumber = document.BlockNumber,
        ProgrammingLanguage = document.ProgrammingLanguage,
        InstanceDbName = document.InstanceDbHint,
        Sequences = document.Sequences.Select(sequence => new LineUpGraphPreviewSequence
        {
            SourceKey = sequence.SourceKey,
            SourceName = sequence.SourceName,
            DisplayName = sequence.DisplayName,
            Nodes = sequence.Nodes.Select(node => new LineUpGraphPreviewNode
            {
                NodeKind = node.NodeKind,
                SourceKey = node.SourceKey,
                SourceName = node.SourceName,
                DisplayName = node.DisplayName,
                Number = node.Number,
                IsInitial = node.IsInitial,
                IsFinal = node.IsFinal,
                MaximumStepTime = node.MaximumStepTime,
                WarningTime = node.WarningTime,
                Comment = node.Comment,
                ProgrammingLanguage = node.ProgrammingLanguage,
                IsMissing = node.IsMissing,
                IsJump = node.IsJump
            }).ToList(),
            Actions = sequence.Actions.Select(action => new LineUpGraphPreviewAction
            {
                SourceKey = action.SourceKey,
                StepSourceKey = action.StepSourceKey,
                SourceName = action.SourceName,
                DisplayName = action.DisplayName,
                Ordinal = action.Ordinal,
                StepNumber = action.StepNumber,
                Qualifier = action.Qualifier,
                Tokens = action.Tokens.ToList(),
                ActionText = action.ActionText
            }).ToList(),
            Edges = sequence.Edges.Select(edge => new LineUpGraphPreviewEdge
            {
                SourceKey = edge.SourceKey,
                FromSourceKey = edge.FromSourceKey,
                ToSourceKey = edge.ToSourceKey,
                FromNodeKind = edge.FromNodeKind,
                ToNodeKind = edge.ToNodeKind,
                EdgeKind = edge.EdgeKind,
                BranchOrder = edge.BranchOrder
            }).ToList()
        }).ToList()
    };

    private static string HashCommitRequest(long batchId, LineUpImportCommitRequest request)
    {
        var decisions = string.Join("\n", (request.HierarchyDecisions ?? [])
            .OrderBy(x => x.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.CandidateKey, StringComparer.Ordinal)
            .Select(x => $"H|{x.SequenceSourceKey?.Trim()}|{x.CandidateKey?.Trim()}"));
        var displays = string.Join("\n", (request.DisplayNameChanges ?? [])
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.DisplayName, StringComparer.Ordinal)
            .Select(x => $"D|{x.SourceKey?.Trim()}|{x.DisplayName?.Trim()}"));
        var material = $"lineup-commit-v1|{batchId}|{request.CommitMode?.Trim()}|{request.PreviewRevision}|" +
                       $"{request.PreviewHash?.Trim()}|{request.DiffToken?.Trim()}\n{decisions}\n{displays}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static LineUpImportCommitReceiptDto DeserializeReceipt(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LineUpImportCommitReceiptDto>(json) ??
                   throw ImportError("CommitReceiptInvalid", "The stored commit receipt is empty.");
        }
        catch (JsonException)
        {
            throw ImportError("CommitReceiptInvalid", "The stored commit receipt is invalid.");
        }
    }

    private static LineUpImportException ImportError(string code, string message) =>
        new(code, "source", message);

    private static readonly HashSet<string> SafePersistenceCodes = new(StringComparer.Ordinal)
    {
        "PreviewStale",
        "GraphOwnerMissing",
        "HierarchyOwnerMissing",
        "HierarchyCandidateInvalid",
        "HierarchyCandidateAmbiguous",
        "HierarchyCandidateStale",
        "DuplicateSourceKey",
        "SourceKeyOwnerConflict",
        "GraphNodeKindInvalid",
        "GraphActionOwnerInvalid",
        "GraphEdgeEndpointInvalid",
        "BindingOwnerConflict"
    };
}

/// <summary>
/// 批次表手写 SQL 查询的行绑定类型（无参构造 class；SqlSugar 对 record 投影绑定不稳定，
/// 见 LineUpImportRepository.GetBatchAsync 注释）。
/// </summary>
public sealed class LineUpLatestPreviewRow
{
    public long BatchId { get; set; }
}
