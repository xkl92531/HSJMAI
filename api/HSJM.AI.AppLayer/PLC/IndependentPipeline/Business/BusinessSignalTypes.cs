#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Source types, never classifications inferred from variable names.</summary>
public static class BusinessSignalTypes
{
    public static string Normalize(string? type) => type?.Trim().ToUpperInvariant() switch {
        null or "" or "UNKNOWN" => "Unknown", "CONFLICT" => "Conflict",
        "BOOL" or "BOOLEAN" => "Bool", "BYTE" => "Byte", "WORD" => "Word", "DWORD" => "DWord", "LWORD" => "LWord",
        "SINT" => "SInt", "USINT" => "USInt", "INT" => "Int", "UINT" => "UInt", "DINT" => "DInt", "UDINT" => "UDInt",
        "LINT" => "LInt", "ULINT" => "ULInt", "REAL" => "Real", "LREAL" => "LReal", "TIME" => "Time", "LTIME" => "LTime",
        _ => type!.Trim()
    };
    public static string Merge(string? existing, string? incoming) {
        var old=Normalize(existing); var next=Normalize(incoming);
        if(old=="Conflict"||next=="Conflict")return "Conflict";
        if(next=="Unknown")return old;
        return old=="Unknown"||old.Equals(next,StringComparison.OrdinalIgnoreCase)?next:"Conflict";
    }
    public static string? SliceType(string path) {
        var match=Regex.Match(path,@"\.%([XBWD])[0-9]+$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        return match.Success?match.Groups[1].Value.ToUpperInvariant() switch {"X"=>"Bool","B"=>"Byte","W"=>"Word","D"=>"DWord",_=>null}:null;
    }
    public static string WithoutSlice(string path)=>Regex.Replace(path,@"\.%[XBWD][0-9]+$","",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static bool IsNumeric(string type)=>Normalize(type) is "SInt" or "USInt" or "Int" or "UInt" or "DInt" or "UDInt" or "LInt" or "ULInt" or "Real" or "LReal" or "Byte" or "Word" or "DWord" or "LWord";
    public static string Get(BusinessDescription description,string alias)=>description.SignalTypes.TryGetValue(alias,out var type)?Normalize(type):"Unknown";
}
