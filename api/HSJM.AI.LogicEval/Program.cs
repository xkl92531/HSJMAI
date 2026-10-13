using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.LogicEval;

string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
    ?? throw new ArgumentException("缺少参数 " + name);

// 保留集（测试线 PLC2）只能经 holdout-acceptance 运行一次：其它命令在读取任何文件之前拒绝
// （--project / --formal-project 是保留集项目号，或任一 --archive 的文件名含保留集项目号）；
// --truth 的标准答案在反序列化后、产出任何输出之前按项目字段再拦一次（Holdout.ReadTruthAsync）。
// truth-formal 例外（协调方裁定）：它只从正式库只读导出旧规则作为标注底稿，不运行本期生成工具，Task 16 要用它导出保留集正式库项目。
if (args.FirstOrDefault() is not ("holdout-acceptance" or "truth-formal") && TouchesHoldout(args))
{
    Console.Error.WriteLine(OfflinePipeline.HoldoutRefusal);
    Environment.ExitCode = 2;
    return;
}

try
{
    switch (args.FirstOrDefault())
    {
        case "truth-formal": await TruthFormalAsync(); break;
        case "evaluate": await EvaluateAsync(); break;
        case "render-check": RenderCheck(); break;
        case "evaluate-offline":
        {
            var truth = await Holdout.ReadTruthAsync(Arg("--truth"));
            var report = await OfflineEval.RunAsync(long.Parse(Arg("--project")), Arg("--archive"), truth, CategoryMap.Load(Arg("--categories")),
                OfflineAiOptions.From(args), args.Contains("--only-truth-units"), CliArgs.Opt(args, "--catalog-out"), CliArgs.Opt(args, "--facts-out"));
            await File.WriteAllTextAsync(Arg("--out"), report);
            Console.WriteLine(report.Split('\n').Take(14).Aggregate((a, b) => a + "\n" + b));
            break;
        }
        case "diff-facts": Environment.ExitCode = await DiffFacts.RunAsync(args); break;
        case "cache-push": Environment.ExitCode = await CachePush.RunAsync(args); break;
        case "compare-old-new": Environment.ExitCode = await CompareOldNew.RunAsync(args); break;
        case "stability": Environment.ExitCode = await Stability.RunAsync(args); break;
        case "holdout-acceptance": Environment.ExitCode = await HoldoutAcceptance.RunAsync(args); break;
        case "candidates":
            if (!int.TryParse(Arg("--per-category"), out var perCategory) || perCategory < 1)
            {
                Console.Error.WriteLine("参数错误：--per-category 必须是 ≥ 1 的整数");
                Environment.ExitCode = 2;
                break;
            }
            await Candidates.RunAsync(long.Parse(Arg("--project")), Arg("--archive"), CategoryMap.Load(Arg("--categories")),
                perCategory, Arg("--out"));
            break;
        default:
            Console.WriteLine(Usage.Text);
            break;
    }
}
catch (HoldoutRefusedException)
{
    Console.Error.WriteLine(OfflinePipeline.HoldoutRefusal);
    Environment.ExitCode = 2;
}

