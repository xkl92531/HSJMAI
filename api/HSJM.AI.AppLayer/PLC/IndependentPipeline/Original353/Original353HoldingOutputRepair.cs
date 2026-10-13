#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>Recognizes a plain holding-output equation, never a device/pin/address naming convention.
/// The implication is local to the completed caller LU; it is not a scan-stable or physical invariant.</summary>
internal sealed class Original353HoldingOutputRepair
{
    readonly PipelineSnapshot source;
    readonly BusinessDescriptionContext descriptions;
    readonly PipelineCompilationContext xml;
    readonly Dictionary<string,PipelineUnit[]> blocks;
    public Original353HoldingOutputRepair(PipelineSnapshot source) {
        this.source=source;descriptions=new(source);xml=new(source);
        blocks=source.Units.GroupBy(u=>u.BlockName,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
    }
    public Original353UnitResult Apply(PipelineUnit caller,IReadOnlyList<PipelineRule> original) {
        var rules=original.ToList();var repairs=new List<Original353Repair>();var issues=new List<string>();
        if(source.Units.Count(u=>u.Id==caller.Id)!=1||source.Units.Single(u=>u.Id==caller.Id)!=caller)
            return new(caller.Id,original,[],["RepairSnapshotUnitMismatch"]);
        try {
            var graph=xml.Xml(caller);var description=descriptions.Build(caller);
            var bindings=new Dictionary<string,Dictionary<string,LogicTerm>>();
            // The strict compiler follows the complete actual input wires (including OR branches).
            // Opaque output markers are binding tokens only and never establish output semantics.
            var compiler=new PipelineFlgNetCompiler(CompilerIdentityView(graph),(call,input,_)=> {
                var info=call.Elements().Single(e=>e.Name.LocalName=="CallInfo");
                if(!PipelineTerms.IsTrue(input("en")))throw new PipelineException("ConditionalCall");
                var parameters=info.Elements().Where(e=>e.Name.LocalName=="Parameter").ToArray();
                if(parameters.Any(p=>A(p,"Section")=="InOut"))throw new PipelineException("InOutCall");
                var bound=new Dictionary<string,LogicTerm>(StringComparer.OrdinalIgnoreCase);
                foreach(var p in parameters.Where(p=>A(p,"Section")=="Input"&&A(p,"Type").Equals("Bool",StringComparison.OrdinalIgnoreCase))) {
                    // An unrelated call input can use syntax outside this local compiler subset
                    // (for example a quoted tag containing a dot). Leave it unbound. TypedFormals
                    // rejects any selected output equation that actually depends on this input.
                    try {bound[A(p,"Name")]=input(A(p,"Name"));}
                    catch(PipelineException) { }
                }
                bindings[A(call,"UId")]=bound;
                return parameters.Where(p=>A(p,"Section")=="Output").ToDictionary(p=>A(p,"Name"),p=>LogicTerm.Signal("@call:"+A(call,"UId")+":"+A(p,"Name")),StringComparer.OrdinalIgnoreCase);
            });
            var callerWrites=compiler.Compile();
            foreach(var rule in original.Where(r=>r.Operator=="AT_MOST_ONE"&&r.Signals.Count>=3)) {
                var candidates=new List<Original353Repair>();
                foreach(var call in graph.Descendants().Where(e=>e.Name.LocalName=="Call")) {
                    var info=call.Elements().Single(e=>e.Name.LocalName=="CallInfo");
                    var bound=bindings[A(call,"UId")];
                    var members=rule.Signals.Select(s=>s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if(!members.All(n=>bound.Values.Any(t=>PipelineTerms.Walk(t).Any(a=>a.Kind=="Signal"&&Eq(a.Name,n)))))continue;
                    if(!blocks.TryGetValue(A(info,"Name"),out var body)||body.Length==0||body.Select(u=>u.BlockId).Distinct().Count()!=1||body.Any(u=>string.IsNullOrWhiteSpace(u.RawContent))) {
                        issues.Add("RepairBodyMissingOrAmbiguous:"+A(info,"Name"));continue;
                    }
                    foreach(var output in info.Elements().Where(p=>p.Name.LocalName=="Parameter"&&A(p,"Section")=="Output"&&Eq(A(p,"Type"),"Bool"))) {
                        var formal="#"+A(output,"Name");
                        try {
                            var writers=Inventory(body,formal,true);
                            if(writers.Count!=1||writers[0].Kind!="Coil") {if(writers.Count>1)issues.Add("RepairOutputOverwritten:"+formal);continue;}
                            var writer=writers[0];
                            var assignments=new PipelineFlgNetCompiler(xml.Xml(writer.Unit)).Compile();
                            var equation=assignments.Single(a=>Eq(a.Target,formal)).Expression;
                            if(!PipelineTerms.Walk(equation).Any(t=>t.Kind=="Previous"&&Eq(t.Name,formal)))continue;
                            if(!PipelineTerms.Walk(equation).All(t=>t.Kind is "And" or "Or" or "Not" or "Constant" or "Signal" or "Previous"))continue;
                            if(!TypedFormals(body,equation,formal,bound))throw new PipelineException("UnprovenFormalTypeOrInputSection");
                            var necessary=Necessary(equation).Where(n=>bound.ContainsKey(n.TrimStart('#'))).ToArray();
                            foreach(var required in necessary) {
                                var actual=bound[required.TrimStart('#')];
                                if(actual.Kind!="Signal"||actual.Name==null||!members.Contains(actual.Name)||!PhysicalBool(actual.Name))continue;
                                if(Inventory(body,required).Count!=0||Inventory(body,actual.Name).Count!=0)
                                    throw new PipelineException("RequiredInputWrittenInCallee");
                                var expression=PipelineTerms.Rewrite(equation,t=>t.Kind=="Signal"&&t.Name!.StartsWith("#")&&bound.TryGetValue(t.Name[1..],out var value)?value:null);
                                // A concrete local valuation must set two original members true while
                                // legitimately setting this output from false. Holding alone is insufficient.
                                if(!Witness(expression,members,actual.Name,formal,out var witness))continue;
                                var token="@call:"+A(call,"UId")+":"+A(output,"Name");
                                var targets=callerWrites.Where(a=>a.Expression.Kind=="Signal"&&a.Expression.Name==token).Select(a=>a.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                                if(targets.Length!=1)throw new PipelineException("OutputActualMissingOrAmbiguous");
                                var actualOutput=ResolveActual(description,caller,targets[0]);
                                if(actualOutput==null||!ObservableBool(description,actualOutput))throw new PipelineException("OutputObservationBindingUnknown");
                                var sameBlock=source.Units.Where(u=>u.BlockId==caller.BlockId).ToArray();
                                if(Inventory(sameBlock,targets[0]).Count!=1)throw new PipelineException("CallerOutputOverwrittenOrUnknown");
                                if(Inventory(sameBlock,actual.Name).Count!=0)throw new PipelineException("RequiredInputWrittenInCaller");
                                var replacement=new PipelineRule("IMPLIES",[new(actualOutput,"Source"),new(actual.Name,"Target")],
                                    "普通自保持输出方程的必要真值：仅调用逻辑单元执行完成后的同一快照，输出为真要求已接线输入为真；缺少输出采样返回 Unknown。");
                                candidates.Add(new(rule,replacement,"AfterLogicUnit",[
                                    "Caller LU:"+caller.Id+" source:"+PipelineHash.Of(caller.RawContent),
                                    "Callee LU:"+writer.Unit.Id+" source:"+PipelineHash.Of(writer.Unit.RawContent)+" ordinary coil "+formal,
                                    "Full AND/OR equation retains its own previous output; required positive literal "+required+" binds to "+actual.Name,
                                    "Unique output writer across imported callee body; unique caller output writer; actual output "+actualOutput,
                                    "Local reset witness (previous output false): "+witness,
                                    ..description.Evidence.Where(e=>e.Kind=="RootInstanceBinding").Select(e=>e.Text)
                                ],[actualOutput]));
                            }
                        }catch(Exception ex)when(ex is PipelineException or InvalidOperationException or ArgumentException) {issues.Add("RepairUnproven:"+formal+":"+ex.Message);}
                    }
                }
                var unique=candidates.DistinctBy(c=>c.Replacement.Key).ToArray();
                if(unique.Length==1) {rules[rules.IndexOf(rule)]=unique[0].Replacement;repairs.Add(unique[0]);}
                else if(unique.Length>1)issues.Add("RepairAmbiguousReplacement:"+rule.Key);
            }
        }catch(Exception ex)when(ex is PipelineException or InvalidOperationException or ArgumentException or System.Xml.XmlException) {issues.Add("RepairSourceUnproven:"+ex.Message);}
        return new(caller.Id,rules,repairs,issues.Distinct().ToArray());
    }
    bool PhysicalBool(string name) {var tags=source.Tags.Where(t=>Eq(t.Name,name)).ToArray();return tags.Length>0&&tags.All(t=>Eq(t.DataType,"Bool")&&Regex.IsMatch(t.Address,@"^%I\d+\.[0-7]$",RegexOptions.IgnoreCase))&&tags.Select(t=>t.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1;}
    static bool ObservableBool(BusinessDescription d,string name)=>d.SignalAliases.Any(p=>Eq(p.Value,name)&&d.SignalTypes.TryGetValue(p.Key,out var type)&&Eq(type,"Bool"));
    static string? ResolveActual(BusinessDescription d,PipelineUnit unit,string name) {
        if(!name.StartsWith('#'))return name;
        var actuals=(d.Symbols??[]).Where(s=>s.BlockId==unit.BlockId&&Eq(s.Path,name[1..])&&d.SignalAliases.ContainsKey(s.Alias))
            .Select(s=>d.SignalAliases[s.Alias]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return actuals.Length==1?actuals[0]:null;
    }
    static bool TypedFormals(PipelineUnit[] body,LogicTerm expression,string output,IReadOnlyDictionary<string,LogicTerm> bound) {
        var contracts=body.Select(u=>u.InterfaceXml).Distinct().ToArray();if(contracts.Length!=1||string.IsNullOrWhiteSpace(contracts[0]))return false;
        var contract=XElement.Parse(contracts[0]);
        foreach(var atom in PipelineTerms.Walk(expression).Where(t=>t.Kind is "Signal" or "Previous")) {
            if(atom.Name==null||!atom.Name.StartsWith('#')||atom.Kind=="Previous"&&!Eq(atom.Name,output))return false;
            var path=Regex.Replace(atom.Name[1..],@"\[[^\]]*\]","");
            var declarations=contract.Descendants().Where(e=>e.Name.LocalName=="Member"&&Eq(string.Join(".",e.AncestorsAndSelf().Where(a=>a.Name.LocalName=="Member").Reverse().Select(a=>A(a,"Name"))),path)).ToArray();
            if(declarations.Length!=1)return false;
            var type=A(declarations[0],"Datatype");if(!Eq(type,"Bool")&&!(atom.Name.Contains('[')&&Regex.IsMatch(type,@"^Array\s*\[.+\]\s+of\s+Bool$",RegexOptions.IgnoreCase)))return false;
            var section=declarations[0].Ancestors().FirstOrDefault(e=>e.Name.LocalName=="Section");
            if(Eq(atom.Name,output)&&A(section,"Name")!="Output")return false;
            if(bound.ContainsKey(atom.Name[1..])&&A(section,"Name")!="Input")return false;
            if(A(section,"Name")=="Input"&&!bound.ContainsKey(atom.Name[1..]))return false;
        }
        return true;
    }
    record Writer(PipelineUnit Unit,string Kind);
    List<Writer> Inventory(IEnumerable<PipelineUnit> units,string target,bool callee=false) {
        var result=new List<Writer>();
        foreach(var unit in units) {
            var graph=xml.Xml(unit);if(graph.Root?.Name.LocalName!="FlgNet")throw new PipelineException("WriterInventoryUnsupported");
            var parts=graph.Root.Elements().Single(e=>e.Name.LocalName=="Parts").Elements().ToDictionary(e=>A(e,"UId"));
            // Calls can mutate instance members invisibly to access wires. Unknown instructions
            // can write indirectly. Known edge/latch nodes remain explicit state, never transparent.
            if(callee&&parts.Values.Any(p=>p.Name.LocalName=="Call"||p.Name.LocalName=="Part"&&A(p,"Name") is not
                ("Contact" or "Coil" or "SCoil" or "RCoil" or "A" or "O" or "Not" or "PBox" or "NBox" or "Sr" or "Rs")))
                throw new PipelineException("CalleeWriterInventoryUnsupportedInstruction");
            var wires=graph.Root.Elements().Single(e=>e.Name.LocalName=="Wires").Elements().ToArray();
            foreach(var access in parts.Values.Where(p=>p.Name.LocalName=="Access")) {
                var symbol=access.Elements().SingleOrDefault(e=>e.Name.LocalName=="Symbol");if(symbol==null)continue;
                var path=(A(access,"Scope")=="LocalVariable"?"#":"")+string.Join(".",symbol.Elements().Select(PipelineFlgNetCompiler.ComponentName));
                if(!Eq(path,target))continue;
                foreach(var wire in wires.Where(w=>w.Elements().Any(e=>e.Name.LocalName=="IdentCon"&&A(e,"UId")==A(access,"UId"))))
                    foreach(var end in wire.Elements().Where(e=>e.Name.LocalName=="NameCon")) {
                        if(!parts.TryGetValue(A(end,"UId"),out var part))throw new PipelineException("WriterInventoryMissingPart");
                        var pin=A(end,"Name");var kind=A(part,"Name");
                        if(part.Name.LocalName=="Call") {
                            var parameter=part.Descendants().SingleOrDefault(e=>e.Name.LocalName=="Parameter"&&A(e,"Name")==pin);
                            if(parameter==null)throw new PipelineException("WriterInventoryMissingInterface");
                            if(A(parameter,"Section")=="Input")continue;
                            if(A(parameter,"Section")=="Output") {result.Add(new(unit,"Call"));continue;}
                        }
                        else if(kind=="Contact"&&pin=="operand")continue;
                        else if(kind is "Coil" or "SCoil" or "RCoil"&&pin=="operand") {result.Add(new(unit,kind));continue;}
                        else if(pin.StartsWith("in",StringComparison.OrdinalIgnoreCase)&&kind is "A" or "O" or "Not")continue;
                        throw new PipelineException("WriterInventoryUnknownAccess:"+target+":"+kind+":"+pin);
                    }
            }
        }
        return result;
    }
    static HashSet<string> Necessary(LogicTerm t) {
        if(t.Kind=="Signal")return new([t.Name!],StringComparer.OrdinalIgnoreCase);
        if(t.Kind is not("And" or "Or")||t.Children is not {Count:>0})return new(StringComparer.OrdinalIgnoreCase);
        var sets=t.Children.Select(Necessary).ToArray();var result=sets[0];foreach(var next in sets.Skip(1))if(t.Kind=="And")result.UnionWith(next);else result.IntersectWith(next);return result;
    }
    static bool Witness(LogicTerm expression,HashSet<string> members,string required,string output,out string witness) {
        witness="";var atoms=PipelineTerms.Walk(expression).Where(t=>t.Kind=="Signal").Select(t=>t.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(atoms.Length>16)return false;
        bool Eval(LogicTerm t,Dictionary<string,bool> values)=>t.Kind switch {"Constant"=>t.Name=="true","Signal"=>values[t.Name!],"Previous"=>false,"Not"=>!Eval(t.Children![0],values),"And"=>t.Children!.All(x=>Eval(x,values)),"Or"=>t.Children!.Any(x=>Eval(x,values)),_=>false};
        for(var mask=0;mask<(1<<atoms.Length);mask++) {
            var values=atoms.Select((n,i)=>(n,v:(mask&(1<<i))!=0)).ToDictionary(p=>p.n,p=>p.v,StringComparer.OrdinalIgnoreCase);
            if(values.GetValueOrDefault(required)&&members.Count(n=>values.GetValueOrDefault(n))>=2&&Eval(expression,values)) {
                witness=string.Join(", ",values.Select(p=>p.Key+"="+p.Value))+", previous("+output+")=false";return true;
            }
        }
        return false;
    }
    static string A(XElement? e,string n)=>(string?)e?.Attribute(n)??"";
    static bool Eq(string? a,string? b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);

    // The shared strict compiler accepts PLC symbol components without '#', '.', or brackets.
    // Unrelated outputs in a real caller can use those characters. Preserve each complete
    // component's identity bijectively for compilation; never infer semantics from its spelling.
    // The original XML remains the evidence and is used by the writer inventory.
    static XDocument CompilerIdentityView(XDocument source) {
        var copy=new XDocument(source);
        var names=copy.Descendants().Where(e=>e.Name.LocalName=="Component").Select(e=>A(e,"Name")).Distinct(StringComparer.Ordinal).ToArray();
        var invalid=names.Where(n=>n.IndexOfAny(['.','#','[',']'])>=0).ToArray();
        if(invalid.Length==0)return copy;
        var replacements=new Dictionary<string,string>(StringComparer.Ordinal);
        var reserved=names.ToHashSet(StringComparer.Ordinal);
        foreach(var name in invalid) {
            var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
            var replacement="Original353Name_"+digest;
            if(!reserved.Add(replacement))throw new PipelineException("CompilerIdentityCollision");
            replacements.Add(name,replacement);
        }
        foreach(var component in copy.Descendants().Where(e=>e.Name.LocalName=="Component"))
            if(replacements.TryGetValue(A(component,"Name"),out var replacement))component.SetAttributeValue("Name",replacement);
        return copy;
    }
}
