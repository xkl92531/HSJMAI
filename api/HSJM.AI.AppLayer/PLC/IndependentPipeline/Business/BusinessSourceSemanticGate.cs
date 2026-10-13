#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Proves a candidate against every valuation of the complete supported LU assignments.
/// A comparison abstraction may prove a rule, but only concrete numeric values may refute it.
/// This is an execution-point proof, not a proof of plant state or scan-end stability.</summary>
public static class BusinessSourceSemanticGate
{
    public const string Version="business-source-semantics-1";
    static LogicTerm Not(LogicTerm term)=>new("Not",null,[term]);
    static LogicTerm And(IEnumerable<LogicTerm> terms) {var items=terms.ToArray();return items.Length==0?new("Constant","true"):new("And",null,items);}
    static LogicTerm Or(IEnumerable<LogicTerm> terms) {var items=terms.ToArray();return items.Length==0?new("Constant","false"):new("Or",null,items);}
    static LogicTerm Implies(LogicTerm condition,LogicTerm assertion)=>Or([Not(condition),assertion]);

    /// <summary>The entire conditional assertion, in the candidate's unchanged anonymous vocabulary.</summary>
    public static LogicTerm Assertion(BusinessCandidate candidate) {
        var body=Body(candidate);
        if(candidate.Conditions is null)throw new PipelineException("SourceInvalidConditions");
        var conditions=candidate.Conditions.Select(c=>c.Value?LogicTerm.Signal(c.Signal):Not(LogicTerm.Signal(c.Signal))).ToList();
        if(candidate.ApplicabilityCondition!=null)conditions.Add(candidate.ApplicabilityCondition);
        return Implies(And(conditions),body);
    }
    static LogicTerm Body(BusinessCandidate candidate) {
        if(candidate.Signals is null||candidate.Signals.Any(s=>s is null)||candidate.Signals.Select(s=>s.Name).Distinct(StringComparer.Ordinal).Count()!=candidate.Signals.Count)
            throw new PipelineException("SourceInvalidSignals");
        var sources=candidate.Signals.Where(s=>s.Role=="Source").Select(s=>LogicTerm.Signal(s.Name)).ToArray();
        var targets=candidate.Signals.Where(s=>s.Role=="Target").Select(s=>LogicTerm.Signal(s.Name)).ToArray();
        var members=candidate.Signals.Where(s=>s.Role=="Member").Select(s=>LogicTerm.Signal(s.Name)).ToArray();
        int count=candidate.Signals.Count;
        LogicTerm AtMostOne()=>And(members.SelectMany((m,i)=>members.Take(i).Select(n=>Not(And([m,n])))));
        return candidate.Operator switch {
            "IMPLIES" when sources.Length==1&&targets.Length>0&&count==sources.Length+targets.Length=>Implies(sources[0],And(targets)),
            "REQUIRES_0" when sources.Length==1&&targets.Length>0&&count==sources.Length+targets.Length=>Implies(sources[0],And(targets.Select(Not))),
            "ALL_TRUE" when sources.Length==1&&members.Length>0&&count==sources.Length+members.Length=>Implies(sources[0],And(members)),
            "MUST_BE_FALSE" when targets.Length==1&&count==1=>Not(targets[0]),
            "AT_MOST_ONE" when members.Length>=2&&count==members.Length=>AtMostOne(),
            "EXACTLY_ONE" when members.Length>=2&&count==members.Length=>And([AtMostOne(),Or(members)]),
            "SAME_STATE" when members.Length>=2&&count==members.Length=>Or([And(members),And(members.Select(Not))]),
            "EXPRESSION" when candidate.Assertion!=null=>candidate.Assertion,
            _=>throw new PipelineException("SourceUnsupportedOperatorOrRoles")
        };
    }

