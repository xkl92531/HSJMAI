#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>库里的逻辑单元：Id、所在块名、networkid（块内第几个 CompileUnit，空网络也计）。</summary>
public sealed record DbLogicUnitRef(long Id, string BlockName, int? NetworkId);

public enum Original353RerunKind { LogicUnit, Block }

/// <summary>按范围重跑的请求。BlockUnits：该块在库里的全部逻辑单元（单 LU 时用来找共用同一归档程序段的兄弟 LU）。</summary>
public sealed record Original353RerunRequest(Original353RerunKind Kind, string BlockName, DbLogicUnitRef? Target,
    IReadOnlyList<DbLogicUnitRef> BlockUnits)
{
    public static Original353RerunRequest ForLogicUnit(DbLogicUnitRef target, IReadOnlyList<DbLogicUnitRef> blockUnits) =>
        new(Original353RerunKind.LogicUnit, target.BlockName, target, blockUnits);

    public static Original353RerunRequest ForBlock(string blockName, IReadOnlyList<DbLogicUnitRef> blockUnits) =>
        new(Original353RerunKind.Block, blockName, null, blockUnits);
}

/// <summary>
/// 解析后的重跑范围。ArchiveUnitIds：范围内的归档程序段 Id（= 入库规则 ValidStates.LogicUnitId）；DbLuIds：范围内的库逻辑单元
/// （删同 LU 的老方法规则用）；DbNetworks：库 networkid（单 LU 时；整块为 null）——报警清单按（块名, networkid）认范围。
/// </summary>
public sealed record Original353RerunScopeResolved(Original353RerunKind Kind, string BlockName, long BlockId, string BlockKind,
    IReadOnlySet<long> ArchiveUnitIds, IReadOnlySet<long> DbLuIds, IReadOnlySet<int>? DbNetworks, IReadOnlyList<string> Notes)
{
    public bool CatalogInScope(AlarmCatalogRow row) =>
        string.Equals((row.SourceBlock ?? "").Trim(), BlockName, StringComparison.OrdinalIgnoreCase)
        && (DbNetworks is null || (row.SourceNetwork is int n && DbNetworks.Contains(n)));
}

