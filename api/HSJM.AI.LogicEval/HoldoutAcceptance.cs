using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>保留集（测试线 PLC2）最终验收：唯一允许运行保留集的入口，整个计划只运行一次。
/// 一次加载、一次 AI 读取，评测与新旧对照共用（不因对照再调一次 AI）。名字规则族按全开（当前发布配置）运行，
/// 结果为「新旧并行期配置」（计划 5 裁定 P1）。
/// 拒绝顺序：不得加 --no-ai/--no-network-ai → 标记已存在 → 参数/文件不齐（只查存在、不读内容）→ 环境探测（测试库、模型，不碰保留集数据）→ 原子占标记；占到标记后才读任何数据。
/// 中途出错时标记保留（不得重跑），并在标记文件追加失败说明。</summary>
static class HoldoutAcceptance
{
    public static readonly NameRuleSwitches Switches = NameRuleSwitches.AllOn;

    public static async Task<int> RunAsync(string[] args)
    {
        if (CliArgs.Flag(args, "--no-ai") || CliArgs.Flag(args, "--no-network-ai"))
        {
            Console.Error.WriteLine("保留集验收必须完整真实运行（不得加 --no-ai / --no-network-ai）");
            return 2;
        }
        var marker = CliArgs.Req(args, "--marker");
        if (File.Exists(marker))
        {
            Console.Error.WriteLine($"保留集验收已运行过（标记文件 {marker} 已存在），不得再次运行");
            return 2;
        }
        var archive = CliArgs.Req(args, "--archive");
        var truthPath = CliArgs.Req(args, "--truth");
        var categories = CliArgs.Req(args, "--categories");
        var outDir = CliArgs.Req(args, "--out-dir");
        var ai = OfflineAiOptions.From(args);
        // 占标记前把能查的都查掉，避免参数写错白白用掉唯一一次（只判断存在，不读内容）
        var problems = new List<string>();
        if (!Path.GetFileName(archive).Contains(Holdout.TestProject.ToString(), StringComparison.Ordinal))
            problems.Add($"--archive 文件名须含保留集测试库项目号 {Holdout.TestProject}（如 holdout-plc2-xmlv2-rerun-{Holdout.TestProject}.zip）");
        foreach (var (name, path) in new[] { ("--archive", archive), ("--truth", truthPath), ("--categories", categories) })
            if (!File.Exists(path)) problems.Add($"{name} 文件不存在：{path}");
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("保留集验收未开始（标记未占用）：\n  " + string.Join("\n  ", problems));
            return 2;
        }
        // 环境探测（审查 Important 2）：测试库或模型不可用时占标记会白白用掉唯一一次；模型 502 更隐蔽——各 FB/程序段只记 Failed，
        // 整次运行照常「完成」。所以先探测（不碰保留集数据），失败不占标记
        try
        {
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Console.WriteLine("环境探测：" + await OfflinePipeline.ProbeAsync(probeTimeout.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"保留集验收未开始（标记未占用）：环境探测失败——{ex.GetType().Name}: {ex.Message}"
                + "\n  请确认测试库 hsjm_ai_new_test 可连接、网站「独立 PLC 解析与候选分析」用途已绑定可用模型、本机代理对 192.168.0.0/16 直连");
            return 2;
        }
        var configuration =Switches.GeneratorSignature.Length == 0 ? "名字规则族全开（新旧并行期配置）" : "关闭 " + Switches.GeneratorSignature;
        if (!HoldoutClaim.TryClaim(marker, $"测试线 PLC2 保留集验收（{configuration}），开始于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}"))
        {
            Console.Error.WriteLine($"保留集验收已运行过（标记文件 {marker} 已存在），不得再次运行");
            return 2;
        }
        try
        {
            Directory.CreateDirectory(outDir);
            var truth = JsonSerializer.Deserialize<TruthFile>(await File.ReadAllTextAsync(truthPath), TruthJson.Options)!;
            var map = CategoryMap.Load(categories);
            var setup = await OfflinePipeline.LoadAsync(Holdout.TestProject, archive, allowHoldout: true);
            var read = await OfflinePipeline.ReadAiAsync(setup, ai);
            var evaluate = await OfflineEval.ReportAsync(setup, Switches, read, truth, map, onlyTruthUnits: false,
                catalogOut: Path.Combine(outDir, "holdout-catalog.md"), factsOut: Path.Combine(outDir, "holdout-facts.json"));
            await File.WriteAllTextAsync(Path.Combine(outDir, "holdout-evaluate.md"), $"> 保留集验收：{configuration}\n\n" + evaluate);
            var (compare, json) = CompareOldNew.Build(setup, read, map, truth);
            await File.WriteAllTextAsync(Path.Combine(outDir, "holdout-compare.md"), compare);
            await File.WriteAllTextAsync(Path.Combine(outDir, "holdout-compare.json"), json);
            await File.AppendAllTextAsync(marker, $"\n完成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine(string.Join("\n", evaluate.Split('\n').Take(16)));
            return 0;
        }
        catch (Exception ex)
        {
            await File.AppendAllTextAsync(marker, $"\n失败于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}：{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
