#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Reduced ordered BDD. Universal proof, with bounded memory; never samples valuations.</summary>
internal sealed class PipelineBooleanProof
{
    readonly record struct Node(int Variable,int Low,int High);
    readonly List<Node> nodes=[new(int.MaxValue,0,0),new(int.MaxValue,1,1)];
    readonly Dictionary<Node,int> unique=new();
    readonly Dictionary<(char,int,int),int> cache=new();
    readonly Dictionary<string,int> variables=new(StringComparer.OrdinalIgnoreCase);
    int operations;
    int Make(int variable,int low,int high) {
        if(low==high)return low;
        var node=new Node(variable,low,high);
        if(unique.TryGetValue(node,out var id))return id;
        if(nodes.Count>=100000)throw new PipelineException("ProofBudgetExceeded");
        id=nodes.Count;nodes.Add(node);unique.Add(node,id);return id;
    }
    int Apply(char op,int left,int right,int depth=0) {
        if(++operations>1000000||depth>256)throw new PipelineException("ProofBudgetExceeded");
        if(left>right)(left,right)=(right,left);
        if(op=='&') {if(left==0)return 0;if(left==1)return right;if(left==right)return left;}
        if(op=='|') {if(left==0)return right;if(left==1||right==1)return 1;if(left==right)return left;}
        if(op=='^') {if(left==0)return right;if(left==right)return 0;if(left==1&&right==1)return 0;}
        if(cache.TryGetValue((op,left,right),out var cached))return cached;
        var variable=Math.Min(nodes[left].Variable,nodes[right].Variable);
        if(variable==int.MaxValue)throw new PipelineException("IncompleteProof");
        var l=nodes[left];var r=nodes[right];
        var low=Apply(op,l.Variable==variable?l.Low:left,r.Variable==variable?r.Low:right,depth+1);
        var high=Apply(op,l.Variable==variable?l.High:left,r.Variable==variable?r.High:right,depth+1);
        var value=Make(variable,low,high);cache[(op,left,right)]=value;return value;
    }
    int Not(int value)=>Apply('^',1,value);
    int Term(LogicTerm term,int depth=0) {
        if(depth>256)throw new PipelineException("ProofBudgetExceeded");
        if(term.Kind is "Signal" or "Previous" or "TimerElapsed" or "Compare") {
            var key=PipelineTerms.AtomKey(term);
            if(!variables.TryGetValue(key,out var index)) {index=variables.Count;variables.Add(key,index);}
            return Make(index,0,1);
        }
        return term.Kind switch {
            "Constant" when term.Name is "true" or "false"=>term.Name=="true"?1:0,
            "Not" when term.Children?.Count==1=>Not(Term(term.Children[0],depth+1)),
            "And" when term.Children?.Count>0=>term.Children.Aggregate(1,(acc,t)=>Apply('&',acc,Term(t,depth+1))),
            "Or" when term.Children?.Count>0=>term.Children.Aggregate(0,(acc,t)=>Apply('|',acc,Term(t,depth+1))),
            _=>throw new PipelineException("IncompleteProof")
        };
    }
    public bool Prove(PipelineRule rule,PipelineDescription description) {
        var outputs=description.Assignments.ToDictionary(a=>a.Target,a=>a.Expression,StringComparer.OrdinalIgnoreCase);
        int Value(PipelineSignal signal)=>Term(outputs.TryGetValue(signal.Name,out var expression)?expression:LogicTerm.Signal(signal.Name));
        var members=rule.Signals.Where(s=>s.Role=="Member").Select(Value).ToArray();
        var targets=rule.Signals.Where(s=>s.Role=="Target").Select(Value).ToArray();
        var source=rule.Signals.Where(s=>s.Role=="Source").Select(Value).SingleOrDefault();
        int All(IEnumerable<int> values)=>values.Aggregate(1,(a,b)=>Apply('&',a,b));
        int atMostOne=1;
        if(rule.Operator is "AT_MOST_ONE" or "EXACTLY_ONE")
            for(int i=0;i<members.Length;i++)for(int j=0;j<i;j++)atMostOne=Apply('&',atMostOne,Not(Apply('&',members[i],members[j])));
        var result=rule.Operator switch {
            "AT_MOST_ONE"=>atMostOne,
            "EXACTLY_ONE"=>Apply('&',atMostOne,members.Aggregate(0,(a,b)=>Apply('|',a,b))),
            "SAME_STATE"=>Apply('|',All(members),All(members.Select(Not))),
            "IMPLIES"=>Apply('|',Not(source),All(targets)),
            "ALL_TRUE"=>Apply('|',Not(source),All(members)),
            "REQUIRES_0"=>Apply('|',Not(source),All(targets.Select(Not))),
            "MUST_BE_FALSE"=>Not(targets.Single()),
            _=>throw new PipelineException("UnsupportedOperatorOrRoles")
        };
        return result==1;
    }
}
