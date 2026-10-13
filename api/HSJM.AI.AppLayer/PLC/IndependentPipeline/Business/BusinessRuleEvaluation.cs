#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessEvaluationResult(string Status,string Code);

/// <summary>
/// Evaluates already-bound rules after the caller validates project, freshness, quality and coherence.
/// The execution point and stable duration are explicit caller observations, never guessed from values.
/// </summary>
public static class BusinessRuleEvaluation
{
    public static BusinessEvaluationResult Evaluate(BusinessCandidate rule,Dictionary<string,bool?> values,
        Dictionary<string,double?> scalars,string evaluationPoint,long? stableForMs) {
        if(rule.Applicability=="ProgramRelation"&&evaluationPoint!="AfterLogicUnit")return new("Unknown","AfterLogicUnitSnapshotRequired");
        if(rule.Applicability is "NormalOperation" or "StableDiagnosticSnapshot" && evaluationPoint!="StableDiagnosticSnapshot")return new("Unknown","StableDiagnosticSnapshotRequired");
        if(rule.Applicability=="AfterSettling") {
            if(stableForMs is null)return new("Unknown","StableDurationRequired");
            double required;
            if(rule.SettlingTimeMs.HasValue)required=rule.SettlingTimeMs.Value;
            else if(rule.SettlingTimeSignal!=null&&scalars.TryGetValue(rule.SettlingTimeSignal,out var duration)&&duration.HasValue&&double.IsFinite(duration.Value))required=duration.Value*rule.SettlingTimeScale;
            else return new("Unknown","SettlingTimeUnavailable");
            if(!double.IsFinite(required)||required<0||stableForMs<0)return new("Unknown","InvalidStableDuration");
            if(stableForMs.Value<required)return new("NotApplicable","SettlingTimeNotReached");
        }
        else if(rule.Applicability is not("ProgramRelation" or "NormalOperation" or "StableDiagnosticSnapshot"))return new("Unknown","UnsupportedApplicability");
        bool V(string name)=>values.TryGetValue(name,out var value)&&value.HasValue?value.Value:throw new PipelineException("ObservationUnavailable:"+name);
        string? unavailable=null;
        bool? Optional(string name) {
            if(values.TryGetValue(name,out var value)&&value.HasValue)return value.Value;
            unavailable??="ObservationUnavailable:"+name;return null;
        }
        bool? Compare(LogicTerm term) {
            try {return PipelineNumeric.Compare(term,scalars);}
            catch(PipelineException ex)when(ex.Code=="MissingNumericSignal"){unavailable??=ex.Code;return null;}
        }
        bool? Expr(LogicTerm term,int depth=0) {
            if(depth>64)throw new PipelineException("ExpressionBudgetExceeded");
            return term.Kind switch {
                "Signal"=>Optional(term.Name!),"Constant" when term.Name is "true" or "false"=>term.Name=="true",
                "Not" when term.Children?.Count==1=>!Expr(term.Children[0],depth+1),
                "And" when term.Children?.Count>0=>term.Children.Select(t=>Expr(t,depth+1)).Aggregate((bool?)true,(a,b)=>a&b),
                "Or" when term.Children?.Count>0=>term.Children.Select(t=>Expr(t,depth+1)).Aggregate((bool?)false,(a,b)=>a|b),
                "Compare"=>Compare(term),_=>throw new PipelineException("UnsupportedExpression")
            };
        }
        try {
            if(rule.Conditions is null)return new("Unknown","InvalidRule");
            bool? applicable=true;
            foreach(var condition in rule.Conditions)applicable &= condition.Value?Optional(condition.Signal):!Optional(condition.Signal);
            if(rule.ApplicabilityCondition!=null)applicable &= Expr(rule.ApplicabilityCondition);
            if(applicable==false)return new("NotApplicable","ConditionNotMet");
            if(applicable is null)return new("Unknown",unavailable??"ApplicabilityUnknown");
            if(rule.Signals is null||rule.Signals.Any(s=>s is null)||rule.Signals.Select(s=>s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rule.Signals.Count)return new("Unknown","InvalidRule");
            var sources=rule.Signals.Where(s=>s.Role=="Source").ToArray();var targets=rule.Signals.Where(s=>s.Role=="Target").ToArray();var members=rule.Signals.Where(s=>s.Role=="Member").ToArray();
            if(rule.Operator!="EXPRESSION")foreach(var signal in rule.Signals)_=V(signal.Name);
            bool? result=rule.Operator switch {
                "AT_MOST_ONE" when members.Length>=2&&members.Length==rule.Signals.Count=>members.Count(s=>V(s.Name))<=1,
                "EXACTLY_ONE" when members.Length>=2&&members.Length==rule.Signals.Count=>members.Count(s=>V(s.Name))==1,
                "SAME_STATE" when members.Length>=2&&members.Length==rule.Signals.Count=>members.All(s=>V(s.Name))||members.All(s=>!V(s.Name)),
                "IMPLIES" when sources.Length==1&&targets.Length>=1&&sources.Length+targets.Length==rule.Signals.Count=>!V(sources[0].Name)||targets.All(s=>V(s.Name)),
                "REQUIRES_0" when sources.Length==1&&targets.Length>=1&&sources.Length+targets.Length==rule.Signals.Count=>!V(sources[0].Name)||targets.All(s=>!V(s.Name)),
                "ALL_TRUE" when sources.Length==1&&members.Length>=1&&sources.Length+members.Length==rule.Signals.Count=>!V(sources[0].Name)||members.All(s=>V(s.Name)),
                "MUST_BE_FALSE" when targets.Length==1&&rule.Signals.Count==1=>!V(targets[0].Name),
                "EXPRESSION" when rule.Assertion!=null=>Expr(rule.Assertion),_=>throw new PipelineException("UnsupportedOperatorOrRoles")
            };
            return result is null?new("Unknown",unavailable??"AssertionUnknown"):
                new(result.Value?"Pass":"Violation",result.Value?"ConstraintSatisfied":"ConstraintViolated");
        }
        catch(PipelineException ex){return new("Unknown",ex.Code);}
        catch(ArgumentException){return new("Unknown","InvalidRule");}
    }
}
