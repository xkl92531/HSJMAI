#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
public sealed record OriginalXmlDocument(string FileName,string Content);
public sealed record Original353ProjectResult(long ProjectId,string GeneratorVersion,string SourceFingerprint,
    IReadOnlyList<Original353UnitResult> Units,int RuleCount,int RepairCount,IReadOnlyList<string> RequiredContextSignals)
{
    public int SafetyRuleCount {get;init;}
    public int ConditionalFeedbackCount {get;init;}
}
public static class OriginalConstraintWorkflow
{
    public const string Version="original353-source-repair-5-safety-1-conditional-feedback-3-indirect-coil-1-understanding-plan4-1";
    /// <summary>关掉任一生成器名字规则族时追加 |namerules-off:&lt;族…&gt;（R1）。</summary>
    public static string VersionFor(NameRuleSwitches? switches)=>
        switches is null||switches.GeneratorSignature.Length==0?Version:Version+"|namerules-off:"+switches.GeneratorSignature;
    /// <summary>Fresh immutable import for the isolated original-generator path. A replaced block's
    /// old units are never retained, even if the uploaded replacement has an unavailable body.</summary>
    public static PipelineSnapshot Reimport(PipelineSnapshot source,IReadOnlyList<OriginalXmlDocument> files) {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(files);
        if(files.Count is 0 or >512)throw new PipelineException("XmlFilesRequiredOrBudgetExceeded");
        var blocks=files.Select(file=>PipelineExportReader.Read(file.Content,source.ProjectId,file.FileName)).ToArray();
        if(blocks.GroupBy(b=>b.Name,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()!=1))
            throw new PipelineException("DuplicateUploadedBlock");
        var names=blocks.Select(b=>b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var units=source.Units.Where(u=>!names.Contains(u.BlockName)).Concat(blocks.SelectMany(b=>b.Units)).ToArray();
        if(units.GroupBy(u=>u.Id).Any(g=>g.Count()!=1))throw new PipelineException("AmbiguousImportedUnitIdentity");
        return source with{Units=units};
    }
    public static Original353ProjectResult Generate(PipelineSnapshot source,CancellationToken token=default,NameRuleSwitches? switches=null) {
        var generator=new Original353Generator(source,switches);var units=new List<Original353UnitResult>();
        foreach(var unit in source.Units) {token.ThrowIfCancellationRequested();units.Add(generator.Generate(unit));}
        return new(source.ProjectId,VersionFor(switches),source.Fingerprint(),units,units.Sum(u=>u.Rules.Count),units.Sum(u=>u.Repairs.Count),
            units.SelectMany(u=>u.Repairs).SelectMany(r=>r.RequiredContextSignals)
                .Concat(units.SelectMany(u=>u.SafetyRules).SelectMany(r=>r.RequiredContextSignals))
                .Concat(units.SelectMany(u=>u.ConditionalFeedbackRules).SelectMany(r=>r.BypassSignals))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {SafetyRuleCount=units.Sum(u=>u.SafetyRules.Count),ConditionalFeedbackCount=units.Sum(u=>u.ConditionalFeedbackRules.Count)};
    }
}
