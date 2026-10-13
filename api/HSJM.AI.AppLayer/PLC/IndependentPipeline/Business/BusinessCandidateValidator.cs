#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Evidence/reference and applicability checks. This is not a claim of universal PLC proof.</summary>
public static class BusinessCandidateValidator
{
    public static BusinessDecision Validate(BusinessDescription description,BusinessCandidate candidate) {
        BusinessDecision Reject(string code)=>new(candidate,"Rejected",code);
        if(candidate.Signals is null || candidate.Signals.Count==0 || candidate.Signals.Count>64 ||
            candidate.Signals.Any(s=>s is null||string.IsNullOrWhiteSpace(s.Name)||!description.SignalAliases.ContainsKey(s.Name)))return Reject("UnknownSignalAlias");
        if(candidate.Signals.Select(s=>s.Name).Distinct(StringComparer.Ordinal).Count()!=candidate.Signals.Count)return Reject("DuplicateSignal");
        var source=candidate.Signals.Count(s=>s.Role=="Source");var target=candidate.Signals.Count(s=>s.Role=="Target");var member=candidate.Signals.Count(s=>s.Role=="Member");
        var valid=candidate.Operator switch {
            "AT_MOST_ONE" or "EXACTLY_ONE" or "SAME_STATE"=>member>=2&&member==candidate.Signals.Count,
            "IMPLIES" or "REQUIRES_0"=>source==1&&target>=1&&source+target==candidate.Signals.Count,
            "ALL_TRUE"=>source==1&&member>=1&&source+member==candidate.Signals.Count,
            "MUST_BE_FALSE"=>target==1&&candidate.Signals.Count==1,
            "EXPRESSION"=>member>=1&&member==candidate.Signals.Count&&candidate.Assertion is not null,
            _=>false
        };
        if(!valid)return Reject("UnsupportedOperatorOrRoles");
        // Empty metadata is retained only for legacy synthetic descriptions. New renders publish
        // every alias, including Unknown; absence in a partially typed description is not Bool.
        var typed=description.SignalTypes.Count>0;
        bool IsBool(string alias)=>!typed||BusinessSignalTypes.Get(description,alias)=="Bool";
        if(candidate.Operator!="EXPRESSION"&&candidate.Signals.Any(s=>!IsBool(s.Name)))return Reject("BooleanSignalTypeRequired");
        if(candidate.Operator!="EXPRESSION"&&candidate.Assertion is not null)return Reject("UnexpectedAssertion");
        if(candidate.EvidenceIds is null||candidate.EvidenceIds.Count==0)return Reject("CodeEvidenceRequired");
        var evidence=description.Evidence.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        if(candidate.EvidenceIds.Any(id=>!evidence.Contains(id)))return Reject("UnknownEvidenceReference");
        if(string.IsNullOrWhiteSpace(candidate.Explanation))return Reject("ExplanationRequired");
        if(candidate.Conditions is null||candidate.Conditions.Any(c=>c is null||string.IsNullOrWhiteSpace(c.Signal)||!description.SignalAliases.ContainsKey(c.Signal)))return Reject("UnknownConditionAlias");
        if(candidate.Conditions.Any(c=>!IsBool(c.Signal)))return Reject("BooleanConditionTypeRequired");
        if(candidate.Conditions.GroupBy(c=>c.Signal,StringComparer.Ordinal).Any(g=>g.Select(c=>c.Value).Distinct().Count()>1))return Reject("ContradictoryConditions");
        if(candidate.Applicability is not("ProgramRelation" or "NormalOperation" or "AfterSettling" or "StableDiagnosticSnapshot"))return Reject("ExplicitApplicabilityRequired");
        if(candidate.SettlingTimeMs<0)return Reject("InvalidSettlingTime");
        if(!double.IsFinite(candidate.SettlingTimeScale)||candidate.SettlingTimeScale<=0)return Reject("InvalidSettlingTimeScale");
        if(candidate.SettlingTimeSignal is not null&&!description.SignalAliases.ContainsKey(candidate.SettlingTimeSignal))return Reject("UnknownSettlingTimeSignal");
        if(typed&&candidate.SettlingTimeSignal is not null) {
            var durationType=BusinessSignalTypes.Get(description,candidate.SettlingTimeSignal);
            if(!BusinessSignalTypes.IsNumeric(durationType)&&durationType is not ("Time" or "LTime"))return Reject("NumericSettlingTimeTypeRequired");
        }
        if(candidate.Applicability=="AfterSettling" && candidate.SettlingTimeMs is null && candidate.SettlingTimeSignal is null)return Reject("SettlingTimeEvidenceRequired");
        if(candidate.ApplicabilityCondition is not null||candidate.Assertion is not null) {
            var nodes=0;
            void Check(LogicTerm term,int depth,bool scalar=false,string? numericType=null) {
                if(++nodes>512||depth>32)throw new PipelineException("ConditionBudgetExceeded");
                var count=term.Children?.Count??0;
                if(term.Kind is "Signal" or "ScalarSignal") {
                    if(count!=0||scalar!=(term.Kind=="ScalarSignal"))throw new PipelineException("UnsupportedCondition");
                    if(!description.SignalAliases.ContainsKey(term.Name??""))throw new PipelineException("UnknownConditionAlias");
                    if(scalar) {
                        var expected=BusinessSignalTypes.Normalize(term.DataType??numericType);
                        if(numericType is not null&&expected!=BusinessSignalTypes.Normalize(numericType))throw new PipelineException("NumericOperandTypeMismatch");
                        PipelineNumeric.Coerce(0,expected);
                        if(typed&&BusinessSignalTypes.Get(description,term.Name!)!=expected)throw new PipelineException("NumericSignalTypeMismatch");
                    }
                    else if(!IsBool(term.Name!)||term.DataType is not null&&BusinessSignalTypes.Normalize(term.DataType)!="Bool")throw new PipelineException("BooleanConditionTypeRequired");
                    return;
                }
                if(term.Kind=="Number"&&scalar&&count==0) {
                    if(term.DataType is not null&&numericType is not null&&BusinessSignalTypes.Normalize(term.DataType)!=BusinessSignalTypes.Normalize(numericType))throw new PipelineException("NumericOperandTypeMismatch");
                    if(!double.TryParse(term.Name,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var number))throw new PipelineException("InvalidNumericConstant");
                    PipelineNumeric.Coerce(number,term.DataType??numericType??"");return;
                }
                if(scalar)throw new PipelineException("UnsupportedCondition");
                if(term.Kind=="Constant"&&term.Name is "true" or "false"&&count==0)return;
                if(term.Kind=="Compare"&&count==2&&term.Name is "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le") {
                    PipelineNumeric.Coerce(0,term.DataType??"");foreach(var c in term.Children!)Check(c,depth+1,true,term.DataType);return;
                }
                if(term.Kind=="Not"&&count==1 || term.Kind is "And" or "Or"&&count>0) {
                    foreach(var c in term.Children!)Check(c,depth+1);return;
                }
                throw new PipelineException("UnsupportedCondition");
            }
            try {
                if(candidate.ApplicabilityCondition is not null)Check(candidate.ApplicabilityCondition,0);
                if(candidate.Assertion is not null) {
                    Check(candidate.Assertion,0);
                    var dependencies=PipelineTerms.Walk(candidate.Assertion).Where(t=>t.Kind is "Signal" or "ScalarSignal").Select(t=>t.Name!).ToHashSet(StringComparer.Ordinal);
                    if(!dependencies.SetEquals(candidate.Signals.Select(s=>s.Name)))return Reject("AssertionSignalMismatch");
                }
                var terms=candidate.Conditions.Select(c=>c.Value?LogicTerm.Signal(c.Signal):new LogicTerm("Not",null,[LogicTerm.Signal(c.Signal)])).Append(candidate.ApplicabilityCondition??new("Constant","true")).ToArray();
                var expression=new LogicTerm("And",null,terms);
                var guardTarget="\u001fapplicability";
                var proofDescription=new PipelineDescription(0,"","",[],[new(guardTarget,expression)],[],"{}");
                if(new PipelineBooleanProof().Prove(new("MUST_BE_FALSE",[new(guardTarget,"Target")],""),proofDescription))return Reject("UnreachableApplicability");
                if(candidate.Assertion is not null) {
                    var violation=new LogicTerm("And",null,[expression,new("Not",null,[candidate.Assertion])]);
                    var assertionProof=proofDescription with {Assignments=[new(guardTarget,violation)]};
                    if(new PipelineBooleanProof().Prove(new("MUST_BE_FALSE",[new(guardTarget,"Target")],""),assertionProof))return Reject("TautologicalAssertion");
                }
            }catch(PipelineException ex){return Reject(ex.Code);}
        }
        return new(candidate,"Validated","ReferencesAndShapeValidated");
    }
    public static BusinessCandidate Bind(BusinessDescription description,BusinessCandidate candidate)=>candidate with {
        Signals=candidate.Signals.Select(s=>s with {Name=description.SignalAliases[s.Name]}).ToArray(),
        Conditions=candidate.Conditions.Select(c=>c with {Signal=description.SignalAliases[c.Signal]}).Distinct().ToArray(),
        SettlingTimeSignal=candidate.SettlingTimeSignal is null?null:description.SignalAliases[candidate.SettlingTimeSignal],
        ApplicabilityCondition=candidate.ApplicabilityCondition is null?null:PipelineTerms.Rewrite(candidate.ApplicabilityCondition,t=>
            t.Kind is "Signal" or "ScalarSignal"?t with {Name=description.SignalAliases[t.Name!]}:null),
        Assertion=candidate.Assertion is null?null:PipelineTerms.Rewrite(candidate.Assertion,t=>
            t.Kind is "Signal" or "ScalarSignal"?t with {Name=description.SignalAliases[t.Name!]}:null)
    };
    public static string Key(BusinessCandidate candidate)=>PipelineHash.Of(System.Text.Json.JsonSerializer.Serialize(new {
        candidate.Operator,Signals=candidate.Signals.OrderBy(s=>s.Role,StringComparer.Ordinal).ThenBy(s=>s.Name,StringComparer.Ordinal),
        Conditions=candidate.Conditions.OrderBy(s=>s.Signal,StringComparer.Ordinal).ThenBy(s=>s.Value),candidate.Applicability,candidate.ApplicabilityCondition,candidate.Assertion,candidate.SettlingTimeMs,candidate.SettlingTimeSignal,candidate.SettlingTimeScale
    }));
}
