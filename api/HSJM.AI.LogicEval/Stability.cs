using System.Diagnostics;
using System.Text;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>spec §13.6 第 3、4 条：清空缓存连续两次真实 AI 运行，比较代入后的约束集合与契约签名；记录首次与无变更再跑的理解步骤耗时。不写数据库。
/// --no-ai --cache &lt;目录&gt;：冒烟用，三次都只读这一个已有缓存、不调 AI（两次读同一缓存应判一致），结论不代表稳定性验收。</summary>
static class Stability
{
    public static async Task<int> RunAsync(string[] args)
    {
        var project = long.Parse(CliArgs.Req(args, "--project"));
        var noNetworkAi = CliArgs.Flag(args, "--no-network-ai");
        var smoke = CliArgs.Flag(args, "--no-ai");
        (string Name, OfflineAiOptions Options)[] plan;
        if (smoke)
        {
            var cache = CliArgs.Req(args, "--cache");
            plan =
            [
                ("首次（冒烟：读已有缓存）", new(cache, true, noNetworkAi)), ("第二次（冒烟：读同一缓存）", new(cache, true, noNetworkAi)),
                ("无变更再跑（冒烟：读同一缓存）", new(cache, true, noNetworkAi)),
            ];
        }
        else
        {
            var work = CliArgs.Req(args, "--work");
            var run1 = Path.Combine(work, "run1");
            var run2 = Path.Combine(work, "run2");
            foreach (var dir in new[] { run1, run2 })
                if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Console.Error.WriteLine($"缓存目录 {dir} 不为空：稳定性验收要求清空缓存后运行");
                    return 2;
                }
            plan =
            [
                ("首次（空缓存）", new(run1, false, noNetworkAi)), ("第二次（另一空缓存）", new(run2, false, noNetworkAi)),
                ("无变更再跑（复用首次缓存，不调 AI）", new(run1, true, noNetworkAi)),
            ];
        }
        var map = CategoryMap.Load(CliArgs.Req(args, "--categories"));
        var setup = await OfflinePipeline.LoadAsync(project, CliArgs.Req(args, "--archive"));
        var clock = Stopwatch.StartNew();
        var old = OfflinePipeline.Old(setup, NameRuleSwitches.AllOn);
        var oldTime = clock.Elapsed;

        var runs = new List<(string Name, OfflineAiRead Ai, TimeSpan Merge, IReadOnlyList<EvalFact> Facts)>();
        foreach (var (name, options) in plan)
        {
            var ai = await OfflinePipeline.ReadAiAsync(setup, options);
            clock.Restart();
            var (merged, report) = OfflinePipeline.Merge(setup, old, ai);
            var mergeTime = clock.Elapsed;
            runs.Add((name, ai, mergeTime, ResultFacts.From(setup.Source, merged, report.Alarms, map)));
            Console.WriteLine($"{name}：契约 {ai.ContractTime}，普通网络 {ai.NetworkTime}，合并 {mergeTime}，模型调用 {ai.ModelCalls}");
        }
        var text = Render(project, oldTime, runs, smoke);
        await File.WriteAllTextAsync(CliArgs.Req(args, "--out"), text);
        Console.WriteLine(string.Join("\n", text.Split('\n').Take(16)));
        return 0;
    }

    static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss");

    static string Render(long project, TimeSpan oldTime,
        IReadOnlyList<(string Name, OfflineAiRead Ai, TimeSpan Merge, IReadOnlyList<EvalFact> Facts)> runs, bool smoke)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 稳定性与耗时：项目 {project}" + (smoke ? "（冒烟：--no-ai，三次读同一缓存，不代表稳定性验收）" : ""));
        sb.AppendLine();
        sb.AppendLine($"旧流程生成（含模式规则，即现在的发布生成步骤）：{Clock(oldTime)}");
        sb.AppendLine();
        sb.AppendLine("| 运行 | FB 契约 | 普通网络 | 合并 | 理解步骤合计 | 模型调用 | 未命中 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in runs)
            sb.AppendLine($"| {r.Name} | {Clock(r.Ai.ContractTime)} | {Clock(r.Ai.NetworkTime)} | {Clock(r.Merge)} | "
                + $"{Clock(r.Ai.ContractTime + r.Ai.NetworkTime + r.Merge)} | {r.Ai.ModelCalls} | {r.Ai.CacheMisses} |");
        var first = runs[0];
        var warm = runs[2];
        var firstTotal = first.Ai.ContractTime + first.Ai.NetworkTime + first.Merge;
        var warmTotal = warm.Ai.ContractTime + warm.Ai.NetworkTime + warm.Merge;
        var diff = RunComparison.Diff(runs[0].Facts, runs[1].Facts);
        var contracts = ContractSignatures.Diff(runs[0].Ai.Contracts, runs[1].Ai.Contracts);
        var stable = diff.OnlyA.Count == 0 && diff.OnlyB.Count == 0 && diff.PreconditionsDiffer.Count == 0;
        sb.AppendLine();
        sb.AppendLine($"- spec §13.6-4 首次发布理解步骤 ≤ 45 分钟：{Clock(firstTotal)} → {(firstTotal <= TimeSpan.FromMinutes(45) ? "达标" : "未达标")}"
            + "（离线可测部分，不截断；发布器另有读取期限，超时部分会在期限处截断。不含 AI 说明措辞、入库事务与报警清单写入）");
        sb.AppendLine($"- spec §13.6-4 无变更再次上传比现在多 ≤ 1 分钟：{Clock(warmTotal)} → {(warmTotal <= TimeSpan.FromMinutes(1) ? "达标" : "未达标")}；"
            + $"该次模型调用 {warm.Ai.ModelCalls} 次、缓存未命中 {warm.Ai.CacheMisses} 次（首次读取失败的 FB/程序段不写缓存，生产上每次发布都会重读）");
        sb.AppendLine($"- spec §13.6-3 清空缓存连续两次，代入后约束集合一致：" + (stable ? "一致"
            : $"不一致（只在首次 {diff.OnlyA.Count}，只在第二次 {diff.OnlyB.Count}，前提不同 {diff.PreconditionsDiffer.Count}）"));
        sb.AppendLine($"- 两次读出的 FB 契约签名：{(contracts.Count == 0 ? "一致" : $"{contracts.Count} 个 FB 不同")}");
        sb.AppendLine();
        sb.Append(RunComparison.Report(diff, "两次运行的约束差异", "首次", "第二次"));
        sb.AppendLine();
        sb.AppendLine($"## 两次读出不同契约的 FB（{contracts.Count}，spec §14：标记人工查看）");
        foreach (var c in contracts)
        {
            sb.AppendLine($"### {c.Fb}");
            foreach (var x in c.OnlyA) sb.AppendLine($"- 只在首次：`{x}`");
            foreach (var x in c.OnlyB) sb.AppendLine($"- 只在第二次：`{x}`");
        }
        sb.AppendLine();
        sb.AppendLine("## 普通网络 AI 状态");
        foreach (var r in runs)
            sb.AppendLine($"- {r.Name}：" + (r.Ai.Networks is null
                ? r.Ai.NetworkError is null ? "未运行" : $"整体出错：{r.Ai.NetworkError}"
                : string.Join("，", r.Ai.Networks.Values.GroupBy(o => o.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))));
        return sb.ToString();
    }
}