async Task TruthFormalAsync()
{
    var formalId = long.Parse(Arg("--formal-project"));
    var map = CategoryMap.Load(Arg("--categories"));
    await using var db = await EvalDb.OpenAsync("hsjm_ai_new", readOnly: true);
    var addresses = await EvalDb.AddressesAsync(db, formalId);
    var (rules, skippedRules, skippedSignals) = await EvalDb.RulesAsync(db, formalId, null);
    var units = await EvalDb.FormalUnitsAsync(db, rules.Where(r => r.SourceLuId != null).Select(r => r.SourceLuId!.Value));
    var source = Original353ArchiveSource.Read(Arg("--archive"), formalId,
        addresses.Select(a => new PipelineTag(a.Key, a.Value, "Bool")).ToArray());
    var locator = new UnitLocator(source.Units);
    var facts = new List<EvalFact>();
    int byBlockNetwork = 0, bySignals = 0, unlocated = 0;
    foreach (var rule in rules)
    {
        // 归档网络号数的是 NetworkSource 根，正式库 networkid 来自 Python 解析器，两者可能错位：
        // 块名+网络号找到的程序段必须写入或读到规则的至少一个信号才采用，否则按信号重新定位
        PipelineUnit? unit = rule.SourceLuId is long lu && units.TryGetValue(lu, out var u) ? locator.Find(u.Block, u.Network) : null;
        if (unit != null && !locator.Touches(unit, rule.Signals)) unit = null;
        if (unit != null) byBlockNetwork++;
        else
        {
            unit = locator.Find(rule.Signals);
            if (unit != null) bySignals++; else unlocated++;
        }
        // 正式库没有时序信息，标准答案一律按非延时类处理
        facts.AddRange(ConstraintFacts.FromRule(rule.Operator, rule.Signals, addresses, RuleMeta.None, preconditionsKnown: false,
            map.Of(unit), unit?.BlockName ?? "?", unit?.NetworkOrder, "formal-db:" + rule.Source));
    }
    var file = new TruthFile("formal:" + formalId, "hsjm_ai_new 正式库（用户确认准确率很高）", DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        facts.GroupBy(f => f.Key).Select(g => g.First()).OrderBy(f => f.Category).ThenBy(f => f.Key, StringComparer.Ordinal).ToArray());
    await File.WriteAllTextAsync(Arg("--out"), JsonSerializer.Serialize(file, TruthJson.Options));
    Console.WriteLine($"正式库规则 {rules.Count} 条 → 最小事实 {file.Facts.Count} 条，写入 {Arg("--out")}");
    Console.WriteLine($"定位：按块名+网络号 {byBlockNetwork} 条，按信号回退 {bySignals} 条，未定位 {unlocated} 条");
    Console.WriteLine($"跳过：operator 为空的规则 {skippedRules} 条，信号名或角色为空的信号 {skippedSignals} 行");
}

async Task EvaluateAsync()
{
    var projectId = long.Parse(Arg("--project"));
    var map = CategoryMap.Load(Arg("--categories"));
    var truth = await Holdout.ReadTruthAsync(Arg("--truth"));
    await using var db = await EvalDb.OpenAsync("hsjm_ai_new_test", readOnly: true);
    var addresses = await EvalDb.AddressesAsync(db, projectId);
    var (rules, skippedRules, skippedSignals) = await EvalDb.RulesAsync(db, projectId, "XMLV2%");
    var source = Original353ArchiveSource.Read(Arg("--archive"), projectId,
        addresses.Select(a => new PipelineTag(a.Key, a.Value, "Bool")).ToArray());
    var produced = new List<EvalFact>();
    foreach (var (rule, meta, unit) in RuleUnits.Attribute(rules, source.Units, new UnitLocator(source.Units)))
        produced.AddRange(ConstraintFacts.FromRule(rule.Operator, rule.Signals, addresses, meta,
            preconditionsKnown: true, map.Of(unit), unit?.BlockName ?? "?", unit?.NetworkOrder, rule.Source ?? ""));
    // 标准答案只覆盖抽样程序段时，只评这些程序段的产出（有 Units 时恰为 Units，含标注为零条约束的程序段）
    IReadOnlyList<EvalFact> scoped = args.Contains("--only-truth-units") ? LogicEvaluator.Scope(truth, produced) : produced;
    if (args.Contains("--only-truth-units"))
    {
        var unlocatedDropped = produced.Count(f => f.Block == "?");
        Console.WriteLine($"--only-truth-units：产出事实 {produced.Count} 条，评测范围内 {scoped.Count} 条；"
            + $"其中 {unlocatedDropped} 条因无法定位所在程序段（Block \"?\"）被排除");
    }
    var outside = LogicEvaluator.TruthOutsideUnits(truth);
    if (outside > 0)
        Console.WriteLine($"注意：标准答案有 {outside} 条事实所在程序段不在 Units 列表里（仍计入召回率，请核对抽样范围）");
    var result = LogicEvaluator.Evaluate(truth.Facts, scoped);
    var report = LogicEvaluator.Report(result, $"评测：测试库项目 {projectId} 对照 {truth.Project}");
    await File.WriteAllTextAsync(Arg("--out"), report);
    Console.WriteLine($"召回率 {result.Overall.Recall:P1}，准确率 {result.Overall.Precision:P1}，报告写入 {Arg("--out")}");
    Console.WriteLine($"跳过：operator 为空的规则 {skippedRules} 条，信号名或角色为空的信号 {skippedSignals} 行");
}

