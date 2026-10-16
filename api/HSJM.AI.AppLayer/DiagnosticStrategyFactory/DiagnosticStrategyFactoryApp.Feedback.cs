using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory;

public partial class DiagnosticStrategyFactoryApp
{
    /// <summary>Resolve XMLV2 timed feedback rules (command → in-position feedback) against live values, scoped by PLC.
    /// 2026-10-15：判定逻辑不再只认 FB3002_Valve。来源按优先级：
    /// ① 报警清单 Timeout 行（FB 程序体契约在调用点代入得到，任何 FB 库都适用）：命令 = 行的输出，
    ///    到位 = 各到位组（任一组全部为 true）或任一旁路为 true，超时报警 = 行的报警位；同一输出有多行时不猜。
    /// ② 调用方是 FlgNet 且调用 FB3002_Valve：按调用点实际接线的完整到位逻辑（原有路径，含唯一写入检查）。
    /// 两者都没有的规则不在这里判（之后按「已识别约束，暂不判故障」记无法判定），与改动前一致。
    /// No schema migration, rule regeneration, natural-language parsing or serial per-rule delays.</summary>
    private async Task<HashSet<long>> EvaluateFullFeedbackLogicAsync(List<PLC_IOConstraintRuleEntity> rules,
        ConstraintCheckOutcome outcome, List<DiagnosticIssue> violations, List<string> log, CancellationToken ct,
        IReadOnlySet<long> manualProjects)
    {
        var handled = new HashSet<long>();
        var timed = rules.Where(r => r.Source == "XMLV2_TIMED" && r.Operator is "IMPLIES" or "IMPLIES_ANY"
            && r.ProjectId.HasValue).ToArray();
        if (timed.Length == 0) return handled;
        var ids = timed.Select(r => r.Id).ToList();
        var signals = await _pLC_IOConstraintSignalService.GetListAsync(s => ids.Contains(s.RuleId) && !s.IsDeleted);
        foreach (var group in timed.GroupBy(r => r.ProjectId.Value))
        {
            ct.ThrowIfCancellationRequested();
            string[] RuleSignals(PLC_IOConstraintRuleEntity rule, string role) => signals
                .Where(s => s.RuleId == rule.Id && !s.IsDeleted && s.SignalRole == role)
                .Select(s => s.SignalName).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var plans = new Dictionary<long, FeedbackLogicPlan>();

            // ① 报警清单 Timeout 行
            var catalog = await LoadTimeoutCatalogAsync(group.Key, log, ct);
            foreach (var rule in group)
            {
                var sources = RuleSignals(rule, "Source");
                if (sources.Length != 1) continue;
                var plan = FullFeedbackLogic.FromCatalog(sources[0], RuleSignals(rule, "Target"),
                    FeedbackBypass(rule), catalog);
                if (plan != null) plans[rule.Id] = plan;
            }

            // ② FB3002_Valve 调用点完整到位逻辑（原有路径）
            var xmlCandidates = group.Where(r => !plans.ContainsKey(r.Id)).ToArray();
            if (xmlCandidates.Length > 0 && _pLC_BlockService is not null && _pLC_LogicUnitService is not null)
                await AddValveCallPlansAsync(group.Key, xmlCandidates, RuleSignals, plans, ct);
            if (plans.Count == 0) continue;

            var tagGroups = (await _pLC_IOTagService.GetListAsync(t => t.ProjectId == group.Key && !t.IsDeleted))
                .Where(t => t.ProjectId == group.Key && !t.IsDeleted && !string.IsNullOrWhiteSpace(t.TagName))
                .GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase).ToArray();
            var ambiguous = tagGroups.Where(g => g.Count() != 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tags = tagGroups.Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
            var requests = await BuildFeedbackReadRequestsAsync(group.Key,
                plans.Values.SelectMany(p => p.Symbols).Where(s => !ambiguous.Contains(s)), tags, ct);
            var reads = new List<PLC_ExactReadResult>();
            // Bounded batches collect every leaf, including DB bypass/simulation bits, once per project.
            foreach (var batch in requests.Chunk(200))
            {
                try { reads.AddRange(await _pLC_VariableAccessService.ReadExactDataAsync(group.Key, batch, ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* Failed batches remain Unknown; never substitute false. */ }
            }
            // 同一命令按到位组拆成的多条规则共用一份到位逻辑：只判一次，避免同一报警报出多条违规。
            foreach (var same in group.Where(r => plans.ContainsKey(r.Id))
                         .GroupBy(r => FullFeedbackLogic.PlanKey(plans[r.Id]), StringComparer.OrdinalIgnoreCase))
            {
                var rule = same.First();
                var evaluation = FullFeedbackLogic.Judge(rule, plans[rule.Id], reads, tags, DateTimeOffset.UtcNow);
                foreach (var member in same) handled.Add(member.Id);
                if (manualProjects.Contains(group.Key) && (evaluation.Verdict == RuleVerdict.Passed
                    || evaluation.Issue?.Reason.StartsWith("动作已发出", StringComparison.Ordinal) == true))
                {
                    log.Add($"完整反馈规则 [{rule.Id}] {rule.RuleName}: 手动模式跳过普通动作判断；原生报警未激活");
                    continue;
                }
                outcome.Record(evaluation);
                if (evaluation.Verdict == RuleVerdict.Violated) violations.Add(evaluation.Issue);
                log.Add($"完整反馈规则 [{string.Join(",", same.Select(r => r.Id))}] {rule.RuleName}: {evaluation.Verdict}；"
                    + (evaluation.Issue?.Reason ?? FullFeedbackLogic.Format(plans[rule.Id].Feedback)));
            }
        }
        outcome.SetViolations(violations);
        return handled;
    }

    /// <summary>读本项目报警清单的 Timeout 行。独立管道未启用 / 读取失败时返回空（记日志），不影响约束检查。</summary>
    private async Task<IReadOnlyList<HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.AlarmCatalogRow>> LoadTimeoutCatalogAsync(
        long projectId, List<string> log, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.AlarmCatalogRow> rows;
            if (TimeoutAlarmCatalogOverride is not null)
                rows = await TimeoutAlarmCatalogOverride(projectId, ct);
            else if (_pipelineStore is not null && _pipelineStore.TryGetConnectionString(out var connection, out _))
                rows = (await new HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.AlarmCatalogStore(connection)
                    .LoadAsync(projectId, ct)).Rows;
            else
                return [];
            return (rows ?? []).Where(r => r != null && string.Equals(r.Kind, "Timeout", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.OutputSignal) && !string.IsNullOrWhiteSpace(r.AlarmSymbol)).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.Add($"PLC {projectId} 报警清单读取失败，完整反馈判定不用超时报警：{ex.Message}");
            _logger?.LogWarning(ex, "[约束检查] 项目 {ProjectId} 报警清单读取失败，完整反馈判定不用超时报警", projectId);
            return [];
        }
    }

    /// <summary>原有路径：调用方 FlgNet 里 FB3002_Valve 调用点的完整到位逻辑，命令与报警位须全项目唯一写入。</summary>
    private async Task AddValveCallPlansAsync(long projectId, IReadOnlyList<PLC_IOConstraintRuleEntity> rules,
        Func<PLC_IOConstraintRuleEntity, string, string[]> ruleSignals, Dictionary<long, FeedbackLogicPlan> plans,
        CancellationToken ct)
    {
        var names = rules.Select(FeedbackBlockName).OfType<string>().Distinct().ToList();
        if (names.Count == 0) return;
        var blocks = (await _pLC_BlockService.GetListAsync(b => b.ProjectId == projectId && !b.IsDeleted && names.Contains(b.BlockName)))
            .Where(b => b.ProjectId == projectId && !b.IsDeleted && names.Contains(b.BlockName)).ToArray();
        var blockIds = blocks.Select(b => b.Id).ToList();
        if (blockIds.Count == 0) return;
        var units = (await _pLC_LogicUnitService.GetListAsync(u => u.BlockId != null && blockIds.Contains(u.BlockId.Value) && !u.IsDeleted))
            .Where(u => u.BlockId.HasValue && blockIds.Contains(u.BlockId.Value) && !u.IsDeleted).ToArray();
        var found = new Dictionary<long, FeedbackLogicPlan>();
        var compiled = units.GroupBy(u => u.BlockId.Value).ToDictionary(g => g.Key,
            g => g.SelectMany(u => FullFeedbackLogic.Extract(u.RawContent)).ToArray());
        foreach (var rule in rules)
        {
            var sources = ruleSignals(rule, "Source");
            if (sources.Length != 1) continue;
            var ownerBlocks = blocks.Where(b => b.BlockName == FeedbackBlockName(rule)).Select(b => b.Id).ToArray();
            if (ownerBlocks.Length != 1) continue;
            var candidates = compiled.GetValueOrDefault(ownerBlocks[0], [])
                .Where(p => string.Equals(p.Command, sources[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1) found[rule.Id] = candidates[0];
        }
        if (found.Count == 0) return;
        var tagGroups = (await _pLC_IOTagService.GetListAsync(t => t.ProjectId == projectId && !t.IsDeleted))
            .Where(t => t.ProjectId == projectId && !t.IsDeleted && !string.IsNullOrWhiteSpace(t.TagName))
            .GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase).ToArray();
        var tags = tagGroups.Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        var writeNames = found.Values.SelectMany(p => new[] { p.Command, p.Alarm })
            .SelectMany(name => new[] { name, tags.GetValueOrDefault(name)?.Address }
                .OfType<string>().SelectMany(symbol => symbol.Split('.').Select((_, i) => string.Join('.', symbol.Split('.').Take(i + 1)))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var writerRows = (await _pLC_VariableAccessService.GetListAsync(v => v.ProjectId == projectId
            && !v.IsDeleted && writeNames.Contains(v.VariableName) && (v.AccessType == "WRITE" || v.AccessType == "READWRITE")))
            .Where(v => v.ProjectId == projectId && !v.IsDeleted && writeNames.Contains(v.VariableName)
                && v.AccessType is "WRITE" or "READWRITE").ToArray();
        var extraIds = writerRows.Where(v => v.LogicUnitId.HasValue).Select(v => v.LogicUnitId.Value)
            .Except(units.Select(u => u.Id)).Distinct().ToList();
        var extra = extraIds.Count == 0 ? new List<PLC_LogicUnitEntity>()
            : await _pLC_LogicUnitService.GetListAsync(u => extraIds.Contains(u.Id) && !u.IsDeleted);
        var allUnits = units.Concat(extra.Where(u => extraIds.Contains(u.Id) && !u.IsDeleted)).DistinctBy(u => u.Id).ToArray();
        var unknownWriters = writerRows.Where(v => !v.LogicUnitId.HasValue
                || !allUnits.Any(u => u.Id == v.LogicUnitId && !string.IsNullOrWhiteSpace(u.RawContent)))
            .Select(v => v.VariableName).ToArray();
        var writes = FullFeedbackLogic.GlobalWriters(allUnits.Select(u => u.RawContent));
        foreach (var (id, plan) in found)
        {
            bool Sole(string name) => writes != null && !unknownWriters.Any(w => FullFeedbackLogic.Overlaps(w, name))
                && writes.Count(w => FullFeedbackLogic.Overlaps(w, name)
                    || tags.TryGetValue(name, out var t) && !string.IsNullOrEmpty(t.Address) && FullFeedbackLogic.Overlaps(w, t.Address)) == 1;
            if (Sole(plan.Command) && Sole(plan.Alarm)) plans[id] = plan;
        }
    }

    private static string[] FeedbackBypass(PLC_IOConstraintRuleEntity rule) => ValidStatesStrings(rule, "BypassSignals");

    private static string[] ValidStatesStrings(PLC_IOConstraintRuleEntity rule, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(rule.ValidStates ?? "");
            return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private async Task<PLC_ExactReadRequest[]> BuildFeedbackReadRequestsAsync(long projectId,
        IEnumerable<string> symbols, IReadOnlyDictionary<string, PLC_IOTagEntity> tags, CancellationToken ct)
    {
        var requests = new List<PLC_ExactReadRequest>();
        var dbSymbols = new List<PLC_ExactReadRequest>();
        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A physical IO tag may itself contain dots. DB members must still resolve through the
            // uploaded interface, even if an old IO tag row happens to use the same XML spelling.
            if (tags.TryGetValue(symbol, out var tag) && !string.IsNullOrWhiteSpace(tag.TableName)
                && (!symbol.Contains('.') || System.Text.RegularExpressions.Regex.IsMatch(tag.Address ?? "",
                    @"^%?[IQ]X?\d+(?:\.\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                requests.Add(new PLC_ExactReadRequest(symbol, tag.TableName, tag.TagName));
            else if (ConstraintDbSignals.Request(symbol) is { } request)
                dbSymbols.Add(request);
        }
        if (dbSymbols.Count == 0 || _feedbackBlockInterfaces == null) return requests.ToArray();
        ct.ThrowIfCancellationRequested();
        var names = dbSymbols.Select(r => r.BlockName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Interface rows have no project key; their owning DB block supplies the project boundary.
        var blocks = (await _pLC_BlockService.GetListAsync(b => b.ProjectId == projectId && !b.IsDeleted))
            .Where(b => b.ProjectId == projectId && !b.IsDeleted && b.BlockName != null && names.Contains(b.BlockName))
            .GroupBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1 && new[] { "DB", "GlobalDB", "InstanceDB" }
                .Contains(g.Single().BlockType, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        var blockIds = blocks.Values.Select(b => b.Id).ToList();
        if (blockIds.Count == 0) return requests.ToArray();
        ct.ThrowIfCancellationRequested();
        var members = (await _feedbackBlockInterfaces.GetListAsync(m => m.BlockId.HasValue
            && blockIds.Contains(m.BlockId.Value) && !m.IsDeleted))
            .Where(m => m.BlockId.HasValue && blockIds.Contains(m.BlockId.Value) && !m.IsDeleted)
            .GroupBy(m => m.BlockId.Value)
            .ToDictionary(g => g.Key, g => AlarmVariablePaths.Index(g.Select(m => m.VariablePath)));
        ct.ThrowIfCancellationRequested();
        foreach (var symbol in dbSymbols)
        {
            if (!blocks.TryGetValue(symbol.BlockName, out var block) || !members.TryGetValue(block.Id, out var index)) continue;
            var memberPath = AlarmVariablePaths.Find(index, symbol.VariablePath);
            if (memberPath == null) continue; // Missing/ambiguous paths stay Unknown; never guess the old spelling.
            requests.Add(new PLC_ExactReadRequest(symbol.RequestKey, block.BlockName, memberPath));
        }
        return requests.ToArray();
    }

    private static string FeedbackBlockName(PLC_IOConstraintRuleEntity rule)
    {
        try
        {
            using var doc = JsonDocument.Parse(rule.ValidStates ?? "");
            return doc.RootElement.TryGetProperty("BlockName", out var block) && block.ValueKind == JsonValueKind.String
                ? block.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
