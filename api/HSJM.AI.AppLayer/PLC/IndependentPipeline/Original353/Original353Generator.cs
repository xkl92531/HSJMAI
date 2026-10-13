#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353UnitResult(long LogicUnitId,IReadOnlyList<PipelineRule> Rules,IReadOnlyList<Original353Repair> Repairs,IReadOnlyList<string> Issues)
{
    public IReadOnlyList<Original353SafetyRule> SafetyRules {get;init;}=[];
    public IReadOnlyList<Original353ConditionalFeedback> ConditionalFeedbackRules {get;init;}=[];
}
public sealed record Original353Repair(PipelineRule Original,PipelineRule Replacement,string EvaluationPoint,IReadOnlyList<string> Evidence,IReadOnlyList<string> RequiredContextSignals);

/// <summary>The frozen 353-rule generator with a narrowly evidenced correction layer.
/// 计划 5：名字/猜测规则族按 <see cref="NameRuleSwitches"/> 取舍（默认全开）；第一期 FB 程序体代码证明不受开关影响。</summary>
public sealed class Original353Generator(PipelineSnapshot source, NameRuleSwitches? switches = null)
{
    readonly NameRuleSwitches on=switches??NameRuleSwitches.AllOn;
    readonly Lazy<Original353HoldingOutputRepair> repair=new(()=>new(source));
    readonly Lazy<Original353SafetyRules> safety=new(()=>new(source));
    readonly Lazy<Original353FbContractRules> contracts=new(()=>new(source));
    public Original353UnitResult Generate(PipelineUnit unit) {
        if(string.IsNullOrWhiteSpace(unit.RawContent))return new(unit.Id,[],[],["MissingSource"]);
        var tags=source.Tags.GroupBy(t=>t.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>new PLC_IOTagEntity {
            TagName=g.First().Name,Address=g.First().Address,DataType=g.First().DataType
        },StringComparer.OrdinalIgnoreCase);
        var anchors=SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(unit.RawContent);
        var rules=new List<PipelineRule>();
        rules.AddRange(NameRuleFamilies.ToRules(PldStructuralConstraintAugmenter.AugmentFromBlocks(anchors,tags),NameRuleFamily.PinNameAugment,on));
        rules.AddRange(NameRuleFamilies.ToRules(PldStructuralConstraintAugmenter.AugmentMaskedAlarmFromBlocks(anchors,tags),NameRuleFamily.MaskedAlarm,on));
        var originals=rules.Concat(contracts.Value.Extract(unit)).DistinctBy(r=>r.Key).ToArray();
        var repaired=on.IsOn(NameRuleFamily.HoldingOutputRepair)&&originals.Any(r=>r.Operator=="AT_MOST_ONE"&&r.Signals.Count>=3)
            ?repair.Value.Apply(unit,originals):new(unit.Id,originals,[],[]);
        var additions=on.IsOn(NameRuleFamily.SafetyInversion)?safety.Value.Extract(unit):new Original353SafetyResult([],[]);
        IReadOnlyList<Original353ConditionalFeedback> feedback=on.IsOn(NameRuleFamily.InterfacePositionFeedback)
            ?Original353FeedbackGroups.Extract(source,unit):[];
        return repaired with {
            // The current persisted rule format drops conditional metadata. Keep feedback
            // expectations in a separate typed collection until a trusted settling trace,
            // complete binding and durable guard contract exist. They cannot be instant rules.
            Rules=repaired.Rules.Concat(additions.Rules.Select(r=>r.Rule)).DistinctBy(r=>r.Key).ToArray(),
            Issues=repaired.Issues.Concat(additions.Issues)
                .Concat(feedback.SelectMany(group=>group.Rule.MaskedFeedbackInputs
                    .Select(name=>"MaskedFeedbackInput:"+name)))
                .Distinct().ToArray(),
            SafetyRules=additions.Rules,
            ConditionalFeedbackRules=feedback
        };
    }
}
