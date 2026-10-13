#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>一次 XMLV2 发布的结果：生成结果、写入 / 删除的规则数、缺程序体检查与因此原样保留的旧规则数。</summary>
public sealed record Original353PublishResult(Original353ProjectResult Generated, int RulesWritten, int RulesDeleted,
    Original353SourceIntegrityReport Integrity, int PreservedRules);

/// <summary>XMLV2 publisher: the upload path always generates constraints and wording here.
/// Legacy IOConstraintPreprocessService code is kept but no longer called on upload.</summary>
public sealed class Original353TestPublisher(
    IConfiguration configuration,
    PostgresPipelineStore sourceStore,
    IPLC_ProjectService projectService,
    IPLC_BlockService blockService,
    IPLC_IOTagService ioTagService,
    IPLC_IOConstraintRuleService ruleService,
    IPLC_IOConstraintSignalService signalService,
    IPLC_LogicUnitService logicUnitService,
    IPLC_BlockCallService blockCallService,
    Original353RuleTextService ruleTextService,
    Understanding.UnderstandingPublishStep understandingStep,
    ILogger<Original353TestPublisher> logger) : ISelfScopedAutoInject
{
    public async Task<Original353PublishResult> PublishArchiveAsync(long projectId, string archivePath,
        CancellationToken token = default)
    {
        var (source, comments, modeBindings) = await PrepareArchiveAsync(projectId, archivePath);
        return await PublishSourceAsync(source, persistSourceLuId: false, comments, modeBindings, token);
    }

    /// <summary>
    /// 审查 P2/P3（2026-10-02）：单 LU / 单块按范围重跑。用保存的上传包跑全项目生成（代码分析全项目，AI 走缓存），
    /// 只替换范围内的 XMLV2 规则（与同 LU 的老方法规则）和报警清单行；写库在一个事务里、生成成功后才替换。
    /// refreshAi：范围内的普通网络 AI 与规则措辞不读缓存、重新生成（被调 FB 的契约缓存不清）。
    /// 映射不上、范围内缺程序体、范围内 AI 读取不完整 → 抛 PipelineException，什么都不改。
    /// </summary>
    public async Task<(Original353PublishResult Result, Original353RerunScopeResolved Scope)> PublishArchiveScopeAsync(long projectId,
        string archivePath, Original353RerunRequest request, bool refreshAi, CancellationToken token = default)
    {
        var (source, comments, modeBindings) = await PrepareArchiveAsync(projectId, archivePath);
        var scope = Original353RerunScope.Resolve(source, request);
        var refresh = refreshAi ? scope.ArchiveUnitIds : null;
        var (generated, understanding) = await GenerateAsync(source, comments, modeBindings, token, refresh);
        Original353RerunScope.RequireAiComplete(source, scope, understanding.Report);
        var scoped = Original353RerunScope.Restrict(generated, scope);
        var wording = await ruleTextService.BuildAsync(source, scoped, token, refresh);
        Original353AiWordingPolicy.RequireComplete(scoped, wording);
        var mapped = MapGenerated(projectId, scoped, source.Tags, persistSourceLuId: false, wording, comments, modeBindings,
            BlockNames(source));
        var newRules = mapped.Select(item => item.Rule).ToList();
        var newSignals = mapped.SelectMany(item => item.Signals).ToList();

        token.ThrowIfCancellationRequested();
        int deleted;
        using var transaction = projectService.BeginTran();
        try
        {
            var oldRules = await ruleService.GetListAsync(rule => rule.ProjectId == projectId && !rule.IsDeleted);
            var replace = Original353RerunScope.RulesToReplace(oldRules, scope,
                    Original353SourceIntegrity.BlockResolver(source, [(scope.BlockId, scope.BlockName)]))
                .ToHashSet();
            var replaceable = oldRules.Where(rule => replace.Contains(rule.Id)).ToList();
            foreach (var rule in replaceable) rule.IsDeleted = true;
            if (replaceable.Count > 0) await ruleService.UpdateRangeAsync(replaceable);
            if (newRules.Count > 0) await ruleService.BulkCopyAsync(newRules);
            if (newSignals.Count > 0) await signalService.BulkCopyAsync(newSignals);
            transaction.Complete();
            deleted = replaceable.Count;
        }
        catch
        {
            projectService.RollbackTran();
            throw;
        }
        await understandingStep.SaveAlarmCatalogAsync(projectId, understanding.Report,
            $"{generated.GeneratorVersion}|{Understanding.FbContractPrompt.Version}", token,
            (rows, ct) => LoadAlarmUnitCandidatesAsync(projectId, rows, ct), scope.CatalogInScope);
        logger.LogInformation("XMLV2 scoped rerun project={ProjectId} kind={Kind} block={Block} networks={Networks} units={Units} rules={Rules} deleted={Deleted} refreshAi={RefreshAi} notes={Notes}",
            projectId, scope.Kind, scope.BlockName, scope.DbNetworks is null ? "*" : string.Join(",", scope.DbNetworks),
            scope.ArchiveUnitIds.Count, newRules.Count, deleted, refreshAi, string.Join("；", scope.Notes));
        return (new Original353PublishResult(scoped, newRules.Count, deleted,
            new Original353SourceIntegrityReport([], []), 0), scope);
    }

    /// <summary>读取保存包与本项目点表、校验目标库与归档覆盖率。</summary>
    private async Task<(PipelineSnapshot Source, IReadOnlyDictionary<string, string?> Comments,
        IReadOnlyList<Original353ManualModeBinding> ModeBindings)> PrepareArchiveAsync(long projectId, string archivePath)
    {
        ValidateTarget(projectId);
        var tagRows = (await ioTagService.GetListAsync(tag => tag.ProjectId == projectId && !tag.IsDeleted))
            .Where(tag => !string.IsNullOrWhiteSpace(tag.TagName)).ToArray();
        var tags = tagRows
            .Select(tag => new PipelineTag(tag.TagName!, tag.Address ?? "", tag.DataType ?? "") { Comment = tag.Comment })
            .ToArray();
        var comments = tagRows.GroupBy(tag => tag.TagName!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => (string?)group.First().Comment,
                StringComparer.OrdinalIgnoreCase);
        var source = Original353ArchiveSource.Read(archivePath, projectId, tags);
        var activeBlocks = await blockService.GetListAsync(block => block.ProjectId == projectId && !block.IsDeleted);
        Original353ArchiveCoverage.RequireComplete(source, activeBlocks);
        var modeBindings = Original353ManualModeBindings.Read(archivePath);
        return (source, comments, modeBindings);
    }

    /// <summary>全项目生成：原生成器 + 模式冲突 + 理解步骤（FB 契约 / 普通网络 AI，走缓存）+ 展示标签。</summary>
    private async Task<(Original353ProjectResult Generated, Understanding.UnderstandingStepResult Understanding)> GenerateAsync(
        PipelineSnapshot source, IReadOnlyDictionary<string, string?> tagComments,
        IReadOnlyList<Original353ManualModeBinding> modeBindings, CancellationToken token, IReadOnlySet<long>? refreshNetworkUnits = null)
    {
        var generated = OriginalConstraintWorkflow.Generate(source, token, NameRuleSwitches.FromConfiguration(configuration));
        generated = Original353ModeConflictRules.Append(generated, source, modeBindings);
        var understanding = await understandingStep.ApplyAsync(source, generated, token, refreshNetworkUnits);
        generated = understanding.Result;
        generated = Original353RulePresentation.WithLabels(generated, source.Tags, tagComments);
        return (generated, understanding);
    }

    /// <summary>
    /// 报警清单行引用到的块下的库逻辑单元（Id、networkid）及其调用记录里的被调块名，供清单行回查 LogicUnitId。
    /// 发布在上传入库之后，此时逻辑单元 Id 是当前的。
    /// </summary>
    private async Task<IReadOnlyList<Understanding.AlarmCatalogUnitCandidate>> LoadAlarmUnitCandidatesAsync(long projectId,
        IReadOnlyList<Understanding.AlarmCatalogRow> rows, CancellationToken token)
    {
        var names = rows.Select(r => r.SourceBlock?.Trim() ?? "").Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockNameById = (await blockService.GetListAsync(b => b.ProjectId == projectId && !b.IsDeleted))
            .Where(b => !string.IsNullOrWhiteSpace(b.BlockName) && names.Contains(b.BlockName.Trim()))
            .ToDictionary(b => b.Id, b => b.BlockName.Trim());
        if (blockNameById.Count == 0) return [];
        var units = new List<(long Id, long BlockId, int NetworkId)>();
        foreach (var chunk in blockNameById.Keys.Chunk(500))
        {
            token.ThrowIfCancellationRequested();
            var ids = chunk.ToList();
            var found = await logicUnitService.GetDb().AsQueryable()
                .Where(l => l.BlockId.HasValue && ids.Contains(l.BlockId.Value) && l.IsDeleted == false && l.NetworkId != null)
                .Select(l => new { l.Id, l.BlockId, l.NetworkId })
                .ToListAsync();
            units.AddRange(found.Where(l => l.BlockId != null && l.NetworkId != null)
                .Select(l => (l.Id, l.BlockId!.Value, l.NetworkId!.Value)));
        }
        if (units.Count == 0) return [];
        var luIds = units.Select(u => u.Id).ToHashSet();
        token.ThrowIfCancellationRequested();
        var calls = await blockCallService.GetDb().AsQueryable()
            .Where(c => c.ProjectId == projectId && c.IsDeleted == false && c.LogicUnitId != null)
            .Select(c => new { c.LogicUnitId, c.CalleeBlockName })
            .ToListAsync();
        var calleesByUnit = calls
            .Where(c => c.LogicUnitId is long id && luIds.Contains(id) && !string.IsNullOrWhiteSpace(c.CalleeBlockName))
            .GroupBy(c => c.LogicUnitId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(c => c.CalleeBlockName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        return units.Select(u => new Understanding.AlarmCatalogUnitCandidate(u.Id, blockNameById[u.BlockId], u.NetworkId,
                calleesByUnit.TryGetValue(u.Id, out var callees) ? callees : []))
            .ToArray();
    }

    private void ValidateTarget(long projectId)
    {
        var businessConnection = configuration["WebConfig:DBList:0:ConnectionString"];
        var expected = configuration["IndependentPlcPipeline:ExpectedDatabase"];
        if (string.IsNullOrWhiteSpace(businessConnection) || string.IsNullOrWhiteSpace(expected))
            throw new PipelineException("TestDatabaseConfigurationRequired");
        var businessTarget = new NpgsqlConnectionStringBuilder(businessConnection);
        var sourceTarget = new NpgsqlConnectionStringBuilder(sourceStore.ConnectionString);
        var businessDatabase = businessTarget.Database;
        if (!expected.EndsWith("_test", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(businessDatabase, expected, StringComparison.Ordinal)
            || !string.Equals(sourceTarget.Database, expected, StringComparison.Ordinal)
            || !string.Equals(sourceTarget.Host, businessTarget.Host, StringComparison.OrdinalIgnoreCase)
            || sourceTarget.Port != businessTarget.Port)
            throw new PipelineException("TestDatabaseMismatch");
        if (projectId <= 0) throw new PipelineException("ProjectIdRequired");
    }

    /// <summary>生成结果 → 入库行。每条规则的 ValidStates.Generator 取 generated.GeneratorVersion（含名字规则开关标记）。</summary>
    /// <summary>blockNameByUnit：归档单元 Id → 块名，写进 ValidStates.BlockName（按范围重跑 / 缺程序体保留按它认块）。</summary>
    internal static Original353LegacyRuleRows[] MapGenerated(long projectId, Original353ProjectResult generated,
        IReadOnlyList<PipelineTag> tags, bool persistSourceLuId,
        IReadOnlyDictionary<(long UnitId, string RuleKey), Original353RuleWording> wording,
        IReadOnlyDictionary<string, string?> tagComments, IReadOnlyList<Original353ManualModeBinding> modeBindings,
        IReadOnlyDictionary<long, string>? blockNameByUnit = null) =>
        generated.Units.SelectMany(unit => unit.Rules
            .Concat(unit.ConditionalFeedbackRules.Select(group => group.Rule))
            .GroupBy(rule => rule.Key)
            .Select(group => group.OrderByDescending(rule => rule.RequiresSettlingEvidence).First())
            .Select(rule => Original353LegacyRuleMapper.Map(projectId, unit.LogicUnitId, rule, tags,
                persistSourceLuId, wording.GetValueOrDefault((unit.LogicUnitId, rule.Key)), tagComments,
                modeBindings, generated.GeneratorVersion, blockNameByUnit?.GetValueOrDefault(unit.LogicUnitId))))
            .ToArray();

    private async Task<Original353PublishResult> PublishSourceAsync(PipelineSnapshot source,
        bool persistSourceLuId, IReadOnlyDictionary<string, string?> tagComments,
        IReadOnlyList<Original353ManualModeBinding> modeBindings, CancellationToken token)
    {
        var projectId = source.ProjectId;
        var (generated, understanding) = await GenerateAsync(source, tagComments, modeBindings, token);
        if (generated.RuleCount == 0)
            throw new PipelineException("IncompleteXmlRecognition");
        // 审查 P4：缺程序体的非 FC 块——它们的旧规则与报警清单行原样保留（不删），结果与日志列出；FC 不计
        var integrity = Original353SourceIntegrity.Inspect(source, generated);
        if (integrity.HasNonFc)
            logger.LogWarning("XMLV2 publish project={ProjectId}: {Count} non-FC blocks have no program body in the archive; their old rules and alarm rows are kept: {Blocks}",
                projectId, integrity.NonFc.Count, integrity.DescribeNonFc(20));
        var wording = await ruleTextService.BuildAsync(source, generated, token);
        Original353AiWordingPolicy.RequireComplete(generated, wording);
        var mapped = MapGenerated(projectId, generated, source.Tags, persistSourceLuId, wording, tagComments, modeBindings,
            BlockNames(source));
        var newRules = mapped.Select(item => item.Rule).ToList();
        var newSignals = mapped.SelectMany(item => item.Signals).ToList();

        token.ThrowIfCancellationRequested();
        // 缺程序体块的旧行推回块要枚举单元身份（可能数秒）：在事务外先读一遍旧规则把枚举做掉，事务里复用解析器（审查复核 M-4）
        var resolver = Original353SourceIntegrity.ResolverFor(source, integrity);
        if (integrity.HasNonFc)
            Original353SourceIntegrity.PreservedRuleIds(integrity,
                await ruleService.GetListAsync(rule => rule.ProjectId == projectId && !rule.IsDeleted), resolver);
        int deleted, preservedCount;
        using var transaction = projectService.BeginTran();
        try
        {
            // The legacy diagnostic reader excludes NULL ConstraintStatus through SQL's
            // three-valued comparison with 6. Normalize only this test project's unset
            // primitive tags to the existing "pending" value; preserve explicit statuses.
            await ioTagService.UpdateAsync(
                tag => new PLC_IOTagEntity { ConstraintStatus = 0 },
                tag => tag.ProjectId == projectId && !tag.IsDeleted
                    && tag.ConstraintStatus == null
                    && GlobalStaticVariable.PlcDataTypeList.Contains(tag.DataType));
            var oldRules = await ruleService.GetListAsync(rule => rule.ProjectId == projectId && !rule.IsDeleted);
            var preserved = Original353SourceIntegrity.PreservedRuleIds(integrity, oldRules, resolver);
            var replaceable = oldRules.Where(rule => !string.Equals(rule.Source, "Manual", StringComparison.OrdinalIgnoreCase)
                && !preserved.Contains(rule.Id)).ToList();
            foreach (var rule in replaceable) rule.IsDeleted = true;
            if (replaceable.Count > 0) await ruleService.UpdateRangeAsync(replaceable);
            if (newRules.Count > 0) await ruleService.BulkCopyAsync(newRules);
            if (newSignals.Count > 0) await signalService.BulkCopyAsync(newSignals);
            transaction.Complete();
            deleted = replaceable.Count;
            preservedCount = preserved.Count;
        }
        catch
        {
            projectService.RollbackTran();
            throw;
        }
        // 约束已提交后再写报警清单（放在 try 之外：清单这一步的取消不能触发对已提交事务的回滚）。
        // 缺程序体的非 FC 块的旧清单行原样保留（只替换其余行）。
        var keptBlocks = integrity.NonFc.Select(u => u.BlockName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await understandingStep.SaveAlarmCatalogAsync(projectId, understanding.Report,
            $"{generated.GeneratorVersion}|{Understanding.FbContractPrompt.Version}", token,
            (rows, ct) => LoadAlarmUnitCandidatesAsync(projectId, rows, ct),
            keptBlocks.Count == 0 ? null : row => !keptBlocks.Contains(row.SourceBlock?.Trim() ?? ""));
        logger.LogInformation("XMLV2 test publish project={ProjectId} version={Version} rules={Rules} active={Active} timed={Timed} review={Review} deleted={Deleted} preservedMissingSource={Preserved} missingSourceNonFc={MissingNonFc} missingSourceFc={MissingFc}",
            projectId, generated.GeneratorVersion, newRules.Count,
            newRules.Count(rule => rule.Source == "XMLV2"),
            newRules.Count(rule => rule.Source == "XMLV2_TIMED"),
            newRules.Count(rule => rule.Source == "XMLV2_REVIEW"), deleted, preservedCount,
            integrity.NonFc.Count, integrity.Fc.Count);
        return new Original353PublishResult(generated, newRules.Count, deleted, integrity, preservedCount);
    }

    /// <summary>归档单元 Id → 块名。</summary>
    internal static IReadOnlyDictionary<long, string> BlockNames(PipelineSnapshot source) =>
        source.Units.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().BlockName);
}