/// <summary>
/// 审查 P2/P3（2026-10-02）：单 LU / 单块按范围重跑。全项目照常生成（代码分析看全项目、AI 走缓存），只替换范围内的规则与报警清单行。
/// 映射口径与报警清单回查（<see cref="AlarmCatalogLocator"/>）一致：（块名忽略大小写, 库 networkid = 归档 DbNetworkId）。
/// 宁可失败也不丢规则：映射不上、范围内缺程序体、范围内 AI 读取不完整 → 失败，什么都不改。
/// </summary>
public static class Original353RerunScope
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    public static Original353RerunScopeResolved Resolve(PipelineSnapshot source, Original353RerunRequest request)
    {
        var block = (request.BlockName ?? "").Trim();
        var blockUnits = source.Units.Where(u => C.Equals(u.BlockName.Trim(), block)).ToArray();
        if (blockUnits.Length == 0)
        {
            if (request.Kind == Original353RerunKind.LogicUnit)
                throw new PipelineException($"RerunLogicUnitNotInArchive:{block}#{request.Target?.NetworkId}");
            throw new PipelineException($"RerunBlockNotInArchive:{block}");
        }
        var kind = string.IsNullOrWhiteSpace(blockUnits[0].BlockKind) ? "?" : blockUnits[0].BlockKind;
        if (blockUnits.All(u => string.IsNullOrWhiteSpace(u.RawContent)))
            throw new PipelineException($"RerunScopeMissingSource:{block}({kind})");
        var blockId = blockUnits[0].BlockId;
        var notes = new List<string>();

        if (request.Kind == Original353RerunKind.Block)
        {
            var dbIds = request.BlockUnits.Select(u => u.Id).ToHashSet();
            var archiveNetworks = blockUnits.Select(u => u.DbNetworkId).Where(n => n is not null).Select(n => n!.Value).ToHashSet();
            var unmapped = request.BlockUnits.Where(u => u.NetworkId is null || !archiveNetworks.Contains(u.NetworkId.Value)).ToArray();
            if (unmapped.Length > 0)
                notes.Add($"块里 {unmapped.Length} 个逻辑单元在 XML 包里没有对应程序段（空网络等），按块整体处理");
            return new(Original353RerunKind.Block, block, blockId, kind, blockUnits.Select(u => u.Id).ToHashSet(), dbIds, null, notes);
        }

        var target = request.Target ?? throw new PipelineException("RerunLogicUnitRequired");
        if (target.NetworkId is not int network)
            throw new PipelineException($"RerunLogicUnitNotInArchive:{block}#");
        var units = blockUnits.Where(u => u.DbNetworkId == network).ToArray();
        if (units.Length == 0)
            throw new PipelineException($"RerunLogicUnitNotInArchive:{block}#{network}");
        if (units.Any(u => string.IsNullOrWhiteSpace(u.RawContent)))
            throw new PipelineException($"RerunScopeMissingSource:{block}({kind})#{network}");
        var siblings = request.BlockUnits.Where(u => u.NetworkId == network).Select(u => u.Id).Append(target.Id).ToHashSet();
        if (siblings.Count > 1)
            notes.Add($"库里 {siblings.Count} 个逻辑单元共用同一个程序段（{block} 网络 {network}），按程序段整体重跑");
        return new(Original353RerunKind.LogicUnit, block, blockId, kind, units.Select(u => u.Id).ToHashSet(), siblings,
            new HashSet<int> { network }, notes);
    }

    /// <summary>
    /// 范围内要替换（软删）的旧规则 Id：
    /// XMLV2 规则——ValidStates.LogicUnitId 是范围内的归档程序段；整块时另含块名相同（或旧行没有块名、按归档单元身份推回本块）的已不存在程序段的规则；
    /// 老方法规则（非 XMLV2、非手工）——SourceLuId 是范围内的库逻辑单元（不混入老方法）。手工规则永远不动；推不回块的规则不动。
    /// </summary>
    public static IReadOnlyList<long> RulesToReplace(IEnumerable<PLC_IOConstraintRuleEntity> oldRules, Original353RerunScopeResolved scope,
        Func<long, string?> blockOf)
    {
        var result = new List<long>();
        foreach (var rule in oldRules)
        {
            if (string.Equals(rule.Source, "Manual", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Original353SourceIntegrity.IsXmlV2Source(rule.Source))
            {
                if (rule.SourceLuId is long lu && scope.DbLuIds.Contains(lu)) result.Add(rule.Id);
                continue;
            }
            var origin = Original353SourceIntegrity.ReadOrigin(rule);
            if (origin.UnitId is long unit && scope.ArchiveUnitIds.Contains(unit))
            {
                result.Add(rule.Id);
                continue;
            }
            if (scope.Kind != Original353RerunKind.Block) continue;
            var ruleBlock = origin.BlockName ?? (origin.UnitId is long id ? blockOf(id) : null);
            if (ruleBlock is not null && C.Equals(ruleBlock.Trim(), scope.BlockName)) result.Add(rule.Id);
        }
        return result;
    }

    /// <summary>生成结果只留范围内的程序段（措辞、校验、入库都只针对它们）。</summary>
    public static Original353ProjectResult Restrict(Original353ProjectResult generated, Original353RerunScopeResolved scope)
    {
        var units = generated.Units.Where(u => scope.ArchiveUnitIds.Contains(u.LogicUnitId)).ToArray();
        return generated with
        {
            Units = units,
            RuleCount = units.Sum(u => u.Rules.Count),
            RepairCount = units.Sum(u => u.Repairs.Count),
            SafetyRuleCount = units.Sum(u => u.SafetyRules.Count),
            ConditionalFeedbackCount = units.Sum(u => u.ConditionalFeedbackRules.Count)
        };
    }

    /// <summary>理解步骤里非确定性地少产规则的记号（代码证明单段异常、到期、整体关掉、依赖它的过滤异常）。</summary>
    public static readonly IReadOnlyList<string> CodeProofIncompleteNotes =
        ["CodeProofFailed", "CodeProofDeadline", "CodeProofDisabled", "CodeProofFiltersFailed"];

    /// <summary>
    /// 范围内 AI 读取与代码证明必须完整（整项目发布时这些失败只丢对应规则；按范围替换时丢规则就等于删掉旧规则，所以改为失败）：
    /// 理解步骤整体失败 / 普通网络阶段整体出错 / 范围内普通网络 Failed 或 Deadline /
    /// 范围内程序段调用的 FB（含被调 FB 再调用的下层 FB）契约读取 Failed 或超长分段读取有段失败（状态仍 Ok/Rejected）/
    /// 代码证明有单段异常、到期、整体关掉或过滤异常（NoteCounts 里只有项目级计数，无法定位到程序段，按项目判）→ 失败。
    /// 确定性的结果（TooLarge、Rejected、NoBody、TextOnly、SegmentTooLarge）照常放行。
    /// </summary>
    public static void RequireAiComplete(PipelineSnapshot source, Original353RerunScopeResolved scope, UnderstandingReport? report)
    {
        if (report is null) throw new PipelineException("RerunUnderstandingFailed");
        if (report.NetworkReadError is not null) throw new PipelineException("RerunNetworkAiFailed:" + report.NetworkReadError);
        var units = source.Units.Where(u => scope.ArchiveUnitIds.Contains(u.Id)).ToArray();
        foreach (var unit in units)
            if (report.NetworkStatus.TryGetValue(unit.Id, out var status)
                && status is NetworkAiStatus.Failed or NetworkAiStatus.Deadline)
                throw new PipelineException($"RerunNetworkAiIncomplete:{unit.BlockName}#{unit.DbNetworkId}:{status}");
        var called = CalledFbs(source, units);
        var failedFbs = report.FbStatus.Where(s => s.Value == FbContractStatus.Failed).Select(s => s.Key).ToHashSet(C);
        var missing = called.Where(failedFbs.Contains).Order(C).ToArray();
        if (missing.Length > 0)
            throw new PipelineException("RerunFbContractUnavailable:" + string.Join(",", missing.Take(5)));
        var segmentFailed = called.Where(name => report.FbSegmentFailures.Keys.Contains(name, C)).Order(C).ToArray();
        if (segmentFailed.Length > 0)
            throw new PipelineException("RerunFbContractIncomplete:" + string.Join(",", segmentFailed.Take(5)));
        var proofNotes = CodeProofIncompleteNotes.Where(n => report.NoteCounts.GetValueOrDefault(n) > 0).ToArray();
        if (proofNotes.Length > 0)
            throw new PipelineException("RerunCodeProofIncomplete:" + string.Join(",", proofNotes));
    }

    /// <summary>范围内程序段调用的 FB 及其程序体里再调用的下层 FB（传递闭包；下层契约会派生到外层调用点）。</summary>
    static IReadOnlySet<string> CalledFbs(PipelineSnapshot source, IEnumerable<PipelineUnit> units)
    {
        var calleesByBlock = source.Units.GroupBy(u => u.BlockName.Trim(), C)
            .ToDictionary(g => g.Key, g => g.SelectMany(u => u.Callees).Distinct(C).ToArray(), C);
        var seen = new HashSet<string>(C);
        var queue = new Queue<string>(units.SelectMany(u => u.Callees));
        while (queue.Count > 0)
        {
            var name = queue.Dequeue()?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            if (calleesByBlock.TryGetValue(name, out var next))
                foreach (var callee in next) queue.Enqueue(callee);
        }
        return seen;
    }
}
