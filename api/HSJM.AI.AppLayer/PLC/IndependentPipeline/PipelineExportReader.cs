#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

public sealed record PipelineImportBlock(long Id,string Name,string Kind,string Language,string InterfaceXml,
    string SourceFile,string SourceHash,IReadOnlyList<PipelineUnit> Units);

/// <summary>Independent Openness import for source snapshots; never updates the original project/block tables.</summary>
public static class PipelineExportReader
{
    public static long StableId(string text) {
        var bytes=SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes)&0x3fffffffffffffffUL)+1;
    }
    /// <summary>全局 DB 导出（SW.Blocks.GlobalDB）的块名与接口 XML；不是全局 DB 或没有接口时返回 false。不抛异常。</summary>
    public static bool TryReadGlobalDbInterface(string text,out string name,out string interfaceXml) {
        name=""; interfaceXml="";
        try {
            using var reader=XmlReader.Create(new StringReader(text),new XmlReaderSettings {
                DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000
            });
            var doc=XDocument.Load(reader);
            var block=doc.Root?.Elements().SingleOrDefault(e=>e.Name.LocalName=="SW.Blocks.GlobalDB");
            var attributes=block?.Elements().SingleOrDefault(e=>e.Name.LocalName=="AttributeList");
            name=attributes?.Elements().SingleOrDefault(e=>e.Name.LocalName=="Name")?.Value.Trim() ?? "";
            interfaceXml=attributes?.Elements().SingleOrDefault(e=>e.Name.LocalName=="Interface")?.ToString(SaveOptions.DisableFormatting) ?? "";
            return name.Length>0 && interfaceXml.Length>0;
        } catch(Exception ex) when(ex is XmlException or InvalidOperationException) { name=""; interfaceXml=""; return false; }
    }
    public static PipelineImportBlock Read(string text,long projectId,string file) {
        using var reader=XmlReader.Create(new StringReader(text),new XmlReaderSettings {
            DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000
        });
        var doc=XDocument.Load(reader);
        var block=doc.Root?.Elements().SingleOrDefault(e=>e.Name.LocalName is "SW.Blocks.FB" or "SW.Blocks.FC" or "SW.Blocks.OB")
            ?? throw new PipelineException("UnsupportedExportBlock");
        var attributes=block.Elements().SingleOrDefault(e=>e.Name.LocalName=="AttributeList") ?? throw new PipelineException("MissingBlockAttributes");
        string Field(string name)=>attributes.Elements().SingleOrDefault(e=>e.Name.LocalName==name)?.Value ?? "";
        var name=Field("Name"); if(string.IsNullOrWhiteSpace(name))throw new PipelineException("MissingBlockName");
        var kind=block.Name.LocalName["SW.Blocks.".Length..]; var language=Field("ProgrammingLanguage");
        var contract=attributes.Elements().SingleOrDefault(e=>e.Name.LocalName=="Interface")?.ToString(SaveOptions.DisableFormatting) ?? "";
        var id=StableId($"{projectId}:{kind}:{name}"); var units=new List<PipelineUnit>(); int order=0; int compileUnitOrder=0;
        foreach(var compileUnit in block.Descendants().Where(e=>e.Name.LocalName=="SW.Blocks.CompileUnit")) {
            // 与上传解析器（parser.py _extract_logic_units）同口径：每个 CompileUnit 计 1，空网络也计 → 等于库 networkid
            compileUnitOrder++;
            var network=compileUnit.Descendants().FirstOrDefault(e=>e.Name.LocalName=="NetworkSource");
            if(network==null)continue;
            foreach(var root in network.Elements()) {
                order++;
                var calls=root.Descendants().Where(e=>e.Name.LocalName=="CallInfo").Select(e=>(string?)e.Attribute("Name"))
                    .Where(n=>!string.IsNullOrWhiteSpace(n)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                units.Add(new(id,name,StableId($"{id}:{order}:{root.Name.LocalName}"),language,root.ToString(SaveOptions.DisableFormatting),"",calls,
                    order,contract,kind,file,root.Name.LocalName) { CompileUnitOrder=compileUnitOrder });
            }
        }
        if(units.Count==0)units.Add(new(id,name,StableId($"{id}:missing-body"),language,"","",[],0,contract,kind,file,"MissingBody"));
        // 组织块的事件类型（ProgramCycle / Startup / HardwareInterrupt …）：只有程序循环 OB 每个扫描都执行（ConstantBits）
        if(kind=="OB"&&Field("SecondaryType").Trim() is {Length:>0} obType)
            for(var i=0;i<units.Count;i++) units[i]=units[i] with { OrganizationType=obType };
        return new(id,name,kind,language,contract,file,PipelineHash.Of(text),units);
    }
    public static string PublicInterfaceFingerprint(PipelineImportBlock block) {
        if(string.IsNullOrWhiteSpace(block.InterfaceXml))return "";
        var contract=XElement.Parse(block.InterfaceXml);
        var declarations=contract.Descendants().Where(e=>e.Name.LocalName=="Section" && (string?)e.Attribute("Name") is "Input" or "Output" or "InOut" or "Return")
            .SelectMany(section=>section.Descendants().Where(e=>e.Name.LocalName=="Member").Select(m=>
                $"{(string?)section.Attribute("Name")}|{string.Join("/",m.AncestorsAndSelf().TakeWhile(a=>a!=section).Reverse().Select(a=>(string?)a.Attribute("Name")))}|{(string?)m.Attribute("Datatype")}"))
            .OrderBy(v=>v,StringComparer.Ordinal);
        return PipelineHash.Of(string.Join("\n",declarations));
    }
}
