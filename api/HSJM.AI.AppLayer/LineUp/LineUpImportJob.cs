using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.AppLayer.LineUp.Resolution;
using HSJM.AI.AppLayer.LineUp.Storage;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpImportJob(
    ILineUpImportRepository repository,
    LineUpImportStorage storage,
    LineUpAddressResolver addressResolver,
    LineUpHierarchyResolver hierarchyResolver) : ISelfScopedAutoInject
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    [Hangfire.JobDisplayName("LineUp导入 · 批次{0}")]
    public async Task ExecuteAsync(long batchId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var leaseToken = Guid.NewGuid().ToString("N");
        var batch = await repository.TryAcquireParsingLeaseAsync(
            batchId,
            leaseToken,
            now,
            now.Add(LeaseDuration),
            cancellationToken);
        if (batch is null)
        {
            return;
        }

        try
        {
            await using var stream = await storage.OpenReadAsync(batch.StorageKey, cancellationToken);
            var sources = await new LineUpGraphPackageReader()
                .ReadAsync(stream, batch.SourceFileName, cancellationToken);
            var documents = new List<LineUpImportDocumentDto>(sources.Count);
            foreach (var source in sources.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                var parsed = await new LineUpGraphXmlParser().ParseAsync(source, cancellationToken);
                var graph = new LineUpGraphNormalizer().Normalize(parsed);
                var address = await addressResolver.ResolveAsync(
                    batch.LineId,
                    batch.DeviceId,
                    graph,
                    cancellationToken);
                var hierarchy = await hierarchyResolver.ResolveAsync(
                    batch.LineId,
                    batch.DeviceId,
                    address.PlcProjectId,
                    graph,
                    cancellationToken);
                documents.Add(ProjectDocument(graph, address, hierarchy));
            }

            var projectIdsForState = documents.Select(x => x.Address.PlcProjectId)
                .Where(x => x is > 0).Select(x => x!.Value).Distinct().OrderBy(x => x).ToArray();
            var graphBlockIdsForState = documents.Select(x => x.Address.GraphBlockId)
                .Where(x => x is > 0).Select(x => x!.Value).Distinct().OrderBy(x => x).ToArray();
            var incomingKeys = documents.SelectMany(x => x.Sequences)
                .Select(x => x.SourceKey).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var existingState = await repository.LoadExistingGraphStateAsync(
                projectIdsForState,
                graphBlockIdsForState,
                incomingKeys,
                cancellationToken);
            var batchIssues = CrossDocumentIssues(documents)
                .Concat(ExistingOwnerIssues(documents, existingState))
                .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                .ThenBy(x => x.IssueType, StringComparer.Ordinal)
                .ToList();
            var allIssues = documents.SelectMany(x => x.Issues)
                .Concat(batchIssues)
                .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                .ThenBy(x => x.IssueType, StringComparer.Ordinal)
                .ThenBy(x => x.Message, StringComparer.Ordinal)
                .ToList();
            var content = new LineUpImportPreviewContentDto
            {
                SchemaVersion = LineUpImportPreviewCodec.SchemaVersion,
                LineId = batch.LineId,
                DeviceId = batch.DeviceId,
                Documents = documents,
                Issues = allIssues,
                Summary = BuildSummary(documents, allIssues),
                DiffToken = LineUpImportDiffToken.Compute(documents, existingState),
                CanCommitStructure = documents.Count > 0 &&
                                     documents.All(x => x.Address.CanCommitStructure && x.Hierarchy.CanCommitStructure) &&
                                     allIssues.All(x => !string.Equals(x.Severity, "Blocking", StringComparison.Ordinal)),
                CanCommitForMonitoring = documents.Count > 0 &&
                                         documents.All(x => x.Address.CanCommitStructure && x.Hierarchy.CanCommitStructure) &&
                                         allIssues.All(x => !string.Equals(x.Severity, "Blocking", StringComparison.Ordinal)) &&
                                         documents.All(x => x.Address.InstanceDbBlockId is > 0)
            };
            var encoded = LineUpImportPreviewCodec.Serialize(content, batch.SourceFileName);
            var projectIds = documents.Select(x => x.Address.PlcProjectId)
                .Where(x => x is > 0)
                .Select(x => x!.Value)
                .Distinct()
                .ToArray();
            var completed = await repository.CompletePreviewAsync(
                new(
                    batch.BatchId,
                    leaseToken,
                    content.SchemaVersion,
                    encoded.Json,
                    encoded.Hash,
                    JsonSerializer.Serialize(content.Summary),
                    projectIds.Length == 1 ? projectIds[0] : null,
                    allIssues.Select(x => new LineUpImportIssueWrite(
                        x.Severity,
                        x.IssueType,
                        x.SourceKey,
                        x.Message)).ToArray()),
                cancellationToken);
            if (!completed)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LineUpImportException error)
        {
            Console.WriteLine($"[LineUpImport] batch {batch.BatchId} LineUpImportException {error.Code}: {error.Message}\n{error.StackTrace}");
            await repository.FailParsingAsync(batch.BatchId, leaseToken, error.Code, CancellationToken.None);
        }
        catch (Exception error)
        {
            // 真实异常必须落日志与批次表（原来被吞成 PreviewBuildFailed 无从排查）
            var detail = $"PreviewBuildFailed: {error.GetType().Name}: {error.Message} @ {error.StackTrace?.Split('\n').FirstOrDefault(x => x.Contains("LineUp"))?.Trim()}";
            Console.WriteLine($"[LineUpImport] batch {batch.BatchId} {detail}");
            await repository.FailParsingAsync(batch.BatchId, leaseToken, detail, CancellationToken.None);
        }
    }

    private static LineUpImportDocumentDto ProjectDocument(
        LineUpGraphPreview graph,
        LineUpAddressResolution address,
        LineUpHierarchyResolution hierarchy)
    {
        var issues = graph.Issues.Select(x => new LineUpImportIssueDto
            {
                Severity = x.Severity,
                IssueType = x.IssueType,
                SourceKey = x.SourceKey,
                Message = x.Message
            })
            .Concat(address.Issues.Select(x => ProjectIssue(x)))
            .Concat(hierarchy.Issues.Select(x => ProjectIssue(x)))
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.IssueType, StringComparer.Ordinal)
            .ThenBy(x => x.Message, StringComparer.Ordinal)
            .ToList();
        return new()
        {
            SourceFileName = graph.SourceFileName,
            SourceRelativePath = graph.SourceRelativePath,
            SourceSha256 = graph.SourceSha256,
            BlockName = graph.BlockName,
            BlockNumber = graph.BlockNumber,
            ProgrammingLanguage = graph.ProgrammingLanguage,
            InstanceDbHint = graph.InstanceDbName,
            Address = address,
            Hierarchy = hierarchy,
            Issues = issues,
            Sequences = graph.Sequences.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                .Select(sequence => new LineUpImportSequenceDto
                {
                    SourceKey = sequence.SourceKey,
                    SourceName = sequence.SourceName,
                    DisplayName = sequence.DisplayName,
                    Nodes = sequence.Nodes.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                        .Select(node => new LineUpImportNodeDto
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
                            ActionGroupTitle = node.ActionGroupTitle,
                            ProgrammingLanguage = node.ProgrammingLanguage,
                            IsMissing = node.IsMissing,
                            IsJump = node.IsJump,
                            LogicNetworks = ProjectLogicNetworks(node.LogicNetworks)
                        }).ToList(),
                    Actions = sequence.Actions.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                        .Select(action => new LineUpImportActionDto
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
                    Edges = sequence.Edges.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                        .Select(edge => new LineUpImportEdgeDto
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
    }

    /// <summary>
    /// 把解析器产出的 LAD 逻辑网络（Parts/Wires）投影进导入预览 DTO。
    /// 转换条件（TransitionCondition）、步联锁、步监督共用该结构，前端按 role 过滤后渲染梯形图。
    /// </summary>
    private static List<LineUpImportLogicNetworkDto> ProjectLogicNetworks(
        IReadOnlyList<LineUpGraphLogicNetwork> networks) => networks
        .OrderBy(network => network.Role, StringComparer.Ordinal)
        .ThenBy(network => network.Ordinal)
        .ThenBy(network => network.SourceKey, StringComparer.Ordinal)
        .Select(network => new LineUpImportLogicNetworkDto
        {
            SourceKey = network.SourceKey,
            OwnerSourceKey = network.OwnerSourceKey,
            Role = network.Role,
            Ordinal = network.Ordinal,
            ProgrammingLanguage = network.ProgrammingLanguage ?? string.Empty,
            Title = network.Title ?? string.Empty,
            Parts = network.Parts
                .OrderBy(part => part.UId)
                .ThenBy(part => part.SourceKey, StringComparer.Ordinal)
                .Select(part => new LineUpImportLogicPartDto
                {
                    SourceKey = part.SourceKey,
                    UId = part.UId,
                    PartName = part.PartName ?? string.Empty,
                    Scope = part.Scope ?? string.Empty,
                    SymbolPath = part.SymbolPath.Select(value => value ?? string.Empty).ToList(),
                    ConstantValue = part.ConstantValue ?? string.Empty,
                    Negated = part.Negated,
                    Attributes = NameValuePairs(part.Attributes),
                    TemplateValues = NameValuePairs(part.TemplateValues),
                    PinNames = part.PinNames.Select(value => value ?? string.Empty).ToList()
                }).ToList(),
            Wires = network.Wires
                .OrderBy(wire => wire.UId)
                .ThenBy(wire => wire.SourceKey, StringComparer.Ordinal)
                .Select(wire => new LineUpImportLogicWireDto
                {
                    SourceKey = wire.SourceKey,
                    UId = wire.UId,
                    Endpoints = wire.Endpoints.Select(endpoint => new LineUpImportLogicEndpointDto
                    {
                        Kind = endpoint.Kind ?? string.Empty,
                        UId = endpoint.UId,
                        Name = endpoint.Name ?? string.Empty
                    }).ToList()
                }).ToList()
        }).ToList();

    private static List<LineUpImportNameValuePairDto> NameValuePairs(Dictionary<string, string> source) => source
        .OrderBy(item => item.Key, StringComparer.Ordinal)
        .Select(item => new LineUpImportNameValuePairDto(item.Key, item.Value))
        .ToList();

    private static List<LineUpImportIssueDto> CrossDocumentIssues(
        IReadOnlyCollection<LineUpImportDocumentDto> documents) =>
        documents.SelectMany(document => document.Sequences.Select(sequence => new
            {
                document.SourceRelativePath,
                sequence.SourceKey
            }))
            .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
            .Where(group => group.Select(x => x.SourceRelativePath).Distinct(StringComparer.Ordinal).Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LineUpImportIssueDto
            {
                Severity = "Blocking",
                IssueType = "CrossDocumentSourceKeyCollision",
                SourceKey = group.Key,
                Message = "More than one source document produces the same immutable Sequence source key."
            }).ToList();

    private static LineUpImportIssueDto ProjectIssue(LineUpResolutionIssue issue) => new()
    {
        Severity = issue.Severity.ToString(),
        IssueType = issue.IssueType,
        SourceKey = issue.SourceKey,
        Message = issue.Message
    };

    private static LineUpImportSummaryDto BuildSummary(
        IReadOnlyCollection<LineUpImportDocumentDto> documents,
        IReadOnlyCollection<LineUpImportIssueDto> issues) => new()
    {
        DocumentCount = documents.Count,
        SequenceCount = documents.Sum(x => x.Sequences.Count),
        StepCount = documents.Sum(x => x.Sequences.Sum(y => y.Nodes.Count(z =>
            string.Equals(z.NodeKind, "Step", StringComparison.OrdinalIgnoreCase)))),
        TransitionCount = documents.Sum(x => x.Sequences.Sum(y => y.Nodes.Count(z =>
            string.Equals(z.NodeKind, "Transition", StringComparison.OrdinalIgnoreCase)))),
        ActionCount = documents.Sum(x => x.Sequences.Sum(y => y.Actions.Count)),
        EdgeCount = documents.Sum(x => x.Sequences.Sum(y => y.Edges.Count)),
        BlockingIssueCount = issues.Count(x => string.Equals(x.Severity, "Blocking", StringComparison.Ordinal))
    };

    private static IEnumerable<LineUpImportIssueDto> ExistingOwnerIssues(
        IEnumerable<LineUpImportDocumentDto> documents,
        LineUpExistingGraphState existingState)
    {
        var incomingOwners = documents
            .Where(x => x.Address.PlcProjectId is > 0 && x.Address.GraphBlockId is > 0)
            .SelectMany(document => document.Sequences.Select(sequence => new
            {
                PlcProjectId = document.Address.PlcProjectId!.Value,
                GraphBlockId = document.Address.GraphBlockId!.Value,
                sequence.SourceKey
            }))
            .ToLookup(x => (x.PlcProjectId, x.SourceKey));
        var deadOwners = existingState.DeadOwnerGraphBlockIds is { Count: > 0 } dead
            ? new HashSet<long>(dead)
            : null;
        foreach (var existing in existingState.Sequences
                     .OrderBy(x => x.PlcProjectId)
                     .ThenBy(x => x.SourceKey, StringComparer.Ordinal))
        {
            // owner 块已软删/不存在（项目重传替换块）：同 key 的图新版本即将接管，不判冲突。
            if (deadOwners != null && deadOwners.Contains(existing.GraphBlockId))
            {
                continue;
            }
            var expectedOwners = incomingOwners[(existing.PlcProjectId, existing.SourceKey)]
                .Select(x => x.GraphBlockId)
                .Distinct()
                .ToArray();
            if (expectedOwners.Length > 0 && expectedOwners.All(x => x != existing.GraphBlockId))
            {
                yield return new()
                {
                    Severity = "Blocking",
                    IssueType = "ExistingSourceKeyOwnerConflict",
                    SourceKey = existing.SourceKey,
                    Message = "The immutable Sequence source key is already owned by another GRAPH block in this PLC project."
                };
            }
        }
    }

}

internal static class LineUpImportDiffToken
{
    internal static string Compute(
        IEnumerable<LineUpImportDocumentDto> documents,
        LineUpExistingGraphState existingState)
    {
        var material = string.Join("\n", documents
            .OrderBy(x => x.SourceRelativePath, StringComparer.Ordinal)
            .SelectMany(document => document.Sequences.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                .Select(sequence => $"{document.SourceSha256}|{sequence.SourceKey}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"lineup-diff-v1\n{existingState.StateHash}\n{material}")))
            .ToLowerInvariant();
    }
}

internal static class LineUpImportPreviewCodec
{
    internal const int SchemaVersion = 1;
    internal const int MaximumEncodedBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static LineUpEncodedPreview Serialize(LineUpImportPreviewContentDto content, string sourceFileName)
    {
        var json = JsonSerializer.Serialize(content, Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumEncodedBytes)
        {
            throw new LineUpImportException("PreviewTooLarge", sourceFileName, "The generated preview exceeds the 32 MiB limit.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(json, hash);
    }

    internal static LineUpImportPreviewContentDto Deserialize(string json, string sourceFileName)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
            {
                throw new LineUpImportException("PreviewTooLarge", sourceFileName, "The stored preview exceeds the 32 MiB limit.");
            }

            return JsonSerializer.Deserialize<LineUpImportPreviewContentDto>(json, Options) ??
                   throw new LineUpImportException("PreviewIntegrityFailed", sourceFileName, "The stored preview is empty.");
        }
        catch (JsonException)
        {
            throw new LineUpImportException("PreviewIntegrityFailed", sourceFileName, "The stored preview is invalid.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed record LineUpEncodedPreview(string Json, string Hash);
