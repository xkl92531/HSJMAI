#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.IO;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Independent evidence compilation and rendering; never invokes the old renderer or save services.</summary>
public static class PipelineEvidenceBuilder
{
    public const string Version = "independent-evidence-11";
    public static string Key(PipelineSnapshot source, PipelineUnit unit) =>
        Key(source.Fingerprint(),unit);
    public static string Key(string sourceFingerprint, PipelineUnit unit) =>
        PipelineHash.Of($"{Version}:{sourceFingerprint}:{unit.Id}");

    public static PipelineDescription Build(PipelineSnapshot source, PipelineUnit unit, string? sourceFingerprint = null,
        PipelineCompilationContext? context = null)
    {
        context ??= new(source);
        foreach (var callee in unit.Callees)
            if (!source.Units.Any(u => string.Equals(u.BlockName, callee, StringComparison.Ordinal)))
                throw new PipelineException("MissingCallee");
        var limitations = new List<string>();
        var compiler = context.Compiler(unit);
        var assignments = compiler.Compile();
        foreach (var assignment in assignments) PipelineTerms.Rewrite(assignment.Expression,_ => null);
        var usedNames = assignments.Select(a => a.Target).Concat(assignments.SelectMany(a => Names(a.Expression))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boolNames = assignments.Select(a=>a.Target).Concat(assignments.SelectMany(a=>PipelineTerms.Atoms(a.Expression)).Where(t=>t.Kind=="Signal").Select(t=>t.Name!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scalarNames=assignments.SelectMany(a=>PipelineTerms.Walk(a.Expression)).Where(t=>t.Kind=="ScalarSignal").Select(t=>t.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(scalarNames.Overlaps(boolNames))throw new PipelineException("NumericBooleanAliasRequiresTypeProof");
        var writtenNames = assignments.Select(a => a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stateUpdates = compiler.CallEvidence.SelectMany(c => c.StateUpdates ?? []).ToArray();
        var timerReads = compiler.Timers.SelectMany(t => Names(t.Input)).ToArray();
        var stateReads = stateUpdates.SelectMany(s => Names(s.Expression)).ToArray();
        if (timerReads.Any(writtenNames.Contains)) throw new PipelineException("TimerFeedbackRequiresExecutionOrder");
        if (stateReads.Any(writtenNames.Contains)) throw new PipelineException("CallInputOutputAliasRequiresExecutionOrder");
        var allReads = usedNames.Concat(timerReads).Concat(stateReads).ToArray();
        var instances = compiler.CallEvidence.SelectMany(c => c.InstancePaths ?? []).Concat(compiler.Timers.Select(t => t.Key)).ToArray();
        if (instances.Distinct(StringComparer.OrdinalIgnoreCase).Count() != instances.Length)
            throw new PipelineException("RepeatedCallInstanceExecution");
        if (allReads.Any(n => instances.Any(i => string.Equals(n,i,StringComparison.OrdinalIgnoreCase) || n.StartsWith(i+".",StringComparison.OrdinalIgnoreCase))))
            throw new PipelineException("InstanceMemoryAliasRequiresExecutionOrder");
        var isTemplate = allReads.Any(n => n.StartsWith("#",StringComparison.Ordinal));
        var readSet = allReads.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedTags = source.Tags.Where(t => readSet.Contains(t.Name)).ToArray();
        if (usedTags.GroupBy(t => t.Name,StringComparer.OrdinalIgnoreCase).Any(g => g.Select(t => t.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            throw new PipelineException("AmbiguousSignalAddress");
        if (usedTags.Where(t => !string.IsNullOrWhiteSpace(t.Address))
            .GroupBy(t => t.Address.Replace("%", "").Replace(" ", "").ToUpperInvariant(),StringComparer.Ordinal)
            .Any(g => g.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new PipelineException("AliasedSignalsRequireBindingProof");
        if (usedTags.Any(t => boolNames.Contains(t.Name)&&!string.Equals(t.DataType,"Bool",StringComparison.OrdinalIgnoreCase)))
            throw new PipelineException("NonBooleanSignal");
        if (assignments.GroupBy(a => a.Target, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new PipelineException("MultipleWritesRequireExecutionOrder");
        var markdown = $"程序块 {unit.BlockName}，逻辑单元 {unit.Id}。" +
            (isTemplate ? "以下是块内形参模板，必须绑定调用实例后才能生成实际点位约束：\n" : "以下关系适用于本单元赋值执行后：\n") +
            (assignments.Count == 0 ? "该网络没有可执行输出。" : string.Join("\n", assignments.Select(a =>
                $"- `{a.Target}` 赋值为 {PipelineFlgNetCompiler.Describe(a.Expression)}。")));
        foreach (var timer in compiler.Timers)
            markdown += $"\n- TON `{timer.Key}`：输入 {PipelineFlgNetCompiler.Describe(timer.Input)} 连续成立 {timer.DelayMs}ms 后 Q 成立；输入为 false 时复位，不能当作即时连线。";
        foreach (var call in compiler.CallEvidence)
            markdown += $"\n- 调用 `{call.Callee}`（Call UId={call.CallUid}），已展开网络 {string.Join(",",call.BodyUnits)} 并替换实际输入；各输出见上述表达式。";
        foreach (var update in stateUpdates)
            markdown += $"\n- 实例状态 `{update.Target}` 更新为 {PipelineFlgNetCompiler.Describe(update.Expression)}。";
        var signals = boolNames.OrderBy(n => n,StringComparer.Ordinal).ToArray();
        // Raw program is retained as evidence, including timer parameters and instance bindings.
        var evidence = JsonSerializer.Serialize(new { unit.Id, unit.BlockName, unit.RawContent, unit.Bindings,
            unit.Callees, Assignments = assignments, Timers=compiler.Timers, Calls=compiler.CallEvidence,
            StateUpdates=stateUpdates, IsTemplate=isTemplate, Limitations = limitations, EvaluationPoint = "AfterLogicUnit" });
        return new(unit.Id, Key(sourceFingerprint ?? source.Fingerprint(), unit), markdown, signals, assignments, limitations, evidence,
            compiler.Timers,isTemplate,compiler.CallEvidence,stateUpdates);
    }

    public static IEnumerable<string> Names(LogicTerm term) => term.Kind is "Signal" or "ScalarSignal"
        ? [term.Name!] : (term.Children ?? []).SelectMany(Names);
}
