#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Model approval cannot bypass source contradictions or unresolved binding.</summary>
public interface IBusinessAcceptancePolicy
{
    BusinessDecision Validate(PipelineSnapshot source,PipelineUnit unit,BusinessDescription description,
        BusinessCandidate candidate,BusinessTemplateCandidateResult? templates);
}

public sealed class BusinessAcceptancePolicy:IBusinessAcceptancePolicy
{
    public const string Version="source-acceptance-1";
    public BusinessDecision Validate(PipelineSnapshot source,PipelineUnit unit,BusinessDescription description,
        BusinessCandidate candidate,BusinessTemplateCandidateResult? templates) {
        var shape=BusinessCandidateValidator.Validate(description,candidate);
        if(shape.Status!="Validated")return shape;
        var semantic=BusinessSourceSemanticGate.Validate(source,unit,description,candidate);
        if(semantic.Status=="Rejected")return semantic;
        if(semantic.Status=="Validated")return new(candidate,"Accepted","CodeProven:"+Version+":AfterLogicUnit");
        if(semantic.Code=="SourceObservationPointNotEstablished") {
            // Preserve the actual point proved by the compiler instead of presenting a local
            // equation as a relation valid at arbitrary stable scan-end observations.
            var local=candidate with{Applicability="ProgramRelation",SettlingTimeMs=null,SettlingTimeSignal=null,SettlingTimeScale=1};
            var localProof=BusinessSourceSemanticGate.Validate(source,unit,description,local);
            if(localProof.Status=="Validated")return new(local,"Accepted","CodeProven:"+Version+":ScopeRestrictedToAfterLogicUnit");
        }
        var binding=BusinessCallAcceptanceGate.Validate(source,unit,description,candidate,templates);
        if(binding.Status=="Validated"&&binding.Code=="CurrentCodeTemplateBindingValidated")
            return new(candidate,"Accepted","CodeProven:CurrentCodeTemplateBindingValidated");
        if(binding.Status is "Validated" or "Rejected")return binding;
        return new(candidate,"Rejected","SemanticProofUnavailable:"+semantic.Code+"|"+binding.Code);
    }
    public static BusinessCandidate? ToAliases(BusinessDescription description,BusinessCandidate bound) {
        var aliases=description.SignalAliases.GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase)
            .Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().Key,StringComparer.OrdinalIgnoreCase);
        try {
            LogicTerm? Map(LogicTerm? term)=>term is null?null:PipelineTerms.Rewrite(term,t=>t.Kind is "Signal" or "ScalarSignal"?t with{Name=aliases[t.Name!]}:null);
            return bound with{Signals=bound.Signals.Select(s=>s with{Name=aliases[s.Name]}).ToArray(),
                Conditions=bound.Conditions.Select(c=>c with{Signal=aliases[c.Signal]}).ToArray(),
                SettlingTimeSignal=bound.SettlingTimeSignal is null?null:aliases[bound.SettlingTimeSignal],
                Assertion=Map(bound.Assertion),ApplicabilityCondition=Map(bound.ApplicabilityCondition)};
        }catch(KeyNotFoundException){return null;}
    }
}
