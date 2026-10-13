using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Models;

public class LineUpGraphDocument : LineUpGraphNamedElement
{
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceRelativePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string BlockName { get; set; } = string.Empty;
    public int BlockNumber { get; set; }
    public string ProgrammingLanguage { get; set; } = string.Empty;
    public string? InstanceDbName { get; set; }
    public List<LineUpGraphSequence> Sequences { get; set; } = new();
}

public class LineUpGraphSequence : LineUpGraphNamedElement
{
    public string SourceKey { get; set; } = string.Empty;
    public List<LineUpGraphStep> Steps { get; set; } = new();
    public List<LineUpGraphTransition> Transitions { get; set; } = new();
    public List<LineUpGraphAction> Actions { get; set; } = new();
    public List<LineUpGraphBranch> Branches { get; set; } = new();
    public List<LineUpGraphRawConnection> RawConnections { get; set; } = new();

    /// <summary>
    /// 2026-09-28（FB151/PLC2）：直连序列结束标记 EndConnection 的节点（典型为最终转换）。
    /// 解析期该终止边不进入 RawConnections；归一化据此豁免其 TransitionWithoutOutgoingEdge。
    /// </summary>
    public List<LineUpGraphSequenceEndAnchor> EndAnchors { get; set; } = new();
}

/// <summary>连到 GRAPH 序列终点（EndConnection）的节点锚点（仅记录类型与编号）。</summary>
public sealed record LineUpGraphSequenceEndAnchor(LineUpGraphEndpointKind Kind, int Number);

public class LineUpGraphStep : LineUpGraphNamedElement
{
    public string SourceKey { get; set; } = string.Empty;
    public int Number { get; set; }
    public bool Init { get; set; }
    public string MaximumStepTime { get; set; } = string.Empty;
    public string WarningTime { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string ActionGroupTitle { get; set; } = string.Empty;
    public List<LineUpGraphLogicNetwork> Interlocks { get; set; } = new();
    public List<LineUpGraphLogicNetwork> Supervisions { get; set; } = new();
}

public class LineUpGraphTransition : LineUpGraphNamedElement
{
    public string SourceKey { get; set; } = string.Empty;
    public int Number { get; set; }
    public bool IsMissing { get; set; }
    public string ProgrammingLanguage { get; set; } = string.Empty;
    public string RawXml { get; set; } = string.Empty;
    public List<LineUpGraphLogicNetwork> ConditionNetworks { get; set; } = new();
}

public class LineUpGraphAction : LineUpGraphNamedElement
{
    public string SourceKey { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public int StepNumber { get; set; }
    public string Qualifier { get; set; } = string.Empty;
    public string? Event { get; set; }
    public bool? Interlock { get; set; }
    public List<string> Tokens { get; set; } = new();
    public string ActionText { get; set; } = string.Empty;
}

public class LineUpGraphBranch : LineUpGraphNamedElement
{
    public string SourceKey { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Type { get; set; } = string.Empty;
    public int Cardinality { get; set; }
}

public class LineUpGraphRawConnection : LineUpGraphNamedElement
{
    public string SourceKey { get; set; } = string.Empty;
    public LineUpGraphEndpoint From { get; set; } = new();
    public LineUpGraphEndpoint To { get; set; } = new();
    public string LinkType { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}

public class LineUpGraphEndpoint
{
    public LineUpGraphEndpointKind Kind { get; set; }
    public int Number { get; set; }
    public LineUpGraphBranchPortKind PortKind { get; set; }
    public int? Port { get; set; }
}

public enum LineUpGraphEndpointKind
{
    Step,
    Transition,
    Branch
}

public enum LineUpGraphBranchPortKind
{
    None,
    In,
    Out
}

public abstract class LineUpGraphNamedElement
{
    private string _sourceName = string.Empty;
    private string _displayName = string.Empty;

    public string SourceName
    {
        get => _sourceName;
        set
        {
            _sourceName = value ?? string.Empty;
            if (string.IsNullOrEmpty(_displayName))
            {
                _displayName = _sourceName;
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set => _displayName = value ?? string.Empty;
    }
}
