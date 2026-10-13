#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public enum BusinessMemoryRelation { Unmapped, Disjoint, Overlap, Unknown }
public sealed record BusinessMemoryRange(string Area,long StartBit,long BitLength);
public sealed record BusinessMemoryLocation(string Status,string Reason,BusinessMemoryRange? Range=null);

/// <summary>Known Siemens address ranges only. Unmapped symbols retain their source identities; declared
/// but unsupported addresses/types remain Unknown. This does not infer device relationships from names.</summary>
public sealed class BusinessMemoryAliases
{
    public const string Version="business-memory-aliases-1";
    private static readonly ConditionalWeakTable<PipelineSnapshot,BusinessMemoryAliases> Contexts=new();
    private readonly Dictionary<string,PipelineTag[]> tags;
    private readonly ConcurrentDictionary<string,BusinessMemoryLocation> locations=new(StringComparer.OrdinalIgnoreCase);
    public BusinessMemoryAliases(IEnumerable<PipelineTag> tags) => this.tags=tags.GroupBy(t=>t.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
    public static BusinessMemoryAliases For(PipelineSnapshot source)=>Contexts.GetValue(source,s=>new(s.Tags));
    public BusinessMemoryLocation Resolve(string symbol)=>locations.GetOrAdd(symbol,ResolveCore);
    public static bool SymbolicallyOverlaps(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+".",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+".",StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+"[",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+"[",StringComparison.OrdinalIgnoreCase);
    public BusinessMemoryRelation Compare(string left,string right) {
        if(SymbolicallyOverlaps(left,right))return BusinessMemoryRelation.Overlap;
        var a=Resolve(left);var b=Resolve(right);
        // Absence of IOTag metadata is not an invented address; source-name/instance analysis remains in force.
        if(a.Status=="Unmapped"||b.Status=="Unmapped")return BusinessMemoryRelation.Unmapped;
        if(a.Range is { } ar&&b.Range is { } br) {
            if(ar.Area!=br.Area)return BusinessMemoryRelation.Disjoint;
            if(ar.StartBit==br.StartBit)return BusinessMemoryRelation.Overlap;
            if(a.Status=="Known"&&b.Status=="Known")return ar.StartBit<br.StartBit+br.BitLength&&br.StartBit<ar.StartBit+ar.BitLength?BusinessMemoryRelation.Overlap:BusinessMemoryRelation.Disjoint;
            // A known range ending before the unknown extent starts is still definitely disjoint.
            if(a.Status=="Known"&&ar.StartBit+ar.BitLength<=br.StartBit||b.Status=="Known"&&br.StartBit+br.BitLength<=ar.StartBit)return BusinessMemoryRelation.Disjoint;
        }
        return BusinessMemoryRelation.Unknown;
    }
    public bool MayOverlap(string left,string right)=>Compare(left,right) is BusinessMemoryRelation.Overlap or BusinessMemoryRelation.Unknown;

    private BusinessMemoryLocation ResolveCore(string original) {
        if(original.StartsWith("L:",StringComparison.Ordinal))return new("Unmapped","LocalRequiresInstanceBinding");
        var symbol=original.StartsWith("G:",StringComparison.Ordinal)?original[2..]:original;
        if(tags.TryGetValue(symbol,out var matches)) {
            var resolved=matches.Select(t=>ParseAddress(t.Address,t.DataType)).Distinct().ToArray();
            return resolved.Length==1?resolved[0]:new("Unknown","AmbiguousTagAddress");
        }
        var slice=Regex.Match(symbol,@"^(?<base>.+)\.%[Xx](?<bit>\d+)$",RegexOptions.CultureInvariant);
        if(slice.Success) {
            var parent=Resolve(slice.Groups["base"].Value);
            if(parent.Status=="Unmapped")return parent;
            if(parent.Status!="Known"||parent.Range is null||!long.TryParse(slice.Groups["bit"].Value,out var bit)||bit>=parent.Range.BitLength)
                return new("Unknown","UnresolvedOrOutOfRangeBitSlice");
            // Slice indices denote value bits. Multi-byte physical placement also needs a byte-order /
            // layout contract; do not silently apply little-endian arithmetic to a Siemens WORD.
            if(parent.Range.BitLength>8)return new("Unknown","MultiByteSliceLayoutNotProven");
            return new("Known","BitSlice",parent.Range with{StartBit=parent.Range.StartBit+bit,BitLength=1});
        }
        // Bare strings that merely resemble an address may be symbolic identifiers. Only an explicit
        // percent address or the IOTag.Address field establishes an address, never identifier spelling.
        if(symbol.StartsWith('%'))return ParseAddress(symbol);
        if(tags.Keys.Any(name=>symbol.StartsWith(name+".",StringComparison.OrdinalIgnoreCase)||symbol.StartsWith(name+"[",StringComparison.OrdinalIgnoreCase)))
            return new("Unknown","UnresolvedFieldLayout");
        return new("Unmapped","NoAddressMetadata");
    }
    public static BusinessMemoryLocation ParseAddress(string address,string? dataType=null) {
        if(string.IsNullOrWhiteSpace(address))return new("Unknown","MissingAddress");
        var text=address.Trim().ToUpperInvariant();if(text.StartsWith('%'))text=text[1..];
        var bit=Regex.Match(text,@"^(?<area>[IQM])X?(?<byte>\d+)\.(?<bit>[0-7])$",RegexOptions.CultureInvariant);
        var scalar=Regex.Match(text,@"^(?<area>[IQM])(?<width>[BWD])(?<byte>\d+)$",RegexOptions.CultureInvariant);
        var db=Regex.Match(text,@"^DB(?<db>\d+)\.DB(?<width>[XBWD])(?<byte>\d+)(?:\.(?<bit>[0-7]))?$",RegexOptions.CultureInvariant);
        string area;long width;string bytes;long bitOffset=0;
        if(bit.Success) {area=bit.Groups["area"].Value;width=1;bytes=bit.Groups["byte"].Value;bitOffset=long.Parse(bit.Groups["bit"].Value,CultureInfo.InvariantCulture);}
        else if(scalar.Success) {area=scalar.Groups["area"].Value;width=Width(scalar.Groups["width"].Value);bytes=scalar.Groups["byte"].Value;}
        else if(db.Success&&long.TryParse(db.Groups["db"].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var dbNumber)&&dbNumber>0) {
            area="DB"+dbNumber;var code=db.Groups["width"].Value;
            if((code=="X")!=db.Groups["bit"].Success)return new("Unknown","InvalidDbAddress");
            width=Width(code);bytes=db.Groups["byte"].Value;bitOffset=db.Groups["bit"].Success?long.Parse(db.Groups["bit"].Value,CultureInfo.InvariantCulture):0;
        } else return new("Unknown","UnsupportedAddressFormat");
        if(!long.TryParse(bytes,NumberStyles.None,CultureInfo.InvariantCulture,out var byteOffset)||byteOffset>(long.MaxValue-128)/8)return new("Unknown","AddressOverflow");
        var range=new BusinessMemoryRange(area,byteOffset*8+bitOffset,width);
        if(dataType is not null) {
            var typeWidth=dataType.Trim().ToUpperInvariant() switch {"BOOL"=>1,"BYTE" or "SINT" or "USINT" or "CHAR"=>8,"WORD" or "INT" or "UINT" or "WCHAR"=>16,"DWORD" or "DINT" or "UDINT" or "REAL" or "TIME"=>32,"LWORD" or "LINT" or "ULINT" or "LREAL" or "LTIME"=>64,_=>0};
            if(typeWidth==0)return new("Unknown","UnknownDataTypeExtent",range with{BitLength=0});
            if(typeWidth!=width)return new("Unknown","AddressDataTypeWidthMismatch",range with{BitLength=0});
        }
        return new("Known","ExplicitAddressRange",range);
    }
    private static int Width(string code)=>code switch {"X"=>1,"B"=>8,"W"=>16,"D"=>32,_=>0};
}
