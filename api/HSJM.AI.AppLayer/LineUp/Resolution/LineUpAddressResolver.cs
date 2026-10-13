using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp.Resolution;

public sealed class LineUpAddressResolver(
    ILineUpHierarchyRepository hierarchyRepository,
    ILineUpPlcMetadataRepository plcRepository) : ISelfScopedAutoInject
{
    public async Task<LineUpVariableAddressResolution> ResolveVariableAsync(
        long plcProjectId,
        long instanceDbBlockId,
        string canonicalVariablePath,
        CancellationToken cancellationToken)
    {
        var path = canonicalVariablePath?.Trim() ?? string.Empty;
        var issues = new List<LineUpResolutionIssue>();
        if (plcProjectId <= 0 || instanceDbBlockId <= 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "InstanceDbBlockMissing", "The supplied PLC project or InstanceDB block identity is invalid."));
            return VariableResult(plcProjectId, instanceDbBlockId, null, null, LineUpBindingStatus.Unresolved, issues);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "VariablePathMissing", "A complete canonical variable path is required."));
            return VariableResult(plcProjectId, instanceDbBlockId, null, null, LineUpBindingStatus.Unresolved, issues);
        }

        var data = await plcRepository.LoadVariableDataAsync(
            plcProjectId,
            instanceDbBlockId,
            [path],
            cancellationToken);
        var blocks = data.Blocks
            .Where(x => x.Id == instanceDbBlockId && x.ProjectId == plcProjectId &&
                        EqualsNormalized(x.ProgrammingLanguage, "DB") &&
                        EqualsNormalized(x.BlockType, "InstanceDB"))
            .OrderBy(x => x.Id)
            .ToArray();
        if (blocks.Length == 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "InstanceDbBlockMissing", "The supplied block is not an active DB/InstanceDB in the supplied PLC project."));
            return VariableResult(plcProjectId, instanceDbBlockId, null, null, LineUpBindingStatus.Unresolved, issues);
        }

        if (blocks.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "InstanceDbBlockAmbiguous", "The supplied InstanceDB identity is not unique."));
            return VariableResult(plcProjectId, instanceDbBlockId, null, null, LineUpBindingStatus.Unresolved, issues);
        }

        var block = blocks[0];
        var interfaces = data.Interfaces
            .Where(x => x.Id > 0 && x.BlockId == instanceDbBlockId && EqualsNormalized(x.VariablePath, path))
            .OrderBy(x => x.Id)
            .ToArray();
        if (interfaces.Length == 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "BlockInterfaceMissing", "No active BlockInterface exactly matches the complete canonical variable path."));
            return VariableResult(plcProjectId, instanceDbBlockId, block, null, LineUpBindingStatus.Unresolved, issues);
        }

        if (interfaces.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "BlockInterfaceAmbiguous", "More than one active BlockInterface exactly matches the complete canonical variable path."));
            return VariableResult(plcProjectId, instanceDbBlockId, block, null, LineUpBindingStatus.Unresolved, issues);
        }

        var row = interfaces[0];
        var metadataValid = true;
        if (block.BlockNumber is null or <= 0)
        {
            metadataValid = false;
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbBlockNumberMissing", "The InstanceDB block has no valid stored block number."));
        }

        if (string.IsNullOrWhiteSpace(block.BlockName))
        {
            metadataValid = false;
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbBlockNameMissing", "The InstanceDB block has no safe stored block name."));
        }

        if (string.IsNullOrWhiteSpace(row.VariablePath))
        {
            metadataValid = false;
            issues.Add(Issue(LineUpIssueSeverity.Warning, "VariablePathMissing", "The matched BlockInterface has no stored variable path."));
        }

        if (string.IsNullOrWhiteSpace(row.DataType))
        {
            metadataValid = false;
            issues.Add(Issue(LineUpIssueSeverity.Warning, "VariableDataTypeMissing", "The matched BlockInterface has no stored data type."));
        }

        if (row.OffsetByte is null || row.SizeBits is null || string.IsNullOrWhiteSpace(row.Offset))
        {
            metadataValid = false;
            issues.Add(Issue(LineUpIssueSeverity.Warning, "StoredOffsetMissing", "The matched BlockInterface has incomplete stored offset metadata."));
        }

        issues.Add(Issue(LineUpIssueSeverity.Warning, "CanonicalNumericAddressUnavailable", "No authoritative standard-versus-optimized access metadata is available, so no numeric address is inferred."));
        issues.Add(Issue(LineUpIssueSeverity.Warning, "RuntimeReadValidationPending", "No IGW/device runtime read is performed in Phase 1."));
        return VariableResult(
            plcProjectId,
            instanceDbBlockId,
            block,
            row,
            metadataValid ? LineUpBindingStatus.NeedsReview : LineUpBindingStatus.Unresolved,
            issues);
    }

    public async Task<LineUpAddressResolution> ResolveAsync(
        long lineId,
        long deviceId,
        LineUpGraphPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var sourceKeys = preview.Sequences.Select(x => x.SourceKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var hierarchy = await hierarchyRepository.LoadContextAsync(
            lineId,
            deviceId,
            sourceKeys,
            cancellationToken);
        var issues = new List<LineUpResolutionIssue>();
        var candidates = new List<LineUpAddressCandidate>();

        if (hierarchy.Line is null || hierarchy.Line.Id <= 0 || hierarchy.Line.Id != lineId)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "LineMissing", "The selected production line is unavailable."));
            return Result(lineId, deviceId, preview, null, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        if (hierarchy.Device is null || hierarchy.Device.Id <= 0 || hierarchy.Device.Id != deviceId)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "DeviceMissing", "The selected PLC device is unavailable."));
            return Result(lineId, deviceId, preview, null, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        if (hierarchy.Device.LineId is null || hierarchy.Device.LineId <= 0 || hierarchy.Device.LineId != lineId)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "DeviceLineMismatch", "The selected PLC device is outside the selected production line."));
            return Result(lineId, deviceId, preview, null, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        // 设备 → PLC 项目：优先用项目详情页「绑定 PLC 设备」的 ID 关联（ai_plc_project_new.plcdeviceid），
        // 设备 ProjectName 与项目名的精确匹配降级为兜底（历史数据/未绑定设备的项目）。
        // 名字漂移（如项目叫 PLC1六面检测、设备 ProjectName 只写 六面检测）不再阻断解析。
        var selectedProjectName = hierarchy.Device.ProjectName?.Trim();
        var data = await plcRepository.LoadResolutionDataByDeviceIdAsync(
            deviceId,
            preview.BlockName,
            preview.BlockNumber,
            cancellationToken);
        var projects = data.Projects
            .Where(x => x.Id > 0)
            .OrderBy(x => x.Id)
            .ToArray();
        var projectEvidence = "DeviceBindingId";
        if (projects.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(selectedProjectName))
            {
                issues.Add(Issue(LineUpIssueSeverity.Blocking, "PlcProjectMissing", "The selected device has no usable PLC project identity."));
                return Result(lineId, deviceId, preview, null, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
            }

            data = await plcRepository.LoadResolutionDataAsync(
                selectedProjectName,
                preview.BlockName,
                preview.BlockNumber,
                cancellationToken);
            projects = data.Projects
                .Where(x => x.Id > 0 && EqualsNormalized(x.ProjectName, selectedProjectName))
                .OrderBy(x => x.Id)
                .ToArray();
            projectEvidence = "ExactProjectName";
        }
        foreach (var projectCandidate in projects)
        {
            candidates.Add(Candidate("PlcProject", projectCandidate.Id, projectCandidate.ProjectName, projectEvidence));
        }

        if (projects.Length == 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "PlcProjectMissing", "No active PLC project matches the selected device project name."));
            return Result(lineId, deviceId, preview, null, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        if (projects.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "PlcProjectAmbiguous", "More than one active PLC project matches the selected device project name."));
            return Result(lineId, deviceId, preview, null, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        var project = projects[0];
        if (preview.BlockNumber <= 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "GraphBlockNumberInvalid", "The imported GRAPH FB has no valid block number."));
            return Result(lineId, deviceId, preview, project, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        var graphUniverse = data.Blocks
            .Where(x => x.Id > 0 && x.ProjectId == project.Id &&
                        EqualsNormalized(x.ProgrammingLanguage, "GRAPH") &&
                        EqualsNormalized(x.BlockType, "FB"))
            .OrderBy(x => x.Id)
            .ToArray();
        var nameMatches = graphUniverse.Where(x => EqualsNormalized(x.BlockName, preview.BlockName)).ToArray();
        var numberMatches = graphUniverse.Where(x => x.BlockNumber == preview.BlockNumber).ToArray();
        var exactGraphs = graphUniverse
            .Where(x => EqualsNormalized(x.BlockName, preview.BlockName) && x.BlockNumber == preview.BlockNumber)
            .ToArray();
        foreach (var graphCandidate in nameMatches.Concat(numberMatches).DistinctBy(x => x.Id).OrderBy(x => x.Id))
        {
            candidates.Add(Candidate("GraphBlock", graphCandidate.Id, graphCandidate.BlockName, "GraphIdentityCandidate"));
        }

        var hasIdentityConflict = nameMatches.Length > 0 && numberMatches.Length > 0 &&
                                  nameMatches.Select(x => x.Id).OrderBy(x => x)
                                      .SequenceEqual(numberMatches.Select(x => x.Id).OrderBy(x => x)) == false;
        if (hasIdentityConflict)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "GraphBlockIdentityConflict", "GRAPH FB name and number identify different active blocks."));
            return Result(lineId, deviceId, preview, project, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        if (exactGraphs.Length == 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "GraphBlockMissing", "No active FB/GRAPH block matches the imported immutable name and number."));
            return Result(lineId, deviceId, preview, project, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        if (exactGraphs.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "GraphBlockAmbiguous", "More than one active FB/GRAPH block matches the imported immutable identity."));
            return Result(lineId, deviceId, preview, project, null, null, LineUpStructuralStatus.Blocked, candidates, issues);
        }

        var graph = exactGraphs[0];
        var relevantCalls = data.CallSites
            .Where(x => x.ProjectId == project.Id &&
                        EqualsNormalized(x.CalleeBlockName, preview.BlockName) &&
                        EqualsNormalized(x.CalleeBlockType, "FB"))
            .OrderBy(x => x.CallId)
            .ToArray();
        if (relevantCalls.Any(x => x.BindingParseStatus == LineUpCallBindingParseStatus.MultipleMatchingBindings))
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbAmbiguous", "A matching callsite contains conflicting InstanceDB bindings."));
            return Result(lineId, deviceId, preview, project, graph, null, LineUpStructuralStatus.Ready, candidates, issues);
        }

        var instanceNames = relevantCalls
            .Where(x => x.CallId > 0 && x.CallerBlockId > 0 && x.LogicUnitId > 0 &&
                        x.BindingParseStatus == LineUpCallBindingParseStatus.Parsed &&
                        !string.IsNullOrWhiteSpace(x.InstanceDbName))
            .Select(x => x.InstanceDbName!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (instanceNames.Length == 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbMissing", "No usable callsite proves the GRAPH FB InstanceDB."));
            return Result(lineId, deviceId, preview, project, graph, null, LineUpStructuralStatus.Ready, candidates, issues);
        }

        if (instanceNames.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbAmbiguous", "Usable callsites name more than one distinct GRAPH InstanceDB."));
            return Result(lineId, deviceId, preview, project, graph, null, LineUpStructuralStatus.Ready, candidates, issues);
        }

        var dbBlocks = data.Blocks
            .Where(x => x.Id > 0 && x.ProjectId == project.Id &&
                        EqualsNormalized(x.BlockName, instanceNames[0]) &&
                        EqualsNormalized(x.ProgrammingLanguage, "DB") &&
                        EqualsNormalized(x.BlockType, "InstanceDB"))
            .OrderBy(x => x.Id)
            .ToArray();
        foreach (var dbCandidate in dbBlocks)
        {
            candidates.Add(Candidate("InstanceDbBlock", dbCandidate.Id, dbCandidate.BlockName, "ExactInstanceDbName"));
        }

        if (dbBlocks.Length == 0)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbBlockMissing", "The proven InstanceDB name has no unique active DB/InstanceDB block in the selected project."));
            return Result(lineId, deviceId, preview, project, graph, null, LineUpStructuralStatus.Ready, candidates, issues);
        }

        if (dbBlocks.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "InstanceDbBlockAmbiguous", "The proven InstanceDB name matches more than one active DB/InstanceDB block."));
            return Result(lineId, deviceId, preview, project, graph, null, LineUpStructuralStatus.Ready, candidates, issues);
        }

        // Phase 2：实例 DB 已证明。查 GRAPH 步位台账（ai_plc_graphsteptag）确认 EXEC_BITS 符号路径已注册。
        var ledger = await plcRepository.LoadGraphStepLedgerAsync(
            project.Id,
            preview.BlockName,
            cancellationToken);
        if (ledger is { HasExecBitsArray: true })
        {
            candidates.Add(Candidate("RuntimeLedger", dbBlocks[0].Id, ledger.ExecBitsArrayDbAddress, "ExecBitsLedgerReady"));
        }
        else
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "RuntimeVariablePathsPending", "No GRAPH step ledger (ai_plc_graphsteptag) EXEC_BITS paths are registered for this FB; run the GRAPH step registration before enabling monitoring."));
        }

        issues.Add(Issue(LineUpIssueSeverity.Warning, "RuntimeReadValidationPending", "No IGW/device runtime read is performed in Phase 1; the binding must pass Prepare/Validate before monitoring starts."));
        return Result(lineId, deviceId, preview, project, graph, dbBlocks[0], LineUpStructuralStatus.Ready, candidates, issues);
    }

    private static LineUpAddressResolution Result(
        long lineId,
        long deviceId,
        LineUpGraphPreview preview,
        LineUpProjectLookup? project,
        LineUpBlockLookup? graph,
        LineUpBlockLookup? instanceDb,
        LineUpStructuralStatus structuralStatus,
        IEnumerable<LineUpAddressCandidate> candidates,
        IEnumerable<LineUpResolutionIssue> issues)
    {
        var resultIssues = issues.ToList();
        if (preview.Issues.Any(x =>
                string.Equals(x.Severity?.Trim(), "Blocking", StringComparison.OrdinalIgnoreCase)))
        {
            structuralStatus = LineUpStructuralStatus.Blocked;
            resultIssues.Add(Issue(
                LineUpIssueSeverity.Blocking,
                "GraphTopologyBlocking",
                "The normalized GRAPH contains a Blocking topology issue and cannot be committed."));
        }

        var runtimeStatus = instanceDb is null
            ? LineUpBindingStatus.Unresolved
            : LineUpBindingStatus.NeedsReview;
        return new(
            lineId,
            deviceId,
            project?.Id,
            project?.ProjectName?.Trim(),
            graph?.Id,
            graph?.BlockName?.Trim(),
            instanceDb?.Id,
            instanceDb?.BlockName?.Trim(),
            structuralStatus,
            structuralStatus == LineUpStructuralStatus.Ready,
            false,
            preview.Sequences.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                .Select(x => new LineUpSequenceAddressResolution(x.SourceKey, runtimeStatus))
                .ToArray(),
            candidates.OrderBy(x => x.CandidateKind, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray(),
            resultIssues.OrderBy(x => x.IssueType, StringComparer.Ordinal).ThenBy(x => x.SourceKey, StringComparer.Ordinal).ToArray());
    }

    private static LineUpVariableAddressResolution VariableResult(
        long plcProjectId,
        long instanceDbBlockId,
        LineUpBlockLookup? block,
        LineUpBlockInterfaceLookup? row,
        LineUpBindingStatus status,
        IEnumerable<LineUpResolutionIssue> issues)
    {
        var symbolicPath = !string.IsNullOrWhiteSpace(block?.BlockName) && !string.IsNullOrWhiteSpace(row?.VariablePath)
            ? $"{block.BlockName!.Trim()}.{row.VariablePath!.Trim()}"
            : null;
        return new(
            plcProjectId,
            instanceDbBlockId,
            row?.Id,
            row?.VariablePath?.Trim(),
            row?.DataType?.Trim(),
            row?.OffsetByte,
            row?.OffsetBit,
            row?.SizeBits,
            row?.Offset?.Trim(),
            symbolicPath,
            LineUpAddressMode.Unknown,
            null,
            status,
            issues.OrderBy(x => x.IssueType, StringComparer.Ordinal).ToArray());
    }

    private static LineUpAddressCandidate Candidate(string kind, long id, string? name, string evidence) =>
        new($"lac:v1:{kind}:{id}", kind, id, name?.Trim() ?? string.Empty, [evidence]);

    private static LineUpResolutionIssue Issue(LineUpIssueSeverity severity, string type, string message) =>
        new(severity, type, null, message);

    private static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
