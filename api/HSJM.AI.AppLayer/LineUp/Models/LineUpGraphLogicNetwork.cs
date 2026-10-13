using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Models;

public sealed class LineUpGraphLogicNetwork
{
    public string SourceKey { get; set; } = string.Empty;
    public string OwnerSourceKey { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string ProgrammingLanguage { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<LineUpGraphLogicPart> Parts { get; set; } = new();
    public List<LineUpGraphLogicWire> Wires { get; set; } = new();
}

public sealed class LineUpGraphLogicPart
{
    public string SourceKey { get; set; } = string.Empty;
    public int UId { get; set; }
    public string PartName { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public List<string> SymbolPath { get; set; } = new();
    public string ConstantValue { get; set; } = string.Empty;
    public bool Negated { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TemplateValues { get; set; } = new(StringComparer.Ordinal);
    public List<string> PinNames { get; set; } = new();
}

public sealed class LineUpGraphLogicWire
{
    public string SourceKey { get; set; } = string.Empty;
    public int UId { get; set; }
    public List<LineUpGraphLogicEndpoint> Endpoints { get; set; } = new();
}

public sealed class LineUpGraphLogicEndpoint
{
    public string Kind { get; set; } = string.Empty;
    public int? UId { get; set; }
    public string Name { get; set; } = string.Empty;
}
