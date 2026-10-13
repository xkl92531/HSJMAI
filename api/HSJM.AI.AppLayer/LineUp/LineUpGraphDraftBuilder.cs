#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpGraphDraftBuilder : ISelfScopedAutoInject
{
    /// <summary>
    /// 2026-09-15 升 4：LineUpGraphNormalizer 复合分支（Branch↔Branch 直连）从整片 Blocking
    /// 改为边界透明展开——归一化行为变更。图谱资产修订的复用判定按（SourceHash+ParserSchemaVersion），
    /// 8 月旧修订（含丢边时代 Blocking 的 PreviewJson）在 XML 未变时会永远被复用，导致免上传链路
    /// 重新生成预览成功、commit 成功，而 Enable 监控时 Projector 校验旧资产修订仍 GraphPreviewBlockingIssue。
    /// 升版本号强制全部块在下次预览时重建资产修订（LoadCommittedAssetRevisionAsync 取最新 → 校验通过）。
    /// </summary>
    public const int ParserSchemaVersion = 4;
    public const string PreviewSchema = "lineup.graph-preview.v2";

    public async Task<LineUpGraphDraftCandidate> BuildAsync(
        Stream content,
        string logicalFileName,
        LineUpPlcGraphBlockIdentity block,
        string plcParseTaskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(block);
        cancellationToken.ThrowIfCancellationRequested();

        var safeFileName = SafeLogicalFileName(logicalFileName);
        ValidateBlock(block, safeFileName);
        if (string.IsNullOrWhiteSpace(plcParseTaskId))
        {
            throw new ArgumentException("PLC parse task id is required.", nameof(plcParseTaskId));
        }

        var sources = await new LineUpGraphPackageReader()
            .ReadAsync(content, logicalFileName, cancellationToken);
        if (sources.Count != 1)
        {
            throw new LineUpImportException(
                sources.Count == 0 ? "NoGraphSources" : "MultipleGraphSources",
                safeFileName,
                "A PLC GRAPH block must resolve to exactly one logical XML or ML source.");
        }

        var source = sources[0];
        var document = await new LineUpGraphXmlParser().ParseAsync(source, cancellationToken);
        ValidateParsedIdentity(document, block, safeFileName);
        var normalized = new LineUpGraphNormalizer().Normalize(document);
        cancellationToken.ThrowIfCancellationRequested();

        var sequences = normalized.Sequences.Select(CopySequence).ToArray();
        var issues = normalized.Issues.Select(CopyIssue).ToArray();
        var preview = new LineUpGraphDraftPreview(
            normalized.BlockName,
            normalized.BlockNumber,
            normalized.ProgrammingLanguage,
            normalized.InstanceDbName,
            sequences,
            issues)
        {
            Schema = PreviewSchema
        };
        var summary = BuildSummary(sequences, issues);

        return new LineUpGraphDraftCandidate(
            block.PlcProjectId,
            block.PlcBlockId,
            plcParseTaskId.Trim(),
            source.Sha256,
            BuildStableKey(block),
            block.BlockName.Trim(),
            block.BlockType.Trim(),
            block.BlockNumber,
            block.ProgrammingLanguage.Trim(),
            preview,
            summary,
            issues);
    }

    private static void ValidateBlock(LineUpPlcGraphBlockIdentity block, string safeFileName)
    {
        if (!string.Equals(block.ProgrammingLanguage?.Trim(), "GRAPH", StringComparison.OrdinalIgnoreCase))
        {
            throw new LineUpImportException(
                "NotGraphBlock",
                safeFileName,
                "The selected PLC block is not programmed in GRAPH.");
        }

        if (block.PlcProjectId <= 0 || block.PlcBlockId <= 0 ||
            string.IsNullOrWhiteSpace(block.BlockName) ||
            !string.Equals(block.BlockType?.Trim(), "FB", StringComparison.OrdinalIgnoreCase) ||
            block.BlockNumber < 0)
        {
            throw new LineUpImportException(
                "InvalidGraphBlockIdentity",
                safeFileName,
                "The PLC GRAPH block identity is incomplete.");
        }
    }

    private static void ValidateParsedIdentity(
        LineUpGraphDocument document,
        LineUpPlcGraphBlockIdentity block,
        string safeFileName)
    {
        if (!string.Equals(document.ProgrammingLanguage?.Trim(), "GRAPH", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(document.BlockName?.Trim(), block.BlockName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            document.BlockNumber != block.BlockNumber)
        {
            throw new LineUpImportException(
                "GraphBlockIdentityMismatch",
                safeFileName,
                "The parsed GRAPH source does not match the PLC block created by this parse task.");
        }
    }

    private static string BuildStableKey(LineUpPlcGraphBlockIdentity block) =>
        $"graph-block|{block.PlcProjectId}|{NormalizeKeyPart(block.BlockType)}|{block.BlockNumber}|{NormalizeKeyPart(block.BlockName)}";

    private static string NormalizeKeyPart(string value) => value.Trim().ToLowerInvariant();

    private static string SafeLogicalFileName(string logicalFileName)
    {
        var normalized = (logicalFileName ?? string.Empty).Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var safe = new string(leaf.Where(character => !char.IsControl(character)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "upload" : safe;
    }

    private static LineUpGraphPreviewSequence CopySequence(LineUpGraphPreviewSequence source) => new()
    {
        SourceKey = source.SourceKey,
        SourceName = source.SourceName,
        DisplayName = source.DisplayName,
        Nodes = source.Nodes.Select(CopyNode).ToList(),
        Actions = source.Actions.Select(CopyAction).ToList(),
        Edges = source.Edges.Select(CopyEdge).ToList()
    };

    private static LineUpGraphPreviewNode CopyNode(LineUpGraphPreviewNode source) => new()
    {
        NodeKind = source.NodeKind,
        SourceKey = source.SourceKey,
        SourceName = source.SourceName,
        DisplayName = source.DisplayName,
        Number = source.Number,
        IsInitial = source.IsInitial,
        IsFinal = source.IsFinal,
        MaximumStepTime = source.MaximumStepTime,
        WarningTime = source.WarningTime,
        Comment = source.Comment,
        ActionGroupTitle = source.ActionGroupTitle,
        LogicNetworks = source.LogicNetworks.Select(CopyLogicNetwork).ToList(),
        ProgrammingLanguage = source.ProgrammingLanguage,
        IsMissing = source.IsMissing,
        RawXml = source.RawXml,
        IsJump = source.IsJump
    };

    private static LineUpGraphPreviewAction CopyAction(LineUpGraphPreviewAction source) => new()
    {
        SourceKey = source.SourceKey,
        StepSourceKey = source.StepSourceKey,
        SourceName = source.SourceName,
        DisplayName = source.DisplayName,
        Ordinal = source.Ordinal,
        StepNumber = source.StepNumber,
        Qualifier = source.Qualifier,
        Event = source.Event,
        Interlock = source.Interlock,
        Tokens = source.Tokens.ToList(),
        ActionText = source.ActionText
    };

    private static LineUpGraphPreviewEdge CopyEdge(LineUpGraphPreviewEdge source) => new()
    {
        SourceKey = source.SourceKey,
        FromSourceKey = source.FromSourceKey,
        ToSourceKey = source.ToSourceKey,
        FromNodeKind = source.FromNodeKind,
        ToNodeKind = source.ToNodeKind,
        EdgeKind = source.EdgeKind,
        BranchOrder = source.BranchOrder
    };

    private static LineUpGraphLogicNetwork CopyLogicNetwork(LineUpGraphLogicNetwork source) => new()
    {
        SourceKey = source.SourceKey,
        OwnerSourceKey = source.OwnerSourceKey,
        Role = source.Role,
        Ordinal = source.Ordinal,
        ProgrammingLanguage = source.ProgrammingLanguage,
        Title = source.Title,
        Parts = source.Parts.Select(CopyLogicPart).ToList(),
        Wires = source.Wires.Select(CopyLogicWire).ToList()
    };

    private static LineUpGraphLogicPart CopyLogicPart(LineUpGraphLogicPart source) => new()
    {
        SourceKey = source.SourceKey,
        UId = source.UId,
        PartName = source.PartName,
        Scope = source.Scope,
        SymbolPath = source.SymbolPath.ToList(),
        ConstantValue = source.ConstantValue,
        Negated = source.Negated,
        Attributes = new Dictionary<string, string>(source.Attributes, StringComparer.Ordinal),
        TemplateValues = new Dictionary<string, string>(source.TemplateValues, StringComparer.Ordinal),
        PinNames = source.PinNames.ToList()
    };

    private static LineUpGraphLogicWire CopyLogicWire(LineUpGraphLogicWire source) => new()
    {
        SourceKey = source.SourceKey,
        UId = source.UId,
        Endpoints = source.Endpoints.Select(CopyLogicEndpoint).ToList()
    };

    private static LineUpGraphLogicEndpoint CopyLogicEndpoint(LineUpGraphLogicEndpoint source) => new()
    {
        Kind = source.Kind,
        UId = source.UId,
        Name = source.Name
    };

    private static LineUpGraphPreviewIssue CopyIssue(LineUpGraphPreviewIssue source) => new()
    {
        Severity = source.Severity,
        IssueType = source.IssueType,
        SourceKey = source.SourceKey,
        Message = source.Message
    };

    private static LineUpImportSummaryDto BuildSummary(
        IReadOnlyCollection<LineUpGraphPreviewSequence> sequences,
        IReadOnlyCollection<LineUpGraphPreviewIssue> issues) => new()
    {
        DocumentCount = 1,
        SequenceCount = sequences.Count,
        StepCount = sequences.Sum(sequence => sequence.Nodes.Count(node =>
            string.Equals(node.NodeKind, "Step", StringComparison.OrdinalIgnoreCase))),
        TransitionCount = sequences.Sum(sequence => sequence.Nodes.Count(node =>
            string.Equals(node.NodeKind, "Transition", StringComparison.OrdinalIgnoreCase))),
        ActionCount = sequences.Sum(sequence => sequence.Actions.Count),
        EdgeCount = sequences.Sum(sequence => sequence.Edges.Count),
        BlockingIssueCount = issues.Count(issue =>
            string.Equals(issue.Severity, "Blocking", StringComparison.Ordinal))
    };
}
