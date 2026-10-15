#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding
{
    /// <summary>块↔工站绑定状态，记在 ai_plc_stationblockbinding.reason 里（不改表结构）。</summary>
    public enum StationBindingStatus { Manual, Suggested, Rejected }

    /// <summary>块的有效工站从哪里来。</summary>
    public enum StationBindingSource { Manual, BlockStation, Suggested }

    public sealed record StationBindingResolution(string StationCode, StationBindingSource Source);

    public sealed record GraphBlockRef(long BlockId, long ProjectId, string BlockName, string? StationCode);

    /// <summary>一次手动绑定要做的数据变更。</summary>
    public sealed class ManualBindPlan
    {
        public List<PLC_StationBlockBindingEntity> ToInsert { get; } = new();
        public List<long> SoftDeleteIds { get; } = new();
        public List<GraphBlockRef> BlocksToStamp { get; } = new();
        public List<(string BlockName, string Reason)> Skipped { get; } = new();

        /// <summary>有绑定行或块工位要变的项目（据此排队 GRAPH 步位重新注册）</summary>
        public List<long> ChangedProjectIds { get; } = new();
    }

    /// <summary>
    /// 块↔工站绑定规则（2026-10-14「自动建议 + 人工确认」）。
    /// - 手动绑定说了算；提取只生成自动建议，不碰手动绑定和已拒绝的建议。
    /// - 有效工站：手动绑定 → 块工位字段（提取时按染色回填）→ 高置信自动建议；已拒绝的不用。
    /// - 绑定按「项目 + 块名」记，同名块只保留一条有效绑定（同名块以前会被处理多次、绑出多条）。
    /// - 同一 GRAPH 模板 FB 的多个实例 DB 按各自名字里的工位号归工位，避免整组实例都记到 FB 的工位上。
    /// - 所有工位都要过本 PLC 的工位白名单，并统一成白名单里的写法（OP20 / OP020 视为同一工位）。
    /// </summary>
    internal static class StationBlockBindingRules
    {
        /// <summary>与 PLC_GraphStationBindingApp.ManualReason 相同，勿改文案（历史手动行据此识别）</summary>
        public const string ManualReason = "手动绑定";
        public const string SuggestedReasonPrefix = "自动建议";
        public const string RejectedReason = "已拒绝";

        /// <summary>自动建议达到这个置信度才拿来当有效工站</summary>
        public const decimal SuggestionUseThreshold = 0.6m;

        private const decimal NameRuleConfidence = 0.9m;
        private const string ColoringReasonPrefix = "DerivedFromStationColoring";
        private const int MaxNamesInWarning = 5;

        public static string SuggestedReason(string detail) => $"{SuggestedReasonPrefix}:{detail}";

        /// <summary>手动绑定 / 已拒绝按原文识别，其余（含 10-09 之前的自动行、空原因）都算自动建议。</summary>
        public static StationBindingStatus Classify(string? reason)
        {
            var r = reason?.Trim();
            if (string.Equals(r, ManualReason, StringComparison.Ordinal)) return StationBindingStatus.Manual;
            if (string.Equals(r, RejectedReason, StringComparison.Ordinal)) return StationBindingStatus.Rejected;
            return StationBindingStatus.Suggested;
        }

        /// <summary>同一块的绑定行里该用哪些：有手动行只用手动行，否则用自动建议；已拒绝的永远不用。</summary>
        public static List<PLC_StationBlockBindingEntity> SelectEffective(IEnumerable<PLC_StationBlockBindingEntity> rows)
        {
            var live = Live(rows).ToList();
            var manual = live.Where(r => Classify(r.Reason) == StationBindingStatus.Manual).ToList();
            return manual.Count > 0
                ? manual
                : live.Where(r => Classify(r.Reason) == StationBindingStatus.Suggested).ToList();
        }

        /// <summary>块的有效工站：手动绑定 → 块工位字段 → 高置信自动建议（多个工位同分时不用）。</summary>
        public static StationBindingResolution? ResolveBlockStation(
            IEnumerable<PLC_StationBlockBindingEntity> bindings,
            string? blockStationCode,
            HashSet<string>? allowed)
        {
            var rows = Live(bindings).Where(r => !string.IsNullOrWhiteSpace(r.StationCode)).ToList();

            var manual = rows
                .Where(r => Classify(r.Reason) == StationBindingStatus.Manual)
                .OrderByDescending(r => r.ModifyTime ?? r.CreateTime ?? DateTime.MinValue)
                .Select(r => Canonicalize(r.StationCode, allowed))
                .FirstOrDefault(code => code != null);
            if (manual != null)
                return new StationBindingResolution(manual, StationBindingSource.Manual);

            var blockStation = Canonicalize(blockStationCode, allowed);
            if (blockStation != null)
                return new StationBindingResolution(blockStation, StationBindingSource.BlockStation);

            var suggested = PickUniqueBest(rows
                .Where(r => Classify(r.Reason) == StationBindingStatus.Suggested
                            && (r.Confidence ?? 0m) >= SuggestionUseThreshold)
                .Select(r => (Canonicalize(r.StationCode, allowed), r.Confidence ?? 0m)));
            return suggested == null ? null : new StationBindingResolution(suggested, StationBindingSource.Suggested);
        }

        /// <summary>
        /// 工位提取时生成自动建议。
        /// 名字里只有一个工位号的块按名字建议（优先）；否则只采用有直接染色证据、且没有多工位冲突的打分结果——
        /// 共享块（多个工位都在用）和只靠调用链传递来的证据不建议，这两类是以前乱绑的主要来源。
        /// 已有手动绑定的块不建议；被拒绝过的「块 + 工位」不再建议；白名单外的工位不建议。每个块名最多一条。
        /// </summary>
        public static List<PLC_StationBlockBindingEntity> BuildSuggestions(
            long projectId,
            IEnumerable<PLC_StationBlockBindingEntity> scored,
            IEnumerable<GraphBlockRef> blocks,
            IEnumerable<PLC_StationBlockBindingEntity> kept,
            HashSet<string>? allowed)
        {
            var keptRows = Live(kept).ToList();
            var manualNames = keptRows
                .Where(r => Classify(r.Reason) == StationBindingStatus.Manual)
                .Select(r => NameKey(r.BlockName))
                .ToHashSet(StringComparer.Ordinal);
            var rejected = keptRows
                .Where(r => Classify(r.Reason) == StationBindingStatus.Rejected)
                .Select(r => (NameKey(r.BlockName), FamilyKey(r.StationCode)))
                .ToHashSet();

            var result = new Dictionary<string, PLC_StationBlockBindingEntity>(StringComparer.Ordinal);

            bool TryAdd(long? blockId, string blockName, string? station, decimal confidence, string detail, string? evidenceJson)
            {
                var key = NameKey(blockName);
                if (key.Length == 0 || manualNames.Contains(key) || result.ContainsKey(key)) return false;
                var code = Canonicalize(station, allowed);
                if (code == null || rejected.Contains((key, FamilyKey(code)))) return false;
                result[key] = new PLC_StationBlockBindingEntity
                {
                    ProjectId = projectId,
                    BlockId = blockId,
                    BlockName = blockName.Trim(),
                    StationCode = code,
                    Confidence = Math.Round(confidence, 3),
                    Reason = SuggestedReason(detail),
                    IsShared = false,
                    EvidenceJson = evidenceJson,
                };
                return true;
            }

            foreach (var block in (blocks ?? Enumerable.Empty<GraphBlockRef>())
                         .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                         .OrderBy(b => b.BlockId))
            {
                var nameStation = SingleStationInText(block.BlockName);
                if (nameStation != null)
                    TryAdd(block.BlockId, block.BlockName, nameStation, NameRuleConfidence, "名称含工位号", null);
            }

            foreach (var candidate in scored ?? Enumerable.Empty<PLC_StationBlockBindingEntity>())
            {
                if (candidate == null
                    || candidate.IsShared == true
                    || string.IsNullOrWhiteSpace(candidate.BlockName)
                    || !(candidate.Reason ?? string.Empty).StartsWith(ColoringReasonPrefix, StringComparison.Ordinal))
                    continue;
                TryAdd(candidate.BlockId, candidate.BlockName, candidate.StationCode,
                    candidate.Confidence ?? 0m, "工位染色", candidate.EvidenceJson);
            }

            return result.Values.ToList();
        }

        /// <summary>
        /// 手动绑定计划：按「项目 + 块名」只绑一次（同名块一起勾选也只插一条），
        /// 同名下其它工站的行、自动建议、已拒绝行和重复的手动行一并软删。
        /// </summary>
        public static ManualBindPlan PlanManualBind(
            string stationCode,
            IEnumerable<GraphBlockRef> requested,
            IEnumerable<PLC_StationBlockBindingEntity> existing,
            long operatorId,
            string operatorName,
            DateTime now)
        {
            var plan = new ManualBindPlan();
            var existingRows = Live(existing).ToList();

            foreach (var group in (requested ?? Enumerable.Empty<GraphBlockRef>())
                         .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                         .GroupBy(b => (b.ProjectId, Name: NameKey(b.BlockName))))
            {
                var first = group.OrderBy(b => b.BlockId).First();
                var blockName = first.BlockName.Trim();
                var rows = existingRows
                    .Where(r => r.ProjectId == group.Key.ProjectId && NameKey(r.BlockName) == group.Key.Name)
                    .ToList();

                var keep = rows
                    .Where(r => Classify(r.Reason) == StationBindingStatus.Manual && SameStation(r.StationCode, stationCode))
                    .OrderBy(r => r.CreateTime ?? DateTime.MinValue)
                    .FirstOrDefault();

                if (keep != null)
                {
                    plan.Skipped.Add((blockName, "已绑定该工站"));
                }
                else
                {
                    plan.ToInsert.Add(new PLC_StationBlockBindingEntity
                    {
                        ProjectId = group.Key.ProjectId,
                        StationCode = stationCode,
                        BlockId = first.BlockId,
                        BlockName = blockName,
                        Confidence = 1.0m,
                        Reason = ManualReason,
                        IsShared = false,
                        CreaterId = operatorId,
                        CreaterName = operatorName,
                        CreateTime = now,
                        ModifierId = operatorId,
                        ModifierName = operatorName,
                        ModifyTime = now,
                    });
                }

                var toDelete = rows.Where(r => !ReferenceEquals(r, keep)).Select(r => r.Id).ToList();
                var toStamp = group.Where(b => !string.Equals(b.StationCode?.Trim(), stationCode, StringComparison.OrdinalIgnoreCase)).ToList();
                plan.SoftDeleteIds.AddRange(toDelete);
                plan.BlocksToStamp.AddRange(toStamp);
                if ((keep == null || toDelete.Count > 0 || toStamp.Count > 0) && !plan.ChangedProjectIds.Contains(group.Key.ProjectId))
                {
                    plan.ChangedProjectIds.Add(group.Key.ProjectId);
                }
            }

            return plan;
        }

        /// <summary>
        /// 解绑后块工位字段改成什么：剩下的有效绑定 → 该块逻辑单元染色最多的工位；都没有才返回 null（清空）。
        /// </summary>
        public static string? ResolveStationAfterUnbind(
            IEnumerable<PLC_StationBlockBindingEntity> remaining,
            IEnumerable<(string StationCode, decimal? Confidence)> logicUnitStations,
            HashSet<string>? allowed)
        {
            var fromBindings = ResolveBlockStation(remaining, null, allowed);
            if (fromBindings != null)
                return fromBindings.StationCode;

            var ranked = (logicUnitStations ?? Enumerable.Empty<(string StationCode, decimal? Confidence)>())
                .Select(x => (Code: Canonicalize(x.StationCode, allowed), Confidence: x.Confidence ?? 0m))
                .Where(x => x.Code != null)
                .GroupBy(x => x.Code!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Code: g.Key, Count: g.Count(), Score: g.Sum(x => x.Confidence)))
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.Score)
                .ToList();
            if (ranked.Count == 0) return null;
            if (ranked.Count > 1 && ranked[1].Count == ranked[0].Count && ranked[1].Score == ranked[0].Score) return null;
            return ranked[0].Code;
        }

        /// <summary>
        /// 重新提取时块工位字段该写什么（null = 不改）。
        /// 有手动绑定时一律以手动为准（字段被改乱了也改回来）；否则用染色投票，块名/描述里明确的工位号优先；
        /// 只有文本线索时只填空字段。白名单外的工位不写。
        /// </summary>
        public static string? DecideBackfillStation(
            string? currentStation,
            string? coloringWinner,
            string? textStation,
            string? manualStation,
            HashSet<string>? allowed)
        {
            var current = currentStation?.Trim();
            var manual = Canonicalize(manualStation, allowed);
            if (manual != null)
                return string.Equals(current, manual, StringComparison.OrdinalIgnoreCase) ? null : manual;

            var text = Canonicalize(textStation, allowed);
            var candidate = !string.IsNullOrWhiteSpace(coloringWinner)
                ? text ?? Canonicalize(coloringWinner, allowed)
                : string.IsNullOrEmpty(current) ? text : null;

            if (candidate == null || string.Equals(current, candidate, StringComparison.OrdinalIgnoreCase))
                return null;
            return candidate;
        }

        /// <summary>GRAPH 实例 DB 归哪个工位：名字里只有一个（白名单内的）工位号就用它，否则沿用 FB 的工位。</summary>
        public static string? ResolveInstanceStation(string? dbName, string? fbStation, HashSet<string>? allowed)
        {
            var fromName = Canonicalize(SingleStationInText(dbName), allowed);
            return fromName ?? Canonicalize(fbStation, allowed);
        }

        /// <summary>
        /// EXEC_BITS 信号（{实例DB}.RT_DATA.EXEC_BITS...）是否属于该工位：
        /// 实例 DB 名明确写着别的工位时不属于；名字里没有工位号时无法判断，保持原样算属于。
        /// </summary>
        public static bool ExecBitBelongsToStation(string? variableName, string? stationCode, HashSet<string>? allowed)
        {
            var name = variableName?.Trim() ?? string.Empty;
            var dot = name.IndexOf('.');
            var dbName = dot > 0 ? name.Substring(0, dot) : name;
            var owner = Canonicalize(SingleStationInText(dbName), allowed);
            return owner == null || SameStation(owner, stationCode);
        }

        /// <summary>
        /// 一键排查前的提示：范围内工位还有哪些 GRAPH 块没人工确认工站，以及哪些 GRAPH 块没有任何工站。
        /// </summary>
        public static List<string> BuildCoverageWarnings(
            IEnumerable<GraphBlockRef> graphBlocks,
            IEnumerable<PLC_StationBlockBindingEntity> bindings,
            ISet<string> stationsInScope)
        {
            var rows = Live(bindings).ToList();
            var unconfirmed = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var unassigned = new List<string>();

            foreach (var block in (graphBlocks ?? Enumerable.Empty<GraphBlockRef>())
                         .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                         .GroupBy(b => (b.ProjectId, Name: NameKey(b.BlockName)))
                         .Select(g => g.OrderBy(b => b.BlockId).First()))
            {
                var blockRows = rows
                    .Where(r => r.ProjectId == block.ProjectId && NameKey(r.BlockName) == NameKey(block.BlockName))
                    .ToList();
                if (blockRows.Any(r => Classify(r.Reason) == StationBindingStatus.Manual))
                    continue;

                var resolution = ResolveBlockStation(blockRows, block.StationCode, null);
                if (resolution == null)
                {
                    unassigned.Add(block.BlockName.Trim());
                }
                else if (stationsInScope != null && stationsInScope.Contains(resolution.StationCode))
                {
                    if (!unconfirmed.TryGetValue(resolution.StationCode, out var names))
                        unconfirmed[resolution.StationCode] = names = new List<string>();
                    names.Add(block.BlockName.Trim());
                }
            }

            var warnings = unconfirmed
                .Select(kv => $"{kv.Key} 有 {kv.Value.Count} 个 GRAPH 块未人工确认工站：{JoinNames(kv.Value)}，" +
                              "当前按提取结果或自动建议判断，建议在「批量绑定工站」中确认")
                .ToList();
            if (unassigned.Count > 0)
            {
                warnings.Add($"有 {unassigned.Count} 个 GRAPH 块没有工站归属：{JoinNames(unassigned)}，" +
                             "这些块的步不会计入任何工位，建议在「批量绑定工站」中绑定");
            }
            return warnings;
        }

        // ── 工具 ──

        private static IEnumerable<PLC_StationBlockBindingEntity> Live(IEnumerable<PLC_StationBlockBindingEntity>? rows) =>
            (rows ?? Enumerable.Empty<PLC_StationBlockBindingEntity>()).Where(r => r != null && r.IsDeleted != true);

        private static string NameKey(string? blockName) => (blockName ?? string.Empty).Trim().ToLowerInvariant();

        private static string FamilyKey(string? stationCode)
        {
            var normalized = StationCodeHeuristics.NormalizeStationCode(stationCode ?? string.Empty);
            var family = StationCodeHeuristics.GetAliasFamilyKey(normalized);
            return string.IsNullOrEmpty(family) ? normalized : family;
        }

        /// <summary>是否同一工位（大小写不敏感，OP30 / OP030 视为同一个）</summary>
        public static bool IsSameStation(string? left, string? right) => SameStation(left, right);

        private static bool SameStation(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
            && (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)
                || FamilyKey(left) == FamilyKey(right));

        /// <summary>统一成白名单里的写法；不在白名单里返回 null。没有白名单时原样返回。</summary>
        internal static string? Canonicalize(string? stationCode, HashSet<string>? allowed)
        {
            var code = stationCode?.Trim();
            if (string.IsNullOrEmpty(code)) return null;
            if (allowed == null) return code;
            if (allowed.TryGetValue(code, out var exact)) return exact;
            var family = FamilyKey(code);
            return allowed.FirstOrDefault(a => FamilyKey(a) == family);
        }

        /// <summary>文本里只出现一个工位（同族算一个）时返回它的规范写法（如 OP20 → OP020），否则 null。</summary>
        private static string? SingleStationInText(string? text)
        {
            var families = StationCodeHeuristics.ExtractStationCodes(text ?? string.Empty)
                .Select(StationCodeHeuristics.NormalizeStationCode)
                .Where(code => !string.IsNullOrEmpty(code))
                .GroupBy(FamilyKey)
                .ToList();
            return families.Count == 1 ? families[0].First() : null;
        }

        private static string? PickUniqueBest(IEnumerable<(string? Code, decimal Confidence)> candidates)
        {
            var list = candidates.Where(c => c.Code != null).ToList();
            if (list.Count == 0) return null;
            var best = list.Max(c => c.Confidence);
            var top = list.Where(c => c.Confidence == best).Select(c => c.Code!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return top.Count == 1 ? top[0] : null;
        }

        private static string JoinNames(List<string> names) =>
            string.Join("、", names.Take(MaxNamesInWarning)) + (names.Count > MaxNamesInWarning ? " 等" : string.Empty);
    }
}
