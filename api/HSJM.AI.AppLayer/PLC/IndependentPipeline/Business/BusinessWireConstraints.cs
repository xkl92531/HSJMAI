#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Direct shared-wire candidates, still reviewed in whole-code context. No pin names/address layout heuristics.</summary>
public static class BusinessWireConstraints
{
    public static IReadOnlyList<BusinessCandidate> Extract(PipelineUnit unit,BusinessDescription description) {
        if(string.IsNullOrWhiteSpace(unit.RawContent)||description.Status!="Rendered")return [];
        XElement root;
        try {
            using var reader=XmlReader.Create(new StringReader(unit.RawContent),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000});
            root=XElement.Load(reader);
        }catch(XmlException){return [];}
        var aliases=description.SignalAliases.GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First().Key,StringComparer.OrdinalIgnoreCase);
        var origin=description.Evidence.FirstOrDefault(e=>e.Kind=="SourceStructure");if(origin is null)return [];
        var rules=new List<BusinessCandidate>();
        foreach(var network in root.DescendantsAndSelf().Where(e=>e.Name.LocalName=="FlgNet")) {
            var parts=network.Elements().FirstOrDefault(e=>e.Name.LocalName=="Parts")?.Elements().ToArray()??[];
            var wires=network.Elements().FirstOrDefault(e=>e.Name.LocalName=="Wires")?.Elements().ToArray()??[];
            string Id(XElement element)=>(string?)element.Attribute("UId")??"";
            var accesses=parts.Where(e=>e.Name.LocalName=="Access").GroupBy(Id).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single());
            string? Target(string coilId) {
                var connections=wires.Where(w=>w.Elements().Any(e=>e.Name.LocalName=="NameCon"&&Id(e)==coilId&&(string?)e.Attribute("Name")=="operand")).ToArray();
                if(connections.Length!=1||connections[0].Elements().Count()!=2)return null;
                var accessRef=connections[0].Elements().SingleOrDefault(e=>e.Name.LocalName=="IdentCon");
                if(accessRef==null||!accesses.TryGetValue(Id(accessRef),out var access)||(string?)access.Attribute("Scope")!="GlobalVariable")return null;
                var components=access.Elements().FirstOrDefault(e=>e.Name.LocalName=="Symbol")?.Elements().ToArray()??[];
                if(components.Length==0||components.Any(e=>e.Name.LocalName!="Component"))return null;
                try {return string.Join(".",components.Select(PipelineFlgNetCompiler.ComponentName));}catch(PipelineException){return null;}
            }
            var writers=parts.Where(p=>p.Name.LocalName=="Part"&&((string?)p.Attribute("Name")) is "Coil" or "SCoil" or "RCoil")
                .Select(p=>(Id:Id(p),Target:Target(Id(p)))).Where(p=>p.Target!=null).ToArray();
            foreach(var wire in wires) {
                var coils=parts.Where(p=>p.Name.LocalName=="Part"&&(string?)p.Attribute("Name")=="Coil"&&!p.Elements().Any(e=>e.Name.LocalName=="Negated")&&
                    wire.Elements().Any(e=>e.Name.LocalName=="NameCon"&&Id(e)==Id(p)&&(string?)e.Attribute("Name")=="in"))
                    .Select(p=>Target(Id(p))).Where(n=>n!=null&&aliases.ContainsKey(n)&&writers.Count(w=>string.Equals(w.Target,n,StringComparison.OrdinalIgnoreCase))==1)
                    .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if(coils.Length<2)continue;
                var endpoints=wire.Elements().ToArray();
                // Multiple power/variable sources or unknown connector forms do not establish one shared driver.
                if(endpoints.Any(e=>e.Name.LocalName is not("NameCon" or "IdentCon" or "Powerrail" or "OpenCon")))continue;
                bool IsNamedDriver(XElement endpoint) {
                    if(endpoint.Name.LocalName!="NameCon")return false;
                    var matching=parts.Where(p=>Id(p)==Id(endpoint)).ToArray();
                    if(matching.Length!=1)return false;
                    var part=matching[0];var pin=(string?)endpoint.Attribute("Name");
                    if(part.Name.LocalName=="Call")return pin=="eno"||part.Descendants().Any(p=>p.Name.LocalName=="Parameter"&&
                        (string?)p.Attribute("Name")==pin&&(string?)p.Attribute("Section") is "Output" or "Return");
                    return part.Name.LocalName=="Part"&&pin is "out" or "Q" or "eno";
                }
                var namedDrivers=endpoints.Count(IsNamedDriver);
                var direct=endpoints.Count(e=>e.Name.LocalName is "IdentCon" or "Powerrail");
                if(namedDrivers+direct!=1)continue;
                rules.Add(new("SAME_STATE",coils.Select(n=>new PipelineSignal(aliases[n],"Member")).ToArray(),
                    "普通非保持线圈接同一驱动线；须结合全块其它写入和执行时刻复核。",[origin.Id],[],"ProgramRelation"));
            }
        }
        return rules.DistinctBy(BusinessCandidateValidator.Key).ToArray();
    }
}