void RenderCheck()
{
    var archives = args.Select((a, i) => (a, i)).Where(x => x.a == "--archive" && x.i + 1 < args.Length)
        .Select(x => args[x.i + 1]).ToArray();
    if (archives.Length == 0) throw new ArgumentException("缺少参数 --archive");
    var blocks = args.Select((a, i) => (a, i)).Where(x => x.a == "--block" && x.i + 1 < args.Length)
        .Select(x => args[x.i + 1]).ToArray();
    foreach (var archive in archives)
    {
        var source = Original353ArchiveSource.Read(archive, 1, []);
        int clipped = 0, unusable = 0;
        var maxRaw = 0;
        string maxRawAt = "";
        var failures = new List<string>();
        foreach (var unit in source.Units)
        {
            // 与产品 LoadOrBuildPlainLanguageAsync 相同的单段步骤：渲染 + 截断 + 可用性检查
            var ok = Original353RuleTextService.TryRenderPlainLanguage(unit, out _, out var rawLength, out var error, source.Tags);
            if (error != null)
            {
                failures.Add($"    unit={unit.Id} block={unit.BlockName} network={unit.NetworkOrder?.ToString() ?? "-"} {error.GetType().FullName}: {error.Message}");
                continue;
            }
            if (!ok) unusable++;
            if (rawLength > Original353RuleTextService.PlainLanguageMaxChars) clipped++;
            if (rawLength > maxRaw) { maxRaw = rawLength; maxRawAt = $"{unit.BlockName} 网络 {unit.NetworkOrder?.ToString() ?? "-"}"; }
        }
        Console.WriteLine($"{Path.GetFileName(archive)}：程序段 {source.Units.Count}，渲染异常 {failures.Count}，截断 {clipped}，"
            + $"不可用（无异常）{unusable}，最长原始译文 {maxRaw} 字（{maxRawAt}）");
        foreach (var line in failures) Console.WriteLine(line);
        SclStats(source, blocks);
    }
}

// SCL 程序段翻译统计（裁定 F20：可复现的验证，替代临时脚本）：SCL 程序段的语句种类分布、含 SCL 的 FB 是否仍为纯文字块；
// --block <名字> 另列该块各种语句数
void SclStats(PipelineSnapshot source, IReadOnlyList<string> blockNames)
{
    static string Kinds(IEnumerable<IrStatement> statements) =>
        string.Join(" ", statements.GroupBy(s => SclTranslator.IsSourceControlMarker(s) ? "Return(源码标记)" : s.Kind)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));
    var scl = source.Units.Where(u => UnitTranslator.Translate(u, source.Tags).RootKind == "StructuredText").ToArray();
    var statements = scl.SelectMany(u => UnitTranslator.Translate(u, source.Tags).Statements).ToArray();
    var fbs = scl.Where(u => string.Equals(u.BlockKind, "FB", StringComparison.OrdinalIgnoreCase))
        .Select(u => u.BlockName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var textOnly = fbs.Count(fb => IrBlock.Load(source, fb).IsTextOnly);
    Console.WriteLine($"  SCL 程序段 {scl.Length}，语句 {statements.Length}（{Kinds(statements)}）；含 SCL 的 FB {fbs.Length} 个，其中仍为纯文字块 {textOnly} 个");
    foreach (var name in blockNames)
    {
        var block = IrBlock.Load(source, name);
        var all = block.Units.SelectMany(u => u.Statements).ToArray();
        Console.WriteLine($"  块 {name}：程序段 {block.Units.Count}，语句 {all.Length}（{Kinds(all)}），"
            + $"Assign/Set/Reset 共 {all.Count(s => s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset)}，"
            + $"纯文字块={block.IsTextOnly}");
        if (args.Contains("--dump"))
            foreach (var unit in block.Units) Console.WriteLine(IrText.Unit(unit));
    }
}

bool TouchesHoldout(string[] a)
{
    foreach (var name in new[] { "--project", "--formal-project" })
        for (var i = 0; i + 1 < a.Length; i++)
            if (a[i] == name && long.TryParse(a[i + 1], out var id) && Holdout.IsHoldoutProject(id)) return true;
    for (var i = 0; i + 1 < a.Length; i++)
        if (a[i] == "--archive" && Holdout.IsHoldoutArchive(a[i + 1])) return true;
    return false;
}