    public static BusinessDecision Validate(PipelineSnapshot source,PipelineUnit unit,BusinessDescription description,BusinessCandidate candidate)
    {
        BusinessDecision Unknown(string code)=>new(candidate,"Unknown",code);
        try {
            if(description.Status!="Rendered"||description.LogicUnitId!=unit.Id||description.BlockId!=unit.BlockId)
                return Unknown("SourceDescriptionIdentityMismatch");
            var context=new PipelineCompilationContext(source);var xml=context.Xml(unit);
            if(xml.Root!.Name.LocalName!="FlgNet")return Unknown("SourceUnsupportedLanguage");
            if(xml.Descendants().Any(e=>e.Name.LocalName=="Call"))return Unknown("SourceCallSemanticsRequired");
            var compiler=context.Compiler(unit);var assignments=compiler.Compile();
            if(assignments.Count==0)return Unknown("SourceNoCompleteAssignments");
            // Retention and elapsed-time atoms are over-approximations, not reachable histories.
            if(assignments.Any(a=>PipelineTerms.Any(a.Expression,n=>n.Kind is "Previous" or "TimerElapsed"))||compiler.Timers.Count>0)
                return Unknown("SourceStateOrTimerProofRequired");
            assignments=BindRootLocals(unit,description,assignments);
            var written=assignments.ToDictionary(a=>a.Target,a=>a.Expression,StringComparer.OrdinalIgnoreCase);
            // Local and explicitly global accesses can identify the same root-instance member.
            // Recheck dependencies after binding; the raw compiler cannot see that equivalence.
            if(assignments.Any(a=>PipelineTerms.Any(a.Expression,n=>n.Kind=="Signal"&&written.ContainsKey(n.Name!))))
                return Unknown("SourceOutputReadEpochRequiresProof");
            LogicTerm Bind(LogicTerm value)=>PipelineTerms.Rewrite(value,n=> {
                if(n.Kind is not("Signal" or "ScalarSignal"))return null;
                if(n.Name is null||!description.SignalAliases.TryGetValue(n.Name,out var real))throw new PipelineException("SourceUnboundCandidateSignal");
                return n with{Name=real};
            });
            var assertion=Bind(Assertion(candidate));var body=Bind(Body(candidate));
            if(!PipelineTerms.Walk(body).Any(n=>n.Kind=="Signal"&&written.ContainsKey(n.Name!)))return Unknown("SourceNoWrittenAssertionSignal");
            var all=assignments.SelectMany(a=>PipelineTerms.Walk(a.Expression)).Concat(PipelineTerms.Walk(assertion)).ToArray();
            if(all.Any(n=>n.Kind is not("Signal" or "Constant" or "Not" or "And" or "Or" or "Compare" or "ScalarSignal" or "Number")))
                return Unknown("SourceUnsupportedExpression");
            var names=written.Keys.Concat(all.Where(n=>n.Kind is "Signal" or "ScalarSignal").Select(n=>n.Name!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var memory=BusinessMemoryAliases.For(source);
            if(names.Any(n=>memory.Resolve(n).Status=="Unknown"))return Unknown("SourceMemoryBindingUnknown");
            for(int i=0;i<names.Length;i++)for(int j=0;j<i;j++)
                if(memory.Compare(names[i],names[j]) is BusinessMemoryRelation.Overlap or BusinessMemoryRelation.Unknown)
                    return Unknown("SourcePhysicalAliasRequiresProof");
            foreach(var name in written.Keys.Concat(all.Where(n=>n.Kind=="Signal").Select(n=>n.Name!)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                var types=source.Tags.Where(t=>t.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Select(t=>t.DataType).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if(types.Any(t=>!t.Equals("Bool",StringComparison.OrdinalIgnoreCase)))return Unknown("SourceNonBooleanOperand");
            }
            // Compiler rejects multiple writes and cross-output reads; substitution is exactly one LU epoch.
            var substituted=PipelineTerms.Rewrite(assertion,n=>n.Kind=="Signal"&&written.TryGetValue(n.Name!,out var value)?value:null);
            bool proven=Proves(substituted);
            if(proven) {
                if(candidate.Applicability!="ProgramRelation")return Unknown("SourceObservationPointNotEstablished");
                return new(candidate,"Validated","SourceSemanticProvenAfterLogicUnit");
            }
            // No elapsed duration can be inferred from a combinational counterexample.
            if(candidate.Applicability=="AfterSettling")return Unknown("SourceSettlingProofRequired");
            if(candidate.Applicability is not("ProgramRelation" or "NormalOperation" or "StableDiagnosticSnapshot"))return Unknown("SourceUnsupportedApplicability");
            // An input assigned elsewhere is not an arbitrary reachable state (for example a startup
            // network may set an invariant bit). Never call an unconstrained local valuation a real
            // counterexample when another execution model is needed to establish its reachability.
            if(HasExternalInputWrites(source,unit,description,substituted))return Unknown("SourceExternalInputReachabilityNotEstablished");
            if(!PipelineTerms.Any(substituted,n=>n.Kind=="Compare")||HasConcreteNumericCounterexample(substituted))
                return new(candidate,"Rejected","SourceSemanticCounterexample");
            return Unknown("SourceComparisonReachabilityNotEstablished");
        }
        catch(PipelineException ex){return Unknown("SourceProof:"+ex.Code);}
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or KeyNotFoundException){return Unknown("SourceAmbiguousStructure");}
    }

    static IReadOnlyList<PipelineAssignment> BindRootLocals(PipelineUnit unit,BusinessDescription description,IReadOnlyList<PipelineAssignment> assignments) {
        var localNames=assignments.Select(a=>a.Target).Concat(assignments.SelectMany(a=>PipelineTerms.Walk(a.Expression))
            .Where(n=>n.Kind is "Signal" or "ScalarSignal").Select(n=>n.Name!)).Where(n=>n.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(localNames.Length==0)return assignments;
        if(description.Evidence.Count(e=>e.Kind=="RootInstanceBinding")!=1)
            throw new PipelineException("SourceLocalInstanceBindingUnavailable");
        var bindings=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var local in localNames) {
            // Symbols from other blocks and non-observable formal P aliases never select an instance.
            // Both target and reads must use the renderer's proven, unique CallInfo instance mapping.
            var symbols=(description.Symbols??[]).Where(s=>s.BlockId==unit.BlockId&&s.Path.TrimStart('#').Equals(local[1..],StringComparison.OrdinalIgnoreCase)
                &&s.Section is "Static" or "Input" or "Output"&&description.SignalAliases.ContainsKey(s.Alias)).ToArray();
            if(symbols.Length!=1)throw new PipelineException("SourceLocalInstanceBindingUnavailable");
            var actual=description.SignalAliases[symbols[0].Alias];
            if(description.SignalAliases.Count(p=>p.Value.Equals(actual,StringComparison.OrdinalIgnoreCase))!=1)
                throw new PipelineException("SourceLocalInstanceBindingAmbiguous");
            bindings.Add(local,actual);
        }
        string Name(string name)=>bindings.TryGetValue(name,out var actual)?actual:name;
        return assignments.Select(a=>new PipelineAssignment(Name(a.Target),PipelineTerms.Rewrite(a.Expression,
            n=>n.Kind is "Signal" or "ScalarSignal"?n with{Name=Name(n.Name!)}:null))).ToArray();
    }

    static bool HasExternalInputWrites(PipelineSnapshot source,PipelineUnit unit,BusinessDescription description,LogicTerm assertion) {
        var inputs=PipelineTerms.Walk(assertion).Where(n=>n.Kind is "Signal" or "ScalarSignal").Select(n=>n.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memory=BusinessMemoryAliases.For(source);
        var localBindings=(description.Symbols??[]).Where(s=>s.BlockId==unit.BlockId&&description.SignalAliases.ContainsKey(s.Alias))
            .GroupBy(s=>"#"+s.Path.TrimStart('#'),StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1)
            .ToDictionary(g=>g.Key,g=>description.SignalAliases[g.Single().Alias],StringComparer.OrdinalIgnoreCase);
        bool AffectsInput(string name)=>inputs.Contains(name)||inputs.Any(input=>memory.Compare(input,name) is BusinessMemoryRelation.Overlap or BusinessMemoryRelation.Unknown);
        foreach(var other in source.Units.Where(u=>u.Id!=unit.Id)) {
            // XML parses are short lived. No mutable compiler/tree cache survives a proof.
            var context=new PipelineCompilationContext(source);
            var touchesInputs=false;
            string AtInstance(string name)=>other.BlockId==unit.BlockId&&localBindings.TryGetValue(name,out var actual)?actual:name;
            try {
                var xml=context.Xml(other);
                if(xml.Root!.Name.LocalName!="FlgNet") {
                    if(inputs.Any(n=>other.RawContent.Contains(n,StringComparison.OrdinalIgnoreCase)))return true;
                    continue;
                }
                var references=xml.Descendants().Where(e=>e.Name.LocalName=="Access"&&((string?)e.Attribute("Scope") is "GlobalVariable" or "LocalVariable"))
                    .Select(a=>((string?)a.Attribute("Scope")=="LocalVariable"?"#":"")+string.Join(".",a.Elements().Where(e=>e.Name.LocalName=="Symbol").SelectMany(s=>s.Elements()).Select(PipelineFlgNetCompiler.ComponentName))).ToArray();
                touchesInputs=references.Any(name=>AffectsInput(AtInstance(name)));
                if(!touchesInputs)continue;
                if(context.Compiler(other).Compile().Any(a=>AffectsInput(AtInstance(a.Target))))return true;
            }
            catch(PipelineException) {
                // Unknown language, calls or instruction semantics can hide a writer.
                if(touchesInputs||inputs.Any(n=>other.RawContent.Contains(n.TrimStart('#').Split('.').Last(),StringComparison.OrdinalIgnoreCase)))return true;
            }
        }
        return false;
    }

    static bool Proves(LogicTerm term) {
        const string probe="\u001fsource-proof";
        var description=new PipelineDescription(0,"","",[probe],[new(probe,Not(term))],[],"{}");
        return new PipelineBooleanProof().Prove(new("MUST_BE_FALSE",[new(probe,"Target")],""),description);
    }

    static bool HasConcreteNumericCounterexample(LogicTerm term) {
        // Bounded witness search is ONLY a rejection mechanism. Failure never proves validity.
        var comparisons=PipelineTerms.Walk(term).Where(n=>n.Kind=="Compare").ToArray();
        var scalars=comparisons.SelectMany(PipelineTerms.Walk).Where(n=>n.Kind=="ScalarSignal").GroupBy(n=>n.Name!,StringComparer.OrdinalIgnoreCase).ToArray();
        if(scalars.Length>4)return false;
        var seeds=new HashSet<double>{-1,0,1};
        foreach(var number in comparisons.SelectMany(PipelineTerms.Walk).Where(n=>n.Kind=="Number"))
            if(double.TryParse(number.Name,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)) {
                seeds.Add(value);seeds.Add(value-1);seeds.Add(value+1);
            }
        var choices=new List<double[]>();
        foreach(var scalar in scalars) {
            var values=new HashSet<double>();
            foreach(var value in seeds)try {
                var typed=PipelineNumeric.Coerce(value,scalar.First().DataType??"");
                if(scalar.All(s=>PipelineNumeric.Coerce(typed,s.DataType??"")==typed))values.Add(typed);
            } catch(PipelineException) { }
            if(values.Count==0)return false;
            choices.Add(values.ToArray());
        }
        var concrete=new Dictionary<string,double?>(StringComparer.OrdinalIgnoreCase);int remaining=1024;
        bool Search(int index) {
            if(remaining<=0)return false;
            if(index==scalars.Length) {
                remaining--;
                try {
                    var evaluated=PipelineTerms.Rewrite(term,n=>n.Kind=="Compare"?new("Constant",PipelineNumeric.Compare(n,concrete)?"true":"false"):null);
                    return !Proves(evaluated);
                } catch(PipelineException) {return false;}
            }
            foreach(var value in choices[index]) {concrete[scalars[index].Key]=value;if(Search(index+1))return true;}
            return false;
        }
        return Search(0);
    }
}
