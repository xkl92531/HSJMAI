#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353SafetyRule(PipelineRule Rule,string EvaluationPoint,
    IReadOnlyList<string> Evidence,IReadOnlyList<string> RequiredContextSignals,string SourceHash);
internal sealed record Original353SafetyResult(IReadOnlyList<Original353SafetyRule> Rules,IReadOnlyList<string> Issues);

/// <summary>Proves a local ordinary-coil inversion of a typed FB instance output.
/// This describes the completed LU, never the physical door position or a later scan.</summary>
internal sealed class Original353SafetyRules(PipelineSnapshot source)
{
    static string A(XElement? e,string name)=>(string?)e?.Attribute(name)??"";
    static bool Eq(string? a,string? b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    static bool Named(XElement e,string name)=>e.Name.LocalName==name;

    public Original353SafetyResult Extract(PipelineUnit caller) {
        var issues=new List<string>();var rules=new List<Original353SafetyRule>();
        if(source.Units.Count(u=>u.Id==caller.Id)!=1||source.Units.Single(u=>u.Id==caller.Id)!=caller)
            return new([], ["SafetySnapshotUnitMismatch"]);
        XDocument graph;
        try {graph=XDocument.Parse(caller.RawContent);}
        catch(System.Xml.XmlException) {return new([],[]);}
        if(graph.Root?.Name.LocalName!="FlgNet"||!graph.Descendants().Any(e=>Named(e,"Call"))||
            !graph.Descendants().Any(e=>Named(e,"Part")&&A(e,"Name")=="Coil"))return new([],[]);

        var calls=graph.Descendants().Where(e=>Named(e,"Call")).ToArray();
        var candidateCalls=new Dictionary<string,InstanceOutput>(StringComparer.OrdinalIgnoreCase);
        var repeatedMembers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var call in calls) {
            var info=call.Elements().SingleOrDefault(e=>Named(e,"CallInfo"));
            if(info==null||!Eq(A(info,"BlockType"),"FB"))continue;
            var instance=info.Elements().SingleOrDefault(e=>Named(e,"Instance"));
            if(instance==null||!Eq(A(instance,"Scope"),"LocalVariable"))continue;
            string path;
            try {path="#"+string.Join(".",instance.Elements().Where(e=>Named(e,"Component")).Select(PipelineFlgNetCompiler.ComponentName));}
            catch(PipelineException) {continue;}
            if(path=="#")continue;
            foreach(var output in info.Elements().Where(e=>Named(e,"Parameter")&&Eq(A(e,"Section"),"Output")&&Eq(A(e,"Type"),"Bool"))) {
                var pin=A(output,"Name");if(pin.Length==0)continue;
                var member=path+"."+pin;
                if(repeatedMembers.Contains(member))continue;
                if(candidateCalls.ContainsKey(member)) {issues.Add("SafetyRepeatedInstanceOutput:"+member);candidateCalls.Remove(member);repeatedMembers.Add(member);continue;}
                candidateCalls[member]=new(call,info,pin,member);
            }
        }
        if(candidateCalls.Count==0)return new([],issues.Distinct().ToArray());
        var hasCandidateRead=false;
        foreach(var access in graph.Descendants().Where(e=>Named(e,"Access")&&Eq(A(e,"Scope"),"LocalVariable"))) {
            var symbol=access.Elements().SingleOrDefault(s=>Named(s,"Symbol"));
            if(symbol==null)continue;
            try {
                var path="#"+string.Join(".",symbol.Elements().Select(PipelineFlgNetCompiler.ComponentName));
                if(candidateCalls.ContainsKey(path)) {hasCandidateRead=true;break;}
            }catch(PipelineException) { }
        }
        if(!hasCandidateRead)return new([],issues.Distinct().ToArray());

        IReadOnlyList<PipelineAssignment> assignments;
        try {
            var compilerView=CompilerIdentityView(graph,out var identities);
            var compiler=new PipelineFlgNetCompiler(compilerView,(call,input,_)=> {
                var info=call.Elements().Single(e=>Named(e,"CallInfo"));
                if(!PipelineTerms.IsTrue(input("en")))throw new PipelineException("SafetyConditionalCall");
                return info.Elements().Where(p=>Named(p,"Parameter")&&Eq(A(p,"Section"),"Output"))
                    .ToDictionary(p=>A(p,"Name"),p=>LogicTerm.Signal("@call:"+A(call,"UId")+":"+A(p,"Name")),StringComparer.OrdinalIgnoreCase);
            });
            assignments=compiler.Compile().Select(a=>new PipelineAssignment(
                identities.GetValueOrDefault(a.Target,a.Target),
                PipelineTerms.Rewrite(a.Expression,t=>t.Kind=="Signal"&&t.Name!=null&&identities.TryGetValue(t.Name,out var original)
                    ? LogicTerm.Signal(original):null))).ToArray();
        }catch(Exception ex)when(ex is PipelineException or InvalidOperationException or ArgumentException) {
            return new([], [..issues,"SafetyUnprovenNetwork:"+ex.Message]);
        }
        var possible=assignments.Select(a=>(a.Target,Input:InvertedSignal(a.Expression)))
            .Where(a=>a.Input!=null&&candidateCalls.ContainsKey(a.Input)).ToArray();
        if(possible.Length==0)return new([],issues.Distinct().ToArray());

        BusinessDescription description;
        try {description=new BusinessDescriptionContext(source).Build(caller);}
        catch(Exception ex)when(ex is PipelineException or InvalidOperationException or ArgumentException or System.Xml.XmlException) {
            return new([], [..issues,"SafetyRootDescriptionUnproven:"+ex.Message]);
        }
        var roots=description.Evidence.Where(e=>e.Kind=="RootInstanceBinding").ToArray();
        if(roots.Length!=1)return new([], [..issues,"SafetyRootInstanceBindingMissingOrAmbiguous"]);
        foreach(var (target,input) in possible) {
            var selected=candidateCalls[input!];
            try {
                VerifyCalleeOutput(selected);
                var physical=PhysicalOutput(target);
                var observed=ObservedMember(description,caller,selected.Member);
                if(Eq(physical,observed))throw new PipelineException("SafetyAliasedMembers");
                VerifySingleWriter(caller,physical,selected.Member,selected);
                var rule=new PipelineRule("EXACTLY_ONE",[new(observed,"Member"),new(physical,"Member")],
                    "普通线圈与已接线FB实例布尔输出互为反相；仅此逻辑单元执行完成后的同一快照检查，任一值缺失为 Unknown。不表示门扇物理位置。");
                rules.Add(new(rule,"AfterLogicUnit",[
                    "Caller LU:"+caller.Id+" source:"+PipelineHash.Of(caller.RawContent),
                    "Typed FB call "+A(selected.Info,"Name")+" instance "+selected.Member+" output "+selected.Output,
                    "Complete FlgNet ordinary coil equation: "+target+" = NOT "+selected.Member,
                    "One explicit physical-output writer and one instance-output call in the imported block",
                    ..roots.Select(r=>r.Text)
                ],[observed,physical],PipelineHash.Of(caller.RawContent)));
            }catch(Exception ex)when(ex is PipelineException or InvalidOperationException or ArgumentException or System.Xml.XmlException) {
                issues.Add("SafetyUnproven:"+target+":"+ex.Message);
            }
        }
        return new(rules.DistinctBy(r=>r.Rule.Key).ToArray(),issues.Distinct().ToArray());
    }

