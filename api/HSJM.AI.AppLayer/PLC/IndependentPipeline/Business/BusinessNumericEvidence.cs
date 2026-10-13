#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessNumericOutput(string Pin,string Target,string Via,string WriteKind);
public sealed record BusinessNumericFact(string EvidenceId,string BlockRef,int? NetworkOrder,string Uid,string Instruction,
    int XmlOrder,string DataType,IReadOnlyDictionary<string,string> Inputs,string Enable,string Precondition,string Expression,
    IReadOnlyList<BusinessNumericOutput> Outputs,IReadOnlyList<string> Limitations);

/// <summary>Supplementary anonymous wiring facts, not generated constraints or a proof of numeric invariants.</summary>
public static class BusinessNumericEvidence
{
    public const string Version = "business-numeric-evidence-1";
    private static readonly HashSet<string> Arithmetic = new(StringComparer.Ordinal) { "Add","Sub","Mul","Div","Move" };
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal) { "Ge","Gt","Le","Lt","Eq","Ne" };
    private static readonly HashSet<string> Edges = new(StringComparer.Ordinal) { "PContact","NContact","PBox","NBox" };
    private static readonly HashSet<string> Coils = new(StringComparer.Ordinal) { "Coil","SCoil","RCoil" };
    private static readonly HashSet<string> BooleanParts = new(StringComparer.Ordinal) { "Contact","A","AND","And","O","OR","Or","NOT","Not" };

    public static IReadOnlyList<BusinessNumericFact> Build(BusinessDescription description) {
        var facts = new List<BusinessNumericFact>();
        foreach (var evidence in description.Evidence.Where(e => e.Kind == "SourceStructure")) {
            // Do not expose source that the renderer/template slice did not select for model consumption.
            if (!description.ModelText.Contains("["+evidence.Id+"] "+evidence.Text,StringComparison.Ordinal)) continue;
            var block = Regex.Match(evidence.Text,@"\bBlockRef:([^\s]+)").Groups[1].Value;
            var orderText = Regex.Match(evidence.Text,@"\bNetworkOrder:([^\s]+)").Groups[1].Value;
            int? order = int.TryParse(orderText,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed) ? parsed : null;
            var start=evidence.Text.IndexOf('<'); var end=evidence.Text.LastIndexOf('>');
            if (start<0 || end<start) continue;
            try {
                using var input = new StringReader(evidence.Text[start..(end+1)]);
                using var reader = XmlReader.Create(input,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32*1024*1024 });
                var root=XElement.Load(reader);
                if (root.Name.LocalName!="FlgNet") continue;
                if (!root.Descendants().Any(e=>Named(e,"Part")&&(Arithmetic.Contains(A(e,"Name"))||Comparisons.Contains(A(e,"Name"))))) continue;
                facts.AddRange(new Network(root,evidence.Id,block,order).Build());
            } catch (XmlException) {
                facts.Add(new(evidence.Id,block,order,"?","Unknown",0,"Unknown",new Dictionary<string,string>(),
                    "Unknown(MalformedSource)","Unknown(MalformedSource)","Unknown(MalformedSource)",[],["MalformedOrOversizedXml"]));
            }
        }
        return facts;
    }

    public static string BuildContext(BusinessDescription description) => Format(Build(description));
    public static string Format(IReadOnlyList<BusinessNumericFact> facts) {
        if (facts.Count==0) return "";
        var text=new StringBuilder();
        text.AppendLine("Anonymous numeric wiring projection ("+Version+"). These are source facts, not accepted constraints. SymbolRef identifiers are opaque. Numeric constants have their declared datatype only: never infer physical units, clock frequency or a delay from names or REAL literals. Unknown is not TRUE.");
        text.AppendLine("Inputs are read at the stated instruction's evaluation point; a target is written after that enabled instruction completes normally. Arithmetic overflow/division failure and PLC numeric precision are not proved here. ENO is not assumed equal to EN. PREVIOUS(bit) is edge memory, not the current operand. Edge memory update timing is not expanded.");
        text.AppendLine("NetworkOrder is source network order within a BlockRef. XmlOrder is XML position only, NOT an execution order for independent branches. Dataflow producer-to-consumer edges preserve dependency order. Multiple writes in one network have UnknownWriteOrder unless independently proved; an increment can be overwritten by another enabled write. This projection never replaces those writes with a final value.");
        foreach (var group in facts.GroupBy(f=>f.BlockRef)) {
            if (group.Any(f=>!f.NetworkOrder.HasValue)||group.GroupBy(f=>f.NetworkOrder).Any(g=>g.Select(f=>f.EvidenceId).Distinct().Count()>1)) {
                text.Append("BlockRef:").Append(group.Key).AppendLine(" UnknownNetworkOrder: missing/duplicate network metadata; do not infer cross-network write order.");
                continue;
            }
            var order=group.Where(f=>f.NetworkOrder.HasValue).Select(f=>f.NetworkOrder!.Value).Distinct().Order().ToArray();
            text.Append("BlockRef:").Append(group.Key).Append(" NetworkOrder sequence: ").AppendJoin(" -> ",order).AppendLine(" (when declared order metadata is unambiguous).");
        }
        foreach (var fact in facts) {
            text.Append('[').Append(fact.EvidenceId).Append("] BlockRef:").Append(fact.BlockRef).Append(" NetworkOrder:").Append(fact.NetworkOrder?.ToString(CultureInfo.InvariantCulture)??"Unknown")
                .Append(" UID:").Append(fact.Uid).Append(" ").Append(fact.Instruction).Append(" XmlOrder:").Append(fact.XmlOrder).Append(" Type:").AppendLine(fact.DataType);
            text.Append("  Inputs: ").AppendJoin("; ",fact.Inputs.Select(p=>p.Key+"="+p.Value)).Append("; EN=").Append(fact.Enable).Append("; pre=").AppendLine(fact.Precondition);
            text.Append("  Value/flow: ").AppendLine(fact.Expression);
            foreach (var output in fact.Outputs) text.Append("  ").Append(output.Pin).Append(" -> ").Append(output.Target).Append(" via ").Append(output.Via).Append("; ").AppendLine(output.WriteKind);
            foreach (var limitation in fact.Limitations) text.Append("  ").AppendLine(limitation);
        }
        return text.ToString();
    }

    private static bool Named(XElement element,string name) => element.Name.LocalName==name;
    private static string A(XElement element,string name) => (string?)element.Attribute(name)??"";
    private static string Unknown(string reason) => "Unknown("+reason+")";
    private static string And(string left,string right) => "AND("+left+", "+right+")";
    private static string Not(string value) => "NOT("+value+")";

    private sealed class Network
    {
        private readonly string evidence,block;
        private readonly int? order;
        private readonly Dictionary<string,XElement> nodes=new(StringComparer.Ordinal);
        private readonly Dictionary<string,List<XElement>> wires=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<XElement,int> ordinals;
        private readonly List<string> structuralProblems=[];
        private int budget;
        public Network(XElement root,string evidence,string block,int? order) {
            this.evidence=evidence; this.block=block; this.order=order;
            ordinals=root.DescendantsAndSelf().Select((e,i)=>(e,i)).ToDictionary(p=>p.e,p=>p.i+1);
            foreach (var node in root.Elements().Where(e=>Named(e,"Parts")).SelectMany(p=>p.Elements())) {
                var uid=A(node,"UId");
                if (uid.Length==0||!nodes.TryAdd(uid,node)) structuralProblems.Add("DuplicateOrMissingNodeUid");
            }
            foreach (var wire in root.Elements().Where(e=>Named(e,"Wires")).SelectMany(p=>p.Elements().Where(e=>Named(e,"Wire"))))
                foreach (var endpoint in wire.Elements().Where(e=>Named(e,"NameCon"))) {
                    var key=A(endpoint,"UId")+"."+A(endpoint,"Name");
                    if (!wires.TryGetValue(key,out var list)) wires[key]=list=[];
                    list.Add(wire);
                }
        }

        public IReadOnlyList<BusinessNumericFact> Build() {
            var result=new List<BusinessNumericFact>();
            foreach (var node in nodes.Values.Where(e=>Named(e,"Part"))) {
                var kind=A(node,"Name"); var uid=A(node,"UId");
                if (!Arithmetic.Contains(kind)&&!Comparisons.Contains(kind)&&!Edges.Contains(kind)&&!Coils.Contains(kind)) continue;
                var inputs=new Dictionary<string,string>(StringComparer.Ordinal);
                foreach (var pin in InputPins(node)) inputs[pin]=Resolve(uid,pin);
                var enable=Arithmetic.Contains(kind)||Has(uid,"en") ? Resolve(uid,"en") : "NotApplicable";
                var pre=Comparisons.Contains(kind)||kind is "PContact" or "NContact"||Has(uid,"pre") ? Resolve(uid,"pre") : "NotPresent";
                var expression=Value(node);
                var outputs=Targets(node);
                var limitations=new List<string>(structuralProblems);
                if (!order.HasValue) limitations.Add("UnknownNetworkOrder");
                if (kind is "Sub" or "Div") limitations.Add("Operand order is in1 then in2; never commute these inputs.");
                if (Edges.Contains(kind)) limitations.Add("Edge output depends on previous bit state; no period/frequency or elapsed-time conversion is asserted.");
                if (Arithmetic.Contains(kind)) limitations.Add("Write occurs only under EN (and connected pre); disabled/failing execution does not establish this assignment.");
                if (node.Attributes().Any(a=>a.Name.LocalName=="DisabledENO")) limitations.Add("DisabledENO="+A(node,"DisabledENO")+"; ENO behavior is not proved by this projection.");
                var dataType=node.Elements().FirstOrDefault(e=>Named(e,"TemplateValue")&&A(e,"Name")=="SrcType")?.Value??"Unspecified";
                result.Add(new(evidence,block,order,uid,kind,ordinals[node],dataType,inputs,enable,pre,expression,outputs,limitations));
            }
            var writes=result.SelectMany(f=>f.Outputs.Where(o=>o.WriteKind is not ("Dataflow" or "Open")&&!o.Target.StartsWith("Unknown(",StringComparison.Ordinal))
                    .Select(o=>(Fact:f,Output:o,Writer:o.Via.Split(" -> ",StringSplitOptions.None).Last().Split('.')[0])))
                .GroupBy(x=>x.Output.Target,StringComparer.Ordinal).Where(g=>g.Select(x=>x.Writer).Distinct().Count()>1).ToArray();
            foreach (var group in writes) foreach (var entry in group)
                ((List<string>)entry.Fact.Limitations).Add("UnknownWriteOrder: "+group.Key+" has writes at UIDs "+string.Join(",",group.Select(x=>x.Writer).Distinct())+" in this network; XML order alone is not a final-value proof.");
            return result;
        }

        private IEnumerable<string> InputPins(XElement node) {
            var kind=A(node,"Name");
            if (kind=="Move") return ["in"];
            if (Arithmetic.Contains(kind)||Comparisons.Contains(kind)) {
                var count=Arity(node,2);
                return count<1 ? ["UnknownArity"] : Enumerable.Range(1,count).Select(i=>"in"+i).ToArray();
            }
            if (kind is "PContact" or "NContact") return ["operand","bit"];
            if (kind is "PBox" or "NBox") return ["in","bit"];
            return ["in"];
        }
        private int Arity(XElement node,int fallback) {
            var value=node.Elements().FirstOrDefault(e=>Named(e,"TemplateValue")&&A(e,"Name")=="Card")?.Value;
            if (value is null) return fallback;
            return int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var count)&&count is >0 and <=32 ? count : -1;
        }
        private bool Has(string uid,string pin) => wires.ContainsKey(uid+"."+pin);
        private bool Neg(XElement node,string pin) => node.Elements().Any(e=>Named(e,"Negated")&&A(e,"Name").Equals(pin,StringComparison.OrdinalIgnoreCase));
        private string Resolve(string uid,string pin) { budget=1024;return Input(uid,pin,new HashSet<string>(StringComparer.OrdinalIgnoreCase),0); }
        private string Input(string uid,string pin,HashSet<string> visiting,int depth) {
            if (--budget<0||depth>64) return Unknown("ExpressionBudget");
            if (structuralProblems.Count>0) return Unknown("AmbiguousNodeIdentity");
            var key=uid+"."+pin;
            if (!wires.TryGetValue(key,out var links)||links.Count==0) return Unknown("Unconnected "+key);
            if (links.Count!=1) return Unknown("MultipleWires "+key);
            if (!visiting.Add(key)) return Unknown("Cycle "+key);
            try {
                var sources=new List<XElement>();var accesses=new List<XElement>();
                foreach (var endpoint in links[0].Elements()) {
                    if (Named(endpoint,"IdentCon")) { accesses.Add(endpoint);continue; }
                    if (Named(endpoint,"Powerrail")) { sources.Add(endpoint);continue; }
                    if (Named(endpoint,"OpenCon")) continue;
                    if (!Named(endpoint,"NameCon")) return Unknown("UnknownEndpoint "+key);
                    var id=A(endpoint,"UId");var port=A(endpoint,"Name");
                    if (id==uid&&port.Equals(pin,StringComparison.OrdinalIgnoreCase)) continue;
                    if (!nodes.TryGetValue(id,out var part)) return Unknown("MissingNode "+id);
                    var role=Role(part,port);
                    if (role=="Output") sources.Add(endpoint);
                    else if (role!="Input") return Unknown("UnknownPin "+id+"."+port);
                }
                // An IdentCon on a wire driven by an instruction is its assigned variable, not another driver.
                if (!sources.Any(e=>Named(e,"NameCon"))) sources.AddRange(accesses);
                if (sources.Count!=1) return Unknown("MultipleOrMissingDrivers "+key);
                var endpointSource=sources[0]; string value;
                if (Named(endpointSource,"Powerrail")) value="TRUE";
                else if (Named(endpointSource,"IdentCon")) value=Access(A(endpointSource,"UId"));
                else value=Output(A(endpointSource,"UId"),A(endpointSource,"Name"),visiting,depth+1);
                return nodes.TryGetValue(uid,out var target)&&Neg(target,pin) ? Not(value) : value;
            } finally { visiting.Remove(key); }
        }
        private string Access(string uid) {
            if (!nodes.TryGetValue(uid,out var access)||!Named(access,"Access")) return Unknown("MissingAccess "+uid);
            if (A(access,"Scope") is "LiteralConstant" or "TypedConstant") {
                var type=access.Descendants().FirstOrDefault(e=>Named(e,"ConstantType"))?.Value;
                var value=access.Descendants().FirstOrDefault(e=>Named(e,"ConstantValue"))?.Value;
                return type is null||value is null ? Unknown("IncompleteLiteral "+uid) : type+"("+value+")";
            }
            var symbol=A(access,"SymbolRef");
            // Never reconstruct a real identifier from Component names or guess its business meaning.
            return Regex.IsMatch(symbol,@"^[PS][0-9]+$") ? symbol : Unknown("MissingSymbolRef "+uid);
        }
        private string Output(string uid,string pin,HashSet<string> visiting,int depth) {
            if (--budget<0||depth>64) return Unknown("ExpressionBudget");
            if (!nodes.TryGetValue(uid,out var node)) return Unknown("MissingNode "+uid);
            var kind=A(node,"Name");
            string In(string input) => Input(uid,input,visiting,depth+1);
            string value;
            if (pin.Equals("eno",StringComparison.OrdinalIgnoreCase)) return Unknown("ENOState "+uid);
            if (kind=="Contact") value=And(In("in"),In("operand"));
            else if (kind is "A" or "AND" or "And" or "O" or "OR" or "Or") {
                var count=Arity(node,-1);
                if (count<1) return Unknown("UnknownBooleanArity "+uid);
                var expected=Enumerable.Range(1,count).Select(i=>"in"+i).ToArray();
                if (Pins(uid).Any(p=>Regex.IsMatch(p,@"^in\d+$",RegexOptions.IgnoreCase)&&!expected.Contains(p,StringComparer.OrdinalIgnoreCase))) return Unknown("UnexpectedBooleanInput "+uid);
                value=(kind is "O" or "OR" or "Or"?"OR":"AND")+"("+string.Join(", ",expected.Select(In))+")";
            } else if (kind is "NOT" or "Not") value=Not(In("in"));
            else if (Comparisons.Contains(kind)) {
                value=And(In("pre"),kind+"("+In("in1")+", "+In("in2")+")");
                if (Has(uid,"en")) value=And(In("en"),value);
            } else if (Edges.Contains(kind)) {
                var operand=kind is "PContact" or "NContact" ? "operand" : "in";
                value=(kind is "PContact" or "PBox"?"RISING_EDGE":"FALLING_EDGE")+"("+In(operand)+", PREVIOUS("+In("bit")+"))";
                if (kind is "PContact" or "NContact") value=And(In("pre"),value);
            } else if (Arithmetic.Contains(kind)) {
                value="RESULT("+uid+"."+pin+", "+NumericValue(node,In)+"; EN="+In("en")+(Has(uid,"pre")?"; pre="+In("pre"):"")+")";
            } else return Unknown("UnsupportedInstruction "+uid+"."+pin);
            if (value.Length>32768) return Unknown("ExpressionBudget");
            return Neg(node,pin)?Not(value):value;
        }
        private string NumericValue(XElement node,Func<string,string> input) {
            var kind=A(node,"Name");var uid=A(node,"UId");
            if (kind=="Move") return Pins(uid).Any(p=>Regex.IsMatch(p,@"^in\d+$",RegexOptions.IgnoreCase)) ? Unknown("UnexpectedNumericInput "+uid) : "Move("+input("in")+")";
            var count=Comparisons.Contains(kind)||kind is "Sub" or "Div" ? 2 : Arity(node,2);
            if (count<1||Arity(node,count)!=count) return Unknown("NumericArity "+uid);
            var expected=Enumerable.Range(1,count).Select(i=>"in"+i).ToArray();
            if(Pins(uid).Any(p=>Regex.IsMatch(p,@"^in\d+$",RegexOptions.IgnoreCase)&&!expected.Contains(p,StringComparer.OrdinalIgnoreCase))) return Unknown("UnexpectedNumericInput "+uid);
            return kind+"("+string.Join(", ",expected.Select(input))+")";
        }
        private string Value(XElement node) {
            var uid=A(node,"UId");var kind=A(node,"Name");
            if (Arithmetic.Contains(kind)||Comparisons.Contains(kind)) {
                var value=NumericValue(node,p=>Resolve(uid,p));
                return Neg(node,"out")?Not(value):value;
            }
            if (Coils.Contains(kind)) {
                var input=Resolve(uid,"in");if(Neg(node,"operand"))input=Not(input);
                return kind=="Coil"?input:kind=="SCoil"?"WHEN "+input+": SET; ELSE retain previous":"WHEN "+input+": RESET; ELSE retain previous";
            }
            budget=1024;return Output(uid,"out",new HashSet<string>(StringComparer.OrdinalIgnoreCase),0);
        }
        private IEnumerable<string> Pins(string uid) => wires.Keys.Where(k=>k.StartsWith(uid+".",StringComparison.Ordinal)).Select(k=>k[(uid.Length+1)..]);
        private string Role(XElement node,string pin) {
            var kind=A(node,"Name");
            if (Named(node,"Call")) {
                if(pin.Equals("en",StringComparison.OrdinalIgnoreCase))return "Input";
                if(pin.Equals("eno",StringComparison.OrdinalIgnoreCase))return "Output";
                var parameter=node.Descendants().FirstOrDefault(e=>Named(e,"Parameter")&&A(e,"Name").Equals(pin,StringComparison.OrdinalIgnoreCase));
                return parameter is null?"Unknown":A(parameter,"Section")=="Input"?"Input":A(parameter,"Section") is "Output" or "Return"?"Output":"Unknown";
            }
            if (!Arithmetic.Contains(kind)&&!Comparisons.Contains(kind)&&!Edges.Contains(kind)&&!Coils.Contains(kind)&&!BooleanParts.Contains(kind))
                return "Unknown";
            var p=pin.ToLowerInvariant();
            if (p=="out"||Arithmetic.Contains(kind)&&p=="eno"||kind=="Move"&&Regex.IsMatch(p,@"^out\d+$")) return "Output";
            var input=kind switch {
                "Contact" or "Coil" or "SCoil" or "RCoil" => p is "in" or "operand",
                "PContact" or "NContact" => p is "pre" or "operand" or "bit",
                "PBox" or "NBox" => p is "in" or "bit",
                "A" or "AND" or "And" or "O" or "OR" or "Or" => Regex.IsMatch(p,@"^in\d+$"),
                "NOT" or "Not" => p=="in",
                "Move" => p is "in" or "en" or "pre",
                "Add" or "Sub" or "Mul" or "Div" => p is "en" or "pre"||Regex.IsMatch(p,@"^in\d+$"),
                "Ge" or "Gt" or "Le" or "Lt" or "Eq" or "Ne" => p is "pre" or "en" or "in1" or "in2",
                _ => false
            };
            if(input)return "Input";
            return "Unknown";
        }
        private string DirectTarget(string uid,string pin) {
            if (!wires.TryGetValue(uid+"."+pin,out var links)||links.Count!=1) return Unknown("UnconnectedOrMultipleTarget "+uid+"."+pin);
            var identifiers=links[0].Elements().Where(e=>Named(e,"IdentCon")).ToArray();
            if (identifiers.Length!=1||links[0].Elements().Count()!=2) return Unknown("AmbiguousTarget "+uid+"."+pin);
            var id=A(identifiers[0],"UId");
            return VariableTarget(id);
        }
        private string VariableTarget(string id) {
            if (!nodes.TryGetValue(id,out var target)||A(target,"Scope") is not ("LocalVariable" or "GlobalVariable" or "Absolute")) return Unknown("NonVariableTarget "+id);
            return Access(id);
        }
        private IReadOnlyList<BusinessNumericOutput> Targets(XElement node) {
            var result=new List<BusinessNumericOutput>();var uid=A(node,"UId");var kind=A(node,"Name");
            if (Coils.Contains(kind)) {result.Add(new("operand",DirectTarget(uid,"operand"),uid+".operand",kind));return result;}
            if (Edges.Contains(kind)) result.Add(new("bit",DirectTarget(uid,"bit"),uid+".bit","EdgeMemoryUpdate"));
            var pins=Pins(uid).Where(p=>p.Equals("out",StringComparison.OrdinalIgnoreCase)||Regex.IsMatch(p,@"^out\d+$",RegexOptions.IgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(pins.Length==0) {result.Add(new("out",Unknown("UnconnectedOutput "+uid),uid+".out","Unknown"));return result;}
            foreach(var pin in pins) {
                var links=wires[uid+"."+pin];
                if(links.Count!=1){result.Add(new(pin,Unknown("MultipleOutputWires "+uid+"."+pin),uid+"."+pin,"Unknown"));continue;}
                var producers=links[0].Elements().Count(e=>Named(e,"Powerrail")||Named(e,"NameCon")&&nodes.TryGetValue(A(e,"UId"),out var n)&&Role(n,A(e,"Name"))=="Output");
                if(producers!=1){result.Add(new(pin,Unknown("MultipleOutputDrivers "+uid+"."+pin),uid+"."+pin,"Unknown"));continue;}
                if(links[0].Elements().Any(e=>Named(e,"NameCon")&&(!nodes.TryGetValue(A(e,"UId"),out var n)||Role(n,A(e,"Name"))=="Unknown"))) {
                    result.Add(new(pin,Unknown("UnknownOutputEndpoint "+uid+"."+pin),uid+"."+pin,"Unknown"));continue;
                }
                foreach(var endpoint in links[0].Elements()) {
                    var id=A(endpoint,"UId");var targetPin=A(endpoint,"Name");
                    if(Named(endpoint,"NameCon")&&id==uid&&pin.Equals(targetPin,StringComparison.OrdinalIgnoreCase))continue;
                    if(Named(endpoint,"IdentCon")) {result.Add(new(pin,VariableTarget(id),uid+"."+pin,Arithmetic.Contains(kind)?"EnabledNumericWrite":"OutputAssignment"));continue;}
                    if(Named(endpoint,"OpenCon")){result.Add(new(pin,"Open",uid+"."+pin,"Open"));continue;}
                    if(Named(endpoint,"NameCon")&&nodes.TryGetValue(id,out var sink)&&Role(sink,targetPin)=="Input") {
                        if(Coils.Contains(A(sink,"Name"))&&targetPin.Equals("in",StringComparison.OrdinalIgnoreCase))
                            result.Add(new(pin,DirectTarget(id,"operand"),uid+"."+pin+" -> "+id+".in -> "+id+".operand",A(sink,"Name")));
                        else result.Add(new(pin,id+"."+targetPin,uid+"."+pin+" -> "+id+"."+targetPin,"Dataflow"));
                    }else result.Add(new(pin,Unknown("UnknownOrMultipleOutputDriver "+uid+"."+pin),uid+"."+pin,"Unknown"));
                }
            }
            return result;
        }
    }
}
