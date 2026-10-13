#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessTemplateBindingResult(IReadOnlyList<BusinessCandidate> Candidates,IReadOnlyList<string> Diagnostics,bool StableWriterInventoryComplete=false);

/// <summary>Substitutes a reviewed block template through explicit call wiring. It does not infer business semantics.</summary>
public static class BusinessTemplateBinder
{
    private sealed class InventoryState { public bool Complete=true; }
    private static readonly LogicTerm True=new("Constant","true");
    private static readonly LogicTerm False=new("Constant","false");
    private static string A(XElement e,string name)=>e.Attribute(name)?.Value??"";
    private static bool Is(XElement e,string name)=>e.Name.LocalName==name;
    private static PipelineException Unsupported()=>new("UnsupportedTemplateBinding");
    private static XElement Parse(string xml)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000});
        return XElement.Load(reader);
    }
    private static bool Overlaps(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+".",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+".",StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+"[",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+"[",StringComparison.OrdinalIgnoreCase);
    private static LogicTerm Not(LogicTerm t)=>t.Kind=="Constant"?(t.Name=="true"?False:True):t.Kind=="Not"?t.Children![0]:new("Not",null,[t]);
    private static LogicTerm Join(string kind,IEnumerable<LogicTerm> terms)
    {
        // Only identities are removed, after every operand has already been resolved successfully.
        var children=terms.SelectMany(t=>t.Kind==kind?t.Children!:new[]{t}).Where(t=>!(t.Kind=="Constant"&&t.Name==(kind=="And"?"true":"false"))).ToArray();
        return children.Length==0?(kind=="And"?True:False):children.Length==1?children[0]:new(kind,null,children);
    }
    private static LogicTerm And(params LogicTerm[] terms)=>Join("And",terms);
    private static LogicTerm Or(params LogicTerm[] terms)=>Join("Or",terms);
    private static IEnumerable<string> Leaves(LogicTerm term)=>term.Kind=="Signal"?[term.Name!]:term.Children?.SelectMany(Leaves)??[];

    public static IReadOnlyList<BusinessCandidate> Bind(PipelineSnapshot source,PipelineUnit caller,BusinessDescription callerDescription,
        BusinessDescription templateDescription,IReadOnlyList<BusinessCandidate> templateCandidates)
        => BindWithDiagnostics(source,caller,callerDescription,templateDescription,templateCandidates).Candidates;

    /// <summary>False permits unproven model-review candidates only. Callers must retain Diagnostics and
    /// StableWriterInventoryComplete; it never relaxes an already-known conflicting write.</summary>
    public static BusinessTemplateBindingResult BindWithDiagnostics(PipelineSnapshot source,PipelineUnit caller,BusinessDescription callerDescription,
        BusinessDescription templateDescription,IReadOnlyList<BusinessCandidate> templateCandidates,bool requireCompleteStableInventory=true)
    {
        var diagnostics=new List<string>();var inventory=new InventoryState();
        if(callerDescription.Status!="Rendered"||templateDescription.Status is not("Rendered" or "TemplateRendered")||callerDescription.BlockId!=caller.BlockId||callerDescription.LogicUnitId!=caller.Id||templateCandidates.Count>512)
            return new([], ["InvalidBindingInput"]);
        try {return new(BindCore(source,caller,callerDescription,templateDescription,templateCandidates,diagnostics,requireCompleteStableInventory,inventory),diagnostics.Distinct().ToArray(),inventory.Complete);}
        catch(Exception ex) when(ex is XmlException or PipelineException or ArgumentException or InvalidOperationException) {
            diagnostics.Add(ex is PipelineException p?p.Code:"MalformedBindingSource");return new([],diagnostics.Distinct().ToArray());
        }
    }
    private static IReadOnlyList<BusinessCandidate> BindCore(PipelineSnapshot source,PipelineUnit caller,BusinessDescription description,BusinessDescription template,IReadOnlyList<BusinessCandidate> candidates,List<string> diagnostics,bool requireCompleteStableInventory,InventoryState inventory)
    {
        var result=new List<BusinessCandidate>();
        var memory=BusinessMemoryAliases.For(source);
        var effects=new Dictionary<long,IReadOnlyList<string>>();
        var candidateEvidence=candidates.SelectMany(c=>c.EvidenceIds).ToHashSet(StringComparer.Ordinal);
        var callerSourceMarker=$"source LU:{caller.Id} ";
        // Large rendered call descriptions contain thousands of unrelated facts. Only candidate citations
        // and this caller's substitution facts can participate, but each still must be model-visible.
        var visible=description.Evidence.Where(e=>candidateEvidence.Contains(e.Id)||
                e.Kind is "CallBinding" or "CallGate" or "SourceStructure"&&e.Text.Contains(callerSourceMarker,StringComparison.Ordinal))
            .Where(e=>description.ModelText.Contains("["+e.Id+"]",StringComparison.Ordinal)).ToArray();
        var visibleIds=visible.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        var aliases=description.SignalAliases.GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().Key,StringComparer.OrdinalIgnoreCase);
        string? Observable(string key)
        {
            if(key.StartsWith("G:",StringComparison.Ordinal))return aliases.GetValueOrDefault(key[2..]);
            if(!key.StartsWith("L:",StringComparison.Ordinal))return null;
            var symbols=(description.Symbols??[]).Where(s=>s.BlockId==caller.BlockId&&s.Path.Equals(key[2..],StringComparison.OrdinalIgnoreCase)&&s.Section is "Input" or "Output" or "InOut" or "Static"&&description.SignalAliases.ContainsKey(s.Alias)).ToArray();
            return symbols.Select(s=>s.Alias).Distinct(StringComparer.Ordinal).Count()==1?symbols[0].Alias:null;
        }
        string PhysicalKey(string key)
        {
            if(key.StartsWith("G:",StringComparison.Ordinal))return key;
            if(!key.StartsWith("L:",StringComparison.Ordinal))throw Unsupported();
            if(Observable(key) is { } alias)return "G:"+description.SignalAliases[alias];
            var local=key[2..];var possibilities=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localSymbols=(description.Symbols??[]).Where(s=>s.BlockId==caller.BlockId&&description.SignalAliases.ContainsKey(s.Alias)).ToArray();
            foreach(var symbol in localSymbols) {
                var actual=description.SignalAliases[symbol.Alias];
                if(local.StartsWith(symbol.Path+".",StringComparison.OrdinalIgnoreCase)||local.StartsWith(symbol.Path+"[",StringComparison.OrdinalIgnoreCase))
                    possibilities.Add(actual+local[symbol.Path.Length..]);
                if(symbol.Path.StartsWith(local+".",StringComparison.OrdinalIgnoreCase)||symbol.Path.StartsWith(local+"[",StringComparison.OrdinalIgnoreCase)) {
                    var suffix=symbol.Path[local.Length..];
                    if(actual.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))possibilities.Add(actual[..^suffix.Length]);
                }
            }
            if(possibilities.Count==1)return "G:"+possibilities.Single();
            // A declared root member identifies the containing physical instance for its siblings too.
            // Multiple instances or absent provenance cannot certify local/global disjointness.
            var instances=localSymbols.Where(s=>description.SignalAliases[s.Alias].EndsWith("."+s.Path,StringComparison.OrdinalIgnoreCase))
                .Select(s=>description.SignalAliases[s.Alias][..^(s.Path.Length+1)]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(possibilities.Count==0&&instances.Length==1)return "G:"+instances[0]+"."+local;
            throw new PipelineException("UnresolvedCallerWriteAlias:"+key);
        }
        var networks=new List<(PipelineUnit Unit,Network Graph)>();
        foreach(var unit in source.Units.Where(u=>u.BlockId==caller.BlockId)) {
            try {
                if(string.IsNullOrWhiteSpace(unit.RawContent))throw new PipelineException("CallerSourceMissing");
                var roots=Parse(unit.RawContent).DescendantsAndSelf().Where(e=>Is(e,"FlgNet")).ToArray();
                if(roots.Length!=1)throw new PipelineException("UnsupportedCallerWriterLanguage");
                networks.Add((unit,new(roots[0],Observable)));
            }catch(Exception ex) when(unit.Id!=caller.Id&&ex is XmlException or PipelineException or ArgumentException or InvalidOperationException) {
                inventory.Complete=false;diagnostics.Add("CallerLU:"+unit.Id+":"+(ex is PipelineException p?p.Code:"UnparsedCallerWriterSource"));
            }
        }
        var graphs=networks.Where(n=>n.Unit.Id==caller.Id).Select(n=>n.Graph).ToArray();
        if(graphs.Length!=1)return [];
        var graph=graphs[0];
        var writes=new List<string>();
        foreach(var network in networks) {
            string[] targets;
            try {targets=network.Graph.Writes(true).ToArray();}
            catch(PipelineException ex) when(network.Unit.Id!=caller.Id) {
                inventory.Complete=false;diagnostics.Add("CallerLU:"+network.Unit.Id+":"+ex.Code);
                targets=network.Graph.Writes(false).ToArray();
            }
            foreach(var target in targets) {
                try {writes.Add(PhysicalKey(target));}
                catch(PipelineException ex) when(network.Unit.Id!=caller.Id) {inventory.Complete=false;diagnostics.Add("CallerLU:"+network.Unit.Id+":"+ex.Code);}
            }
        }
        // A call with no explicit Output pins may still write globals. Inventory every caller network,
        // including recursive dependencies, before using the absence of a second writer as evidence.
        foreach(var network in networks) {
            var dependencyCalls=network.Graph.Parts.Values.Where(e=>Is(e,"Call")).Select(call=>{
                var info=call.Elements().SingleOrDefault(e=>Is(e,"CallInfo"));
                if(info==null||A(info,"BlockType") is not("FB" or "FC")||string.IsNullOrWhiteSpace(A(info,"Name")))throw Unsupported();
                return A(info,"Name");
            }).ToArray();
            foreach(var name in dependencyCalls.Concat(network.Unit.Callees.Except(dependencyCalls,StringComparer.OrdinalIgnoreCase))) {
                var ids=source.Units.Where(u=>u.BlockName.Equals(name,StringComparison.OrdinalIgnoreCase)).Select(u=>u.BlockId).Distinct().ToArray();
                try {
                    if(ids.Length!=1)throw new PipelineException("MissingOrAmbiguousCallerCallee");
                    writes.AddRange(GlobalEffects(source,ids[0],effects,new HashSet<long>(),0).Select(path=>"G:"+path));
                }catch(Exception ex) when(ex is XmlException or PipelineException or ArgumentException or InvalidOperationException) {
                    inventory.Complete=false;diagnostics.Add("CallerLU:"+network.Unit.Id+":Callee:"+name+":"+(ex is PipelineException p?p.Code:"UnresolvedCalleeGlobalEffects"));
                    // An unknown/missing network does not erase writes already present in other known
                    // networks. The model-candidate path must still reject those concrete conflicts.
                    if(ids.Length==1)writes.AddRange(KnownGlobalEffects(source,ids[0],new HashSet<long>(),0).Select(path=>"G:"+path));
                }
            }
        }
        bool PhysicalOverlap(string a,string b,Action<string> unknown) {
            var relation=memory.Compare(a,b);
            if(relation==BusinessMemoryRelation.Unknown)unknown("UnknownPhysicalAddress:"+a+" <> "+b+":"+memory.Resolve(a).Reason+"/"+memory.Resolve(b).Reason);
            return relation==BusinessMemoryRelation.Overlap;
        }
        bool SingleWriter(string key,Action<string> unknown)=>writes.Count(w=>PhysicalOverlap(w,PhysicalKey(key),unknown))==1;
        var completeSourceInventory=inventory.Complete;
        foreach(var call in graph.Parts.Values.Where(e=>Is(e,"Call"))) {
            var uid=A(call,"UId");var info=call.Elements().SingleOrDefault(e=>Is(e,"CallInfo"));
            if(info==null||A(info,"BlockType") is not("FB" or "FC"))continue;
            var bodies=source.Units.Where(u=>u.BlockName.Equals(A(info,"Name"),StringComparison.OrdinalIgnoreCase)).ToArray();
            if(bodies.Length==0||bodies.Any(u=>u.BlockId!=template.BlockId))continue;
            IReadOnlyList<string> hiddenWrites;
            try {hiddenWrites=GlobalEffects(source,template.BlockId,effects,new HashSet<long>(),0);}
            catch(Exception ex) when(ex is XmlException or PipelineException or ArgumentException or InvalidOperationException){continue;}
            var bindingEvidence=visible.Where(e=>e.Kind=="CallBinding"&&e.Text.Contains($"CallUID:{uid} ",StringComparison.Ordinal)&&e.Text.Contains($"source LU:{caller.Id} ",StringComparison.Ordinal)).ToArray();
            var gateEvidence=visible.Where(e=>e.Kind=="CallGate"&&e.Text.Contains($"CallUID:{uid} ",StringComparison.Ordinal)&&e.Text.Contains($"source LU:{caller.Id} ",StringComparison.Ordinal)).ToArray();
            var structures=visible.Where(e=>e.Kind=="SourceStructure"&&e.Text.Contains($"source LU:{caller.Id} ",StringComparison.Ordinal)).ToArray();
            if(bindingEvidence.Length==0||gateEvidence.Length==0||structures.Length==0)continue;
            LogicTerm enable;
            try {enable=graph.Input(uid,"en");if(Negated(call,"en"))enable=Not(enable);}
            catch(PipelineException){continue;}
            var callWrites=info.Elements().Where(p=>Is(p,"Parameter")&&A(p,"Section") is "Output" or "InOut")
                .Select(p=>graph.OutputTarget(uid,A(p,"Name"))).Where(p=>p!=null).Select(p=>Observable(p!)).Where(a=>a!=null).Select(a=>description.SignalAliases[a!]).ToArray();
            foreach(var candidate in candidates) {
                try {
                    if(requireCompleteStableInventory&&candidate.Applicability=="StableDiagnosticSnapshot"&&!completeSourceInventory)continue;
                    bool unknownPhysical=false;
                    void UnknownPhysical(string detail){unknownPhysical=true;inventory.Complete=false;diagnostics.Add(detail);}
                    if(candidate.Signals.Count==0||candidate.Signals.Count>64||candidate.EvidenceIds.Count==0||candidate.EvidenceIds.Any(id=>!visibleIds.Contains(id)))continue;
                    var mapped=new Dictionary<string,LogicTerm>(StringComparer.Ordinal);
                    var sampled=Leaves(enable).ToHashSet(StringComparer.Ordinal);
                    LogicTerm Map(string alias)
                    {
                        if(mapped.TryGetValue(alias,out var existing))return existing;
                        LogicTerm value;
                        if(template.SignalAliases.TryGetValue(alias,out var actual)) {
                            if(!aliases.TryGetValue(actual,out var target))throw Unsupported();
                            value=LogicTerm.Signal(target);
                            sampled.Add(target);
                        } else {
                            var matches=(template.Symbols??[]).Where(s=>s.Alias==alias&&s.BlockId==template.BlockId).ToArray();
                            if(matches.Length!=1)throw Unsupported();
                            var symbol=matches[0];
                            if(symbol.Section is not("Input" or "Output" or "InOut")||symbol.DataType!="Bool"||symbol.Path.IndexOfAny(['.','[',']','#'])>=0)throw Unsupported();
                            var parameters=info.Elements().Where(p=>Is(p,"Parameter")&&A(p,"Name")==symbol.Path&&A(p,"Section")==symbol.Section&&A(p,"Type")=="Bool").ToArray();
                            if(parameters.Length!=1)throw Unsupported();
                            if(symbol.Section=="Input") {
                                value=graph.Input(uid,symbol.Path);
                                if(Negated(call,symbol.Path))value=Not(value);
                                sampled.UnionWith(Leaves(value));
                            } else {
                                if(Negated(call,symbol.Path))throw Unsupported();
                                var key=graph.OutputTarget(uid,symbol.Path)??throw new PipelineException("UnboundCallOutput:"+uid+"."+symbol.Path);
                                if(!SingleWriter(key,UnknownPhysical))throw new PipelineException("ConflictingCallerWrite:"+PhysicalKey(key));
                                if(Observable(key) is not { } outputAlias)throw new PipelineException("UnobservableCallOutput:"+key);
                                value=LogicTerm.Signal(outputAlias);
                            }
                        }
                        mapped.Add(alias,value);return value;
                    }
                    LogicTerm MapScalar(string alias,string? requestedType=null)
                    {
                        if(template.SignalAliases.ContainsKey(alias))return new("ScalarSignal",Map(alias).Name,DataType:requestedType);
                        var matches=(template.Symbols??[]).Where(s=>s.Alias==alias&&s.BlockId==template.BlockId).ToArray();
                        if(matches.Length!=1)throw Unsupported();var symbol=matches[0];
                        PipelineNumeric.Coerce(0,symbol.DataType);
                        if(symbol.Section!="Input"||symbol.Path.IndexOfAny(['.','[',']','#'])>=0||Negated(call,symbol.Path))throw Unsupported();
                        var parameters=info.Elements().Where(p=>Is(p,"Parameter")&&A(p,"Name")==symbol.Path&&A(p,"Section")=="Input"&&A(p,"Type")==symbol.DataType).ToArray();
                        if(parameters.Length!=1)throw Unsupported();var value=graph.ScalarInput(uid,symbol.Path,symbol.DataType);
                        if(value.Kind=="ScalarSignal")sampled.Add(value.Name!);return value;
                    }
                    int nodeBudget=2048;
                    LogicTerm Rewrite(LogicTerm term,int depth=0)
                    {
                        if(--nodeBudget<0||depth>32)throw Unsupported();
                        if(term.Kind is "Signal" or "ScalarSignal") {
                            if((term.Children?.Count??0)!=0||term.Name==null)throw Unsupported();
                            if(term.Kind=="ScalarSignal") {
                                return MapScalar(term.Name,term.DataType);
                            }
                            return Map(term.Name);
                        }
                        if(term.Kind is "Constant" or "Number")return term;
                        if(term.Kind is not("And" or "Or" or "Not" or "Compare")||term.Children==null)throw Unsupported();
                        return term with{Children=term.Children.Select(t=>Rewrite(t,depth+1)).ToArray()};
                    }
                    var signals=candidate.Signals.Select(s=>(Original:s,Term:Map(s.Name))).ToArray();
                    var conditions=new List<BusinessCondition>();var guards=new List<LogicTerm>();
                    if(enable!=True)guards.Add(enable);
                    foreach(var condition in candidate.Conditions) {
                        var term=Map(condition.Signal);
                        if(term.Kind=="Signal")conditions.Add(new(term.Name!,condition.Value));
                        else guards.Add(condition.Value?term:Not(term));
                    }
                    if(candidate.ApplicabilityCondition!=null)guards.Add(Rewrite(candidate.ApplicabilityCondition));
                    string? settling=null;var settlingMs=candidate.SettlingTimeMs;
                    if(candidate.SettlingTimeSignal!=null) {
                        var term=MapScalar(candidate.SettlingTimeSignal);
                        if(term.Kind=="ScalarSignal")settling=term.Name;
                        else if(term.Kind=="Number"&&settlingMs==null&&double.IsFinite(candidate.SettlingTimeScale)&&candidate.SettlingTimeScale>0) {
                            var number=PipelineNumeric.Coerce(double.Parse(term.Name!,CultureInfo.InvariantCulture),term.DataType!)*candidate.SettlingTimeScale;
                            if(!double.IsFinite(number)||number<0||number>=long.MaxValue)throw Unsupported();settlingMs=(long)Math.Ceiling(number);
                        } else throw Unsupported();
                    }
                    var op=candidate.Operator;LogicTerm? assertion=null;IReadOnlyList<PipelineSignal> boundSignals;
                    if(candidate.Assertion!=null)assertion=Rewrite(candidate.Assertion);
                    bool simple=signals.All(s=>s.Term.Kind=="Signal")&&signals.Select(s=>s.Term.Name).Distinct().Count()==signals.Length;
                    if(simple&&op!="EXPRESSION"&&assertion==null)boundSignals=signals.Select(s=>s.Original with{Name=s.Term.Name!}).ToArray();
                    else if(assertion==null&&op is "IMPLIES" or "ALL_TRUE"&&signals.Count(s=>s.Original.Role=="Source")==1&&signals.Single(s=>s.Original.Role=="Source").Term.Kind=="Signal"&&signals.Where(s=>s.Original.Role!="Source").All(s=>AllPositiveAtoms(s.Term))) {
                        boundSignals=signals.SelectMany(s=>s.Original.Role=="Source"?new[]{new PipelineSignal(s.Term.Name!,"Source")}:Leaves(s.Term).Select(n=>new PipelineSignal(n,s.Original.Role))).Distinct().ToArray();
                        if(boundSignals.Select(s=>s.Name).Distinct().Count()!=boundSignals.Count) {assertion=Assertion(candidate,Map);op="EXPRESSION";boundSignals=Leaves(assertion).Distinct().Select(n=>new PipelineSignal(n,"Member")).ToArray();}
                    } else {
                        assertion??=Assertion(candidate,Map);op="EXPRESSION";
                        boundSignals=Leaves(assertion).Distinct(StringComparer.Ordinal).Select(n=>new PipelineSignal(n,"Member")).ToArray();
                    }
                    if(boundSignals.Count==0||boundSignals.Count>64)continue;
                    if(sampled.Any(a=>callWrites.Any(w=>PhysicalOverlap(description.SignalAliases[a],w,UnknownPhysical))))continue;
                    // Scan-end stability does not make an actual input stable within the scan. A caller
                    // write before/after this call can otherwise bind a copied formal to a different epoch.
                    if(candidate.Applicability=="StableDiagnosticSnapshot"&&sampled.Any(a=>writes.Any(w=>PhysicalOverlap("G:"+description.SignalAliases[a],w,UnknownPhysical)))) {
                        diagnostics.Add("CallerInputOrEnableWrittenWithinScan:CallUID:"+uid);continue;
                    }
                    // Formal Input values are copied on entry. Reading their actuals after a hidden callee write
                    // would silently mix epochs, even when the template's own input/output relation is valid.
                    var referenced=mapped.Values.SelectMany(Leaves).Concat(sampled).Concat(boundSignals.Select(s=>s.Name)).Distinct().ToArray();
                    if(referenced.Any(a=>hiddenWrites.Any(w=>PhysicalOverlap(description.SignalAliases[a],w,UnknownPhysical))))continue;
                    foreach(var alias in referenced) {
                        var actual=description.SignalAliases[alias];var location=memory.Resolve(actual);
                        if(location.Status=="Unknown")UnknownPhysical("UnknownPhysicalAddress:"+actual+":"+location.Reason);
                    }
                    if(op is "AT_MOST_ONE" or "EXACTLY_ONE"&&boundSignals.SelectMany((a,i)=>boundSignals.Skip(i+1).Select(b=>(a,b)))
                        .Any(p=>PhysicalOverlap(description.SignalAliases[p.a.Name],description.SignalAliases[p.b.Name],UnknownPhysical))) {
                        diagnostics.Add("AliasedMutualExclusionMembers:CallUID:"+uid);continue;
                    }
                    if(requireCompleteStableInventory&&unknownPhysical)continue;
                    // Preserve metadata via `with`; only the explicitly substituted fields change.
                    result.Add(candidate with{Operator=op,Signals=boundSignals,Assertion=assertion,Conditions=conditions.Distinct().ToArray(),
                        ApplicabilityCondition=guards.Count==0?null:Join("And",guards),SettlingTimeSignal=settling,SettlingTimeMs=settlingMs,
                        EvidenceIds=candidate.EvidenceIds.Concat(bindingEvidence.Concat(gateEvidence).Concat(structures).Select(e=>e.Id)).Distinct(StringComparer.Ordinal).ToArray(),
                        Explanation=candidate.Explanation+"；依据当前调用的真实参数接线实例化，完整保留布尔分支与调用执行条件；观察点为该调用完成时，后续网络或外部写入后的任意快照不由本次代入保证。"+
                            (!completeSourceInventory||unknownPhysical?"；调用方写入清单或物理地址范围未证明完整，仅保留待模型独立复核的业务候选，禁止标记代码证明。未知依据："+string.Join(" | ",diagnostics):"")});
                }catch(Exception ex) when(ex is PipelineException or InvalidOperationException or ArgumentException) {
                    diagnostics.Add("CallUID:"+uid+":"+(ex is PipelineException p?p.Code:"InvalidCandidateBinding"));
                    // A failed candidate never poisons an independent candidate or call.
                }
            }
        }
        return result.DistinctBy(c=>System.Text.Json.JsonSerializer.Serialize(c)).ToArray();
    }
    private static IReadOnlyList<string> KnownGlobalEffects(PipelineSnapshot source,long blockId,HashSet<long> visiting,int depth)
    {
        if(depth>16||!visiting.Add(blockId))return [];
        var globals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach(var unit in source.Units.Where(u=>u.BlockId==blockId)) {
                var dependencies=new HashSet<string>(unit.Callees,StringComparer.OrdinalIgnoreCase);
                try {
                    if(!string.IsNullOrWhiteSpace(unit.RawContent)) {
                        var roots=Parse(unit.RawContent).DescendantsAndSelf().Where(e=>Is(e,"FlgNet")).ToArray();
                        if(roots.Length==1) {
                            var graph=new Network(roots[0],_=>null);
                            foreach(var call in graph.Parts.Values.Where(e=>Is(e,"Call"))) {
                                var info=call.Elements().FirstOrDefault(e=>Is(e,"CallInfo"));
                                if(info!=null&&!string.IsNullOrWhiteSpace(A(info,"Name")))dependencies.Add(A(info,"Name"));
                            }
                            foreach(var target in graph.Writes(false))if(target.StartsWith("G:",StringComparison.Ordinal))globals.Add(target[2..]);
                        }
                    }
                }catch(Exception ex) when(ex is XmlException or PipelineException or ArgumentException or InvalidOperationException) { /* Retain prior known writes; inventory is already explicitly incomplete. */ }
                foreach(var name in dependencies) {
                    var ids=source.Units.Where(u=>u.BlockName.Equals(name,StringComparison.OrdinalIgnoreCase)).Select(u=>u.BlockId).Distinct().ToArray();
                    if(ids.Length==1)globals.UnionWith(KnownGlobalEffects(source,ids[0],visiting,depth+1));
                }
            }
            return globals.ToArray();
        }finally{visiting.Remove(blockId);}
    }
    private static IReadOnlyList<string> GlobalEffects(PipelineSnapshot source,long blockId,
        Dictionary<long,IReadOnlyList<string>> cache,HashSet<long> visiting,int depth)
    {
        if(cache.TryGetValue(blockId,out var known))return known;
        if(depth>16||!visiting.Add(blockId))throw Unsupported();
        try {
            var units=source.Units.Where(u=>u.BlockId==blockId).ToArray();
            if(units.Length==0)throw new PipelineException("MissingCalleeBlockSource:"+blockId);
            if(units.Length>512)throw new PipelineException("CalleeSourceBudget:"+blockId);
            var missing=units.FirstOrDefault(u=>string.IsNullOrWhiteSpace(u.RawContent));
            if(missing!=null)throw new PipelineException("MissingCalleeNetworkSource:LU:"+missing.Id+":Kind:"+missing.UnitKind);
            var globals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var unit in units) {
                var roots=Parse(unit.RawContent).DescendantsAndSelf().Where(e=>Is(e,"FlgNet")).ToArray();
                if(roots.Length!=1)throw new PipelineException("UnsupportedCalleeWriterLanguage:LU:"+unit.Id+":Language:"+unit.Language);
                var graph=new Network(roots[0],_=>null);
                // Numeric, edge-memory and ordinary state updates remain supported. Only their explicit
                // global targets are effects outside this call; unknown writers cannot certify absence.
                foreach(var target in graph.Writes(true))if(target.StartsWith("G:",StringComparison.Ordinal))globals.Add(target[2..]);
                var dependencies=new HashSet<string>(unit.Callees,StringComparer.OrdinalIgnoreCase);
                foreach(var call in graph.Parts.Values.Where(e=>Is(e,"Call"))) {
                    var info=call.Elements().SingleOrDefault(e=>Is(e,"CallInfo"));
                    if(info==null||A(info,"BlockType") is not("FB" or "FC")||string.IsNullOrWhiteSpace(A(info,"Name")))throw Unsupported();
                    dependencies.Add(A(info,"Name"));
                }
                foreach(var name in dependencies) {
                    var ids=source.Units.Where(u=>u.BlockName.Equals(name,StringComparison.OrdinalIgnoreCase)).Select(u=>u.BlockId).Distinct().ToArray();
                    if(ids.Length!=1)throw Unsupported();
                    globals.UnionWith(GlobalEffects(source,ids[0],cache,visiting,depth+1));
                }
                if(globals.Count>8192)throw Unsupported();
            }
            var result=globals.ToArray();cache.Add(blockId,result);return result;
        }finally{visiting.Remove(blockId);}
    }
    private static bool AllPositiveAtoms(LogicTerm term)=>term.Kind=="Signal"||term.Kind=="And"&&term.Children!.All(AllPositiveAtoms);
    private static LogicTerm Assertion(BusinessCandidate candidate,Func<string,LogicTerm> map)
    {
        var members=candidate.Signals.Where(s=>s.Role=="Member").Select(s=>map(s.Name)).ToArray();
        var targets=candidate.Signals.Where(s=>s.Role=="Target").Select(s=>map(s.Name)).ToArray();
        LogicTerm Source()=>map(candidate.Signals.Single(s=>s.Role=="Source").Name);
        LogicTerm AtMostOne()=>Join("And",members.SelectMany((a,i)=>members.Skip(i+1).Select(b=>Not(And(a,b)))));
        return candidate.Operator switch {
            "IMPLIES"=>Or(Not(Source()),Join("And",targets)),
            "REQUIRES_0"=>Or(Not(Source()),Join("And",targets.Select(Not))),
            "ALL_TRUE"=>Or(Not(Source()),Join("And",members)),
            "MUST_BE_FALSE" when targets.Length==1=>Not(targets[0]),
            "AT_MOST_ONE" when members.Length>=2=>AtMostOne(),
            "EXACTLY_ONE" when members.Length>=2=>And(AtMostOne(),Join("Or",members)),
            "SAME_STATE" when members.Length>=2=>Or(Join("And",members),Join("And",members.Select(Not))),
            _=>throw Unsupported()
        };
    }
    private static bool Negated(XElement part,string pin)=>part.Elements().Any(e=>Is(e,"Negated")&&A(e,"Name")==pin);

    private sealed class Network
    {
        public readonly Dictionary<string,XElement> Parts;
        private readonly XElement[] wires;
        private readonly Func<string,string?> observable;
        private int budget;
        public Network(XElement root,Func<string,string?> observable)
        {
            var parts=root.Elements().Single(e=>Is(e,"Parts")).Elements().ToArray();
            if(parts.Length>4096)throw Unsupported();
            Parts=parts.ToDictionary(e=>A(e,"UId"),StringComparer.Ordinal);
            wires=root.Elements().Single(e=>Is(e,"Wires")).Elements().ToArray();
            if(wires.Length>8192)throw Unsupported();this.observable=observable;
        }
        private XElement? Wire(string uid,string pin)
        {
            var found=wires.Where(w=>w.Elements().Any(e=>Is(e,"NameCon")&&A(e,"UId")==uid&&A(e,"Name")==pin)).ToArray();return found.Length==1?found[0]:null;
        }
        private string? Symbol(string uid)
        {
            if(!Parts.TryGetValue(uid,out var access)||!Is(access,"Access")||A(access,"Scope") is not("GlobalVariable" or "LocalVariable"))return null;
            var components=access.Elements().SingleOrDefault(e=>Is(e,"Symbol"))?.Elements().ToArray();
            if(components==null||components.Length==0||components.Any(e=>!Is(e,"Component")||string.IsNullOrWhiteSpace(A(e,"Name"))))return null;
            string Component(XElement component) {
                var slice=component.Attribute("SliceAccessModifier");if(slice==null)return PipelineFlgNetCompiler.ComponentName(component);
                if(slice.Value.Length<2||char.ToLowerInvariant(slice.Value[0])!='x'||!int.TryParse(slice.Value[1..],out var bit)||bit<0||bit>63)throw Unsupported();
                var copy=new XElement(component);copy.Attribute("SliceAccessModifier")!.Remove();return PipelineFlgNetCompiler.ComponentName(copy)+".%X"+bit;
            }
            return (A(access,"Scope")=="GlobalVariable"?"G:":"L:")+string.Join(".",components.Select(Component));
        }
        private string? DirectTarget(XElement? wire)
        {
            if(wire==null||wire.Elements().Count()!=2)return null;
            var ids=wire.Elements().Where(e=>Is(e,"IdentCon")).ToArray();return ids.Length==1?Symbol(A(ids[0],"UId")):null;
        }
        private string? TimerInstance(XElement timer)
        {
            var instance=timer.Elements().SingleOrDefault(e=>Is(e,"Instance"));
            if(instance==null||A(instance,"Scope") is not("LocalVariable" or "GlobalVariable"))return null;
            var components=instance.Elements().ToArray();
            if(components.Length==0||components.Any(e=>!Is(e,"Component")))return null;
            return (A(instance,"Scope")=="GlobalVariable"?"G:":"L:")+string.Join(".",components.Select(PipelineFlgNetCompiler.ComponentName));
        }
        private bool KnownDataInput(XElement part,string pin)
        {
            if(IsInput(part,pin))return true;
            return A(part,"Name") switch {
                "TON" or "TOF" or "TP" or "TONR"=>pin is "IN" or "PT" or "R" or "en",
                "Move"=>pin is "in" or "en",
                "Add" or "Sub" or "Mul" or "Div"=>pin=="en"||pin.StartsWith("in",StringComparison.Ordinal)&&int.TryParse(pin[2..],out var index)&&index>0,
                "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le"=>pin is "pre" or "in1" or "in2",
                "PContact" or "NContact"=>pin is "pre" or "operand",
                "PBox" or "NBox"=>pin=="in",
                _=>false
            };
        }
        private bool IsUnstoredOutput(string uid,string pin)
        {
            var wire=Wire(uid,pin);if(wire==null)return false;
            // These consumers do not introduce an implicit target: any later Coil/scalar output is
            // inventoried independently. The timer's own instance is always recorded separately.
            var peers=wire.Elements().Where(e=>!(Is(e,"NameCon")&&A(e,"UId")==uid&&A(e,"Name")==pin)).ToArray();
            return peers.Length>0&&peers.All(e=>Is(e,"OpenCon")||Is(e,"NameCon")&&Parts.TryGetValue(A(e,"UId"),out var sink)&&KnownDataInput(sink,A(e,"Name")));
        }
        public string? OutputTarget(string uid,string pin)
        {
            var wire=Wire(uid,pin);var direct=DirectTarget(wire);if(direct!=null)return direct;
            if(wire==null||wire.Elements().Count()!=2)return null;
            var sinks=wire.Elements().Where(e=>Is(e,"NameCon")&&A(e,"UId")!=uid).ToArray();
            if(sinks.Length!=1||A(sinks[0],"Name")!="in"||!Parts.TryGetValue(A(sinks[0],"UId"),out var coil)||A(coil,"Name")!="Coil"||coil.Elements().Any(e=>Is(e,"Negated")))return null;
            return DirectTarget(Wire(A(coil,"UId"),"operand"));
        }
        private bool IsInput(XElement part,string pin)=>Is(part,"Call")?(pin=="en"||part.Descendants().Any(p=>Is(p,"Parameter")&&A(p,"Section")=="Input"&&A(p,"Name")==pin)):
            A(part,"Name") switch {"Contact"=>pin is "in" or "operand","Coil" or "SCoil" or "RCoil"=>pin=="in","NOT" or "Not"=>pin=="in","A" or "AND" or "And" or "O" or "OR" or "Or"=>pin.StartsWith("in",StringComparison.Ordinal)&&int.TryParse(pin[2..],out var index)&&index>0,_=>false};
        public LogicTerm Input(string uid,string pin){budget=8192;return Input(uid,pin,new HashSet<string>(),0);}
        public LogicTerm ScalarInput(string uid,string pin,string type)
        {
            var wire=Wire(uid,pin);if(wire==null||wire.Elements().Count()!=2)throw Unsupported();
            var ids=wire.Elements().Where(e=>Is(e,"IdentCon")).ToArray();if(ids.Length!=1)throw Unsupported();
            var id=A(ids[0],"UId");
            if(Symbol(id) is { } key)return new("ScalarSignal",observable(key)??throw Unsupported(),DataType:type);
            if(!Parts.TryGetValue(id,out var access)||A(access,"Scope") is not("LiteralConstant" or "TypedConstant"))throw Unsupported();
            var literalType=access.Descendants().SingleOrDefault(e=>Is(e,"ConstantType"))?.Value??type;
            var text=access.Descendants().SingleOrDefault(e=>Is(e,"ConstantValue"))?.Value;
            if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number))throw Unsupported();
            var value=PipelineNumeric.Coerce(PipelineNumeric.Coerce(number,literalType),type);
            return new("Number",value.ToString("R",CultureInfo.InvariantCulture),DataType:type);
        }
        private LogicTerm Input(string uid,string pin,HashSet<string> visiting,int depth)
        {
            if(--budget<0||depth>128)throw Unsupported();var wire=Wire(uid,pin)??throw Unsupported();var sources=new List<LogicTerm>();
            foreach(var endpoint in wire.Elements()) {
                if(Is(endpoint,"Powerrail")){sources.Add(True);continue;}
                if(Is(endpoint,"IdentCon")){sources.Add(Access(A(endpoint,"UId")));continue;}
                if(!Is(endpoint,"NameCon"))throw Unsupported();
                var id=A(endpoint,"UId");var port=A(endpoint,"Name");if(id==uid&&port==pin)continue;
                if(!Parts.TryGetValue(id,out var part))throw Unsupported();
                if(IsInput(part,port))continue;
                sources.Add(Output(id,port,visiting,depth+1));
            }
            if(sources.Count!=1)throw Unsupported();return sources[0];
        }
        private LogicTerm Access(string uid)
        {
            if(Symbol(uid) is { } key)return LogicTerm.Signal(observable(key)??throw Unsupported());
            if(!Parts.TryGetValue(uid,out var access)||!Is(access,"Access")||A(access,"Scope") is not("LiteralConstant" or "TypedConstant"))throw Unsupported();
            var value=access.Descendants().SingleOrDefault(e=>Is(e,"ConstantValue"))?.Value.Trim();
            return value?.ToUpperInvariant() switch {"TRUE" or "BOOL#TRUE"=>True,"FALSE" or "BOOL#FALSE"=>False,_=>throw Unsupported()};
        }
        private LogicTerm Output(string uid,string pin,HashSet<string> visiting,int depth)
        {
            if(--budget<0||depth>128||!visiting.Add(uid)||pin!="out")throw Unsupported();
            try {
                var part=Parts[uid];var kind=A(part,"Name");LogicTerm In(string p)=>Input(uid,p,visiting,depth+1);
                LogicTerm value;string[] validPins;
                if(kind=="Contact") {
                    validPins=["in","operand","out"];
                    var power=In("in");var operand=In("operand");
                    value=And(Negated(part,"in")?Not(power):power,Negated(part,"operand")?Not(operand):operand);
                } else if(kind is "A" or "AND" or "And" or "O" or "OR" or "Or") {
                    var cardinality=part.Elements().SingleOrDefault(e=>Is(e,"TemplateValue")&&A(e,"Name")=="Card")?.Value;
                    if(!int.TryParse(cardinality,out var count)||count<1||count>128)throw Unsupported();
                    var pins=Enumerable.Range(1,count).Select(i=>"in"+i).ToArray();validPins=pins.Append("out").ToArray();
                    var children=pins.Select(p=>{var t=In(p);return Negated(part,p)?Not(t):t;}).ToArray();
                    value=Join(kind is "A" or "AND" or "And"?"And":"Or",children);
                    if(wires.SelectMany(w=>w.Elements()).Any(e=>Is(e,"NameCon")&&A(e,"UId")==uid&&!validPins.Contains(A(e,"Name"))))throw Unsupported();
                } else if(kind is "NOT" or "Not") {validPins=["in","out"];var input=In("in");value=Not(Negated(part,"in")?Not(input):input);}
                else throw Unsupported();
                if(part.Elements().Where(e=>Is(e,"Negated")).Any(e=>!validPins.Contains(A(e,"Name"))))throw Unsupported();
                return Negated(part,"out")?Not(value):value;
            }finally{visiting.Remove(uid);}
        }
        public IEnumerable<string> Writes(bool strict)
        {
            foreach(var part in Parts.Values) {
                if(Is(part,"Access"))continue;
                var uid=A(part,"UId");var kind=A(part,"Name");
                if(Is(part,"Call")) {
                    foreach(var p in part.Descendants().Where(e=>Is(e,"Parameter")&&A(e,"Section") is "Output" or "InOut")) {
                        // An unsupported output is not silently omitted from the writer inventory.
                        var wire=Wire(uid,A(p,"Name"));if(wire==null)continue;
                        if(wire.Elements().Count()==2&&wire.Elements().Any(e=>Is(e,"OpenCon")))continue;
                        var target=OutputTarget(uid,A(p,"Name"));if(target!=null)yield return target;else if(strict)throw Unsupported();
                    }
                } else if(kind is "Coil" or "SCoil" or "RCoil") {
                    var target=DirectTarget(Wire(uid,"operand"));if(target==null){if(strict)throw Unsupported();continue;}
                    var input=Wire(uid,"in");
                    var fromCall=kind=="Coil"&&!part.Elements().Any(e=>Is(e,"Negated"))&&input?.Elements().Count()==2&&input.Elements().Any(e=>Is(e,"NameCon")&&A(e,"UId")!=uid&&Parts.TryGetValue(A(e,"UId"),out var driver)&&Is(driver,"Call")&&driver.Descendants().Any(p=>Is(p,"Parameter")&&A(p,"Name")==A(e,"Name")&&A(p,"Section")=="Output"));
                    if(!fromCall)yield return target;
                } else if(kind is "PContact" or "NContact" or "PBox" or "NBox") {
                    var target=DirectTarget(Wire(uid,"bit"));if(target!=null)yield return target;else if(strict)throw Unsupported();
                } else if(kind is not("Contact" or "A" or "AND" or "And" or "O" or "OR" or "Or" or "NOT" or "Not" or "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le")) {
                    // Known scalar instructions have explicit output pins; unknown instructions in this network remain unsafe.
                    if(strict&&kind is not("Move" or "Add" or "Sub" or "Mul" or "Div" or "TON" or "TOF" or "TP" or "TONR"))throw new PipelineException("UnknownWriterInstruction:"+kind+":UID:"+uid);
                    if(kind is "TON" or "TOF" or "TP" or "TONR") {
                        var instance=TimerInstance(part);
                        if(instance==null){if(strict)throw new PipelineException("UnresolvedTimerInstance:"+uid);}
                        else yield return instance;
                    }
                    foreach(var pin in wires.SelectMany(w=>w.Elements()).Where(e=>Is(e,"NameCon")&&A(e,"UId")==uid&&(A(e,"Name").StartsWith("out",StringComparison.Ordinal)||A(e,"Name") is "Q" or "ET")).Select(e=>A(e,"Name")).Distinct()) {
                        var target=DirectTarget(Wire(uid,pin));if(target!=null)yield return target;
                        else if(!IsUnstoredOutput(uid,pin)&&strict)throw new PipelineException("UnresolvedWriterOutput:"+kind+":"+uid+"."+pin);
                    }
                }
            }
        }
    }
}
