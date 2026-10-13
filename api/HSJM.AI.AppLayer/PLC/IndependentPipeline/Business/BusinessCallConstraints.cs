#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Bounded structural proposals only. A snapshot and all its source collections must remain immutable
/// for the run. Pure source analysis is cached by snapshot object identity; a new load receives a new context.
/// No model decisions or external runtime values are cached.</summary>
public static class BusinessCallConstraints
{
    private sealed record Expr(string Kind,string Name="",params Expr[] Children)
    {
        public bool Opaque => Kind=="Opaque" || Children.Any(c=>c.Opaque);
        // Necessary literals for the complete expression, including every OR branch and polarity.
        public HashSet<(string Name,bool Value)> Must(bool value=true)
        {
            if(Kind=="Atom")return [(Name,value)];
            if(Kind=="Not")return Children[0].Must(!value);
            if(Kind is not ("And" or "Or") || Children.Length==0)return [];
            var relevant=Children.Where(c=>!((Kind=="And"&&c.Kind=="True")||(Kind=="Or"&&c.Kind=="False"))).ToArray();
            if(relevant.Length==0)return [];
            var result=relevant[0].Must(value);
            foreach(var child in relevant.Skip(1)) {
                if((Kind=="And")==value)result.UnionWith(child.Must(value));else result.IntersectWith(child.Must(value));
            }
            return result;
        }
    }
    private sealed record Write(string Target,string Kind,int Order,Expr Expression,long UnitId);
    private static readonly Expr Unknown=new("Opaque");
    private static readonly Expr True=new("True");
    private static readonly ConditionalWeakTable<PipelineSnapshot,Lazy<SourceContext>> Contexts=new();
    private sealed record BodyAnalysis(bool Safe,IReadOnlyList<Write> Writes,XElement? Interface);
    private sealed record CallerWrites(bool Safe,IReadOnlyList<string> Targets);
    private sealed class SourceContext
    {
        public readonly PipelineUnit[] Units;
        public readonly Dictionary<string,PipelineUnit[]> Blocks;
        public readonly Dictionary<long,PipelineUnit[]> Owners;
        public readonly Lazy<VisibleEffects> Effects;
        public readonly BusinessMemoryAliases Memory;
        public readonly ConcurrentDictionary<long,Lazy<CallerWrites>> CallerTargets=new();
        private readonly ConcurrentDictionary<string,Lazy<XElement>> documents=new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string,Lazy<BodyAnalysis>> bodies=new(StringComparer.OrdinalIgnoreCase);
        public SourceContext(PipelineSnapshot source)
        {
            Units=source.Units.ToArray();
            Blocks=Units.GroupBy(u=>u.BlockName,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.OrderBy(u=>u.NetworkOrder).ToArray(),StringComparer.OrdinalIgnoreCase);
            Owners=Units.GroupBy(u=>u.BlockId).ToDictionary(g=>g.Key,g=>g.ToArray());
            Memory=BusinessMemoryAliases.For(source);
            Effects=new(()=>new VisibleEffects(this),LazyThreadSafetyMode.ExecutionAndPublication);
        }
        public XElement Document(string raw)=>documents.GetOrAdd(raw,s=>new(()=>Parse(s),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        public PipelineUnit[] Block(string name)=>Blocks.TryGetValue(name,out var units)?units:[];
        public BodyAnalysis Body(string name)=>bodies.GetOrAdd(name,n=>new(()=>AnalyzeBody(n),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        private BodyAnalysis AnalyzeBody(string name)
        {
            var units=Block(name);var writes=new List<Write>();
            if(units.Length==0||units.Length>128||units.Select(u=>u.BlockId).Distinct().Count()!=1)return new(false,writes,null);
            foreach(var body in units) {
                if(string.IsNullOrWhiteSpace(body.RawContent))return new(false,writes,null);
                var networks=Document(body.RawContent).DescendantsAndSelf().Where(e=>Named(e,"FlgNet")).ToArray();
                if(networks.Length!=1||!new Network(networks[0]).CollectWrites(body.NetworkOrder??0,body.Id,writes))return new(false,writes,null);
            }
            var declaration=units.Select(u=>u.InterfaceXml).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));
            return new(writes.Count<=4096,writes,declaration==null?null:Document(declaration));
        }
    }
    private static string A(XElement e,string name)=>e.Attribute(name)?.Value??"";
    private static bool Named(XElement e,string name)=>e.Name.LocalName==name;
    private static bool TimerKind(string kind)=>kind is "TON" or "TOF" or "TP" or "TONR";
    private static XElement Parse(string xml)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000});
        return XElement.Load(reader);
    }
    private static bool Overlaps(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+".",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+".",StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+"[",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+"[",StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<BusinessCandidate> Extract(PipelineSnapshot source,PipelineUnit caller,BusinessDescription description)
    {
        if(description.Status!="Rendered"||string.IsNullOrWhiteSpace(caller.RawContent))return [];
        try { return ExtractCore(source,caller,description); }
        catch(Exception error) when(error is XmlException or PipelineException or ArgumentException or InvalidOperationException) {return [];}
    }
    private static IReadOnlyList<BusinessCandidate> ExtractCore(PipelineSnapshot source,PipelineUnit caller,BusinessDescription description)
    {
        var context=Contexts.GetValue(source,s=>new(()=>new SourceContext(s),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var rules=new List<BusinessCandidate>();
        var aliases=description.SignalAliases.GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First().Key,StringComparer.OrdinalIgnoreCase);
        var visible=description.Evidence.Where(e=>e.Kind is "SourceStructure" or "CallBodyReference")
            .Where(e=>description.ModelText.Contains("["+e.Id+"]",StringComparison.Ordinal)).ToArray();
        foreach(var flg in context.Document(caller.RawContent).DescendantsAndSelf().Where(e=>Named(e,"FlgNet"))) {
            var graph=new Network(flg);
            foreach(var call in graph.Parts.Values.Where(e=>Named(e,"Call"))) {
                var info=call.Elements().SingleOrDefault(e=>Named(e,"CallInfo"));
                if(info==null||A(info,"BlockType")!="FB"||call.Descendants().Any(e=>Named(e,"Negated"))||graph.Input(A(call,"UId"),"en").Kind!="True")continue;
                if(!OtherCallsHaveNoHiddenWrites(context,graph,call))continue;
                var bodies=context.Block(A(info,"Name"));
                if(bodies.Length==0||bodies.Length>128||bodies.Select(u=>u.BlockId).Distinct().Count()!=1||bodies.Any(u=>u.NetworkOrder==null)||bodies.Select(u=>u.NetworkOrder).Distinct().Count()!=bodies.Length)continue;
                var reference=visible.FirstOrDefault(e=>e.Kind=="CallBodyReference"&&e.Text.Contains($"CallUID:{A(call,"UId")} ",StringComparison.Ordinal)&&e.Text.Contains($"source LU:{caller.Id} ",StringComparison.Ordinal));
                if(reference==null)continue;
                var bodyAnalysis=context.Body(A(info,"Name"));var writes=bodyAnalysis.Writes;
                // A global operand can alias this FB's local instance fields. Without full address normalization,
                // even an apparently unrelated global write invalidates this local-state proof.
                if(!bodyAnalysis.Safe||writes.Any(w=>w.Target.StartsWith("G:",StringComparison.Ordinal)))continue;
                var iface=bodyAnalysis.Interface;if(iface==null)continue;
                bool Static(string key)=>key.StartsWith("L:",StringComparison.Ordinal)&&iface.Descendants().Any(e=>Named(e,"Section")&&A(e,"Name")=="Static"&&e.Elements().Any(m=>Named(m,"Member")&&(string.Equals(key[2..],A(m,"Name"),StringComparison.OrdinalIgnoreCase)||key[2..].StartsWith(A(m,"Name")+".",StringComparison.OrdinalIgnoreCase))));
                bool Unique(Write write)=>writes.Count(w=>Overlaps(w.Target,write.Target))==1;
                var states=writes.Where(w=>w.Kind=="Coil"&&!w.Expression.Opaque&&Static(w.Target)&&Unique(w)).ToArray();
                var outputs=info.Elements().Where(e=>Named(e,"Parameter")&&A(e,"Section")=="Output"&&A(e,"Type")=="Bool")
                    .Select(e=>A(e,"Name")).Where(n=>iface.Descendants().Any(e=>Named(e,"Section")&&A(e,"Name")=="Output"&&e.Elements().Any(m=>Named(m,"Member")&&A(m,"Name")==n&&A(m,"Datatype")=="Bool")))
                    .Select(n=>(Pin:n,Write:writes.SingleOrDefault(w=>string.Equals(w.Target,"L:"+n,StringComparison.OrdinalIgnoreCase)&&w.Kind=="Coil"&&Unique(w))))
                    .Where(p=>p.Write!=null&&!p.Write.Expression.Opaque).ToArray();
                if(states.Length>128||outputs.Length>64)continue;
                var requirements=states.Concat(outputs.Select(p=>p.Write!)).Distinct().ToDictionary(w=>w,w=>w.Expression.Must());
                var samples=states.ToDictionary(w=>w,w=>outputs.Where(p=>requirements[p.Write!].Contains((w.Target,true))).ToArray());
                for(int i=0;i<states.Length;i++)for(int j=i+1;j<states.Length;j++) {
                    var x=states[i];var y=states[j];
                    if(x.Order==y.Order||!requirements[x].Contains((y.Target,false))||!requirements[y].Contains((x.Target,false)))continue;
                    var effects=context.Effects.Value;
                    if(!effects.NoOtherStateWrites(caller,A(call,"UId"),A(info,"Name"),graph.InstanceTarget(info)!,x.Target,y.Target))continue;
                    foreach(var ox in samples[x])
                    foreach(var oy in samples[y]) {
                        var before=ox.Write!.Order<Math.Min(x.Order,y.Order)&&oy.Write!.Order<Math.Min(x.Order,y.Order);
                        var after=ox.Write.Order>Math.Max(x.Order,y.Order)&&oy.Write!.Order>Math.Max(x.Order,y.Order);
                        if(!before&&!after)continue; // Mixed old/new state is not an invariant observation.
                        var actualX=graph.OutputTarget(A(call,"UId"),ox.Pin);var actualY=graph.OutputTarget(A(call,"UId"),oy.Pin);
                        if(actualX==null||actualY==null||actualX==actualY||!actualX.StartsWith("G:",StringComparison.Ordinal)||!actualY.StartsWith("G:",StringComparison.Ordinal)||!aliases.TryGetValue(actualX[2..],out var aliasX)||!aliases.TryGetValue(actualY[2..],out var aliasY))continue;
                        if(context.Memory.Resolve(actualX).Status=="Unknown"||context.Memory.Resolve(actualY).Status=="Unknown"||context.Memory.MayOverlap(actualX,actualY))continue;
                        if(!CallerSingleWriters(context,caller,actualX,actualY))continue;
                        var units=new[]{caller.Id,x.UnitId,y.UnitId,ox.Write.UnitId,oy.Write!.UnitId}.Distinct().ToArray();
                        var structures=units.Select(id=>visible.FirstOrDefault(e=>e.Kind=="SourceStructure"&&e.Text.Contains($"source LU:{id} ",StringComparison.Ordinal))).ToArray();
                        if(structures.Any(e=>e==null))continue;
                        rules.Add(new("AT_MOST_ONE",[new(aliasX,"Member"),new(aliasY,"Member")],
                            "被调块的两个内部状态各由唯一普通线圈赋值，完整布尔支路均强制另一状态为假；形式输出由真实赋值读取同一写入 epoch，再沿调用接线绑定到本候选信号。"+
                            (before?"输出在状态更新前取样：仅适用于已完成至少一次调用、无外部改写实例状态的稳定重复执行，并观察当前单元的稳定输出；首次任意初态不保证互斥。":"输出在两次状态更新完成后取样；适用当前单元执行完成时点，需无外部改写实例状态。")+
                            "已排除源快照内可静态绑定到这两个实例状态的其它写入；未覆盖源外强制写入，采样必须符合上述执行时点；不得据此推断反馈输入互斥。",
                            structures.Select(e=>e!.Id).Append(reference.Id).Distinct().ToArray(),[],before?"StableDiagnosticSnapshot":"ProgramRelation"));
                        if(rules.Count>=256)return rules.DistinctBy(BusinessCandidateValidator.Key).ToArray();
                    }
                }
            }
        }
        return rules.DistinctBy(BusinessCandidateValidator.Key).ToArray();
    }
    private sealed class VisibleEffects
    {
        private sealed record CallSite(PipelineUnit Owner,string Uid,string Callee,string? Instance,bool ExactInstance);
        private readonly List<(PipelineUnit Owner,string Target)> writes=[];
        private readonly List<CallSite> calls=[];
        private readonly Dictionary<string,string[]> roots=new(StringComparer.OrdinalIgnoreCase);
        private readonly BusinessMemoryAliases memory;
        public VisibleEffects(SourceContext context)
        {
            memory=context.Memory;
            foreach(var unit in context.Units.Where(u=>!string.IsNullOrWhiteSpace(u.RawContent))) {
                XElement xml;
                try {xml=context.Document(unit.RawContent);}catch(XmlException){continue;}
                foreach(var flg in xml.DescendantsAndSelf().Where(e=>Named(e,"FlgNet"))) {
                    var network=new Network(flg);
                    foreach(var part in network.Parts.Values) {
                        if(Named(part,"Access"))continue;
                        var uid=A(part,"UId");var kind=A(part,"Name");
                        if(Named(part,"Call")) {
                            var info=part.Elements().SingleOrDefault(e=>Named(e,"CallInfo"));if(info==null)continue;
                            string? instance;bool exact;
                            try {instance=network.InstanceTarget(info);exact=instance!=null;}
                            catch(PipelineException){instance=network.InstanceTarget(info,true);exact=false;}
                            calls.Add(new(unit,uid,A(info,"Name"),instance,exact));
                            foreach(var parameter in info.Elements().Where(p=>Named(p,"Parameter")&&A(p,"Section") is "Output" or "InOut"))
                                foreach(var target in network.ConnectedTargets(uid,A(parameter,"Name"),true))writes.Add((unit,target));
                        }else if(TimerKind(kind)&&network.TimerWrites(part,true) is { } timerWrites) {
                            foreach(var target in timerWrites)writes.Add((unit,target));
                        }else {
                            // A malformed timer is not silently omitted: retain its instance plus every possible
                            // connected write region here; the exact caller/body inventory rejects it outright.
                            if(TimerKind(kind)&&network.InstanceTarget(part,true) is { } timerInstance)writes.Add((unit,timerInstance));
                            var pins=kind switch {
                                "Coil" or "SCoil" or "RCoil"=>new[]{"operand"},
                                "PContact" or "NContact" or "PBox" or "NBox"=>new[]{"bit"},
                                "Move" or "Add" or "Sub" or "Mul" or "Div"=>network.Pins(uid).Where(p=>p.StartsWith("out",StringComparison.Ordinal)).ToArray(),
                                _=>network.PurePart(part)?[]:network.Pins(uid)
                            };
                            foreach(var pin in pins)foreach(var target in network.ConnectedTargets(uid,pin,true))writes.Add((unit,target));
                        }
                    }
                }
            }
        }
        private string[] Roots(string block,HashSet<string> visiting)
        {
            lock(roots)return RootsCore(block,visiting);
        }
        private string[] RootsCore(string block,HashSet<string> visiting)
        {
            if(roots.TryGetValue(block,out var cached))return cached;
            if(visiting.Count>=16||!visiting.Add(block))return [];
            try {
                var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var call in calls.Where(c=>string.Equals(c.Callee,block,StringComparison.OrdinalIgnoreCase))) {
                    if(call.Instance==null||!call.ExactInstance)return [];
                    if(call.Instance.StartsWith("G:",StringComparison.Ordinal))result.Add(call.Instance);
                    else foreach(var parent in Roots(call.Owner.BlockName,visiting))result.Add(parent+"."+call.Instance[2..]);
                    if(result.Count>4096)return [];
                }
                return roots[block]=result.ToArray();
            }finally{visiting.Remove(block);}
        }
        private string[] Absolute(PipelineUnit owner,string target)=>target.StartsWith("G:",StringComparison.Ordinal)
            ?[target]:Roots(owner.BlockName,new(StringComparer.OrdinalIgnoreCase)).Select(r=>r+"."+target[2..]).ToArray();
        public bool NoOtherStateWrites(PipelineUnit caller,string callUid,string callee,string instance,string x,string y)
        {
            var instances=Absolute(caller,instance);if(instances.Length!=1)return false;
            var selected=instances[0];var protectedX=selected+"."+x[2..];var protectedY=selected+"."+y[2..];
            if(memory.Resolve(protectedX).Status=="Unknown"||memory.Resolve(protectedY).Status=="Unknown"||memory.MayOverlap(protectedX,protectedY))return false;
            bool Hits(string target)=>memory.MayOverlap(target,protectedX)||memory.MayOverlap(target,protectedY);
            foreach(var write in writes) {
                // All local writes of this same block implementation were already checked for uniqueness above.
                if(string.Equals(write.Owner.BlockName,callee,StringComparison.OrdinalIgnoreCase)&&write.Target.StartsWith("L:",StringComparison.Ordinal))continue;
                if(Absolute(write.Owner,write.Target).Any(Hits))return false;
            }
            foreach(var call in calls) {
                if(call.Instance==null||call.Owner.Id==caller.Id&&call.Uid==callUid)continue;
                foreach(var absolute in Absolute(call.Owner,call.Instance)) {
                    // Entering an enclosing FB executes the selected call; it is not an independent overwrite.
                    if(string.Equals(call.Callee,caller.BlockName,StringComparison.OrdinalIgnoreCase)&&selected.StartsWith(absolute+".",StringComparison.OrdinalIgnoreCase))continue;
                    if(Hits(absolute))return false;
                }
            }
            return true;
        }
    }
    private static bool OtherCallsHaveNoHiddenWrites(SourceContext context,Network graph,XElement selected)
    {
        var selectedInfo=selected.Elements().Single(e=>Named(e,"CallInfo"));
        var instance=graph.InstanceTarget(selectedInfo);if(instance==null)return false;
        foreach(var part in graph.Parts.Values) {
            if(Named(part,"Call")) {
                var info=part.Elements().SingleOrDefault(e=>Named(e,"CallInfo"));if(info==null)return false;
                    if(part!=selected&&graph.InstanceTarget(info) is { } otherInstance&&context.Memory.MayOverlap(otherInstance,instance))return false;
                foreach(var parameter in info.Elements().Where(p=>Named(p,"Parameter")&&A(p,"Section") is "Output" or "InOut")) {
                    var target=graph.OutputTarget(A(part,"UId"),A(parameter,"Name"));
                    if(target!=null&&context.Memory.MayOverlap(target,instance))return false;
                }
            }else if(A(part,"Name") is "Coil" or "SCoil" or "RCoil") {
                var target=graph.Operand(A(part,"UId"));if(target!=null&&context.Memory.MayOverlap(target,instance))return false;
            }
        }
        foreach(var call in graph.Parts.Values.Where(p=>Named(p,"Call")&&p!=selected)) {
            var info=call.Elements().SingleOrDefault(e=>Named(e,"CallInfo"));if(info==null)return false;
            var body=context.Body(A(info,"Name"));
            if(!body.Safe||body.Writes.Any(w=>w.Target.StartsWith("G:",StringComparison.Ordinal)))return false;
        }
        return true;
    }
    private static bool CallerSingleWriters(SourceContext context,PipelineUnit caller,string x,string y)
    {
        var writes=context.CallerTargets.GetOrAdd(caller.BlockId,_=>new(()=>CollectCallerWrites(context,caller.BlockId),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return writes.Safe&&!writes.Targets.Any(t=>context.Memory.Compare(t,x)==BusinessMemoryRelation.Unknown||context.Memory.Compare(t,y)==BusinessMemoryRelation.Unknown)&&
            writes.Targets.Count(t=>context.Memory.Compare(t,x)==BusinessMemoryRelation.Overlap)==1&&writes.Targets.Count(t=>context.Memory.Compare(t,y)==BusinessMemoryRelation.Overlap)==1;
    }
    private static CallerWrites CollectCallerWrites(SourceContext context,long blockId)
    {
        var targets=new List<string>();
        foreach(var unit in context.Owners[blockId]) {
            if(string.IsNullOrWhiteSpace(unit.RawContent))continue;
            var networks=context.Document(unit.RawContent).DescendantsAndSelf().Where(e=>Named(e,"FlgNet")).ToArray();
            if(networks.Length!=1)return new(false,targets);
            var network=new Network(networks[0]);
            foreach(var part in network.Parts.Values) {
                if(Named(part,"Access"))continue;
                if(Named(part,"Call")) {
                    foreach(var output in part.Descendants().Where(p=>Named(p,"Parameter")&&A(p,"Section") is "Output" or "InOut")) {
                        var target=network.OutputTarget(A(part,"UId"),A(output,"Name"));if(target!=null)targets.Add(target);
                    }
                }else if(TimerKind(A(part,"Name"))) {
                    var timerWrites=network.TimerWrites(part);if(timerWrites==null)return new(false,targets);
                    targets.AddRange(timerWrites);
                }else if(A(part,"Name") is "Coil" or "SCoil" or "RCoil") {
                    // The call output followed by its own ordinary coil is one writer, counted above.
                    if(A(part,"Name")=="Coil"&&!part.Elements().Any(e=>Named(e,"Negated"))&&network.Input(A(part,"UId"),"in").Kind=="CallOutput")continue;
                    var target=network.Operand(A(part,"UId"));if(target!=null)targets.Add(target);
                }else if(!network.PurePart(part))return new(false,targets);
            }
        }
        return new(true,targets);
    }
    private sealed class Network
    {
        public readonly Dictionary<string,XElement> Parts;
        private readonly XElement[] wires;
        private int budget;
        public Network(XElement root)
        {
            var parts=root.Elements().Single(e=>Named(e,"Parts")).Elements().ToArray();
            if(parts.Length>4096)throw new PipelineException("CallSliceLimit");
            Parts=parts.ToDictionary(p=>A(p,"UId"));
            wires=root.Elements().Single(e=>Named(e,"Wires")).Elements().ToArray();
        }
        private XElement? Wire(string uid,string pin)
        {
            var found=wires.Where(w=>w.Elements().Any(e=>Named(e,"NameCon")&&A(e,"UId")==uid&&A(e,"Name")==pin)).ToArray();
            return found.Length==1?found[0]:null;
        }
        private string? Symbol(string uid)
        {
            if(!Parts.TryGetValue(uid,out var access)||!Named(access,"Access")||A(access,"Scope") is not("LocalVariable" or "GlobalVariable"))return null;
            var components=access.Elements().SingleOrDefault(e=>Named(e,"Symbol"))?.Elements().ToArray();
            if(components==null||components.Length==0||components.Any(e=>!Named(e,"Component")))return null;
            return ((A(access,"Scope")=="LocalVariable"?"L:":"G:")+string.Join(".",components.Select(Component))).ToUpperInvariant();
        }
        public string? InstanceTarget(XElement info,bool conservative=false)
        {
            var instance=info.Elements().SingleOrDefault(e=>Named(e,"Instance"));
            if(instance==null||A(instance,"Scope") is not("LocalVariable" or "GlobalVariable"))return null;
            var components=instance.Elements().ToArray();
            if(components.Length==0||components.Any(e=>!Named(e,"Component")))return null;
            return ((A(instance,"Scope")=="LocalVariable"?"L:":"G:")+(conservative?Region(components):string.Join(".",components.Select(Component)))).ToUpperInvariant();
        }
        private static string Region(XElement[] components)
        {
            var names=new List<string>();
            foreach(var component in components) {
                try{names.Add(Component(component));}
                catch(PipelineException){names.Add(A(component,"Name"));break;}
            }
            return string.Join(".",names);
        }
        private string? SymbolRegion(string uid)
        {
            if(!Parts.TryGetValue(uid,out var access)||!Named(access,"Access")||A(access,"Scope") is not("LocalVariable" or "GlobalVariable"))return null;
            var components=access.Elements().SingleOrDefault(e=>Named(e,"Symbol"))?.Elements().ToArray();
            if(components==null||components.Length==0||components.Any(e=>!Named(e,"Component")))return null;
            // Unknown array indices conservatively write the containing array, never disappear from the index.
            return ((A(access,"Scope")=="LocalVariable"?"L:":"G:")+Region(components)).ToUpperInvariant();
        }
        private static string Component(XElement component)
        {
            var slice=component.Attribute("SliceAccessModifier");
            if(slice==null)return PipelineFlgNetCompiler.ComponentName(component);
            // A bit write remains a distinct identity, overlapping its containing variable. Never drop the slice.
            var value=slice.Value;
            if(value.Length<2||char.ToLowerInvariant(value[0])!='x'||!int.TryParse(value[1..],out var bit)||bit<0||bit>63)
                throw new PipelineException("UnsupportedCallSliceBinding");
            var copy=new XElement(component);copy.Attribute("SliceAccessModifier")!.Remove();
            return PipelineFlgNetCompiler.ComponentName(copy)+".%X"+bit;
        }
        public string? Operand(string uid)=>DirectTarget(Wire(uid,"operand"));
        public string[] Pins(string uid)=>wires.SelectMany(w=>w.Elements()).Where(e=>Named(e,"NameCon")&&A(e,"UId")==uid).Select(e=>A(e,"Name")).Distinct().ToArray();
        public IEnumerable<string> ConnectedTargets(string uid,string pin,bool conservative=false)
        {
            var wire=Wire(uid,pin);if(wire==null)yield break;
            foreach(var endpoint in wire.Elements()) {
                string? target=null;
                if(Named(endpoint,"IdentCon"))target=conservative?SymbolRegion(A(endpoint,"UId")):Symbol(A(endpoint,"UId"));
                else if(Named(endpoint,"NameCon")&&A(endpoint,"UId")!=uid&&A(endpoint,"Name")=="in"&&Parts.TryGetValue(A(endpoint,"UId"),out var sink)&&A(sink,"Name") is "Coil" or "SCoil" or "RCoil") {
                    foreach(var coilTarget in ConnectedTargets(A(sink,"UId"),"operand",conservative))yield return coilTarget;
                }
                if(target!=null)yield return target;
            }
        }
        private string? DirectTarget(XElement? wire)
        {
            if(wire==null)return null;
            var ids=wire.Elements().Where(e=>Named(e,"IdentCon")).ToArray();
            return ids.Length==1&&wire.Elements().Count()==2?Symbol(A(ids[0],"UId")):null;
        }
        public string? OutputTarget(string uid,string pin)
        {
            var wire=Wire(uid,pin); if(wire==null)return null;
            var direct=DirectTarget(wire);if(direct!=null)return direct;
            var sinks=wire.Elements().Where(e=>Named(e,"NameCon")&&A(e,"UId")!=uid).ToArray();
            if(sinks.Length!=1||wire.Elements().Count()!=2||A(sinks[0],"Name")!="in"||!Parts.TryGetValue(A(sinks[0],"UId"),out var coil)||A(coil,"Name")!="Coil"||coil.Elements().Any(e=>Named(e,"Negated")))return null;
            return Operand(A(coil,"UId"));
        }
        public Expr Input(string uid,string pin){budget=8192;return Input(uid,pin,new HashSet<string>(),0);}
        private Expr Input(string uid,string pin,HashSet<string> visiting,int depth)
        {
            if(--budget<0||depth>128)return Unknown;
            var wire=Wire(uid,pin);if(wire==null)return Unknown;
            var sources=new List<Expr>();
            foreach(var endpoint in wire.Elements()) {
                if(Named(endpoint,"Powerrail")){sources.Add(True);continue;}
                if(Named(endpoint,"IdentCon")) {var symbol=Symbol(A(endpoint,"UId"));sources.Add(symbol==null?Unknown:new Expr("Atom",symbol));continue;}
                if(!Named(endpoint,"NameCon")){return Unknown;}
                var id=A(endpoint,"UId");var port=A(endpoint,"Name");
                if(!Parts.TryGetValue(id,out var part))return Unknown;
                if(id==uid&&port==pin)continue;
                if(port is "out" or "Q" or "eno")sources.Add(Output(id,port,visiting,depth+1));
                else if(Named(part,"Call")&&part.Descendants().Any(p=>Named(p,"Parameter")&&A(p,"Section")=="Output"&&A(p,"Name")==port))sources.Add(new("CallOutput"));
                // All other endpoints must be known input pins; never silently discard a driver.
                else if(port!="in"&&!port.StartsWith("in",StringComparison.Ordinal)&&port!="en"&&port!="operand")return Unknown;
            }
            return sources.Count==1?sources[0]:Unknown;
        }
        private Expr Output(string uid,string pin,HashSet<string> visiting,int depth)
        {
            if(--budget<0||depth>128||!visiting.Add(uid))return Unknown;
            try {
                var part=Parts[uid];var kind=A(part,"Name"); Expr expression;
                Expr In(string name)=>Input(uid,name,visiting,depth+1);
                if(pin!="out")return Unknown;
                if(kind=="Contact") {
                    if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name") is not("in" or "operand" or "out")))return Unknown;
                    var target=Operand(uid);if(target==null)return Unknown;
                    Expr atom=new("Atom",target);
                    if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name")=="operand"))atom=new("Not","",atom);
                    expression=new("And","",In("in"),atom);
                }else if(kind is "O" or "A" or "OR" or "AND") {
                    var card=part.Elements().FirstOrDefault(e=>Named(e,"TemplateValue")&&A(e,"Name")=="Card")?.Value;
                    if(!int.TryParse(card,out var count)||count<1||count>128)return Unknown;
                    if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name")!="out"&&!Enumerable.Range(1,count).Any(i=>A(e,"Name")=="in"+i)))return Unknown;
                    var children=Enumerable.Range(1,count).Select(i=>In("in"+i)).ToArray();
                    for(int i=0;i<count;i++)if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name")=="in"+(i+1)))children[i]=new("Not","",children[i]);
                    expression=new(kind is "O" or "OR"?"Or":"And","",children);
                }else if(kind is "NOT" or "Not")expression=new("Not","",In("in"));
                else if(kind=="Coil"&&!part.Elements().Any(e=>Named(e,"Negated")))expression=In("in");
                else return Unknown;
                if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name")=="in")) {
                    // Contact inversion of the power input is not inversion of the entire contact result.
                    if(kind=="Contact")expression=new("And","",new Expr("Not","",In("in")),expression.Children[1]);
                    else return Unknown;
                }
                if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name")=="out"))expression=new("Not","",expression);
                if(part.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name") is not("in" or "out" or "operand")&&!A(e,"Name").StartsWith("in",StringComparison.Ordinal)))return Unknown;
                return expression;
            }finally{visiting.Remove(uid);}
        }
        public bool PurePart(XElement part)=>A(part,"Name") is "Contact" or "O" or "A" or "AND" or "OR" or "NOT" or "Not" or "Eq" or "Ne" or "Ge" or "Le" or "Gt" or "Lt";
        public IReadOnlyList<string>? TimerWrites(XElement part,bool conservative=false)
        {
            if(!TimerKind(A(part,"Name")))return null;
            var instance=InstanceTarget(part,conservative);if(instance==null)return null;
            var targets=new List<string>{instance};var uid=A(part,"UId");var pins=Pins(uid);
            static bool TimerOutput(string pin)=>pin is "Q" or "ET" or "eno" or "ENO";
            if(pins.Any(p=>!TimerOutput(p)&&p is not("IN" or "PT" or "R" or "en" or "EN" or "pre")))return null;
            foreach(var pin in pins.Where(TimerOutput)) {
                var wire=Wire(uid,pin);if(wire==null)return null;
                foreach(var endpoint in wire.Elements()) {
                    if(Named(endpoint,"OpenCon"))continue;
                    if(Named(endpoint,"IdentCon")) {
                        var target=conservative?SymbolRegion(A(endpoint,"UId")):Symbol(A(endpoint,"UId"));
                        if(target==null)return null;targets.Add(target);continue;
                    }
                    if(!Named(endpoint,"NameCon"))return null; // Powerrail or an unknown connector is another/unknown driver.
                    var id=A(endpoint,"UId");var port=A(endpoint,"Name");if(id==uid&&port==pin)continue;
                    if(!Parts.TryGetValue(id,out var connected))return null;
                    if(Named(connected,"Call")) {
                        if(!connected.Descendants().Any(p=>Named(p,"Parameter")&&A(p,"Section")=="Input"&&A(p,"Name")==port))return null;
                    }else if(port is not("in" or "IN" or "pre" or "en" or "EN" or "operand" or "PT" or "R")&&
                        !(port.StartsWith("in",StringComparison.Ordinal)&&int.TryParse(port[2..],out var ordinal)&&ordinal>0))return null;
                    // Downstream coils are inventoried once as coils. Q/ET stay opaque Boolean/value producers.
                }
            }
            return targets;
        }
        public bool CollectWrites(int order,long unit,List<Write> writes)
        {
            foreach(var part in Parts.Values) {
                if(Named(part,"Access"))continue;
                var kind=A(part,"Name");var uid=A(part,"UId");
                if(TimerKind(kind)) {
                    var targets=TimerWrites(part);if(targets==null)return false;
                    foreach(var target in targets)writes.Add(new(target,kind,order,Unknown,unit));
                }else if(kind is "PContact" or "NContact" or "PBox" or "NBox") {
                    var target=DirectTarget(Wire(uid,"bit"));if(target==null)return false;
                    writes.Add(new(target,kind,order,Unknown,unit));
                }else if(kind is "Coil" or "SCoil" or "RCoil") {
                    var target=Operand(uid);if(target==null)return false;
                    writes.Add(new(target,part.Elements().Any(e=>Named(e,"Negated"))?"Modified":kind,order,kind=="Coil"?Input(uid,"in"):Unknown,unit));
                }else if(kind is "Move" or "Add" or "Sub" or "Mul" or "Div") {
                    var outputs=wires.SelectMany(w=>w.Elements()).Where(e=>Named(e,"NameCon")&&A(e,"UId")==uid&&(A(e,"Name")=="out"||A(e,"Name").StartsWith("out",StringComparison.Ordinal))).Select(e=>A(e,"Name")).Distinct().ToArray();
                    if(outputs.Length==0)return false;
                    foreach(var output in outputs){var target=DirectTarget(Wire(uid,output));if(target==null||!target.StartsWith("L:",StringComparison.Ordinal))return false;writes.Add(new(target,kind,order,Unknown,unit));}
                }else if(!PurePart(part))return false; // Unknown instructions/calls may mutate the entire instance or globals.
            }
            return true;
        }
    }
}
