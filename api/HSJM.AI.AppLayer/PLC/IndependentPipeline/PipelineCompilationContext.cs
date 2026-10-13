#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>One snapshot, one parse per LU, one compiled template per block/interface. No database calls.</summary>
public sealed class PipelineCompilationContext(PipelineSnapshot source)
{
    readonly Dictionary<long,XDocument> xmlCache = new();
    sealed record Template(IReadOnlyDictionary<string,LogicTerm> Outputs,IReadOnlyList<string> NestedInstances,
        IReadOnlyList<PipelineAssignment> NestedState);
    readonly Dictionary<string,Template> templates = new(StringComparer.Ordinal);
    readonly HashSet<long> compiling = new();
    public int ParsedUnits => xmlCache.Count;
    public int CompiledTemplates => templates.Count;

    public XDocument Xml(PipelineUnit unit) {
        if (xmlCache.TryGetValue(unit.Id,out var document)) return document;
        if (string.IsNullOrWhiteSpace(unit.RawContent)) throw new PipelineException("EmptySource");
        try {
            using var reader = XmlReader.Create(new StringReader(unit.RawContent),new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8_000_000
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw new PipelineException("ParseFailed"); }
        if (document.Root?.Name.LocalName is not("FlgNet" or "StructuredText")) throw new PipelineException("UnsupportedLanguageOrRoot");
        xmlCache[unit.Id] = document; return document;
    }

    public IPipelineLogicCompiler Compiler(PipelineUnit unit) => Xml(unit).Root!.Name.LocalName=="StructuredText"
        ? new PipelineStructuredTextCompiler(Xml(unit),unit,source.Tags) : new PipelineFlgNetCompiler(Xml(unit),ResolveCall,
            name=>source.Tags.Where(t=>string.Equals(t.Name,name,StringComparison.OrdinalIgnoreCase)).Select(t=>t.DataType).Distinct(StringComparer.OrdinalIgnoreCase).SingleOrDefault());

    public IReadOnlyDictionary<string,LogicTerm> ResolveCall(XElement call, Func<string,LogicTerm> input,
        IList<PipelineCallEvidence> evidence)
    {
        var info = call.Elements().SingleOrDefault(e => e.Name.LocalName == "CallInfo") ?? throw new PipelineException("MissingCallInfo");
        var name = (string?)info.Attribute("Name") ?? throw new PipelineException("MissingCallee");
        var instance = ReadInstance(info);
        var blockType = (string?)info.Attribute("BlockType");
        if (blockType is not ("FB" or "FC")) throw new PipelineException("UnsupportedCallBlockType");
        if (blockType == "FB" && instance is null) throw new PipelineException("MissingCallInstance");
        if (blockType == "FC" && instance != null) throw new PipelineException("UnexpectedCallInstance");
        var body = source.Units.Where(u => string.Equals(u.BlockName,name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if (body.Length == 0) throw new PipelineException("MissingCallee");
        if (body.Select(u => u.BlockId).Distinct().Count() != 1) throw new PipelineException("AmbiguousCallee");
        var parameters = info.Elements().Where(e => e.Name.LocalName == "Parameter").ToArray();
        if (parameters.Length == 0 || parameters.Any(p => string.IsNullOrWhiteSpace((string?)p.Attribute("Name"))))
            throw new PipelineException("MissingCallInterface");
        if (parameters.GroupBy(p => (string)p.Attribute("Name")!,StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new PipelineException("AmbiguousCallParameter");
        if (parameters.Any(p => (string?)p.Attribute("Section") is not ("Input" or "Output") ||
            (p.Attribute("Type") is { } type && !string.Equals(type.Value,"Bool",StringComparison.OrdinalIgnoreCase))))
            throw new PipelineException("UnsupportedCallParameter");
        var ins = parameters.Where(p => (string?)p.Attribute("Section") == "Input").Select(p => "#"+(string)p.Attribute("Name")!).ToArray();
        var outs = parameters.Where(p => (string?)p.Attribute("Section") == "Output").Select(p => "#"+(string)p.Attribute("Name")!).ToArray();
        var key = PipelineHash.Of(JsonSerializer.Serialize(new { Block=body[0].BlockId, Inputs=ins, Outputs=outs, HasInstance=instance != null }));
        if (!templates.TryGetValue(key,out var template)) {
            if (compiling.Contains(body[0].BlockId)) throw new PipelineException("RecursiveCall");
            if (compiling.Count >= 16) throw new PipelineException("CallDepthLimit");
            compiling.Add(body[0].BlockId);
            try {
                if (body.Length > 1 && (body.Any(u => u.NetworkOrder is null) || body.Select(u => u.NetworkOrder).Distinct().Count() != body.Length))
                    throw new PipelineException("MissingNetworkOrder");
                var locals = ins.ToDictionary(n => n,n => LogicTerm.Signal(n),StringComparer.OrdinalIgnoreCase);
                if (instance != null) foreach (var output in outs) locals[output]=new("Previous",output);
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nestedInstances = new List<string>(); var nestedState = new List<PipelineAssignment>();
                foreach (var network in body.OrderBy(u => u.NetworkOrder)) {
                    var entryLocals=new Dictionary<string,LogicTerm>(locals,StringComparer.OrdinalIgnoreCase);
                    var compiler = Compiler(network);
                    var assignments = compiler.Compile();
                    if (compiler.Timers.Count > 0) throw new PipelineException("CalleeTimerBindingRequiresExecutionModel");
                    nestedInstances.AddRange(compiler.CallEvidence.SelectMany(c => c.InstancePaths ?? []));
                    foreach (var update in compiler.CallEvidence.SelectMany(c => c.StateUpdates ?? []))
                        nestedState.Add(update with { Expression = ExpandLocal(update.Expression,entryLocals) });
                    foreach (var assignment in assignments) {
                        if (!assignment.Target.StartsWith("#",StringComparison.Ordinal))
                            throw new PipelineException("CalleeSideEffectRequiresExecutionModel");
                        if (ins.Contains(assignment.Target,StringComparer.OrdinalIgnoreCase))
                            throw new PipelineException("InputParameterWriteUnsupported");
                        locals[assignment.Target] = ExpandLocal(assignment.Expression,entryLocals);
                        written.Add(assignment.Target);
                    }
                }
                LogicTerm ExpandLocal(LogicTerm expression,IReadOnlyDictionary<string,LogicTerm> entry) => PipelineTerms.Rewrite(expression,t => {
                    if (t.Kind is not ("Signal" or "Previous") || !t.Name!.StartsWith("#",StringComparison.Ordinal)) return null;
                    if (t.Kind == "Previous" && !outs.Contains(t.Name,StringComparer.OrdinalIgnoreCase)) throw new PipelineException("UndeclaredLocalState");
                    return entry.TryGetValue(t.Name,out var bound) ? bound : throw new PipelineException("UninitializedLocalOrMissingBinding");
                });
                if (outs.Any(o => !written.Contains(o))) throw new PipelineException("UnassignedCallOutput");
                template = new(outs.ToDictionary(o => o[1..],o => locals[o],StringComparer.OrdinalIgnoreCase),nestedInstances,nestedState);
                templates[key] = template;
            }
            finally { compiling.Remove(body[0].BlockId); }
        }
        var enable = input("en");
        if (!PipelineTerms.IsTrue(enable)) throw new PipelineException("ConditionalCallRequiresExecutionModel");
        var actualInputs = new Dictionary<string,LogicTerm>(StringComparer.OrdinalIgnoreCase);
        LogicTerm Bind(LogicTerm expression) => PipelineTerms.Rewrite(expression,t => {
            if (t.Kind == "Previous" && t.Name!.StartsWith("#",StringComparison.Ordinal)) {
                if (instance is null) throw new PipelineException("StatefulFunctionRequiresInstance");
                return new("Previous",instance+"."+t.Name[1..]);
            }
            if (t.Kind != "Signal" || !t.Name!.StartsWith("#",StringComparison.Ordinal)) return null;
            if (!ins.Contains(t.Name,StringComparer.OrdinalIgnoreCase)) throw new PipelineException("MissingCallBinding");
            if (!actualInputs.TryGetValue(t.Name,out var expression)) actualInputs[t.Name] = expression = input(t.Name[1..]);
            return expression;
        });
        var outputs = template.Outputs.ToDictionary(p => p.Key,p => Bind(p.Value),StringComparer.OrdinalIgnoreCase);
        var updates = template.NestedState.Select(s => s with { Expression = Bind(s.Expression) }).ToList();
        if (instance != null) updates.AddRange(outputs.Select(p => new PipelineAssignment(instance+"."+p.Key,p.Value)));
        var paths = template.NestedInstances.Concat(instance is null ? [] : new[]{instance}).ToArray();
        evidence.Add(new(name,(string?)call.Attribute("UId") ?? "",body.Select(u => u.Id).ToArray(),outputs,paths,updates));
        return outputs;
    }

    static string? ReadInstance(XElement info) {
        var instance=info.Elements().SingleOrDefault(e => e.Name.LocalName == "Instance");
        if(instance is null) return null;
        if((string?)instance.Attribute("Scope") != "GlobalVariable") throw new PipelineException("NestedInstanceBindingUnsupported");
        var components=(instance.Elements().SingleOrDefault(e => e.Name.LocalName == "Symbol") ?? instance).Elements().ToArray();
        if(components.Length==0 || components.Any(e => e.Name.LocalName!="Component" || e.HasElements ||
            string.IsNullOrWhiteSpace((string?)e.Attribute("Name")) || ((string)e.Attribute("Name")!).IndexOfAny(['.','#','[',']'])>=0))
            throw new PipelineException("UnsupportedCallInstanceBinding");
        return string.Join(".",components.Select(e => (string)e.Attribute("Name")!));
    }
}

public static class PipelineTerms
{
    public static string PreviousKey(string name) => "\u001fprevious:"+name;
    public static string TimerKey(string name) => "\u001ftimer:"+name;
    public static bool Any(LogicTerm term,Func<LogicTerm,bool> predicate) => predicate(term) || (term.Children ?? []).Any(t => Any(t,predicate));
    public static LogicTerm Rewrite(LogicTerm term,Func<LogicTerm,LogicTerm?> replace) {
        int remaining = 20000;
        LogicTerm Visit(LogicTerm node,int depth) {
            if (--remaining < 0 || depth > 256) throw new PipelineException("ExpressionBudgetExceeded");
            var replacement = replace(node);
            if (replacement != null) return replacement;
            return node with { Children = node.Children?.Select(n => Visit(n,depth+1)).ToArray() };
        }
        return Visit(term,0);
    }
    public static bool IsTrue(LogicTerm term) => term.Kind == "Constant" && term.Name == "true" ||
        term.Kind == "And" && term.Children?.Count > 0 && term.Children.All(IsTrue);
    public static IEnumerable<LogicTerm> Walk(LogicTerm term) { yield return term;foreach(var child in term.Children ?? [])foreach(var node in Walk(child))yield return node; }
    public static IEnumerable<LogicTerm> Atoms(LogicTerm term) => term.Kind is "Signal" or "Previous" or "TimerElapsed" or "Compare"
        ? [term] : (term.Children ?? []).SelectMany(Atoms);
    public static string AtomKey(LogicTerm atom) => atom.Kind switch {
        "Previous" => PreviousKey(atom.Name!),"TimerElapsed" => TimerKey(atom.Name!),
        "Compare" => "\u001fcompare:"+PipelineHash.Of(JsonSerializer.Serialize(atom)),_ => atom.Name!
    };
}
