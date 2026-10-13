#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;
public static class PipelineNumeric
{
    public static string NormalizeType(string type)=>type.ToUpperInvariant() switch {
        "REAL"=>"Real","LREAL"=>"LReal","INT"=>"Int","DINT"=>"DInt","UINT"=>"UInt","UDINT"=>"UDInt","SINT"=>"SInt","USINT"=>"USInt",
        "BYTE"=>"Byte","WORD"=>"Word","DWORD"=>"DWord",_=>type
    };
    public static bool Compare(LogicTerm term,IReadOnlyDictionary<string,double?> values) {
        if(term.Kind!="Compare"||term.Children?.Count!=2)throw new PipelineException("InvalidComparison");
        double Read(LogicTerm value) {
            double number;
            if(value.Kind=="Number") {
                if(string.Equals(value.DataType??term.DataType,"Real",StringComparison.OrdinalIgnoreCase)) {
                    if(!float.TryParse(value.Name,NumberStyles.Float,CultureInfo.InvariantCulture,out var single))throw new PipelineException("InvalidNumericConstant");
                    number=single;
                }
                else if(!double.TryParse(value.Name,NumberStyles.Float,CultureInfo.InvariantCulture,out number))throw new PipelineException("InvalidNumericConstant");
            }
            else if(value.Kind=="ScalarSignal") {
                if(!values.TryGetValue(value.Name!,out var observed)||!observed.HasValue)throw new PipelineException("MissingNumericSignal");
                number=observed.Value;
            }
            else throw new PipelineException("UnsupportedNumericExpression");
            var typed=Coerce(number,value.DataType ?? term.DataType ?? "");
            return Coerce(typed,term.DataType ?? value.DataType ?? "");
        }
        var left=Read(term.Children[0]);var right=Read(term.Children[1]);
        return term.Name switch {"Eq"=>left==right,"Ne"=>left!=right,"Gt"=>left>right,"Ge"=>left>=right,"Lt"=>left<right,"Le"=>left<=right,_=>throw new PipelineException("UnsupportedComparison")};
    }
    public static double Coerce(double value,string type) {
        type=NormalizeType(type);
        if(!double.IsFinite(value))throw new PipelineException("NonFiniteNumericInput");
        if(type=="LReal")return value;
        if(type=="Real") {var rounded=(float)value;if(!float.IsFinite(rounded))throw new PipelineException("NumericOutOfRange");return rounded;}
        var range=type switch {
            "SInt"=>(-128d,127d),"USInt"=>(0d,255d),"Int"=>(-32768d,32767d),"UInt"=>(0d,65535d),
            "DInt"=>(-2147483648d,2147483647d),"UDInt"=>(0d,4294967295d),
            // Unsigned bit strings are exact numeric operands within these widths, never Bool.
            // Do not extend this to 64-bit types: double cannot represent every 64-bit integer.
            "Byte"=>(0d,255d),"Word"=>(0d,65535d),"DWord"=>(0d,4294967295d),_=>throw new PipelineException("UnsupportedNumericType")
        };
        if(value<range.Item1||value>range.Item2||Math.Truncate(value)!=value)throw new PipelineException("NumericOutOfRange");
        return value;
    }
}
