#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>缺程序体的单元：归档里该块没有任何带 NetworkSource 内容的 CompileUnit（空块 / 加密块 / 导出失败），
/// 读取器给它一个 MissingBody 单元（RawContent 为空），生成器记 MissingSource。</summary>
public sealed record MissingSourceUnit(long UnitId, long BlockId, string BlockName, string Kind);

/// <summary>NonFc：FB / OB 等非 FC 的缺程序体单元；Fc：FC 的（用户「FC 一律不管」，只计数）。</summary>
public sealed record Original353SourceIntegrityReport(IReadOnlyList<MissingSourceUnit> NonFc, IReadOnlyList<MissingSourceUnit> Fc)
{
    public bool HasNonFc => NonFc.Count > 0;

    /// <summary>前 max 个非 FC 缺程序体块（块名(类型)）与总数。</summary>
    public string DescribeNonFc(int max = 10)
    {
        if (NonFc.Count == 0) return "无";
        var head = string.Join("、", NonFc.Take(max).Select(u => $"{u.BlockName}({u.Kind})"));
        return NonFc.Count > max ? $"{head} 等，共 {NonFc.Count} 个" : $"{head}，共 {NonFc.Count} 个";
    }
}

/// <summary>入库规则的来源：ValidStates 里的归档单元 Id（LogicUnitId）与块名（2026-10-02 起写入；之前的行为 null）。</summary>
public sealed record XmlV2RuleOrigin(long RuleId, string? Source, long? UnitId, string? BlockName);

/// <summary>
/// 审查 P4（2026-10-02）：发布前检查缺程序体的单元。四个调试项目本来就有大量非 FC 的空块（OB 中断块、空 FB 等），
/// 直接判失败会让这些项目从此发布不了——按裁定改为：这些块的旧 XMLV2 规则（与报警清单行）原样保留、不删，
/// 发布结果与日志列出；FC 不计。按范围重跑时范围内出现非 FC 缺程序体 → 重跑失败。
/// </summary>
public static class Original353SourceIntegrity
{
    public const string MissingSourceIssue = "MissingSource";

    /// <summary>旧行没有块名时用归档单元身份推回块：单元 Id = StableId("{块Id}:{序号}:{根元素名}")，按这些根名与序号上限枚举。</summary>
    static readonly string[] KnownRoots = ["FlgNet", "StructuredText", "StatementList", "Graph"];
    public const int MaxLegacyOrder = 4096;

    public static Original353SourceIntegrityReport Inspect(PipelineSnapshot source, Original353ProjectResult generated)
    {
        var byId = source.Units.ToDictionary(u => u.Id);
        var missing = generated.Units.Where(u => u.Issues.Contains(MissingSourceIssue))
            .Select(u => byId.TryGetValue(u.LogicUnitId, out var unit) ? unit : null)
            .Where(u => u is not null).Cast<PipelineUnit>()
            .Select(u => new MissingSourceUnit(u.Id, u.BlockId, u.BlockName, string.IsNullOrWhiteSpace(u.BlockKind) ? "?" : u.BlockKind))
            .DistinctBy(u => u.BlockName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u.BlockName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool IsFc(MissingSourceUnit u) => string.Equals(u.Kind, "FC", StringComparison.OrdinalIgnoreCase);
        return new(missing.Where(u => !IsFc(u)).ToArray(), missing.Where(IsFc).ToArray());
    }

    public static bool IsXmlV2Source(string? source) =>
        source is not null && source.StartsWith("XMLV2", StringComparison.OrdinalIgnoreCase);

    public static XmlV2RuleOrigin ReadOrigin(PLC_IOConstraintRuleEntity rule)
    {
        long? unitId = null; string? blockName = null;
        if (!string.IsNullOrWhiteSpace(rule.ValidStates))
        {
            try
            {
                using var doc = JsonDocument.Parse(rule.ValidStates);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("LogicUnitId", out var id))
                    {
                        if (id.ValueKind == JsonValueKind.String
                            && long.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                            unitId = parsed;
                        else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number))
                            unitId = number;
                    }
                    if (doc.RootElement.TryGetProperty("BlockName", out var block) && block.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(block.GetString()))
                        blockName = block.GetString()!.Trim();
                }
            }
            catch (JsonException) { }
        }
        return new(rule.Id, rule.Source, unitId, blockName);
    }

    /// <summary>
    /// 归档单元 Id 推回所属块（块名，忽略大小写）。先看当前归档里有没有这个单元；没有时只在 candidates 这些块里按身份枚举。
    /// 推不出来返回 null。
    /// </summary>
    public static Func<long, string?> BlockResolver(PipelineSnapshot source, IEnumerable<(long BlockId, string BlockName)> candidates)
    {
        var current = source.Units.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().BlockName);
        var roots = KnownRoots.Concat(source.Units.Select(u => u.UnitKind))
            .Where(r => !string.IsNullOrWhiteSpace(r) && r != "MissingBody")
            .Distinct(StringComparer.Ordinal).ToArray();
        var blocks = candidates.DistinctBy(c => c.BlockId).ToArray();
        Dictionary<long, string>? enumerated = null;
        return unitId =>
        {
            if (current.TryGetValue(unitId, out var name)) return name;
            if (blocks.Length == 0) return null;
            enumerated ??= Enumerate(blocks, roots);
            return enumerated.TryGetValue(unitId, out var legacy) ? legacy : null;
        };
    }

    static Dictionary<long, string> Enumerate(IReadOnlyList<(long BlockId, string BlockName)> blocks, IReadOnlyList<string> roots)
    {
        var map = new Dictionary<long, string>();
        foreach (var (blockId, blockName) in blocks)
            for (var order = 1; order <= MaxLegacyOrder; order++)
                foreach (var root in roots)
                    map.TryAdd(PipelineExportReader.StableId($"{blockId}:{order}:{root}"), blockName);
        return map;
    }

    /// <summary>
    /// 整项目发布时要原样保留的旧规则 Id：XMLV2 规则、属于非 FC 缺程序体的块（ValidStates 块名相同；旧行没有块名时按归档单元身份推回）。
    /// 老方法规则与手工规则不在此列（手工规则发布本来就不动，老方法规则照常删除）。
    /// </summary>
    public static IReadOnlySet<long> PreservedRuleIds(PipelineSnapshot source, Original353SourceIntegrityReport report,
        IEnumerable<PLC_IOConstraintRuleEntity> oldRules) =>
        PreservedRuleIds(report, oldRules, ResolverFor(source, report));

    /// <summary>缺程序体块的单元身份解析器（枚举在首次需要时做一次、之后复用）。发布器在事务外建好并预热，事务里复用（审查复核 M-4）。</summary>
    public static Func<long, string?> ResolverFor(PipelineSnapshot source, Original353SourceIntegrityReport report) =>
        BlockResolver(source, report.NonFc.Select(u => (u.BlockId, u.BlockName)));

    public static IReadOnlySet<long> PreservedRuleIds(Original353SourceIntegrityReport report,
        IEnumerable<PLC_IOConstraintRuleEntity> oldRules, Func<long, string?> resolve)
    {
        if (!report.HasNonFc) return new HashSet<long>();
        var names = report.NonFc.Select(u => u.BlockName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<long>();
        foreach (var rule in oldRules)
        {
            if (!IsXmlV2Source(rule.Source)) continue;
            var origin = ReadOrigin(rule);
            var block = origin.BlockName ?? (origin.UnitId is long id ? resolve(id) : null);
            if (block is not null && names.Contains(block)) kept.Add(rule.Id);
        }
        return kept;
    }
}