    sealed record InstanceOutput(XElement Call,XElement Info,string Output,string Member);

    static XDocument CompilerIdentityView(XDocument original,out Dictionary<string,string> identities) {
        var copy=new XDocument(original);
        var names=copy.Descendants().Where(e=>Named(e,"Component")).Select(e=>A(e,"Name")).Distinct(StringComparer.Ordinal).ToArray();
        var invalid=names.Where(n=>n.IndexOfAny(['.','#','[',']'])>=0).ToArray();
        var replacements=new Dictionary<string,string>(StringComparer.Ordinal);
        var reserved=names.ToHashSet(StringComparer.Ordinal);
        foreach(var name in invalid) {
            var replacement="Original353Name_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
            if(!reserved.Add(replacement))throw new PipelineException("SafetyCompilerIdentityCollision");
            replacements.Add(name,replacement);
        }
        foreach(var component in copy.Descendants().Where(e=>Named(e,"Component")))
            if(replacements.TryGetValue(A(component,"Name"),out var replacement))component.SetAttributeValue("Name",replacement);
        var sourceAccess=original.Descendants().Where(e=>Named(e,"Access")&&e.Elements().Any(s=>Named(s,"Symbol")))
            .ToDictionary(e=>A(e,"UId"),StringComparer.Ordinal);
        identities=new(StringComparer.OrdinalIgnoreCase);
        foreach(var access in copy.Descendants().Where(e=>Named(e,"Access")&&e.Elements().Any(s=>Named(s,"Symbol")))) {
            var old=sourceAccess[A(access,"UId")];
            var scope=Eq(A(access,"Scope"),"LocalVariable")?"#":"";
            var mapped=scope+string.Join(".",access.Elements().Single(e=>Named(e,"Symbol")).Elements().Select(PipelineFlgNetCompiler.ComponentName));
            var actual=scope+string.Join(".",old.Elements().Single(e=>Named(e,"Symbol")).Elements().Select(PipelineFlgNetCompiler.ComponentName));
            if(!Eq(mapped,actual)) {
                if(identities.TryGetValue(mapped,out var prior)&&!Eq(prior,actual))throw new PipelineException("SafetyCompilerIdentityCollision");
                identities[mapped]=actual;
            }
        }
        return copy;
    }

