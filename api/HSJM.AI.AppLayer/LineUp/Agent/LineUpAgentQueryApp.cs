using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.ImageMarks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp.Agent;

/// <summary>Agent 查询业务异常（Code 映射 HTTP 状态）。</summary>
public sealed class LineUpAgentQueryException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record LineUpAgentStepDto(
    int StepNumber,
    string? DisplayName,
    string SourceKey,
    string? SequenceSourceKey,
    string? BlockName,
    string? BomRefNo,
    string? TagCode);

public sealed record LineUpAgentBomImageResultDto(
    LineUpImageHierarchyDto SubStation,
    IReadOnlyList<LineUpAgentStepDto> Steps,
    IReadOnlyList<LineUpImageDto> Images,
    IReadOnlyList<LineUpImageBomDto> Boms,
    IReadOnlyList<LineUpImageMarkerDto> Markers);

public sealed record LineUpAgentLineDto(long Id, string? Code, string? Name);

public sealed record LineUpAgentWorkStationDto(long Id, string? Code, string? Name);

public sealed record LineUpAgentTagValueDto(
    string TagName,
    bool IsGood,
    object? Value,
    string? ErrorMessage);

public sealed record LineUpAgentProjectValuesDto(
    long PlcProjectId,
    string? ProjectName,
    IReadOnlyList<string> Devices,
    int TotalCount,
    IReadOnlyList<LineUpAgentTagValueDto> Tags);

public sealed record LineUpAgentValueFilterDto(
    string Keyword,
    string Mode,
    IReadOnlyList<string> TagCodes,
    IReadOnlyList<string> BomRefNos);

public sealed record LineUpAgentPointValuesResultDto(
    LineUpAgentLineDto? Line,
    LineUpAgentWorkStationDto WorkStation,
    LineUpAgentValueFilterDto? Filter,
    IReadOnlyList<LineUpAgentProjectValuesDto> Projects);

