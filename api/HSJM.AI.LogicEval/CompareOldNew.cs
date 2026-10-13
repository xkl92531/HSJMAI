using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>名字规则族独有的旧产出、未被覆盖时，新流程对它的处理：计划 4 合并会删除部分旧规则
/// （CallSiteSubstitution.Superseded：到位不依赖传感器；RuleFilters：代码证伪的 SAME_STATE、常量输出），
/// 与「新流程合并时保留了旧规则、但自己并不产出」要分开，复核者才看得出某条是被代码证伪的。</summary>
static class NewFlowDisposition
{
    public const string Deleted = "被新流程删除";
    public const string NotProduced = "新流程没产";

    /// <summary>已覆盖返回 null；否则看全开合并结果里是否还有这条：没有 = 合并时被删除，有 = 合并保留了旧规则但新流程不产。</summary>
    public static string? Of(RetirementItem item, IReadOnlySet<string> mergedAllOnKeys) =>
        item.Verdict == RetirementVerdict.Covered ? null : mergedAllOnKeys.Contains(item.Fact.Key) ? NotProduced : Deleted;
}

/// <summary>spec §9：同一归档分别跑「只旧规则」与「新流程合并后」，按约束类型 + 信号逐条比对；
/// 并对每个名字规则族算「关掉它会少哪些旧产出、这些是否已被新流程覆盖」，交人按程序证据判定。不写数据库。
/// 一键排查不改（用户 2026-10-02 决定），不对照旧阀门报警提取。</summary>
static class CompareOldNew
{
    public static async Task<int> RunAsync(string[] args)
    {
        var project = long.Parse(CliArgs.Req(args, "--project"));
        var truthPath = CliArgs.Opt(args, "--truth");
        var truth = truthPath is null ? null : await Holdout.ReadTruthAsync(truthPath);
        var (report, json) = await RunCoreAsync(project, CliArgs.Req(args, "--archive"), CategoryMap.Load(CliArgs.Req(args, "--categories")),
            OfflineAiOptions.From(args), truth, allowHoldout: false);
        var output = CliArgs.Req(args, "--out");
        await File.WriteAllTextAsync(output, report);
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".json"), json);
        Console.WriteLine(string.Join("\n", report.Split('\n').Take(24)));
        return 0;
    }

    public static async Task<(string Report, string Json)> RunCoreAsync(long project, string archive, CategoryMap map,
        OfflineAiOptions aiOptions, TruthFile? truth, bool allowHoldout)
    {
        var setup = await OfflinePipeline.LoadAsync(project, archive, allowHoldout);
        var ai = await OfflinePipeline.ReadAiAsync(setup, aiOptions);
        return Build(setup, ai, map, truth);
    }

    /// <summary>用一份已读好的 AI 结果做对照（holdout-acceptance 与评测共用同一次读取）。</summary>
    public static (string Report, string Json) Build(OfflineSetup setup, OfflineAiRead ai, CategoryMap map, TruthFile? truth)
    {
        var source = setup.Source;
        var oldAll = OfflinePipeline.Old(setup, NameRuleSwitches.AllOn);
        var (merged, report) = OfflinePipeline.Merge(setup, oldAll, ai);
        var oldFacts = ResultFacts.From(source, oldAll, [], map);
        var newFacts = ResultFacts.From(source, merged, report.Alarms, map);
        var diff = RunComparison.Diff(oldFacts, newFacts);
        var mergedKeys = newFacts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        var items = new List<RetirementItem>();
        foreach (var family in NameRuleFamily.GeneratorFamilies)
        {
            var oldWithout = OfflinePipeline.Old(setup, NameRuleSwitches.AllOn.Without(family));
            var (mergedWithout, reportWithout) = OfflinePipeline.Merge(setup, oldWithout, ai);
            items.AddRange(RetirementAudit.Classify(family, oldFacts, ResultFacts.From(source, oldWithout, [], map),
                ResultFacts.From(source, mergedWithout, reportWithout.Alarms, map), truth));
        }

        var json = JsonSerializer.Serialize(new
        {
            Project = setup.ProjectId,
            Diff = diff,
            Items = items.Select(i => new { i.Family, i.Fact, i.Verdict, NewFlow = NewFlowDisposition.Of(i, mergedKeys) }),
        }, TruthJson.Options);
        return (Render(setup.ProjectId, truth, oldFacts, newFacts, diff, items, mergedKeys, source, ai), json);
    }

    static string Render(long project, TruthFile? truth, IReadOnlyList<EvalFact> oldFacts, IReadOnlyList<EvalFact> newFacts, FactDiff diff,
        IReadOnlyList<RetirementItem> items, IReadOnlySet<string> mergedKeys, PipelineSnapshot source, OfflineAiRead ai)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 新旧对照与名字规则下线候选：项目 {project}");
        sb.AppendLine();
        var scope = truth is null ? "无（除「已覆盖」外全部为待判定）"
            : truth.Units is null ? $"{truth.Project}（全项目）" : $"{truth.Project}（抽样 {truth.Units.Count} 个程序段）";
        sb.AppendLine($"标准答案：{scope}");
        sb.AppendLine($"AI 读取：模型 {ai.ModelName}，模型调用 {ai.ModelCalls} 次，旧缓存迁移命中 {ai.LegacyHits} 次，--no-ai 未命中 {ai.CacheMisses} 次"
            + (ai.Networks is null ? ai.NetworkError is null ? "；未读普通网络（--no-network-ai）" : $"；普通网络阶段出错：{ai.NetworkError}" : ""));
        sb.AppendLine();
        sb.AppendLine("## 总览（按「约束类型 + 信号」，全项目）");
        sb.AppendLine("| 项 | 条数 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 只旧规则（旧流程全开） | {oldFacts.Select(f => f.Key).Distinct().Count()} |");
        sb.AppendLine($"| 新流程合并后 | {newFacts.Select(f => f.Key).Distinct().Count()} |");
        sb.AppendLine($"| 旧有新无 | {diff.OnlyA.Count} |");
        sb.AppendLine($"| 新有旧无 | {diff.OnlyB.Count} |");
        sb.AppendLine($"| 两者同 | {diff.Both.Count}（其中前提不同 {diff.PreconditionsDiffer.Count}） |");
        sb.AppendLine();
        sb.AppendLine("## 名字规则下线候选（各族独有的旧产出）");
        sb.AppendLine();
        sb.AppendLine("独有产出 = 全开时旧流程有、只关掉该族时旧流程没有的事实。未覆盖的再按新流程处理分两种："
            + $"「{NewFlowDisposition.Deleted}」= 全开合并时已被新流程删掉（到位不依赖传感器的证伪、代码证伪的 SAME_STATE、常量输出），"
            + $"「{NewFlowDisposition.NotProduced}」= 全开合并保留了旧规则，但关掉该族后新流程自己不产。");
        sb.AppendLine();
        string[] verdicts = [RetirementVerdict.Covered, RetirementVerdict.Keep, RetirementVerdict.SuspectWrong, RetirementVerdict.Undecided];
        sb.AppendLine("| 族 | 独有产出 | " + string.Join(" | ", verdicts) + $" | 未覆盖中{NewFlowDisposition.Deleted} | 未覆盖中{NewFlowDisposition.NotProduced} |");
        sb.AppendLine("|---|---|" + string.Concat(verdicts.Select(_ => "---|")) + "---|---|");
        foreach (var family in NameRuleFamily.All)
        {
            var mine = items.Where(i => i.Family == family).ToArray();
            sb.AppendLine($"| {family} | {mine.Length} | " + string.Join(" | ", verdicts.Select(v => mine.Count(i => i.Verdict == v)))
                + $" | {mine.Count(i => NewFlowDisposition.Of(i, mergedKeys) == NewFlowDisposition.Deleted)}"
                + $" | {mine.Count(i => NewFlowDisposition.Of(i, mergedKeys) == NewFlowDisposition.NotProduced)} |");
        }
        foreach (var family in NameRuleFamily.All)
        {
            var open = items.Where(i => i.Family == family && i.Verdict != RetirementVerdict.Covered).ToArray();
            if (open.Length == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"### {family}（未覆盖 {open.Length}）");
            foreach (var i in open.OrderBy(i => i.Verdict, StringComparer.Ordinal).ThenBy(i => i.Fact.Key, StringComparer.Ordinal))
                sb.AppendLine($"- [{i.Verdict}][{NewFlowDisposition.Of(i, mergedKeys)}] `{i.Fact.Key}` — {i.Fact.Display}"
                    + $"（{i.Fact.Block} 网络 {i.Fact.Network}，来源 {i.Fact.Origin}）");
        }
        sb.AppendLine();
        sb.Append(RunComparison.Report(diff, "旧有新无 / 新有旧无明细", "旧", "新"));
        sb.AppendLine();
        sb.AppendLine("## 附录：程序证据（未覆盖条目所在程序段的翻译，每段截 4000 字）");
        foreach (var (block, network) in items.Where(i => i.Verdict != RetirementVerdict.Covered).Select(i => (i.Fact.Block, i.Fact.Network))
                     .Distinct().OrderBy(x => x.Block, StringComparer.Ordinal).ThenBy(x => x.Network))
        {
            sb.AppendLine();
            sb.AppendLine($"### {block} 网络 {network}");
            var unit = source.Units.FirstOrDefault(u => u.BlockName == block && u.NetworkOrder == network);
            if (unit is null) { sb.AppendLine("（找不到程序段）"); continue; }
            var text = Original353RuleTextService.TryRenderPlainLanguage(unit, out var plain, out _, out var error, source.Tags)
                ? plain : $"（渲染失败：{error?.Message ?? "译文不可用"}）";
            sb.AppendLine("```text");
            sb.AppendLine(text.Length > 4000 ? text[..4000] + "\n…（已截断）" : text);
            sb.AppendLine("```");
        }
        return sb.ToString();
    }
}
