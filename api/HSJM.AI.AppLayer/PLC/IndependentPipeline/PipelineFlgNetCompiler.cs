#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Exact supported combinational FlgNet subset. Missing wires/unknown ports never default to true.</summary>
public sealed class PipelineFlgNetCompiler : IPipelineLogicCompiler
{
    readonly Dictionary<string,XElement> parts;
    readonly Dictionary<string,XElement> accesses;
    readonly Dictionary<string,XElement> calls;
    readonly List<XElement> wires;
    readonly HashSet<string> visiting = new(StringComparer.Ordinal);
    readonly Func<XElement,Func<string,LogicTerm>,IList<PipelineCallEvidence>,IReadOnlyDictionary<string,LogicTerm>>? resolveCall;
    readonly Dictionary<string,IReadOnlyDictionary<string,LogicTerm>> resolvedCalls = new();
    readonly Dictionary<string,PipelineTimer> timerParts = new();
    readonly Func<string,string?>? numericSourceType;
    public List<PipelineCallEvidence> CallEvidence { get; } = new();
    public List<PipelineTimer> Timers { get; } = new();
    public PipelineFlgNetCompiler(XDocument xml,
        Func<XElement,Func<string,LogicTerm>,IList<PipelineCallEvidence>,IReadOnlyDictionary<string,LogicTerm>>? callResolver = null,
        Func<string,string?>? sourceType = null) {
        resolveCall = callResolver;
        numericSourceType=sourceType;
        var containers = xml.Root!.Elements().Where(e => e.Name.LocalName == "Parts").ToArray();
        if (containers.Length != 1) throw new PipelineException("PartsContainerMissingOrAmbiguous");
        var items = containers[0].Elements().ToArray();
        if (items.Any(e => e.Name.LocalName is not ("Part" or "Access" or "Call")))
            throw new PipelineException("UnsupportedCallOrPart");
        if (items.Any(e => string.IsNullOrWhiteSpace((string?)e.Attribute("UId"))) ||
            items.GroupBy(e => (string?)e.Attribute("UId")).Any(g => g.Count() != 1))
            throw new PipelineException("DuplicateOrMissingUid");
        parts = items.Where(e => e.Name.LocalName == "Part").ToDictionary(e => (string)e.Attribute("UId")!,StringComparer.Ordinal);
        accesses = items.Where(e => e.Name.LocalName == "Access").ToDictionary(e => (string)e.Attribute("UId")!,StringComparer.Ordinal);
        calls = items.Where(e => e.Name.LocalName == "Call").ToDictionary(e => (string)e.Attribute("UId")!,StringComparer.Ordinal);
        var wireContainers = xml.Root.Elements().Where(e => e.Name.LocalName == "Wires").ToArray();
        if (wireContainers.Length != 1) throw new PipelineException("WiresContainerMissingOrAmbiguous");
        wires = wireContainers[0].Elements().ToList();
        if (wires.Any(w => w.Name.LocalName != "Wire" || w.Elements().Any(e => e.Name.LocalName is not ("Powerrail" or "IdentCon" or "NameCon" or "OpenCon"))))
            throw new PipelineException("UnsupportedWire");
    }
    public IReadOnlyList<PipelineAssignment> Compile() {
        if (parts.Values.Any(p => (string?)p.Attribute("Name") is not ("Contact" or "Coil" or "SCoil" or "RCoil" or "A" or "O" or "Not" or "TON" or "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le")))
            throw new PipelineException("UnsupportedStatefulOrInstruction");
        var assignments = new List<PipelineAssignment>();
        foreach (var timer in parts.Where(p => (string?)p.Value.Attribute("Name") == "TON")) PrepareTimer(timer.Key);
        foreach (var pair in parts.Where(p => (string?)p.Value.Attribute("Name") is "Coil" or "SCoil" or "RCoil")) {
            ValidateModifiers(pair.Value);
            var target = Operand(pair.Key);
            if (target.Kind != "Signal") throw new PipelineException("InvalidCoilTarget");
            var expression = Input(pair.Key, "in");
            var kind = (string?)pair.Value.Attribute("Name");
            if (kind is "SCoil" or "RCoil") {
                if (IsNegated(pair.Value)) throw new PipelineException("UnsupportedStatefulNegation");
                expression = kind == "SCoil" ? new("Or",null,[expression,new("Previous",target.Name)])
                    : new("And",null,[new("Not",null,[expression]),new("Previous",target.Name)]);
            }
            if (IsNegated(pair.Value)) expression = new("Not", null, [expression]);
            assignments.Add(new(target.Name!, expression));
        }
        foreach (var call in calls) {
            var outputs = CallOutputs(call.Key);
            if (outputs.ContainsKey("eno")) throw new PipelineException("ReservedCallPin");
            foreach (var output in outputs) CollectWrites(call.Key,output.Key,output.Value,assignments);
            CollectWrites(call.Key,"eno",new("Constant","true"),assignments);
        }
        foreach (var part in parts) {
            var kind=(string?)part.Value.Attribute("Name");
            if (kind == "TON") {
                CollectWrites(part.Key,"Q",Output(part.Key,"Q"),assignments);
                CollectWrites(part.Key,"eno",new("Constant","true"),assignments);
                if (HasConsumer(part.Key,"ET")) throw new PipelineException("TimerElapsedValueWriteUnsupported");
            }
            else if (kind is "Coil" or "SCoil" or "RCoil") {
                if (HasConsumer(part.Key,"out")) throw new PipelineException("CoilContinuationUnsupported");
            }
            else if (PortWires(part.Key,"out").Any(w => w.Elements().Any(e => e.Name.LocalName == "IdentCon")))
                CollectWrites(part.Key,"out",Output(part.Key,"out"),assignments);
        }
        if (assignments.Select(a => a.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != assignments.Count)
            throw new PipelineException("MultipleWritesRequireExecutionOrder");
        if (parts.Count > 0 && assignments.Count == 0) throw new PipelineException("NoSupportedOutput");
        var targets = assignments.Select(a => a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return assignments.Select(a => a with { Expression = PipelineTerms.Rewrite(a.Expression,t => {
            if (t.Kind != "Signal" || !targets.Contains(t.Name!)) return null;
            return string.Equals(t.Name,a.Target,StringComparison.OrdinalIgnoreCase) ? new("Previous",a.Target)
                : throw new PipelineException("CrossOutputExecutionOrderRequired");
        }) }).ToArray();
    }
    IEnumerable<XElement> PortWires(string id,string pin) => wires.Where(w => w.Elements().Any(e => e.Name.LocalName == "NameCon" &&
        (string?)e.Attribute("UId") == id && (string?)e.Attribute("Name") == pin));
    bool HasConsumer(string id,string pin) => PortWires(id,pin).Any(w => w.Elements().Any(e => e.Name.LocalName is not "OpenCon" &&
        !(e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id && (string?)e.Attribute("Name") == pin)));
    void CollectWrites(string id,string pin,LogicTerm expression,ICollection<PipelineAssignment> assignments) {
        var matches=PortWires(id,pin).ToArray();
        if(matches.Length>1) throw new PipelineException("AmbiguousOutputWire");
        foreach(var wire in matches) {
            var drivers=wire.Elements().Where(IsOutput).ToArray();
            if(wire.Elements().Any(e => e.Name.LocalName == "Powerrail") || drivers.Length!=1 ||
                (string?)drivers[0].Attribute("UId")!=id || (string?)drivers[0].Attribute("Name")!=pin)
                throw new PipelineException("AmbiguousOutputDriver");
            foreach(var targetRef in wire.Elements().Where(e => e.Name.LocalName=="IdentCon")) {
                var target=Access((string?)targetRef.Attribute("UId") ?? "");
                if(target.Kind!="Signal") throw new PipelineException("InvalidOutputTarget");
                assignments.Add(new(target.Name!,expression));
            }
        }
    }
    LogicTerm Operand(string id) {
        var wire = WireAt(id, "operand");
        var refs = wire.Elements().Where(e => e.Name.LocalName == "IdentCon").ToArray();
        if (refs.Length != 1 || wire.Elements().Count() != 2) throw new PipelineException("AmbiguousOperand");
        return Access((string?)refs[0].Attribute("UId") ?? "");
    }
    LogicTerm Access(string id) {
        if (!accesses.TryGetValue(id, out var access)) throw new PipelineException("MissingAccess");
        var scope = (string?)access.Attribute("Scope");
        if (scope == "LiteralConstant") {
            var constant = access.Descendants().SingleOrDefault(e => e.Name.LocalName == "ConstantValue")?.Value;
            return constant switch {
                "true" or "TRUE" => new("Constant", "true"), "false" or "FALSE" => new("Constant", "false"),
                _ => throw new PipelineException("UnsupportedConstant")
            };
        }
        if (scope is not ("GlobalVariable" or "LocalVariable")) throw new PipelineException("UnsupportedAccessScope");
        var symbol = access.Elements().SingleOrDefault(e => e.Name.LocalName == "Symbol");
        if (symbol is null || !symbol.Elements().Any() || symbol.Elements().Any(e => e.Name.LocalName != "Component"
            || string.IsNullOrEmpty((string?)e.Attribute("Name")) || ((string)e.Attribute("Name")!).IndexOfAny(['.','#','[',']']) >= 0))
            throw new PipelineException("UnsupportedSymbolBinding");
        var name = string.Join(".",symbol.Elements().Select(ComponentName));
        return LogicTerm.Signal(scope == "LocalVariable" ? "#" + name : name);
    }
    public static string ComponentName(XElement component) {
        if(component.Attribute("SliceAccessModifier") is not null)throw new PipelineException("UnsupportedBitSliceBinding");
        var name=(string?)component.Attribute("Name") ?? throw new PipelineException("MissingSymbolName");
        if(!component.HasElements)return name;
        var indices=new List<string>();
        foreach(var access in component.Elements()) {
            if(access.Name.LocalName=="BooleanAttribute" && (string?)access.Attribute("Name")=="HasQuotes"
                && access.Value is "true" or "false") continue;
            if(access.Name.LocalName!="Access"||(string?)access.Attribute("Scope")!="LiteralConstant")throw new PipelineException("DynamicIndexRequiresBinding");
            var value=access.Descendants().SingleOrDefault(e=>e.Name.LocalName=="ConstantValue")?.Value;
            if(!int.TryParse(value,out var index))throw new PipelineException("InvalidArrayIndex");
            indices.Add(index.ToString(CultureInfo.InvariantCulture));
        }
        return indices.Count==0 ? name : name+"["+string.Join(",",indices)+"]";
    }
    LogicTerm ScalarInput(string id,string pin,string type) {
        var wire=WireAt(id,pin);var refs=wire.Elements().Where(e=>e.Name.LocalName=="IdentCon").ToArray();
        if(refs.Length!=1||wire.Elements().Any(IsOutput)||wire.Elements().Any(e=>e.Name.LocalName=="Powerrail"))throw new PipelineException("UnsupportedNumericSource");
        var accessId=(string?)refs[0].Attribute("UId") ?? "";
        if(!accesses.TryGetValue(accessId,out var access))throw new PipelineException("MissingAccess");
        if((string?)access.Attribute("Scope")=="LiteralConstant") {
            var value=access.Descendants().SingleOrDefault(e=>e.Name.LocalName=="ConstantValue")?.Value ?? "";
            if(!double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)||!double.IsFinite(number))throw new PipelineException("UnsupportedNumericConstant");
            var literalType=access.Descendants().SingleOrDefault(e=>e.Name.LocalName=="ConstantType")?.Value ?? type;
            PipelineNumeric.Coerce(number,literalType);
            return new("Number",value,null,literalType);
        }
        var signal=Access(accessId);
        var originalType=numericSourceType is null ? type : numericSourceType(signal.Name!) ?? throw new PipelineException("NumericSourceTypeNotEstablished");
        PipelineNumeric.Coerce(0,originalType);
        return new("ScalarSignal",signal.Name,null,originalType);
    }
    XElement WireAt(string id, string port) {
        var matches = wires.Where(w => w.Elements().Any(e => e.Name.LocalName == "NameCon" &&
            (string?)e.Attribute("UId") == id && (string?)e.Attribute("Name") == port)).ToArray();
        if (matches.Length != 1) throw new PipelineException("MissingOrAmbiguousWire");
        return matches[0];
    }
    LogicTerm Input(string id, string port) {
        var wire = WireAt(id, port);
        var outputDrivers = wire.Elements().Where(IsOutput).ToArray();
        var drivers = outputDrivers.Length > 0 ? outputDrivers : wire.Elements().Where(e => e.Name.LocalName is "Powerrail" or "IdentCon").ToArray();
        if (outputDrivers.Length > 0 && wire.Elements().Any(e => e.Name.LocalName == "Powerrail")) throw new PipelineException("AmbiguousDriver");
        if (drivers.Length != 1) throw new PipelineException("MissingOrAmbiguousDriver");
        var driver = drivers[0];
        return driver.Name.LocalName switch {
            "Powerrail" => new("Constant", "true"),
            "IdentCon" => Access((string?)driver.Attribute("UId") ?? ""),
            _ => Output((string?)driver.Attribute("UId") ?? "",(string?)driver.Attribute("Name") ?? "")
        };
    }
    bool IsOutput(XElement endpoint) {
        if (endpoint.Name.LocalName != "NameCon") return false;
        var id=(string?)endpoint.Attribute("UId") ?? ""; var pin=(string?)endpoint.Attribute("Name");
        if (calls.TryGetValue(id,out var call)) return pin == "eno" || call.Descendants().Any(e => e.Name.LocalName == "Parameter" &&
            (string?)e.Attribute("Name") == pin && (string?)e.Attribute("Section") == "Output");
        return parts.TryGetValue(id,out var part) && (string?)part.Attribute("Name") == "TON" ? pin is "Q" or "eno" : pin == "out";
    }
    IReadOnlyDictionary<string,LogicTerm> CallOutputs(string id) {
        if (resolvedCalls.TryGetValue(id,out var result)) return result;
        if (resolveCall is null) throw new PipelineException("MissingCalleeResolver");
        if (!visiting.Add("call:"+id)) throw new PipelineException("CyclicCallWiring");
        try { result = resolveCall(calls[id],pin => Input(id,pin),CallEvidence); resolvedCalls[id]=result; return result; }
        finally { visiting.Remove("call:"+id); }
    }
    LogicTerm Output(string id,string port) {
        if (calls.ContainsKey(id)) {
            var outputs = CallOutputs(id);
            return port == "eno" ? new("Constant","true") : outputs.TryGetValue(port,out var value) ? value : throw new PipelineException("MissingCallOutput");
        }
        if (parts.TryGetValue(id,out var timerPart) && (string?)timerPart.Attribute("Name") == "TON") {
            var timer = PrepareTimer(id);
            return port == "eno" ? new("Constant","true") : new("And",null,[timer.Input,new("TimerElapsed",timer.Key)]);
        }
        if (!visiting.Add(id) || visiting.Count > 256) throw new PipelineException("CyclicWiringRequiresStateProof");
        try {
            if (!parts.TryGetValue(id,out var part)) throw new PipelineException("MissingPart");
            ValidateModifiers(part);
            var kind = (string?)part.Attribute("Name");
            if(kind is "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le") {
                if(port!="out")throw new PipelineException("UnsupportedOutputPort");
                if(IsNegated(part))throw new PipelineException("UnsupportedComparisonNegation");
                var type=PipelineNumeric.NormalizeType(part.Elements().FirstOrDefault(e=>e.Name.LocalName=="TemplateValue"&&(string?)e.Attribute("Name")=="SrcType")?.Value ?? "");
                if(type is not("Int" or "DInt" or "UInt" or "UDInt" or "Real" or "LReal" or "SInt" or "USInt" or "Byte" or "Word" or "DWord"))throw new PipelineException("UnsupportedComparisonType");
                LogicTerm result=new("Compare",kind,[ScalarInput(id,"in1",type),ScalarInput(id,"in2",type)],type);
                if(PortWires(id,"en").Any())result=new("And",null,[Input(id,"en"),result]);
                if(PortWires(id,"pre").Any())result=new("And",null,[Input(id,"pre"),result]);
                return IsNegated(part)?new("Not",null,[result]):result;
            }
            if (kind == "Contact") {
                var operand = Operand(id);
                if (IsNegated(part)) operand = new("Not",null,[operand]);
                return new("And",null,[Input(id,"in"),operand]);
            }
            if (kind == "Not") {
                var inverted = new LogicTerm("Not",null,[Input(id,"in")]);
                return IsNegated(part) ? new("Not",null,[inverted]) : inverted;
            }
            if (kind is "A" or "O") {
                var ports = wires.SelectMany(w => w.Elements()).Where(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id)
                    .Select(e => (string?)e.Attribute("Name") ?? "").Where(n => n.StartsWith("in",StringComparison.Ordinal)).Distinct().ToArray();
                if (ports.Length == 0 || ports.Any(n => !int.TryParse(n.AsSpan(2),out var number) || number < 1))
                    throw new PipelineException("InvalidGatePorts");
                var card = part.Elements().SingleOrDefault(e => e.Name.LocalName == "TemplateValue" && (string?)e.Attribute("Name") == "Card")?.Value;
                if (card != null && (!int.TryParse(card,out int count) || count != ports.Length)) throw new PipelineException("MissingGateInput");
                var expression = new LogicTerm(kind == "A" ? "And" : "Or",null,
                    ports.OrderBy(n => int.Parse(n.AsSpan(2))).Select(p => Input(id,p)).ToArray());
                return IsNegated(part) ? new("Not",null,[expression]) : expression;
            }
            throw new PipelineException("UnsupportedOutputPort");
        }
        finally { visiting.Remove(id); }
    }
    PipelineTimer PrepareTimer(string id) {
        if (timerParts.TryGetValue(id,out var known)) return known;
        if (!visiting.Add("timer:"+id)) throw new PipelineException("CyclicTimerWiring");
        try {
            var part = parts[id];
            if (part.Elements().Any(e => e.Name.LocalName is not ("Instance" or "TemplateValue"))) throw new PipelineException("UnsupportedTimerModifier");
            var instance = part.Elements().SingleOrDefault(e => e.Name.LocalName == "Instance");
            if (instance is null || (string?)instance.Attribute("Scope") != "GlobalVariable") throw new PipelineException("TimerInstanceBindingRequired");
            var components = (instance.Elements().SingleOrDefault(e => e.Name.LocalName == "Symbol") ?? instance).Elements().ToArray();
            if (components.Length == 0 || components.Any(e => e.Name.LocalName != "Component" || e.HasElements ||
                string.IsNullOrWhiteSpace((string?)e.Attribute("Name")) || ((string)e.Attribute("Name")!).IndexOfAny(['.','#','[',']']) >= 0))
                throw new PipelineException("UnsupportedTimerInstanceBinding");
            var key = string.Join(".",components.Select(e => (string)e.Attribute("Name")!));
            if (Timers.Any(t => string.Equals(t.Key,key,StringComparison.OrdinalIgnoreCase))) throw new PipelineException("RepeatedTimerInstanceExecution");
            if (wires.Any(w => w.Elements().Any(e => e.Name.LocalName == "NameCon" && (string?)e.Attribute("UId") == id && (string?)e.Attribute("Name") == "en"))
                && !PipelineTerms.IsTrue(Input(id,"en"))) throw new PipelineException("ConditionalTimerExecutionUnsupported");
            var presetWire=WireAt(id,"PT");
            var presetRefs=presetWire.Elements().Where(e => e.Name.LocalName == "IdentCon").ToArray();
            if (presetRefs.Length != 1 || presetWire.Elements().Count() != 2 ||
                !accesses.TryGetValue((string?)presetRefs[0].Attribute("UId") ?? "",out var preset) || (string?)preset.Attribute("Scope") != "LiteralConstant")
                throw new PipelineException("DynamicTimerPresetUnsupported");
            var type = preset.Descendants().SingleOrDefault(e => e.Name.LocalName == "ConstantType")?.Value;
            if (!string.Equals(type,"Time",StringComparison.OrdinalIgnoreCase)) throw new PipelineException("UnsupportedTimerTimeType");
            var delay = ParseTime(preset.Descendants().SingleOrDefault(e => e.Name.LocalName == "ConstantValue")?.Value ?? "");
            var timer = new PipelineTimer(key,Input(id,"IN"),delay);
            Timers.Add(timer); timerParts[id]=timer; return timer;
        }
        finally { visiting.Remove("timer:"+id); }
    }
    public static long ParseTime(string literal) {
        var value=literal.Trim().ToLowerInvariant();
        if (value.StartsWith("time#")) value=value[5..]; else if(value.StartsWith("t#")) value=value[2..]; else throw new PipelineException("InvalidTimerPreset");
        value=value.Replace("_","");
        long total=0, previousUnit=long.MaxValue; int offset=0;
        foreach(Match match in Regex.Matches(value,@"(\d+)(ms|d|h|m|s)")) {
            if(match.Index!=offset) throw new PipelineException("InvalidTimerPreset");
            long unit=match.Groups[2].Value switch {"d"=>86400000,"h"=>3600000,"m"=>60000,"s"=>1000,_=>1};
            if(unit>=previousUnit || !long.TryParse(match.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var number) || number>int.MaxValue/unit)
                throw new PipelineException("InvalidTimerPreset");
            previousUnit=unit; total+=number*unit; offset+=match.Length;
        }
        if(offset!=value.Length || total<=0 || total>int.MaxValue) throw new PipelineException("InvalidTimerPreset");
        return total;
    }
    static bool IsNegated(XElement part) => part.Elements().Any(e => e.Name.LocalName == "Negated");
    static void ValidateModifiers(XElement part) {
        var name = (string?)part.Attribute("Name");
        if (part.Elements().Any(e => e.Name.LocalName is not ("Negated" or "TemplateValue"))) throw new PipelineException("UnsupportedPartModifier");
        if (part.Elements().Where(e => e.Name.LocalName == "Negated").Any(e =>
            (string?)e.Attribute("Name") != (name is "Contact" or "Coil" ? "operand" : "out")))
            throw new PipelineException("UnsupportedNegationPort");
    }
    public static string Describe(LogicTerm term) => term.Kind switch {
        "Signal" => "`" + term.Name + "`", "Constant" => term.Name!,
        "Previous" => "上次执行后的 `" + term.Name + "` 状态",
        "TimerElapsed" => "定时器 `" + term.Name + "` 已达到预设时间",
        "ScalarSignal" => "`"+term.Name+"`",
        "Number" => term.Name!,
        "Compare" => Describe(term.Children![0])+(term.Name switch{"Eq"=>" 等于 ","Ne"=>" 不等于 ","Gt"=>" 大于 ","Ge"=>" 大于等于 ","Lt"=>" 小于 ",_=>" 小于等于 "})+Describe(term.Children[1]),
        "Not" => "非（" + Describe(term.Children![0]) + "）",
        "And" => "（" + string.Join(" 且 ",term.Children!.Select(Describe)) + "）",
        "Or" => "（" + string.Join(" 或 ",term.Children!.Select(Describe)) + "）",
        _ => throw new PipelineException("UnsupportedExpression")
    };
}