/// <summary>
/// 供外部 Agent 只读查询：BOM 点位图片（工站/子工站/工步/BOM 编码）、实时点位值（产线+工站编码解析 projectId）。
/// </summary>
public sealed class LineUpAgentQueryApp(
    ILineUpAgentQueryRepository repository,
    LineUpImageMarkApp imageMarkApp,
    PLC_VariableAccessApp variableAccessApp) : ISelfScopedAutoInject
{
    /// <summary>
    /// 按工站编码获取 BOM 点位图片（含点位标记几何与图片内容地址）。
    /// subStationCode 可选：传入=精确定位该子工站；不传时必须给 bomCode——
    /// 后端在该工站全部子工站内按 BOM 编码自动定位（BOM 编码工站内不重名，唯一命中）。
    /// stepNumber 与 bomCode 至少给一个：stepNumber 走 GRAPH 工步→节点 BOM 关联（AI_LineUp_GraphNodeBom），
    /// bomCode 精确匹配 RefNo/TagCode；两者同给取交集。
    /// </summary>
    public async Task<LineUpAgentBomImageResultDto> GetBomPointImageAsync(
        string workStationCode,
        string? subStationCode,
        int? stepNumber,
        string? bomCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workStationCode))
        {
            throw Error("AgentCodesRequired", "WorkStationCode is required.");
        }
        if (stepNumber is null && string.IsNullOrWhiteSpace(bomCode))
        {
            throw Error("AgentFilterRequired", "StepNumber or BomCode is required.");
        }
        if (string.IsNullOrWhiteSpace(subStationCode) && string.IsNullOrWhiteSpace(bomCode))
        {
            throw Error("AgentFilterRequired",
                "SubStationCode is required when bomCode is not provided (step-only lookup needs a substation).");
        }

        // 子工站定位：传编码=精确定位；未传=按 bomCode 在工站全部子工站内搜索（工站内 BOM 不重名→唯一命中）
        long subStationId;
        LineUpImageWorkspaceDto? prefetchedWorkspace = null;
        if (!string.IsNullOrWhiteSpace(subStationCode))
        {
            var rows = await repository.FindSubStationsAsync(workStationCode, subStationCode, null, cancellationToken);
            if (rows.Count == 0)
            {
                throw Error("AgentSubStationNotFound", "The substation does not exist for the given codes.");
            }
            if (rows.Count > 1)
            {
                var lines = string.Join(", ", rows.Select(row =>
                    $"'{row.ProductionLineCode ?? row.ProductionLineId.ToString()}'").Distinct());
                throw Error("AgentSubStationAmbiguous",
                    $"The codes match substations on multiple lines ({lines}); narrow the lookup.");
            }
            subStationId = rows[0].SubStationId;
        }
        else
        {
            var stations = await repository.FindWorkStationsAsync(workStationCode, null, cancellationToken);
            if (stations.Count == 0)
            {
                throw Error("AgentWorkStationNotFound", "The work station does not exist.");
            }
            if (stations.Count > 1)
            {
                var lines = string.Join(", ", stations.Select(row =>
                    $"'{row.ProductionLineCode ?? row.ProductionLineId.ToString()}'").Distinct());
                throw Error("AgentWorkStationAmbiguous",
                    $"The work station code matches stations on multiple lines ({lines}); narrow the lookup.");
            }
            var subs = await repository.ListSubStationsByWorkStationAsync(stations[0].Id, cancellationToken);
            if (subs.Count == 0)
            {
                throw Error("AgentSubStationNotFound", "The work station has no substations.");
            }

            var code = bomCode!.Trim();
            LineUpAgentSubStationBriefRow? hit = null;
            foreach (var sub in subs)
            {
                var candidate = await imageMarkApp.GetStationAsync(sub.SubStationId, null, cancellationToken);
                var matched = candidate.Boms.Any(bom =>
                    string.Equals(bom.RefNo?.Trim(), code, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bom.TagCode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
                if (!matched)
                {
                    continue;
                }
                if (hit is not null)
                {
                    throw Error("AgentBomAmbiguous",
                        $"The BOM code matches substations '{hit.SubStationCode}' and '{sub.SubStationCode}'.");
                }
                hit = sub;
                prefetchedWorkspace = candidate;
            }
            if (hit is null)
            {
                throw Error("AgentBomNotFound", "The BOM code does not exist in this work station.");
            }
            subStationId = hit.SubStationId;
        }

        // 复用既有工作台读取（层级 + 图片 + BOM + 标记），过滤在本层做（bomCode 自动定位时复用已取的 workspace）
        var workspace = prefetchedWorkspace
            ?? await imageMarkApp.GetStationAsync(subStationId, null, cancellationToken);

        IReadOnlyList<LineUpAgentStepDto>? steps = null;
        HashSet<long>? bomFilter = null;

        if (stepNumber is > 0)
        {
            var bindings = await repository.ListBindingsBySubStationsAsync(
                [subStationId], cancellationToken);
            var sequenceIds = bindings.Where(row => row.SequenceId > 0)
                .Select(row => row.SequenceId).Distinct().ToArray();
            var stepRows = await repository.FindStepsAsync(sequenceIds, stepNumber.Value, cancellationToken);
            if (stepRows.Count == 0)
            {
                throw Error("AgentStepNotFound", "The step number is not found for this substation.");
            }

            var nodeKeys = stepRows.Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).ToArray();
            var nodeBoms = await repository.ListNodeBomsAsync(subStationId, nodeKeys, cancellationToken);
            // 跨批次：同一节点只取最新批次（结果按 BatchId 降序，首见即最新）
            var latest = new Dictionary<string, List<LineUpAgentNodeBomRow>>(StringComparer.Ordinal);
            foreach (var row in nodeBoms)
            {
                if (!latest.TryGetValue(row.NodeSourceKey, out var bucket))
                {
                    bucket = [];
                    latest[row.NodeSourceKey] = bucket;
                }
                if (bucket.Count == 0 || bucket[0].BatchId == row.BatchId)
                {
                    bucket.Add(row);
                }
            }

            steps = latest.SelectMany(pair => pair.Value)
                .Select(row => new LineUpAgentStepDto(
                    stepNumber.Value,
                    stepRows.FirstOrDefault(step => step.SourceKey == row.NodeSourceKey)?.DisplayName,
                    row.NodeSourceKey,
                    row.SequenceSourceKey,
                    row.BlockName,
                    row.BomRefNo,
                    row.TagCode))
                .OrderBy(dto => dto.SequenceSourceKey, StringComparer.Ordinal)
                .ThenBy(dto => dto.SourceKey, StringComparer.Ordinal)
                .ToArray();
            bomFilter = latest.SelectMany(pair => pair.Value)
                .Where(row => row.BomId > 0)
                .Select(row => row.BomId)
                .ToHashSet();
        }

        if (!string.IsNullOrWhiteSpace(bomCode))
        {
            var code = bomCode.Trim();
            var exact = workspace.Boms
                .Where(bom => string.Equals(bom.RefNo?.Trim(), code, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(bom.TagCode?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                .Select(bom => bom.Id)
                .ToHashSet();
            if (exact.Count == 0)
            {
                throw Error("AgentBomNotFound", "The BOM code does not exist in this substation.");
            }
            bomFilter = bomFilter is null
                ? exact
                : new HashSet<long>(bomFilter.Where(exact.Contains));
        }

        var boms = bomFilter is null
            ? workspace.Boms
            : workspace.Boms.Where(bom => bomFilter.Contains(bom.Id)).ToArray();
        var markers = bomFilter is null
            ? workspace.Markers
            : workspace.Markers.Where(marker => marker.BomIds.Any(bomFilter.Contains)).ToArray();
        var imageIds = markers.Select(marker => marker.ImageId).ToHashSet();
        var images = bomFilter is null
            ? workspace.Images
            : workspace.Images.Where(image => imageIds.Contains(image.Id)).ToArray();

        return new(workspace.SubStation, steps ?? [], images, boms, markers);
    }

    /// <summary>
    /// 按产线 + 工站编码解析 PLC 项目并读实时点位值。
    /// projectId 解析优先级：子工站结构绑定（AI_LineUp_SubStationGraphBinding.PlcProjectId）
    /// → 设备 ProjectName 与 ai_plc_project_new 精确匹配（LOWER TRIM）。
    /// </summary>
    public async Task<LineUpAgentPointValuesResultDto> GetPointValuesAsync(
        string? line,
        string workStationCode,
        string? tagKeyword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workStationCode))
        {
            throw Error("AgentCodesRequired", "WorkStationCode is required.");
        }

        LineUpAgentLineDto? lineDto = null;
        long? lineId = null;
        if (!string.IsNullOrWhiteSpace(line))
        {
            var key = line.Trim();
            LineUpAgentLineRow? row;
            if (long.TryParse(key, out var parsedId))
            {
                row = await repository.FindLineByIdAsync(parsedId, cancellationToken);
            }
            else
            {
                var rows = await repository.FindLinesAsync(key, cancellationToken);
                if (rows.Count > 1)
                {
                    throw Error("AgentLineAmbiguous", "The line key matches multiple lines.");
                }
                row = rows.Count == 1 ? rows[0] : null;
            }
            if (row is null)
            {
                throw Error("AgentLineNotFound", "The line does not exist.");
            }
            lineDto = new(row.Id, row.Code, row.Name);
            lineId = row.Id;
        }

        var stations = await repository.FindWorkStationsAsync(workStationCode, lineId, cancellationToken);
        if (stations.Count == 0)
        {
            throw Error("AgentWorkStationNotFound", "The work station does not exist for the given line.");
        }
        if (stations.Count > 1)
        {
            var lines = string.Join(", ", stations.Select(row =>
                $"'{row.ProductionLineCode ?? row.ProductionLineId.ToString()}'").Distinct());
            throw Error("AgentWorkStationAmbiguous",
                $"The work station code matches stations on multiple lines ({lines}); provide the line.");
        }
        var station = stations[0];
        if (lineDto is null)
        {
            lineDto = new LineUpAgentLineDto(
                station.ProductionLineId, station.ProductionLineCode, station.ProductionLineName);
        }

        var subStations = await repository.ListSubStationsByWorkStationAsync(station.Id, cancellationToken);
        var deviceIds = subStations.Where(row => row.PlcId > 0).Select(row => row.PlcId).Distinct().ToArray();
        var devices = await repository.ListDevicesAsync(deviceIds, cancellationToken);

        // projectId 解析：绑定优先，设备 ProjectName 兜底
        var bindings = await repository.ListBindingsBySubStationsAsync(
            subStations.Select(row => row.SubStationId).ToArray(), cancellationToken);
        var projectIds = new HashSet<long>();
        var bindingDeviceByProject = new Dictionary<long, HashSet<string>>();
        foreach (var binding in bindings)
        {
            if (binding.PlcProjectId <= 0)
            {
                continue;
            }
            projectIds.Add(binding.PlcProjectId);
            var deviceName = devices.FirstOrDefault(device => device.Id == binding.DeviceId)?.DeviceName;
            if (!string.IsNullOrWhiteSpace(deviceName) &&
                bindingDeviceByProject.TryGetValue(binding.PlcProjectId, out var names))
            {
                names.Add(deviceName);
            }
            else if (!string.IsNullOrWhiteSpace(deviceName))
            {
                bindingDeviceByProject[binding.PlcProjectId] = [deviceName];
            }
        }

        if (projectIds.Count == 0 && devices.Count > 0)
        {
            var byName = await repository.FindProjectsByNameAsync(
                devices.Where(device => !string.IsNullOrWhiteSpace(device.ProjectName))
                    .Select(device => device.ProjectName!)
                    .ToArray(),
                cancellationToken);
            foreach (var project in byName)
            {
                projectIds.Add(project.Id);
                var deviceNames = devices
                    .Where(device => string.Equals(
                        (device.ProjectName ?? string.Empty).Trim(), project.ProjectName?.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .Select(device => device.DeviceName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>();
                bindingDeviceByProject[project.Id] = [.. deviceNames];
            }
        }

        if (projectIds.Count == 0)
        {
            throw Error("AgentPlcProjectNotFound",
                "No PLC project is resolved for this work station (no graph binding and no device project name match).");
        }

        var projectNames = (await repository.FindProjectsByIdAsync(projectIds, cancellationToken))
            .ToDictionary(row => row.Id, row => row.ProjectName);
        var keyword = string.IsNullOrWhiteSpace(tagKeyword) ? null : tagKeyword.Trim();

        // tagKeyword 支持标签名或 BOM 编码：先在工站 BOM 清单里按编码（RefNo/TagCode 精确匹配）解析 TagCode，
        // 解析到就用 TagCode 精确过滤；解析不到回退为标签名包含匹配。
        LineUpAgentValueFilterDto? filter = null;
        HashSet<string>? bomTagCodes = null;
        if (keyword is not null)
        {
            var boms = await repository.FindBomsByCodeAsync(
                subStations.Select(row => row.SubStationId).ToArray(), keyword, cancellationToken);
            var codes = boms
                .Where(bom => !string.IsNullOrWhiteSpace(bom.TagCode))
                .Select(bom => bom.TagCode!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (codes.Count > 0)
            {
                bomTagCodes = codes;
                filter = new(
                    keyword,
                    "BomTagCode",
                    [.. codes.OrderBy(code => code, StringComparer.Ordinal)],
                    [.. boms.Where(bom => !string.IsNullOrWhiteSpace(bom.RefNo))
                        .Select(bom => bom.RefNo!.Trim())
                        .Distinct()]);
            }
            else
            {
                filter = new(keyword, "TagNameKeyword", [], []);
            }
        }

        var projects = new List<LineUpAgentProjectValuesDto>();
        foreach (var projectId in projectIds.OrderBy(id => id))
        {
            List<LineUpAgentTagValueDto> tags;
            try
            {
                var values = await variableAccessApp.GetReadData(projectId);
                tags = values
                    .Where(item => MatchesFilter(item, keyword, bomTagCodes))
                    .Select(item => new LineUpAgentTagValueDto(
                        item.TagName ?? item.OldTagName ?? string.Empty,
                        item.IsGood,
                        item.Value,
                        item.ErrorMessage))
                    .ToList();
            }
            catch (Exception error)
            {
                // 单项目采集失败不影响其余项目返回
                tags = [new("(readFailed)", false, null, error.Message)];
            }
            bindingDeviceByProject.TryGetValue(projectId, out var deviceNames);
            projects.Add(new(
                projectId,
                projectNames.GetValueOrDefault(projectId),
                deviceNames is null ? [] : [.. deviceNames.OrderBy(name => name, StringComparer.Ordinal)],
                tags.Count,
                tags));
        }

        return new(lineDto, new(station.Id, station.Code, station.Name), filter, projects);
    }

    /// <summary>BOM 编码命中（bomTagCodes 非空）时按 TagCode 精确匹配；否则按标签名包含匹配。</summary>
    private static bool MatchesFilter(PLC_ReadTagResult item, string? keyword, HashSet<string>? bomTagCodes)
    {
        if (keyword is null)
        {
            return true;
        }
        if (bomTagCodes is not null)
        {
            var name = (item.TagName ?? item.OldTagName ?? string.Empty).Trim();
            return bomTagCodes.Contains(name);
        }
        return item.TagName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static LineUpAgentQueryException Error(string code, string message) => new(code, message);
}
