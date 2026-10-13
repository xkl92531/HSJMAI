using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp.Resolution;

public sealed class LineUpHierarchyResolver(ILineUpHierarchyRepository repository) : ISelfScopedAutoInject
{
    public async Task<LineUpHierarchyResolution> ResolveAsync(
        long lineId,
        long deviceId,
        long? resolvedPlcProjectId,
        LineUpGraphPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var sequences = preview.Sequences
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var context = await repository.LoadContextAsync(
            lineId,
            deviceId,
            sequences.Select(x => x.SourceKey).Distinct(StringComparer.Ordinal).ToArray(),
            cancellationToken,
            preview.BlockName);
        var issues = new List<LineUpResolutionIssue>();

        if (context.Line is null || context.Line.Id <= 0 || context.Line.Id != lineId)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "LineMissing", null, "The selected production line is unavailable."));
            return Blocked(lineId, deviceId, resolvedPlcProjectId, sequences, issues);
        }

        if (context.Device is null || context.Device.Id <= 0 || context.Device.Id != deviceId)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "DeviceMissing", null, "The selected PLC device is unavailable."));
            return Blocked(lineId, deviceId, resolvedPlcProjectId, sequences, issues);
        }

        if (context.Device.LineId is null || context.Device.LineId <= 0 || context.Device.LineId != lineId)
        {
            issues.Add(Issue(LineUpIssueSeverity.Blocking, "DeviceLineMismatch", null, "The selected PLC device is not assigned to the selected production line."));
            return Blocked(lineId, deviceId, resolvedPlcProjectId, sequences, issues);
        }

        var processes = context.Processes
            .Where(x => x.Id > 0 && x.LineId == lineId)
            .GroupBy(x => x.Id)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single());
        var stations = context.Stations
            .Where(x => x.Id > 0 && x.LineId == lineId && x.ProcessId > 0 && processes.ContainsKey(x.ProcessId))
            .GroupBy(x => x.Id)
            .Where(x => x.Count() == 1)
            .Select(x => x.Single())
            .OrderBy(x => x.Id)
            .ToArray();
        var stationById = stations.ToDictionary(x => x.Id);
        var substations = context.SubStations
            .Where(x => x.Id > 0 && x.LineId == lineId && x.PlcId == deviceId &&
                        x.ProcessId > 0 && x.StationId > 0 &&
                        stationById.TryGetValue(x.StationId, out var station) &&
                        station.ProcessId == x.ProcessId && processes.ContainsKey(x.ProcessId))
            .GroupBy(x => x.Id)
            .Where(x => x.Count() == 1)
            .Select(x => x.Single())
            .OrderBy(x => x.Id)
            .ToArray();
        var substationById = substations.ToDictionary(x => x.Id);

        if (processes.Count != context.Processes.Count ||
            stations.Length != context.Stations.Count ||
            substations.Length != context.SubStations.Count)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "HierarchyRelationshipInvalid", null,
                "One or more hierarchy rows have invalid immutable ID relationships and were ignored."));
        }

        // 子工站不再自动分配新编码/新建：候选默认取工站下第一个既有子工站（SortNum → 编码 → Id）。
        var firstSubStationByStation = substations
            .GroupBy(x => x.StationId)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(y => y.SortNum)
                    .ThenBy(y => y.Code ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(y => y.Id)
                    .First());
        var results = new List<LineUpHierarchySequenceResolution>(sequences.Length);

        // GRAPH 块的人工工站绑定（「批量绑定工站」弹框落库）：块级证据优先于名称启发式。
        // 顺控级既有绑定（ExistingBindings）仍最优先；块绑定只在「本块名 + 解析出的项目号」
        // 都对得上时生效；共享块绑多个工站 → 降级 NeedsReview 让人工/改派裁决。
        var blockBoundStations = ResolveBlockStationBindings(
            preview,
            resolvedPlcProjectId,
            context.BlockStationBindings,
            stations,
            out var blockBindingAmbiguous);
        if (blockBindingAmbiguous)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "BlockStationBindingAmbiguous", null,
                "The GRAPH block is bound to multiple stations; its sequences require manual hierarchy review."));
        }

        foreach (var sequence in sequences)
        {
            var bindingResult = ResolveExistingBinding(
                sequence,
                resolvedPlcProjectId,
                context.ExistingBindings,
                processes,
                stationById,
                substationById,
                issues);
            if (bindingResult is not null)
            {
                results.Add(bindingResult);
                continue;
            }

            if (blockBoundStations.Count == 1)
            {
                // 块绑定的工站是人工指定：Automatic 置信度（解析器自动选中 → 提交可零决策采纳），
                // 子工站默认取该工站下第一个既有子工站（不新建；工站无子工站时降级人工改派）。
                results.Add(CreateStationChoice(sequence, blockBoundStations[0], LineUpConfidenceLevel.Automatic,
                    "BlockStationBinding", firstSubStationByStation));
                continue;
            }

            if (blockBoundStations.Count > 1)
            {
                results.Add(CreateNeedsReview(sequence, blockBoundStations, firstSubStationByStation));
                continue;
            }

            var deviceStationIds = substations.Select(x => x.StationId).Distinct().ToHashSet();
            var deviceStations = stations.Where(x => deviceStationIds.Contains(x.Id)).ToArray();
            var exactSourceCode = StationCodeHeuristics.NormalizeStationCode(sequence.SourceName);
            var exactDeviceMatches = deviceStations
                .Where(x => CodesEqual(x.Code, exactSourceCode))
                .ToArray();

            if (exactDeviceMatches.Length == 1)
            {
                results.Add(CreateStationChoice(sequence, exactDeviceMatches[0], LineUpConfidenceLevel.Automatic,
                    "ExactDeviceScopedStationCode", firstSubStationByStation));
                continue;
            }

            if (exactDeviceMatches.Length > 1)
            {
                issues.Add(Issue(LineUpIssueSeverity.Warning, "StationCodeAmbiguous", sequence.SourceKey,
                    "More than one device-scoped station has the same normalized code."));
                results.Add(CreateNeedsReview(sequence, exactDeviceMatches, firstSubStationByStation));
                continue;
            }

            if (deviceStations.Length == 1)
            {
                results.Add(CreateStationChoice(sequence, deviceStations[0], LineUpConfidenceLevel.Automatic,
                    "UniqueDeviceScopedStation", firstSubStationByStation));
                continue;
            }

            var evidenceCodes = ExtractEvidenceCodes(preview, sequence);
            if (evidenceCodes.Count > 1)
            {
                issues.Add(Issue(LineUpIssueSeverity.Warning, "MultipleStationCodes", sequence.SourceKey,
                    "The immutable GRAPH source contains multiple station codes."));
                results.Add(CreateNeedsReview(sequence, StationsForCodes(stations, evidenceCodes), firstSubStationByStation));
                continue;
            }

            if (evidenceCodes.Count == 1)
            {
                var matches = StationsForCodes(stations, evidenceCodes);
                if (matches.Length == 1)
                {
                    results.Add(CreateStationChoice(sequence, matches[0], LineUpConfidenceLevel.Suggested,
                        "LineScopedStationCode", firstSubStationByStation));
                    continue;
                }

                if (matches.Length > 1)
                {
                    issues.Add(Issue(LineUpIssueSeverity.Warning, "StationCodeAmbiguous", sequence.SourceKey,
                        "More than one line-scoped station has the same normalized code."));
                }
            }

            results.Add(CreateNeedsReview(sequence, stations, firstSubStationByStation));
        }

        return new LineUpHierarchyResolution(
            lineId,
            deviceId,
            resolvedPlcProjectId,
            resolvedPlcProjectId is > 0 && issues.All(x => x.Severity != LineUpIssueSeverity.Blocking),
            AttachDisplayLabels(results, processes, stationById, substationById),
            SortIssues(issues));
    }

    /// <summary>
    /// 块级工站绑定 → 候选工站集：
    /// 匹配条件 = BlockName 相等 + （绑定行带项目号时）项目号与解析出的 PLC 项目一致。
    /// 返回去重后的工站列表（本产线范围内按编码匹配）；多个不同工站时 ambiguous=true。
    /// </summary>
    private static List<LineUpStationLookup> ResolveBlockStationBindings(
        LineUpGraphPreview preview,
        long? resolvedPlcProjectId,
        IReadOnlyList<LineUpBlockStationBindingLookup> blockBindings,
        LineUpStationLookup[] stations,
        out bool ambiguous)
    {
        ambiguous = false;
        var result = new List<LineUpStationLookup>();
        if (string.IsNullOrWhiteSpace(preview.BlockName) || blockBindings.Count == 0)
        {
            return result;
        }

        var codes = blockBindings
            .Where(x => string.Equals(x.BlockName, preview.BlockName, StringComparison.Ordinal))
            .Where(x => x.ProjectId is null || resolvedPlcProjectId is null || resolvedPlcProjectId <= 0 ||
                        x.ProjectId == resolvedPlcProjectId)
            .Where(x => !string.IsNullOrWhiteSpace(x.StationCode))
            .Select(x => x.StationCode!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (codes.Length == 0)
        {
            return result;
        }

        foreach (var code in codes)
        {
            var matches = stations
                .Where(x => CodesEqual(x.Code, code))
                .ToArray();
            if (matches.Length == 1)
            {
                result.Add(matches[0]);
            }
            else if (matches.Length > 1)
            {
                // 同编码多工站在上游 stations 过滤后罕见；按歧义处理
                ambiguous = true;
            }
        }

        if (result.DistinctBy(x => x.Id).Count() > 1)
        {
            ambiguous = true;
        }

        return result.DistinctBy(x => x.Id).ToList();
    }

    private static LineUpHierarchySequenceResolution[] AttachDisplayLabels(
        IEnumerable<LineUpHierarchySequenceResolution> results,
        IReadOnlyDictionary<long, LineUpProcessLookup> processes,
        IReadOnlyDictionary<long, LineUpStationLookup> stations,
        IReadOnlyDictionary<long, LineUpSubStationLookup> substations) =>
        results.Select(result => result with
        {
            Candidates = result.Candidates.Select(candidate =>
            {
                processes.TryGetValue(candidate.ProcessId, out var process);
                stations.TryGetValue(candidate.StationId, out var station);
                var substation = candidate.SubStationId is > 0 &&
                                 substations.TryGetValue(candidate.SubStationId.Value, out var current)
                    ? current
                    : null;
                return candidate with
                {
                    ProcessCode = process?.Code,
                    ProcessName = process?.Name,
                    StationCode = station?.Code,
                    StationName = station?.Name,
                    SubStationCode = substation?.Code ?? candidate.ProposedSubStationCode,
                    SubStationName = substation?.Name ?? candidate.ProposedSubStationName
                };
            }).ToArray()
        }).ToArray();

    private static LineUpHierarchySequenceResolution? ResolveExistingBinding(
        LineUpGraphPreviewSequence sequence,
        long? resolvedPlcProjectId,
        IReadOnlyList<LineUpExistingBindingLookup> allBindings,
        IReadOnlyDictionary<long, LineUpProcessLookup> processes,
        IReadOnlyDictionary<long, LineUpStationLookup> stations,
        IReadOnlyDictionary<long, LineUpSubStationLookup> substations,
        List<LineUpResolutionIssue> issues)
    {
        var bindings = allBindings
            .Where(x => string.Equals(x.SequenceSourceKey, sequence.SourceKey, StringComparison.Ordinal))
            .OrderBy(x => x.BindingPlcProjectId)
            .ThenBy(x => x.SequencePlcProjectId)
            .ThenBy(x => x.SubStationId)
            .ToArray();
        if (bindings.Length == 0)
        {
            return null;
        }

        if (bindings.Length != 1)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "ExistingBindingAmbiguous", sequence.SourceKey,
                "More than one existing binding matches this immutable Sequence source key."));
            return new(sequence.SourceKey, LineUpConfidenceLevel.NeedsReview, null, null, null, null, []);
        }

        var binding = bindings[0];
        if (binding.ProcessId is not > 0 || binding.StationId is not > 0 || binding.SubStationId is not > 0 ||
            !processes.ContainsKey(binding.ProcessId.Value) ||
            !stations.TryGetValue(binding.StationId.Value, out var station) ||
            station.ProcessId != binding.ProcessId.Value ||
            !substations.TryGetValue(binding.SubStationId.Value, out var substation) ||
            substation.ProcessId != binding.ProcessId.Value || substation.StationId != binding.StationId.Value)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "HierarchyRelationshipInvalid", sequence.SourceKey,
                "The existing binding has invalid immutable hierarchy relationships."));
            return new(sequence.SourceKey, LineUpConfidenceLevel.NeedsReview, null, null, null, null, []);
        }

        var projectMatches = resolvedPlcProjectId is > 0 &&
                             binding.SequencePlcProjectId == resolvedPlcProjectId.Value &&
                             binding.BindingPlcProjectId == resolvedPlcProjectId.Value;
        var confidence = projectMatches ? LineUpConfidenceLevel.Automatic : LineUpConfidenceLevel.NeedsReview;
        if (!projectMatches)
        {
            issues.Add(Issue(LineUpIssueSeverity.Warning, "ExistingBindingProjectMismatch", sequence.SourceKey,
                "The existing binding belongs to a different or unresolved PLC project."));
        }

        var candidate = CreateCandidate(
            sequence,
            LineUpHierarchyCandidateKind.ExistingBinding,
            binding.ProcessId.Value,
            binding.StationId.Value,
            binding.SubStationId.Value,
            substation.Code,
            substation.Name,
            confidence,
            projectMatches ? ["ExactSourceKeyBinding", "ExactPlcProject"] : ["ExactSourceKeyBinding", "PlcProjectMismatch"]);
        return new(
            sequence.SourceKey,
            confidence,
            binding.ProcessId,
            binding.StationId,
            binding.SubStationId,
            projectMatches ? candidate.CandidateKey : null,
            [candidate]);
    }

    /// <summary>工站候选：子工站固定取该工站下第一个既有子工站（不新建）；
    /// 工站无子工站时降级 NeedsReview 交人工处理（请先在主数据维护页补齐基础数据）。</summary>
    private static LineUpHierarchySequenceResolution CreateStationChoice(
        LineUpGraphPreviewSequence sequence,
        LineUpStationLookup station,
        LineUpConfidenceLevel confidence,
        string evidenceCode,
        IReadOnlyDictionary<long, LineUpSubStationLookup> firstSubStationByStation)
    {
        if (!firstSubStationByStation.TryGetValue(station.Id, out var subStation))
        {
            return new(sequence.SourceKey, LineUpConfidenceLevel.NeedsReview,
                station.ProcessId, station.Id, null, null, []);
        }
        var candidate = CreateCandidate(
            sequence,
            LineUpHierarchyCandidateKind.ExistingSubStation,
            station.ProcessId,
            station.Id,
            subStation.Id,
            null,
            null,
            confidence,
            [evidenceCode]);
        return new(
            sequence.SourceKey,
            confidence,
            station.ProcessId,
            station.Id,
            subStation.Id,
            confidence == LineUpConfidenceLevel.Automatic ? candidate.CandidateKey : null,
            [candidate]);
    }

    private static LineUpHierarchySequenceResolution CreateNeedsReview(
        LineUpGraphPreviewSequence sequence,
        IEnumerable<LineUpStationLookup> stations,
        IReadOnlyDictionary<long, LineUpSubStationLookup> firstSubStationByStation)
    {
        var candidates = stations
            .OrderBy(x => x.Id)
            .Select(station =>
            {
                // 子工站默认该工站下第一个既有子工站；无子工站的工站候选不带子工站（人工改派前先补主数据）
                firstSubStationByStation.TryGetValue(station.Id, out var subStation);
                return CreateCandidate(
                    sequence,
                    LineUpHierarchyCandidateKind.StationCandidate,
                    station.ProcessId,
                    station.Id,
                    subStation?.Id,
                    null,
                    null,
                    LineUpConfidenceLevel.NeedsReview,
                    ["StationCandidate"]);
            })
            .ToArray();
        return new(sequence.SourceKey, LineUpConfidenceLevel.NeedsReview, null, null, null, null, candidates);
    }

    private static LineUpHierarchyCandidate CreateCandidate(
        LineUpGraphPreviewSequence sequence,
        LineUpHierarchyCandidateKind kind,
        long processId,
        long stationId,
        long? subStationId,
        string? proposedCode,
        string? proposedName,
        LineUpConfidenceLevel confidence,
        IReadOnlyList<string> evidenceCodes)
    {
        var keyMaterial = string.Join("|",
            "lineup-hierarchy-v1",
            sequence.SourceKey,
            kind,
            processId,
            stationId,
            subStationId?.ToString() ?? string.Empty,
            proposedCode?.Trim().ToUpperInvariant() ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
        return new(
            $"lhc:v1:{hash}",
            kind,
            sequence.SourceKey,
            processId,
            stationId,
            subStationId,
            proposedCode,
            proposedName,
            confidence,
            evidenceCodes.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static HashSet<string> ExtractEvidenceCodes(
        LineUpGraphPreview preview,
        LineUpGraphPreviewSequence sequence)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodes(result, preview.BlockName);
        AddCodes(result, sequence.SourceName);
        foreach (var comment in sequence.Nodes
                     .Where(x => string.Equals(x.NodeKind, "Step", StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Comment))
        {
            AddCodes(result, comment);
        }

        return result;
    }

    private static void AddCodes(HashSet<string> target, string? text)
    {
        foreach (var code in StationCodeHeuristics.ExtractStationCodes(text ?? string.Empty))
        {
            target.Add(code);
        }
    }

    private static LineUpStationLookup[] StationsForCodes(
        IEnumerable<LineUpStationLookup> stations,
        IEnumerable<string> codes)
    {
        var normalizedCodes = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return stations
            .Where(x => normalizedCodes.Contains(StationCodeHeuristics.NormalizeStationCode(x.Code ?? string.Empty)))
            .OrderBy(x => x.Id)
            .ToArray();
    }

    private static bool CodesEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(
            StationCodeHeuristics.NormalizeStationCode(left),
            StationCodeHeuristics.NormalizeStationCode(right),
            StringComparison.OrdinalIgnoreCase);

    private static LineUpHierarchyResolution Blocked(
        long lineId,
        long deviceId,
        long? resolvedPlcProjectId,
        IReadOnlyList<LineUpGraphPreviewSequence> sequences,
        List<LineUpResolutionIssue> issues) => new(
            lineId,
            deviceId,
            resolvedPlcProjectId,
            false,
            sequences.Select(x => new LineUpHierarchySequenceResolution(
                x.SourceKey,
                LineUpConfidenceLevel.NeedsReview,
                null,
                null,
                null,
                null,
                [])).ToArray(),
            SortIssues(issues));

    private static LineUpResolutionIssue Issue(
        LineUpIssueSeverity severity,
        string issueType,
        string? sourceKey,
        string message) => new(severity, issueType, sourceKey, message);

    private static LineUpResolutionIssue[] SortIssues(IEnumerable<LineUpResolutionIssue> issues) =>
        issues.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.IssueType, StringComparer.Ordinal)
            .ThenBy(x => x.Message, StringComparer.Ordinal)
            .ToArray();
}
