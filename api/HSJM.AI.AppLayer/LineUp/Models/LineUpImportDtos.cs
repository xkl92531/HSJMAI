using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Models;

public sealed record LineUpImportStartedDto(long BatchId, string Status);

/// <summary>
/// 最新可预览批次定位结果（查看流程图跳转直达预览用）；无可预览批次时 BackResult 为 null。
/// </summary>
public sealed record LineUpLatestPreviewDto(long BatchId);

/// <summary>
/// 从 PLC GRAPH 块（ai_plc_block_new）免上传生成 LineUp 预览的请求。
/// 服务端按 BlockId 取 ai_plc_sourcefile.filecontent 归档原文，复用文件上传同一套解析链。
/// </summary>
public sealed record LineUpImportFromBlockRequest(long BlockId, long LineId, long DeviceId);

public sealed record LineUpImportProgressDto(
    long BatchId,
    string Status,
    string? ErrorCode,
    int PreviewRevision,
    bool CanLoadPreview);

public sealed record LineUpImportCommitRequest(
    string CommitMode,
    int PreviewRevision,
    string PreviewHash,
    string DiffToken,
    IReadOnlyList<LineUpHierarchyDecisionDto> HierarchyDecisions,
    IReadOnlyList<LineUpDisplayNameChangeDto> DisplayNameChanges);

public sealed record LineUpHierarchyDecisionDto(string SequenceSourceKey, string CandidateKey);

public sealed record LineUpDisplayNameChangeDto(string SourceKey, string DisplayName);

/// <summary>
/// 层级改派请求：把已保存的结构绑定（AI_LineUp_SubStationGraphBinding）按序列 SourceKey
/// 改指到同产线同 PLC 下的其它工序/工站/子工站；Basic_* 基础层级数据本身不变，
/// 后续 ReadyForMonitoring 提交会沿用改派后的层级写入运行时绑定。
/// </summary>
// 注意：请求 DTO 必须用可写属性的 class——Newtonsoft 对带可选构造参数的 positional record
// 反序列化会失败（整个 request 被判 "The request field is required" → 400），实测 2026-08-30。
public sealed class LineUpHierarchyReassignRequest
{
    public long LineId { get; set; }

    public long DeviceId { get; set; }

    /// <summary>当前预览批次 Id：顺控尚无绑定行时，从批次预览取顺控元数据直接新建绑定（弹框内免保存结构直接绑定）。</summary>
    public long BatchId { get; set; }

    public IReadOnlyList<LineUpHierarchyReassignItem>? Updates { get; set; }
}

public sealed class LineUpHierarchyReassignItem
{
    public string SequenceSourceKey { get; set; } = string.Empty;

    public long ProcessId { get; set; }

    public long StationId { get; set; }

    public long SubStationId { get; set; }

    public string? NewProcessCode { get; set; }

    public string? NewStationCode { get; set; }

    public string? NewSubStationCode { get; set; }
}

public sealed record LineUpHierarchyReassignReceipt(
    int UpdatedCount,
    int CreatedProcessCount,
    int CreatedStationCount,
    int CreatedSubStationCount,
    int CreatedBindingCount);

public sealed record LineUpImportCommitReceiptDto(
    long BatchId,
    string Status,
    string CommitMode,
    int PreviewRevision,
    string PreviewHash,
    DateTime CommittedAt);

