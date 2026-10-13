#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;
using static HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353FlgNet;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353ConditionalFeedback(PipelineRule Rule,IReadOnlyList<string> BypassSignals,
    string Applicability,bool RequiresSettlingEvidence,IReadOnlyList<string> Evidence)
{
    /// <summary>A supplied true value means an actual trace established the command remained
    /// active past its configured settling boundary. Merely selecting a UI option is not proof.</summary>
    public string Evaluate(IReadOnlyDictionary<string,bool?> values,bool? settled) {
        if(settled is null)return "Unknown";
        if(settled==false)return "NotApplicable";
        var source=Rule.Signals.Single(s=>s.Role=="Source").Name;
        if(!values.TryGetValue(source,out var drive)||drive is null)return "Unknown";
        if(drive==false)return "NotApplicable";
        foreach(var guard in BypassSignals) {
            if(!values.TryGetValue(guard,out var bypass)||bypass is null)return "Unknown";
            if(bypass==true)return "NotApplicable";
        }
        var targets=Rule.Signals.Where(s=>s.Role=="Target").Select(s=>s.Name).ToArray();
        if(targets.Any(t=>!values.TryGetValue(t,out var value)||value is null))return "Unknown";
        return targets.All(t=>values[t]==true)?"Pass":"Violation";
    }
}

