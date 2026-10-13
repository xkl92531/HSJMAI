#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessCoverage(string Status,IReadOnlyList<string> Unresolved,int ProvenRules);
public sealed record BusinessDeterministicPlan(IReadOnlyList<BusinessCandidate> Rules,BusinessCoverage Coverage)
{
    public bool RequiresAi=>Coverage.Status is "NeedsAi" or "Partial";
    public bool MissingSource=>Coverage.Status=="BlockedMissingSource";
}

/// <summary>Complete ordinary Boolean assignment relations at AfterLogicUnit only. This is not
/// a proof of mechanical expectations or arbitrary scan-end snapshots. Unsupported effects remain explicit.</summary>
public static class BusinessDeterministicAnalysis
{
    public const string Version="business-deterministic-1";
    public static BusinessDeterministicPlan Analyze(PipelineSnapshot source,PipelineUnit unit,BusinessDescription description) {
        BusinessDeterministicPlan Need(string reason)=>new([],new("NeedsAi",[reason],0));
        if(description.Status!="Rendered")return new([],new("BlockedMissingSource",[description.Status],0));
        try {
            // A short-lived compiler owns its XML; do not retain another full-project tree cache.
            var context=new PipelineCompilationContext(source);var xml=context.Xml(unit);
            if(xml.Root!.Name.LocalName!="FlgNet")return Need("UnsupportedDeterministicLanguage:"+unit.Language);
            var calls=xml.Descendants().Where(e=>e.Name.LocalName=="Call").ToArray();
            var parts=xml.Descendants().Where(e=>e.Name.LocalName=="Part").ToArray();
            if(calls.Length>0) {
                var missing=calls.SelectMany(c=>c.Descendants().Where(e=>e.Name.LocalName=="CallInfo"))
                    .Select(i=>(string?)i.Attribute("Name")??"")
                    .Any(name=>!source.Units.Any(u=>u.BlockName.Equals(name,StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(u.RawContent)));
                if(missing&&parts.Length==0)return new([],new("BlockedMissingSource",["MissingRequiredSource"],0));
                return Need(missing?"MissingCalleeBody:AnalyzeIndependentCallerLogicOnly":"CallSemanticsAndBindingRequireAnalysis");
            }
            var unsupported=parts.Select(p=>(string?)p.Attribute("Name")??"")
                .Where(kind=>kind is not("Contact" or "Coil" or "SCoil" or "RCoil" or "A" or "O" or "Not")).Distinct().ToArray();
            if(unsupported.Length>0)return Need("UnsupportedDeterministicInstructions:"+string.Join(",",unsupported));
            var compiler=context.Compiler(unit);var assignments=compiler.Compile();
            if(assignments.Count==0)return Need("NoCompleteObservableAssignments");
            var nodes=assignments.SelectMany(a=>PipelineTerms.Walk(a.Expression)).ToArray();
            var outputs=assignments.Select(a=>a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(nodes.Any(n=>n.Kind=="Signal"&&outputs.Contains(n.Name!)))return Need("OutputReadEpochRequiresAnalysis");
            var names=outputs.Concat(nodes.Where(n=>n.Kind=="Signal").Select(n=>n.Name!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var aliases=description.SignalAliases.GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase)
                .Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().Key,StringComparer.OrdinalIgnoreCase);
            var memory=BusinessMemoryAliases.For(source);
            foreach(var name in names) {
                var tags=source.Tags.Where(t=>t.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(!aliases.ContainsKey(name)||tags.Length==0||tags.Any(t=>!t.DataType.Equals("Bool",StringComparison.OrdinalIgnoreCase)))return Need("ObservableBindingRequiresAnalysis");
                if(memory.Resolve(name).Status!="Known")return Need("UnknownMemoryAlias");
            }
            for(var i=0;i<names.Length;i++)for(var j=0;j<i;j++)
                if(memory.Compare(names[i],names[j])!=BusinessMemoryRelation.Disjoint)return Need("PhysicalAliasRequiresAnalysis");
            var evidence=description.Evidence.Where(e=>e.Kind=="SourceStructure"&&e.Text.Contains("[source LU:"+unit.Id+" ",StringComparison.Ordinal)).Select(e=>e.Id).ToArray();
            if(evidence.Length!=1)return Need("CompleteSourceEvidenceRequired");
            LogicTerm Alias(LogicTerm term)=>PipelineTerms.Rewrite(term,t=>t.Kind=="Signal"?LogicTerm.Signal(aliases[t.Name!]):null);
            var values=assignments.ToDictionary(a=>aliases[a.Target],a=>Alias(a.Expression),StringComparer.Ordinal);
            var rules=new List<BusinessCandidate>();var unresolved=new List<string>();
            foreach(var assignment in assignments) {
                if(PipelineTerms.Walk(assignment.Expression).Any(n=>n.Kind is not("Signal" or "Constant" or "Not" or "And" or "Or"))) {
                    unresolved.Add("StateOrNumericSemanticsRequireAnalysis:Output:"+aliases[assignment.Target]);continue;
                }
                var target=LogicTerm.Signal(aliases[assignment.Target]);var expression=values[target.Name!];
                var assertion=new LogicTerm("Or",null,[new("And",null,[target,expression]),new("And",null,[new("Not",null,[target]),new("Not",null,[expression])])]);
                // Check the exact equality under the compiled assignments using the Boolean verifier.
                var substituted=PipelineTerms.Rewrite(assertion,t=>t.Kind=="Signal"&&values.TryGetValue(t.Name!,out var value)?value:null);
                const string probe="\u001fdeterministic-proof";
                var proof=new PipelineDescription(unit.Id,"","",names.Select(n=>aliases[n]).Append(probe).ToArray(),[new(probe,new("Not",null,[substituted]))],[],"{}");
                var validation=PipelineRuleVerifier.Validate(new("MUST_BE_FALSE",[new(probe,"Target")],""),proof);
                if(!validation.Success)return Need("ProofFailed:"+validation.ErrorCode);
                var used=PipelineTerms.Walk(assertion).Where(t=>t.Kind=="Signal").Select(t=>t.Name!).Distinct().Select(n=>new PipelineSignal(n,"Member")).ToArray();
                var rule=new BusinessCandidate("EXPRESSION",used,"完整普通布尔赋值等式；仅在本逻辑单元执行后、读取一致的信号快照时适用。",evidence,[],"ProgramRelation",Assertion:assertion);
                if(BusinessCandidateValidator.Validate(description,rule).Status!="Validated")return Need("DerivedRuleValidationFailed");
                rules.Add(rule);
            }
            return new(rules,new(unresolved.Count==0?"CodeComplete":rules.Count>0?"Partial":"NeedsAi",unresolved,rules.Count));
        }
        catch(PipelineException ex){return Need(ex.Code);}
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException){return Need("AmbiguousSourceStructure");}
    }
    public static BusinessDescription ResidualInput(BusinessDescription description,BusinessDeterministicPlan plan,IReadOnlyList<BusinessCandidate> known)=>description with {
        ModelText=description.ModelText+"\nUnresolved analysis tasks (data):\n"+JsonSerializer.Serialize(plan.Coverage.Unresolved)+
            "\nAlready proven relations (data; do not regenerate or weaken their scope):\n"+JsonSerializer.Serialize(known)+
            "\nOnly propose remaining source-supported relations. Retained code is dependency context, not a request to regenerate solved relations. Never invent missing callee bodies."
    };
}