public sealed class LineUpImportPreviewDto
{
    public long BatchId { get; init; }
    public string Status { get; init; } = string.Empty;
    public int PreviewSchemaVersion { get; init; }
    public int PreviewRevision { get; init; }
    public string PreviewHash { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceFileHash { get; init; } = string.Empty;
    public long LineId { get; init; }
    public long DeviceId { get; init; }
    public List<LineUpImportDocumentDto> Documents { get; init; } = new();
    public List<LineUpImportIssueDto> Issues { get; init; } = new();
    public LineUpImportSummaryDto Summary { get; init; } = new();
    public string DiffToken { get; init; } = string.Empty;
    public bool CanCommitStructure { get; init; }
    public bool CanCommitForMonitoring { get; init; }
}

public sealed class LineUpImportPreviewContentDto
{
    public int SchemaVersion { get; init; }
    public long LineId { get; init; }
    public long DeviceId { get; init; }
    public List<LineUpImportDocumentDto> Documents { get; init; } = new();
    public List<LineUpImportIssueDto> Issues { get; init; } = new();
    public LineUpImportSummaryDto Summary { get; init; } = new();
    public string DiffToken { get; init; } = string.Empty;
    public bool CanCommitStructure { get; init; }
    public bool CanCommitForMonitoring { get; init; }
}

public sealed record LineUpImportDocumentDto
{
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceRelativePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string BlockName { get; init; } = string.Empty;
    public int BlockNumber { get; init; }
    public string ProgrammingLanguage { get; init; } = string.Empty;
    public string? InstanceDbHint { get; init; }
    public List<LineUpImportSequenceDto> Sequences { get; init; } = new();
    public LineUpAddressResolution Address { get; init; } = null!;
    public LineUpHierarchyResolution Hierarchy { get; init; } = null!;
    public List<LineUpImportIssueDto> Issues { get; init; } = new();
}

public sealed class LineUpImportSequenceDto
{
    public string SourceKey { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public List<LineUpImportNodeDto> Nodes { get; init; } = new();
    public List<LineUpImportActionDto> Actions { get; init; } = new();
    public List<LineUpImportEdgeDto> Edges { get; init; } = new();
}

public sealed class LineUpImportNodeDto
{
    public string NodeKind { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Number { get; init; }
    public bool IsInitial { get; init; }
    public bool IsFinal { get; init; }
    public string MaximumStepTime { get; init; } = string.Empty;
    public string WarningTime { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
    public string ActionGroupTitle { get; init; } = string.Empty;
    public string ProgrammingLanguage { get; init; } = string.Empty;
    public bool IsMissing { get; init; }
    public bool IsJump { get; init; }

    /// <summary>
    /// 节点下的 LAD 逻辑网络（转换条件 TransitionCondition / 步联锁 StepInterlock / 步监督 StepSupervision）。
    /// GRAPH 智能导入预览早期版本未回传该字段，导致前端“转换条件”弹框页签永远为空 —— 2026-08-27 补上。
    /// </summary>
    public List<LineUpImportLogicNetworkDto> LogicNetworks { get; init; } = new();
}

public sealed class LineUpImportLogicNetworkDto
{
    public string SourceKey { get; init; } = string.Empty;
    public string OwnerSourceKey { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public string ProgrammingLanguage { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<LineUpImportLogicPartDto> Parts { get; init; } = new();
    public List<LineUpImportLogicWireDto> Wires { get; init; } = new();
}

public sealed class LineUpImportLogicPartDto
{
    public string SourceKey { get; init; } = string.Empty;
    public int UId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public List<string> SymbolPath { get; init; } = new();
    public string ConstantValue { get; init; } = string.Empty;
    public bool Negated { get; init; }
    public List<LineUpImportNameValuePairDto> Attributes { get; init; } = new();
    public List<LineUpImportNameValuePairDto> TemplateValues { get; init; } = new();
    public List<string> PinNames { get; init; } = new();
}

public sealed record LineUpImportNameValuePairDto(string Name, string Value);

public sealed class LineUpImportLogicWireDto
{
    public string SourceKey { get; init; } = string.Empty;
    public int UId { get; init; }
    public List<LineUpImportLogicEndpointDto> Endpoints { get; init; } = new();
}

public sealed class LineUpImportLogicEndpointDto
{
    public string Kind { get; init; } = string.Empty;
    public int? UId { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class LineUpImportActionDto
{
    public string SourceKey { get; init; } = string.Empty;
    public string StepSourceKey { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public int StepNumber { get; init; }
    public string Qualifier { get; init; } = string.Empty;
    public List<string> Tokens { get; init; } = new();
    public string ActionText { get; init; } = string.Empty;
}

public sealed class LineUpImportEdgeDto
{
    public string SourceKey { get; init; } = string.Empty;
    public string FromSourceKey { get; init; } = string.Empty;
    public string ToSourceKey { get; init; } = string.Empty;
    public string FromNodeKind { get; init; } = string.Empty;
    public string ToNodeKind { get; init; } = string.Empty;
    public string EdgeKind { get; init; } = string.Empty;
    public int? BranchOrder { get; init; }
}

public sealed class LineUpImportIssueDto
{
    public string Severity { get; init; } = string.Empty;
    public string IssueType { get; init; } = string.Empty;
    public string? SourceKey { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class LineUpImportSummaryDto
{
    public int DocumentCount { get; init; }
    public int SequenceCount { get; init; }
    public int StepCount { get; init; }
    public int TransitionCount { get; init; }
    public int ActionCount { get; init; }
    public int EdgeCount { get; init; }
    public int BlockingIssueCount { get; init; }
}

public static class LineUpImportLeasePolicy
{
    public static bool CanAcquire(HSJM.AI.IDbService.LineUp.LineUpImportBatchData batch, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (string.Equals(batch.Status?.Trim(), "Uploaded", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(batch.Status?.Trim(), "Parsing", StringComparison.Ordinal) &&
               batch.ParsingLeaseExpiresAt is not null &&
               batch.ParsingLeaseExpiresAt.Value <= now;
    }

    public static bool IsOwner(
        HSJM.AI.IDbService.LineUp.LineUpImportBatchData batch,
        string leaseToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return string.Equals(batch.Status?.Trim(), "Parsing", StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(leaseToken) &&
               string.Equals(batch.ParsingLeaseToken, leaseToken, StringComparison.Ordinal);
    }
}
