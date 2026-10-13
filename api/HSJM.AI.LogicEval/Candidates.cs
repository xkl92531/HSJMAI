using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Npgsql;

namespace HSJM.AI.LogicEval;

/// <summary>为人工标注生成候选：两次 AI 读取 + 现有发布规则，汇总成标注表。仅评测用，不进入产品流程。</summary>
static class Candidates
{
    public const string SystemPrompt = """
你是西门子 PLC 程序分析专家。下面给你一段 PLC 程序段，以及它调用的块、调用它的块，全部由程序从 TIA 导出的 XML 逐部件如实翻译而来。
任务：读懂这段程序在做什么，列出设备正常时 IO 信号之间一定满足的全部关系（约束）。

推理规则：
1. 只依据翻译出的逻辑语句推理。变量名、注释、块名只能帮助阅读，不能单独作为依据。
2. 跨块时用调用接线把形参替换成实参；未接线的输入取接口默认值；接了常数就用该常数。
3. 约束里的信号只能是「IO 点」表里的点名（逐字照抄）。
4. 输出发出后对应到位输入要过一段动作时间才变化的关系，timing 写 after_delay，能读出时间就填 delaySeconds；存在旁路（非 IO 条件满足即视为到位）时写进 bypass。
5. 到位条件是「A 组或 B 组」时每组各写一条 IMPLIES（Target 为该组全部到位点）。
6. 不确定就不输出。

约束类型：AT_MOST_ONE（Member 最多一个为真）、EXACTLY_ONE、SAME_STATE、IMPLIES（Source 真 → 全部 Target 真）、REQUIRES_0_WHEN_FALSE（Source 为 false 时 Target 必须为 false）、MUST_BE_FALSE。
另列超时报警位（输出发出后预设时间内未到位则置位的位）：给出调用方可见的报警变量完整路径、对应输出 IO 点、到位 IO 点、时间。

只输出一个 JSON 对象：
{"understanding":"…","constraints":[{"operator":"…","signals":[{"name":"IO点名","role":"Source|Target|Member"}],"timing":"instant|after_delay","delaySeconds":null,"bypass":["…"],"evidence":"引用语句编号"}],"timeoutAlarms":[{"alarm":"…","output":"…","feedback":["…"],"delaySeconds":null,"evidence":"…"}]}
""";

    public const string SecondPassSuffix = "\n请逐个输出引脚、逐个线圈检查是否还有遗漏的关系。";

    /// <summary>candidates.json 的一项：一个抽样程序段及其候选事实。AiA/AiB 为两次读取的解析状态（Parsed/Empty/Unparseable）；
    /// SkippedItems 为两次回复中格式不对被跳过的条目数；NonIoConstraints/NonIoAlarms 为含非 IO 点整条丢弃的约束/超时报警数。</summary>
    public sealed record UnitCandidates(string Category, string Block, int? Network, long UnitId, string Prompt,
        string AiA, string AiB, int SkippedItems, int NonIoConstraints, int NonIoAlarms, IReadOnlyList<Candidate> Candidates);

    public sealed record Candidate(string Key, string Kind, string Display, IReadOnlyList<string>? Preconditions, IReadOnlyList<string> From);

    const int MaxCalleeChars = 40000;

