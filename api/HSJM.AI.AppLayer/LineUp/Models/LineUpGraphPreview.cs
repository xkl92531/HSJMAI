using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Models;

public sealed class LineUpGraphPreview
{
    public string Schema { get; init; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceRelativePath { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string BlockName { get; set; } = string.Empty;
    public int BlockNumber { get; set; }
    public string ProgrammingLanguage { get; set; } = string.Empty;
    public string? InstanceDbName { get; set; }
    public List<LineUpGraphPreviewSequence> Sequences { get; set; } = new();
    public List<LineUpGraphPreviewIssue> Issues { get; set; } = new();
}

public sealed class LineUpGraphPreviewSequence
{
    public string SourceKey { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<LineUpGraphPreviewAction> Actions { get; set; } = new();
    public List<LineUpGraphPreviewNode> Nodes { get; set; } = new();
    public List<LineUpGraphPreviewEdge> Edges { get; set; } = new();
}

public sealed class LineUpGraphPreviewNode
{
    public string NodeKind { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Number { get; set; }
    public bool IsInitial { get; set; }
    public bool IsFinal { get; set; }
    public string MaximumStepTime { get; set; } = string.Empty;
    public string WarningTime { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string ActionGroupTitle { get; set; } = string.Empty;
    public List<LineUpGraphLogicNetwork> LogicNetworks { get; set; } = new();
    public string ProgrammingLanguage { get; set; } = string.Empty;
    public bool IsMissing { get; set; }
    public string RawXml { get; set; } = string.Empty;
    public bool IsJump { get; set; }
}

public sealed class LineUpGraphPreviewAction
{
    public string SourceKey { get; set; } = string.Empty;
    public string StepSourceKey { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public int StepNumber { get; set; }
    public string Qualifier { get; set; } = string.Empty;
    public string? Event { get; set; }
    public bool? Interlock { get; set; }
    public List<string> Tokens { get; set; } = new();
    public string ActionText { get; set; } = string.Empty;
}

public sealed class LineUpGraphPreviewEdge
{
    public string SourceKey { get; set; } = string.Empty;
    public string FromSourceKey { get; set; } = string.Empty;
    public string ToSourceKey { get; set; } = string.Empty;
    public string FromNodeKind { get; set; } = string.Empty;
    public string ToNodeKind { get; set; } = string.Empty;
    public string EdgeKind { get; set; } = string.Empty;
    public int? BranchOrder { get; set; }
}

public sealed class LineUpGraphPreviewIssue
{
    public string Severity { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string? SourceKey { get; set; }
    public string Message { get; set; } = string.Empty;
}