    static LogicTerm Normalize(LogicTerm term) {
        var children=(term.Children??[]).Select(Normalize).ToArray();
        if(term.Kind=="And") {
            if(children.Any(c=>c.Kind=="Constant"&&c.Name=="false"))return new("Constant","false");
            children=children.Where(c=>c.Kind!="Constant"||c.Name!="true").ToArray();
            return children.Length switch {0=>new("Constant","true"),1=>children[0],_=>term with{Children=children}};
        }
        if(term.Kind=="Or") {
            if(children.Any(c=>c.Kind=="Constant"&&c.Name=="true"))return new("Constant","true");
            children=children.Where(c=>c.Kind!="Constant"||c.Name!="false").ToArray();
            return children.Length switch {0=>new("Constant","false"),1=>children[0],_=>term with{Children=children}};
        }
        if(term.Kind=="Not"&&children.Length==1) {
            if(children[0].Kind=="Not"&&children[0].Children?.Count==1)return children[0].Children[0];
            return term with{Children=children};
        }
        return term with{Children=children};
    }
    static string? InvertedSignal(LogicTerm expression) {
        var n=Normalize(expression);
        return n.Kind=="Not"&&n.Children?.Count==1&&n.Children[0].Kind=="Signal"?n.Children[0].Name:null;
    }
    void VerifyCalleeOutput(InstanceOutput selected) {
        var name=A(selected.Info,"Name");
        var units=source.Units.Where(u=>Eq(u.BlockName,name)).ToArray();
        if(units.Length==0||units.Any(u=>!Eq(u.BlockKind,"FB")||string.IsNullOrWhiteSpace(u.InterfaceXml))||units.Select(u=>u.BlockId).Distinct().Count()!=1||
            units.Select(u=>u.InterfaceXml).Distinct(StringComparer.Ordinal).Count()!=1)
            throw new PipelineException("SafetyCalleeMissingOrAmbiguous");
        var contract=XElement.Parse(units[0].InterfaceXml);
        var outputs=contract.Descendants().Where(e=>Named(e,"Member")&&Eq(A(e,"Name"),selected.Output)&&
            Eq(A(e,"Datatype"),"Bool")&&e.Ancestors().Any(p=>Named(p,"Section")&&Eq(A(p,"Name"),"Output"))).ToArray();
        if(outputs.Length!=1)throw new PipelineException("SafetyCalleeOutputTypeUnproven");
    }
    string PhysicalOutput(string target) {
        if(target.StartsWith('#'))throw new PipelineException("SafetyOutputNotPhysical");
        var tags=source.Tags.Where(t=>Eq(t.Name,target)).ToArray();
        if(tags.Length!=1||!Eq(tags[0].DataType,"Bool")||!Regex.IsMatch(tags[0].Address,@"^%Q\d+\.[0-7]$",RegexOptions.IgnoreCase)||
            source.Tags.Count(t=>Eq(t.Address,tags[0].Address))!=1)
            throw new PipelineException("SafetyPhysicalOutputTypeOrAliasUnknown");
        return tags[0].Name;
    }
    static string ObservedMember(BusinessDescription description,PipelineUnit caller,string member) {
        var symbols=(description.Symbols??[]).Where(s=>s.BlockId==caller.BlockId&&Eq(s.Path,member[1..])&&
            Eq(s.Section,"Static")&&description.SignalAliases.ContainsKey(s.Alias)).ToArray();
        if(symbols.Length!=1)throw new PipelineException("SafetyInstanceMemberBindingUnknown");
        var symbol=symbols[0];
        var actual=description.SignalAliases[symbol.Alias];
        if(!Eq(description.SignalTypes.GetValueOrDefault(symbol.Alias,""),"Bool"))throw new PipelineException("SafetyInstanceMemberTypeUnknown");
        return actual;
    }
    void VerifySingleWriter(PipelineUnit caller,string physical,string member,InstanceOutput selected) {
        var block=source.Units.Where(u=>u.BlockId==caller.BlockId).ToArray();
        var physicalWriters=0;var instanceCalls=0;
        foreach(var unit in block) {
            if(string.IsNullOrWhiteSpace(unit.RawContent))throw new PipelineException("SafetyBlockSourceMissing");
            var xml=XDocument.Parse(unit.RawContent);
            if(xml.Root?.Name.LocalName!="FlgNet")throw new PipelineException("SafetyWriterInventoryUnsupported");
            var parts=xml.Descendants().Where(e=>Named(e,"Parts")).Single().Elements().Where(e=>Named(e,"Access")||Named(e,"Part")||Named(e,"Call"))
                .ToDictionary(e=>A(e,"UId"),StringComparer.Ordinal);
            var wires=xml.Descendants().Where(e=>Named(e,"Wires")).Single().Elements().ToArray();
            foreach(var call in parts.Values.Where(e=>Named(e,"Call"))) {
                var info=call.Elements().SingleOrDefault(e=>Named(e,"CallInfo"));
                var instance=info?.Elements().SingleOrDefault(e=>Named(e,"Instance"));
                if(instance==null||!Eq(A(instance,"Scope"),"LocalVariable"))continue;
                var path="#"+string.Join(".",instance.Elements().Where(e=>Named(e,"Component")).Select(PipelineFlgNetCompiler.ComponentName))+"."+selected.Output;
                if(Eq(path,member))instanceCalls++;
            }
            foreach(var access in parts.Values.Where(e=>Named(e,"Access"))) {
                var symbol=access.Elements().SingleOrDefault(e=>Named(e,"Symbol"));if(symbol==null)continue;
                var path=(Eq(A(access,"Scope"),"LocalVariable")?"#":"")+string.Join(".",symbol.Elements().Select(PipelineFlgNetCompiler.ComponentName));
                if(!Eq(path,physical)&&!Eq(path,member))continue;
                foreach(var wire in wires.Where(w=>w.Elements().Any(e=>Named(e,"IdentCon")&&A(e,"UId")==A(access,"UId")))) {
                    foreach(var pin in wire.Elements().Where(e=>Named(e,"NameCon"))) {
                        if(!parts.TryGetValue(A(pin,"UId"),out var part))throw new PipelineException("SafetyUnknownConnectedPart");
                        var kind=A(part,"Name");var port=A(pin,"Name");
                        if(kind=="Contact"&&port=="operand")continue;
                        if(kind=="Coil"&&port=="operand") {
                            if(Eq(path,physical))physicalWriters++;
                            else throw new PipelineException("SafetyInstanceMemberOverwritten");
                            continue;
                        }
                        if(Named(part,"Call")) {
                            var parameter=part.Descendants().SingleOrDefault(e=>Named(e,"Parameter")&&Eq(A(e,"Name"),port));
                            if(parameter!=null&&Eq(A(parameter,"Section"),"Input"))continue;
                        }
                        throw new PipelineException("SafetyUnknownOrAdditionalWriter:"+path);
                    }
                }
            }
        }
        if(physicalWriters!=1||instanceCalls!=1)throw new PipelineException("SafetyOutputOverwrittenOrInstanceRepeated");
    }
}