    public static async Task RunAsync(long projectId, string archive, CategoryMap map, int perCategory, string outDir)
    {
        if (perCategory < 1) throw new ArgumentOutOfRangeException(nameof(perCategory), "--per-category 必须 ≥ 1");
        Directory.CreateDirectory(Path.Combine(outDir, "prompts"));
        Directory.CreateDirectory(Path.Combine(outDir, "ai"));
        await using var db = await EvalDb.OpenAsync("hsjm_ai_new_test", readOnly: true);
        var addresses = await EvalDb.AddressesAsync(db, projectId);
        var tagInfo = await TagInfoAsync(db, projectId);
        var (rules, skippedRules, skippedSignals) = await EvalDb.RulesAsync(db, projectId, "XMLV2%");
        var source = Original353ArchiveSource.Read(archive, projectId, addresses.Select(a => new PipelineTag(a.Key, a.Value, "Bool")).ToArray());
        var ir = source.Units.ToDictionary(u => u.Id, u => UnitTranslator.Translate(u, source.Tags));

        // 现有发布规则按与 evaluate 相同的方式归属程序段
        var rulesByUnit = RuleUnits.Attribute(rules, source.Units, new UnitLocator(source.Units))
            .Where(x => x.Unit != null).ToLookup(x => x.Unit!.Id);

        var eligible = source.Units.Where(u => UnitLocator.IoTouched(ir[u.Id], addresses.ContainsKey) >= 2).ToList();
        var picked = eligible.GroupBy(map.Of)
            .SelectMany(g =>
            {
                var xs = g.OrderBy(u => u.BlockName, StringComparer.Ordinal).ThenBy(u => u.NetworkOrder).ToList();
                return xs.Count <= perCategory ? xs : Enumerable.Range(0, perCategory).Select(i => xs[(int)((i + 0.5) * xs.Count / perCategory)]);
            })
            .OrderBy(map.Of, StringComparer.Ordinal).ThenBy(u => u.BlockName, StringComparer.Ordinal).ThenBy(u => u.NetworkOrder).ToList();
        var eligibleCategories = eligible.Select(map.Of).ToHashSet(StringComparer.Ordinal);
        var zeroCategories = map.ByCallee.Values.Concat(map.ByLanguage.Values).Append(map.Default).Concat(source.Units.Select(map.Of))
            .Distinct(StringComparer.Ordinal).Where(c => !eligibleCategories.Contains(c)).Order(StringComparer.Ordinal).ToArray();
        if (zeroCategories.Length > 0)
            Console.WriteLine($"没有可抽样程序段（读写 IO 点少于 2 个）的类别：{string.Join("、", zeroCategories)}");
        var names = CandidateFileNames.Assign(picked.Select(u => (u.Id, u.BlockName, u.NetworkOrder)));

        var (baseUrl, model, key) = await ModelAsync(db);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var gate = new SemaphoreSlim(3);
        var results = new Dictionary<long, (string A, string B)>();
        await Task.WhenAll(picked.Select(async u =>
        {
            var name = names[u.Id];
            var prompt = BuildPrompt(source, u, ir, addresses, tagInfo);
            await File.WriteAllTextAsync(Path.Combine(outDir, "prompts", name + ".md"), prompt);
            await gate.WaitAsync();
            try
            {
                var a = await CallAsync(http, baseUrl, model, SystemPrompt, prompt);
                var b = await CallAsync(http, baseUrl, model, SystemPrompt + SecondPassSuffix, prompt);
                await File.WriteAllTextAsync(Path.Combine(outDir, "ai", name + ".A.json"), a);
                await File.WriteAllTextAsync(Path.Combine(outDir, "ai", name + ".B.json"), b);
                lock (results) results[u.Id] = (a, b);
            }
            finally { gate.Release(); }
        }));

        var worksheet = new StringBuilder("# 标注表\n\n判定栏：对 / 错 / 缺前提；最后写本段遗漏的约束。\n");
        var summary = new List<UnitCandidates>();
        var statusTotals = new Dictionary<AiReplyStatus, int>();
        int skippedTotal = 0, nonIoConstraintTotal = 0, nonIoAlarmTotal = 0;
        foreach (var u in picked)
        {
            var category = map.Of(u);
            var name = names[u.Id];
            var candidates = new Dictionary<string, (EvalFact Fact, List<string> From)>(StringComparer.Ordinal);
            void Add(IEnumerable<EvalFact> facts, string from)
            {
                foreach (var f in facts)
                {
                    if (!candidates.TryGetValue(f.Key, out var entry)) candidates[f.Key] = entry = (f, []);
                    if (!entry.From.Contains(from)) entry.From.Add(from);
                }
            }
            var (textA, textB) = results.TryGetValue(u.Id, out var r) ? r : ("", "");
            var a = AiCandidateFacts.Parse(textA, addresses, category, u.BlockName, u.NetworkOrder);
            var b = AiCandidateFacts.Parse(textB, addresses, category, u.BlockName, u.NetworkOrder);
            foreach (var x in new[] { a, b }) statusTotals[x.Status] = statusTotals.GetValueOrDefault(x.Status) + 1;
            var skipped = a.SkippedItems + b.SkippedItems;
            var nonIoConstraints = a.NonIoConstraints + b.NonIoConstraints;
            var nonIoAlarms = a.NonIoAlarms + b.NonIoAlarms;
            skippedTotal += skipped; nonIoConstraintTotal += nonIoConstraints; nonIoAlarmTotal += nonIoAlarms;
            Add(a.Facts, "AI-A");
            Add(b.Facts, "AI-B");
            foreach (var (rule, meta, _) in rulesByUnit[u.Id])
                Add(ConstraintFacts.FromRule(rule.Operator, rule.Signals, addresses, meta, true, category, u.BlockName, u.NetworkOrder, rule.Source ?? ""), "现有规则");

            var ordered = candidates.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            worksheet.Append($"\n## [{category}] {u.BlockName} 网络 {u.NetworkOrder}\n\n材料：prompts/{name}.md\n\n");
            worksheet.Append($"AI 回复：A {StatusText(a.Status)}，B {StatusText(b.Status)}");
            if (skipped > 0) worksheet.Append($"；格式不对跳过 {skipped} 条");
            if (nonIoConstraints + nonIoAlarms > 0) worksheet.Append($"；含非IO点，已丢弃约束 {nonIoConstraints} 条、超时报警 {nonIoAlarms} 条");
            worksheet.Append("\n\n| 事实 | 前提 | 来源 | 判定 |\n|---|---|---|---|\n");
            foreach (var (k, (f, from)) in ordered)
                worksheet.Append($"| `{k}` | {string.Join("、", f.Preconditions ?? [])} | {string.Join("、", from)} |  |\n");
            worksheet.Append("\n本段遗漏的约束：\n");
            summary.Add(new UnitCandidates(category, u.BlockName, u.NetworkOrder, u.Id, $"prompts/{name}.md",
                a.Status.ToString(), b.Status.ToString(), skipped, nonIoConstraints, nonIoAlarms,
                ordered.Select(x => new Candidate(x.Key, x.Value.Fact.Kind, x.Value.Fact.Display, x.Value.Fact.Preconditions, x.Value.From)).ToArray()));
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "worksheet.md"), worksheet.ToString());
        await File.WriteAllTextAsync(Path.Combine(outDir, "candidates.json"), JsonSerializer.Serialize(summary, TruthJson.Options));
        Console.WriteLine($"抽样 {picked.Count} 段（可抽样 {eligible.Count} 段），标注表写入 {Path.Combine(outDir, "worksheet.md")}，候选写入 {Path.Combine(outDir, "candidates.json")}");
        Console.WriteLine($"AI 回复：已解析 {statusTotals.GetValueOrDefault(AiReplyStatus.Parsed)} 次，空回复 {statusTotals.GetValueOrDefault(AiReplyStatus.Empty)} 次，"
            + $"无法解析 {statusTotals.GetValueOrDefault(AiReplyStatus.Unparseable)} 次；格式不对跳过 {skippedTotal} 条；"
            + $"含非IO点丢弃约束 {nonIoConstraintTotal} 条、超时报警 {nonIoAlarmTotal} 条");
        Console.WriteLine($"跳过：operator 为空的规则 {skippedRules} 条，信号名或角色为空的信号 {skippedSignals} 行");
    }

    static string StatusText(AiReplyStatus s) => s switch
    {
        AiReplyStatus.Parsed => "已解析", AiReplyStatus.Empty => "空回复（请求失败或无内容）", _ => "无法解析（无 JSON、被截断或思考未结束）"
    };

    static string BuildPrompt(PipelineSnapshot source, PipelineUnit u, IReadOnlyDictionary<long, IrUnit> ir,
        IReadOnlyDictionary<string, string> addresses, IReadOnlyDictionary<string, (string Type, string Comment)> tagInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 待分析的程序段：{u.BlockKind} {u.BlockName} 网络 {u.NetworkOrder}（{u.Language}）\n");
        sb.AppendLine("## 1. 本程序段逻辑（逐部件翻译，未经删减）");
        sb.AppendLine(IrText.Unit(ir[u.Id]));
        sb.AppendLine($"\n## 2. 本程序段所在块 {u.BlockName} 的接口");
        sb.AppendLine(InterfaceReader.Render(InterfaceReader.Read(u.InterfaceXml), 40));
        var section = 3;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Name, int Depth)>(u.Callees.Select(c => (c, 1)));
        while (queue.Count > 0)
        {
            var (name, depth) = queue.Dequeue();
            if (!seen.Add(name)) continue;
            var body = source.Units.Where(x => string.Equals(x.BlockName, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.NetworkOrder).ToList();
            sb.AppendLine($"\n## {section++}. 被调用的块 {name}（第 {depth} 层）");
            if (body.Count == 0 || body.All(b => string.IsNullOrWhiteSpace(b.RawContent)))
            { sb.AppendLine("- （本工程中没有该块的程序体：可能是系统块、加密块或库块。）"); continue; }
            sb.AppendLine("### 接口"); sb.AppendLine(InterfaceReader.Render(InterfaceReader.Read(body[0].InterfaceXml)));
            sb.AppendLine("### 程序体");
            var total = 0;
            foreach (var b in body)
            {
                var t = IrText.Unit(ir[b.Id]);
                if (total + t.Length > MaxCalleeChars)
                {
                    // 第一个网络本身就超长时截取前面部分，不让程序体整段为空
                    if (total == 0) sb.AppendLine(t[..MaxCalleeChars]);
                    sb.AppendLine($"- …（程序体过长，超过 {MaxCalleeChars} 字的部分省略）");
                    break;
                }
                total += t.Length;
                sb.AppendLine(t);
                if (depth < 2) foreach (var c in b.Callees) queue.Enqueue((c, depth + 1));
            }
        }
        var callers = source.Units.Where(x => x.Callees.Contains(u.BlockName, StringComparer.OrdinalIgnoreCase)).Take(3).ToList();
        sb.AppendLine($"\n## {section++}. 谁调用了 {u.BlockName}（向上一层）");
        if (callers.Count == 0) sb.AppendLine("- （未找到调用方）");
        foreach (var c in callers) sb.AppendLine(IrText.Unit(ir[c.Id]));
        var text = sb.ToString();
        var used = addresses.Keys.Where(n => Regex.IsMatch(text, $@"(?<![\w.\-]){Regex.Escape(n)}(?![\w\-])")).ToArray();
        sb.AppendLine($"\n## {section}. 上文出现的 IO 点（只有这些是可直接采集的实时信号）");
        sb.AppendLine("| 点名 | 地址 | 类型 | 注释 |\n|---|---|---|---|");
        foreach (var n in used.OrderBy(n => addresses[n], StringComparer.Ordinal))
        {
            var (type, comment) = tagInfo.TryGetValue(n, out var info) ? info : ("", "");
            sb.AppendLine($"| {n} | {addresses[n]} | {type} | {comment.Replace('|', '/')} |");
        }
        return sb.ToString();
    }

    static async Task<Dictionary<string, (string Type, string Comment)>> TagInfoAsync(NpgsqlConnection c, long projectId)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand("select tagname, coalesce(datatype,''), coalesce(comment,'') from ai_plc_iotag where projectid=@p and not isdeleted and coalesce(tagname,'')<>''", c);
        cmd.Parameters.AddWithValue("p", projectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) map.TryAdd(rd.GetString(0), (rd.GetString(1), rd.GetString(2)));
        return map;
    }

    /// <summary>网站用途绑定 independent_plc 的模型；模型须已启用且未删除（与 PipelineModelCatalog 口径一致）。</summary>
    static async Task<(string BaseUrl, string Model, string Key)> ModelAsync(NpgsqlConnection c)
    {
        await using var cmd = new NpgsqlCommand("""
            select m.baseurl, m.modelcode, coalesce(m.apikey,''), coalesce(m.isenabled,false) and not coalesce(m.isdeleted,false)
            from ai_model_purpose_binding b left join ai_llm_model m on m.id=b.model_id
            where b.purpose_code='independent_plc'
            """, c);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) throw new InvalidOperationException("网站上未绑定「独立 PLC 解析与候选分析」用途的模型");
        if (rd.IsDBNull(0) || rd.IsDBNull(3) || !rd.GetBoolean(3))
            throw new InvalidOperationException("「独立 PLC 解析与候选分析」用途绑定的模型不存在、已停用或已删除");
        return (rd.GetString(0), rd.GetString(1), rd.GetString(2));
    }

    static async Task<string> CallAsync(HttpClient http, string baseUrl, string model, string system, string user)
    {
        var body = new JsonObject
        {
            ["model"] = model, ["temperature"] = 0.1, ["max_tokens"] = 16000, ["stream"] = false,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system },
                                         new JsonObject { ["role"] = "user", ["content"] = user })
        };
        try
        {
            using var resp = await http.PostAsync(baseUrl.TrimEnd('/') + "/chat/completions",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"模型请求失败：HTTP {(int)resp.StatusCode} {text[..Math.Min(text.Length, 300)]}");
                return "";
            }
            return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"模型请求失败：{ex.GetType().Name} {ex.Message}");
            return "";
        }
    }
}