public static class Original353FeedbackGroups
{
    static bool BodyUsesTypedPorts(PipelineSnapshot source,string blockName,string input,string output) {
        var body=source.Units.Where(u=>u.BlockName.Equals(blockName,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(body.Length==0||body.Select(u=>u.BlockId).Distinct().Count()!=1||body.Any(u=>string.IsNullOrWhiteSpace(u.RawContent)))return false;
        var contracts=body.Select(u=>u.InterfaceXml).Distinct(StringComparer.Ordinal).ToArray();
        if(contracts.Length!=1||string.IsNullOrWhiteSpace(contracts[0]))return false;
        XElement contract;try{contract=XElement.Parse(contracts[0]);}catch(System.Xml.XmlException){return false;}
        bool Declared(string section,string name)=>contract.Descendants().Where(e=>Is(e,"Section")&&Original353FlgNet.A(e,"Name")==section)
            .SelectMany(e=>e.Elements().Where(c=>Is(c,"Member"))).Count(e=>Original353FlgNet.A(e,"Name")==name&&Original353FlgNet.A(e,"Datatype")=="Bool")==1;
        if(!Declared("Input",input)||!Declared("Output",output))return false;
        var read=false;var write=false;
        foreach(var unit in body) {
            XElement xml;try{xml=XElement.Parse(unit.RawContent);}catch(System.Xml.XmlException){return false;}
            if(!Is(xml,"FlgNet"))continue;
            var parts=xml.Descendants().Where(e=>e.Name.LocalName is "Part" or "Call" or "Access"&&Original353FlgNet.A(e,"UId")!="")
                .ToDictionary(e=>Original353FlgNet.A(e,"UId"));
            foreach(var access in xml.Descendants().Where(e=>Is(e,"Access")&&Original353FlgNet.A(e,"Scope")=="LocalVariable")) {
                var symbol=Symbol(access);if(symbol!=input&&symbol!=output)continue;
                foreach(var wire in xml.Descendants().Where(e=>Is(e,"Wire")&&e.Elements().Any(c=>Is(c,"IdentCon")&&Original353FlgNet.A(c,"UId")==Original353FlgNet.A(access,"UId"))))
                    foreach(var pin in wire.Elements().Where(e=>Is(e,"NameCon"))) {
                        if(!parts.TryGetValue(Original353FlgNet.A(pin,"UId"),out var part)||!Is(part,"Part")||Original353FlgNet.A(pin,"Name")!="operand")continue;
                        if(symbol==input&&Original353FlgNet.A(part,"Name")=="Contact")read=true;
                        if(symbol==output&&Original353FlgNet.A(part,"Name")=="Coil")write=true;
                    }
            }
        }
        return read&&write;
    }
    static IReadOnlyList<string> Leaves(LogicTerm t)=>t.Kind=="Signal"?[t.Name!]:t.Children?.SelectMany(Leaves).ToArray()??[];
    static bool? Eval(LogicTerm t,IReadOnlyDictionary<string,bool> values)=>t.Kind switch {
        "Constant"=>t.Name=="true","Signal"=>values.TryGetValue(t.Name!,out var value)?value:null,
        "Not"=>Eval(t.Children![0],values) is { } x?!x:null,
        "And"=>t.Children!.Select(x=>Eval(x,values)).All(v=>v==true),
        "Or"=>t.Children!.Select(x=>Eval(x,values)).Any(v=>v==true),_=>null};
    static string[] Required(LogicTerm term,string[] physical,string[] guards) {
        var variables=physical.Concat(guards).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(variables.Length>12)return [];
        var required=new List<string>();
        foreach(var tested in physical) {
            var counterexample=false;
            for(var mask=0;mask<(1<<physical.Length);mask++) {
                var values=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                foreach(var guard in guards)values[guard]=false;
                for(var i=0;i<physical.Length;i++)values[physical[i]]=(mask&(1<<i))!=0;
                if(values[tested])continue;
                if(Eval(term,values)==true){counterexample=true;break;}
            }
            if(!counterexample)required.Add(tested);
        }
        return required.ToArray();
    }
    static string[] Masked(LogicTerm term,string[] physical,string[] guards) {
        var masked=new List<string>();
        foreach(var tested in physical) {
            var influential=false;
            for(var mask=0;mask<(1<<physical.Length);mask++) {
                var values=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                foreach(var guard in guards)values[guard]=false;
                for(var i=0;i<physical.Length;i++)values[physical[i]]=(mask&(1<<i))!=0;
                values[tested]=false;var whenFalse=Eval(term,values);
                values[tested]=true;var whenTrue=Eval(term,values);
                if(whenFalse!=whenTrue){influential=true;break;}
            }
            if(!influential)masked.Add(tested);
        }
        return masked.ToArray();
    }
    public static IReadOnlyList<Original353ConditionalFeedback> Extract(PipelineSnapshot source,PipelineUnit unit) {
        if(string.IsNullOrWhiteSpace(unit.RawContent)||!source.Units.Any(u=>u.Id==unit.Id&&u.RawContent==unit.RawContent))return [];
        XElement root;try{root=XElement.Parse(unit.RawContent);}catch(System.Xml.XmlException){return [];}
        if(!Is(root,"FlgNet")||!root.Descendants().Any(e=>Is(e,"Call")))return [];
        Original353FlgNet network;try{network=new(root);}catch(Exception e)when(e is InvalidOperationException or ArgumentException){return [];}
        var rules=new List<Original353ConditionalFeedback>();
        foreach(var call in network.Calls) {
            var infos=call.Elements().Where(e=>Is(e,"CallInfo")).Take(2).ToArray();if(infos.Length!=1)continue;
            var info=infos[0];
            var inputs=info.Elements().Where(e=>Is(e,"Parameter")&&Original353FlgNet.A(e,"Section")=="Input"&&Original353FlgNet.A(e,"Type")=="Bool").ToArray();
            var outputs=info.Elements().Where(e=>Is(e,"Parameter")&&Original353FlgNet.A(e,"Section")=="Output"&&Original353FlgNet.A(e,"Type")=="Bool").ToArray();
            if(inputs.Length<2||outputs.Length<2)continue;
            // Only the first two corresponding Bool pairs are considered. These are structural
            // interface positions, never derived from mnemonic pin or plant signal names.
            for(var i=0;i<2;i++) {
                var sourceName=network.BoundOutput(Original353FlgNet.A(call,"UId"),Original353FlgNet.A(outputs[i],"Name"));
                if(sourceName==null||!BoolTag(source,sourceName,"%Q")||network.HasOtherWrite(sourceName,Original353FlgNet.A(call,"UId"),Original353FlgNet.A(outputs[i],"Name")))continue;
                if(!BodyUsesTypedPorts(source,Original353FlgNet.A(info,"Name"),Original353FlgNet.A(inputs[i],"Name"),Original353FlgNet.A(outputs[i],"Name")))continue;
                var expression=network.Pin(Original353FlgNet.A(call,"UId"),Original353FlgNet.A(inputs[i],"Name"));if(expression==null)continue;
                var leaves=Leaves(expression).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if(leaves.Length is <3 or >12||PipelineTerms.Walk(expression).Any(t=>t.Kind is not("Signal" or "And" or "Or" or "Not" or "Constant")))continue;
                var physical=leaves.Where(n=>BoolTag(source,n,"%I")).ToArray();
                var bypass=leaves.Except(physical,StringComparer.OrdinalIgnoreCase).ToArray();
                if(physical.Length is <2 or >8||bypass.Length>4)continue;
                var required=Required(expression,physical,bypass);
                if(required.Length<2)continue;
                var masked=Masked(expression,physical,bypass);
                var rule=new PipelineRule("IMPLIES",[new(sourceName,"Source"),..required.Order(StringComparer.Ordinal).Select(n=>new PipelineSignal(n,"Target"))],
                    "多点反馈的诊断期望：必须先确认命令已稳定超过适用的动作等待时间，且所有旁路为 false；仅用于诊断，不是PLC代码对外部传感器的瞬时保证。")
                {Applicability="AfterSettling",BypassSignals=bypass,RequiresSettlingEvidence=true,
                    MaskedFeedbackInputs=masked};
                rules.Add(new(rule,bypass,"AfterSettling",true,["Caller LU:"+unit.Id,"SourceHash:"+PipelineHash.Of(unit.RawContent),
                    "Call:"+Original353FlgNet.A(call,"UId")+" output:"+Original353FlgNet.A(outputs[i],"Name")+" input:"+Original353FlgNet.A(inputs[i],"Name"),
                    "FullInputExpression:"+JsonSerializer.Serialize(expression),
                    ..masked.Select(name=>"MaskedFeedbackInput:"+name)]));
            }
        }
        return rules.DistinctBy(r=>r.Rule.Key).ToArray();
    }
}
