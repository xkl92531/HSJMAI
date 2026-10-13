#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

public interface IPipelineLogicCompiler
{
    IReadOnlyList<PipelineAssignment> Compile();
    List<PipelineCallEvidence> CallEvidence {get;}
    List<PipelineTimer> Timers {get;}
}

/// <summary>Typed Boolean SCL assignments and IF/ELSIF/ELSE with sequential state substitution.</summary>
public sealed class PipelineStructuredTextCompiler : IPipelineLogicCompiler
{
    sealed record Lexeme(string Text,LogicTerm? Term=null);
    readonly List<Lexeme> tokens=[];
    readonly HashSet<string> boolNames;
    int position;
    public List<PipelineCallEvidence> CallEvidence {get;}=[];
    public List<PipelineTimer> Timers {get;}=[];
    public PipelineStructuredTextCompiler(XDocument xml,PipelineUnit unit,IReadOnlyList<PipelineTag> tags) {
        boolNames=tags.Where(t=>string.Equals(t.DataType,"Bool",StringComparison.OrdinalIgnoreCase)).Select(t=>t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!string.IsNullOrWhiteSpace(unit.InterfaceXml)) {
            var face=XElement.Parse(unit.InterfaceXml);
            foreach(var member in face.Descendants().Where(e=>e.Name.LocalName=="Member"&&(string?)e.Attribute("Datatype")=="Bool")) {
                var names=member.AncestorsAndSelf().Where(e=>e.Name.LocalName=="Member").Reverse().Select(e=>(string?)e.Attribute("Name"));
                boolNames.Add("#"+string.Join(".",names));
            }
        }
        foreach(var element in xml.Root!.Elements()) {
            switch(element.Name.LocalName) {
                case "Blank":case "NewLine":case "LineComment":case "BlockComment":break;
                case "Token":tokens.Add(new(((string?)element.Attribute("Text")??"").ToUpperInvariant()));break;
                case "Access":tokens.Add(new("$",Access(element)));break;
                // REGION labels and arbitrary Text are not executable expressions; reject them until structurally parsed.
                default:throw new PipelineException("UnsupportedSclToken");
            }
        }
        if(tokens.Count>10000)throw new PipelineException("SclTokenBudgetExceeded");
    }
    LogicTerm Access(XElement access) {
        if((string?)access.Attribute("Scope")=="LiteralConstant") {
            var value=access.Descendants().SingleOrDefault(e=>e.Name.LocalName=="ConstantValue")?.Value.ToLowerInvariant();
            if(value is "true" or "false")return new("Constant",value);
            throw new PipelineException("UnsupportedSclValueType");
        }
        var scope=(string?)access.Attribute("Scope");
        if(scope is not("LocalVariable" or "GlobalVariable"))throw new PipelineException("UnsupportedSclAccess");
        var symbol=access.Elements().SingleOrDefault(e=>e.Name.LocalName=="Symbol")??throw new PipelineException("MissingSymbol");
        if(symbol.Elements().Any(e=>e.Name.LocalName!="Component"&&!(e.Name.LocalName=="Token"&&(string?)e.Attribute("Text")==".")))throw new PipelineException("UnsupportedSymbolBinding");
        var components=symbol.Elements().Where(e=>e.Name.LocalName=="Component").ToArray();
        if(components.Length==0||components.Any(c=>string.IsNullOrWhiteSpace((string?)c.Attribute("Name"))||((string)c.Attribute("Name")!).IndexOfAny(['.','#','[',']'])>=0))throw new PipelineException("UnsupportedSymbolBinding");
        var name=(scope=="LocalVariable"?"#":"")+string.Join(".",components.Select(PipelineFlgNetCompiler.ComponentName));
        if(!boolNames.Contains(name))throw new PipelineException("SclBooleanTypeNotEstablished");
        return LogicTerm.Signal(name);
    }
    string Peek=>position<tokens.Count?tokens[position].Text:"EOF";
    bool Take(string text) {if(Peek!=text)return false;position++;return true;}
    void Expect(string text) {if(!Take(text))throw new PipelineException("UnsupportedSclSyntax");}
    static LogicTerm And(LogicTerm a,LogicTerm b)=>new("And",null,[a,b]);
    static LogicTerm Or(LogicTerm a,LogicTerm b)=>new("Or",null,[a,b]);
    static LogicTerm Not(LogicTerm a)=>new("Not",null,[a]);
    static LogicTerm Select(LogicTerm condition,LogicTerm yes,LogicTerm no)=>Or(And(condition,yes),And(Not(condition),no));
    public IReadOnlyList<PipelineAssignment> Compile() {
        var state=new Dictionary<string,LogicTerm>(StringComparer.OrdinalIgnoreCase);
        Statements(state,0);Expect("EOF");
        var targets=state.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return state.Select(p=>new PipelineAssignment(p.Key,PipelineTerms.Rewrite(p.Value,t=>t.Kind=="Signal"&&targets.Contains(t.Name!)?new("Previous",t.Name):null))).ToArray();
    }
    void Statements(Dictionary<string,LogicTerm> state,int depth) {
        if(depth>32)throw new PipelineException("SclNestingLimit");
        while(Peek is not("EOF" or "ELSE" or "ELSIF" or "END_IF")) {
            if(Take(";"))continue;
            if(Take("IF")){If(state,depth+1);Expect("END_IF");Take(";");continue;}
            if(Peek!="$"||tokens[position].Term?.Kind!="Signal")throw new PipelineException("UnsupportedSclStatement");
            var target=tokens[position++].Term!.Name!;Expect(":=");var value=Expression(state,0);Expect(";");state[target]=value;
        }
    }
    void If(Dictionary<string,LogicTerm> state,int depth) {
        if(depth>32)throw new PipelineException("SclNestingLimit");
        var condition=Expression(state,0);Expect("THEN");
        var yes=new Dictionary<string,LogicTerm>(state,StringComparer.OrdinalIgnoreCase);Statements(yes,depth);
        var no=new Dictionary<string,LogicTerm>(state,StringComparer.OrdinalIgnoreCase);
        if(Take("ELSIF"))If(no,depth+1);else if(Take("ELSE"))Statements(no,depth);
        foreach(var key in yes.Keys.Union(no.Keys,StringComparer.OrdinalIgnoreCase))
            state[key]=Select(condition,yes.GetValueOrDefault(key,LogicTerm.Signal(key)),no.GetValueOrDefault(key,LogicTerm.Signal(key)));
    }
    LogicTerm Expression(Dictionary<string,LogicTerm> state,int minimum,int depth=0) {
        if(depth>128)throw new PipelineException("SclExpressionLimit");
        LogicTerm left;
        if(Take("NOT"))left=Not(Expression(state,4,depth+1));
        else if(Take("(")){left=Expression(state,0,depth+1);Expect(")");}
        else if(Take("TRUE"))left=new("Constant","true");
        else if(Take("FALSE"))left=new("Constant","false");
        else if(Peek=="$") {var term=tokens[position++].Term!;left=term.Kind=="Signal"?state.GetValueOrDefault(term.Name!,term):term;}
        else throw new PipelineException("UnsupportedSclExpression");
        while(true) {
            var op=Peek;var precedence=op switch{"OR"=>1,"XOR"=>2,"AND"=>3,_=>-1};
            if(precedence<minimum)break;position++;
            var right=Expression(state,precedence+1,depth+1);
            left=op switch{"AND"=>And(left,right),"OR"=>Or(left,right),_=>Or(And(left,Not(right)),And(Not(left),right))};
        }
        return left;
    }
}
