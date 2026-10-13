#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpRuntimeBindingIssueDto(
    string Code,
    string OwnerSourceKey,
    string Severity = "Blocking",
    string NetworkSourceKey = "",
    string PartSourceKey = "");

public sealed record LineUpRuntimeBindingPreparationDto(
    string BindingId,
    string BindingStatus,
    string DefinitionRevision,
    int TagCount,
    bool CanValidate,
    IReadOnlyList<LineUpRuntimeBindingIssueDto> Issues);

public sealed record LineUpRuntimeBindingValidationDto(
    string BindingId,
    string BindingStatus,
    int GoodCount,
    int TotalCount,
    bool CanEnable,
    IReadOnlyList<LineUpRuntimeBindingIssueDto> Issues);

public sealed record LineUpMonitoringCommand(bool Enabled);

public sealed record LineUpMonitoringStatusDto(
    string BindingId,
    string BindingStatus,
    bool MonitoringEnabled,
    bool GlobalMonitorEnabled);

public sealed class LineUpRuntimeBindingApp(
    ILineUpGraphRuntimeRepository repository,
    ILineUpPlcMetadataRepository metadataRepository,
    LineUpPublishedGraphProjector projector,
    LineUpReadPlanCompiler compiler,
    ILineUpRuntimeValueReader valueReader,
    LineUpMonitorRuntimeGate runtimeGate,
    LineUpMonitorBindingCache bindingCache) : ISelfScopedAutoInject
{
    public const string RuntimeBindingSchema = LineUpRuntimeBindingDocument.SchemaVersion;
    internal const int MaximumRuntimeBindingBytes = 2 * 1024 * 1024;
    internal const int MaximumRuntimeValidationBytes = 256 * 1024;

    private const string Blocking = "Blocking";
    private const string Warning = "Warning";

    // 2026-09-11 用户拍板屏蔽：结构性网络告警——导入的 GRAPH LAD 网络缺块接口行/操作数元数据/块缺失等，
    // 四个 OP080 graph 系统性存在（22~249 条），对应条件在运行态按 Unknown 处理
    // （不阻断「在线」、不进「异常」——见 LineUpMonitorNavigationApp.SnapshotIsOnline）。
    // 生成/校验的问题清单不再展示这些码，避免"生成结果：N 个问题"长期吓人。
    // 其余码（Ambiguous/Unsupported/InterfaceTypeMissing/读值类）保留展示。
    // RuntimeNetworkExternalReference（2026-09-11）：跨 PLC 通信块（DB182_PLC1<->PLC2 类命名）——
    // 引用对端工程的块，本 PLC 网关必然读不到，同样按容忍处理（时效校验侧见跳过分支）。
    // internal：LineUpMonitorBindingSource.IsCurrentAsync 需要按同一集合跳过 doc 一致性核对。
    internal static readonly HashSet<string> ToleratedNetworkWarningCodes = new(StringComparer.Ordinal)
    {
        "RuntimeNetworkInterfaceMissing",
        "RuntimeNetworkOperandMetadataMissing",
        "RuntimeNetworkScopeUnsupported",
        "RuntimeNetworkBlockMissing",
        "RuntimeNetworkConstantTypeMismatch",
        "RuntimeNetworkExternalReference",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<LineUpRuntimeBindingPreparationDto> PrepareAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(bindingId, cancellationToken);
        var built = await BuildAsync(context, cancellationToken);
        if (!built.CanValidate)
        {
            await ResetAsync(context, cancellationToken);
            compiler.Invalidate(bindingId);
            bindingCache.Invalidate(bindingId);
            // 2026-09-10：准备失败此前只在响应 DTO 里带 issues、控制台零输出，
            // 服务端排障只能靠猜——补日志把 Blocking issue 打到容器日志。
            Console.WriteLine(
                "[LineUpRuntimeBinding] 准备失败 binding=" + bindingId +
                " issues=" + string.Join(";", built.Issues.Where(i => i.Severity == "Blocking").Select(i =>
                    i.Code + "@" + i.OwnerSourceKey)));
            return new(
                bindingId.ToString(CultureInfo.InvariantCulture),
                "NeedsReview",
                built.DefinitionRevision,
                built.Tags.Count,
                false,
                built.Issues);
        }

        if (!await repository.SavePreparedAsync(
                bindingId,
                context.PublishedRevision.Id,
                built.Json,
                cancellationToken))
        {
            throw Error("RuntimeBindingConflict", "The published GRAPH binding changed during preparation.");
        }

        compiler.Invalidate(bindingId);
        bindingCache.Invalidate(bindingId);
        return new(
            bindingId.ToString(CultureInfo.InvariantCulture),
            "NeedsReview",
            built.DefinitionRevision,
            built.Tags.Count,
            true,
            built.Issues);
    }

    public async Task<LineUpRuntimeBindingValidationDto> ValidateAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(bindingId, cancellationToken);
        if (string.IsNullOrWhiteSpace(context.Binding.RuntimeBindingJson) ||
            !string.Equals(context.Binding.BindingStatus, "NeedsReview", StringComparison.Ordinal))
        {
            throw Error("RuntimeBindingNotPrepared", "Prepare the current published GRAPH binding before validating it.");
        }

        var built = await BuildAsync(context, cancellationToken);
        if (!built.CanValidate)
        {
            await ResetAsync(context, cancellationToken);
            Console.WriteLine(
                "[LineUpRuntimeBinding] 验证失败 binding=" + bindingId +
                " issues=" + string.Join(";", built.Issues.Where(i => i.Severity == "Blocking").Select(i =>
                    i.Code + "@" + i.OwnerSourceKey)));
            return new(
                bindingId.ToString(CultureInfo.InvariantCulture),
                "NeedsReview",
                0,
                built.Tags.Count,
                false,
                built.Issues);
        }
        if (!JsonEquals(context.Binding.RuntimeBindingJson, built.Json))
        {
            await InvalidateAsync(context.Binding, built.DefinitionRevision, cancellationToken);
            throw Error("RuntimeBindingStale", "The published GRAPH or PLC metadata changed after preparation.");
        }

        var compilation = compiler.Compile(new(
            context.Binding.Id,
            built.DefinitionRevision,
            context.Asset.PlcProjectId,
            context.InstanceDb.BlockName!,
            LineUpRuntimeAddressMode.OptimizedSymbolic,
            built.Sources));
        var issues = new List<LineUpRuntimeBindingIssueDto>(built.Issues);
        foreach (var issue in compilation.Issues.Where(issue =>
                     issue.Code != "DuplicateSourceIgnored" && !ToleratedNetworkWarningCodes.Contains(issue.Code)))
        {
            var source = built.Sources.SingleOrDefault(item =>
                string.Equals(item.SourceKey, issue.SourceKey, StringComparison.Ordinal));
            issues.Add(Issue(
                issue.Code,
                source?.OwnerSourceKey ?? issue.SourceKey,
                IsRequired(source?.Role) ? Blocking : Warning,
                source?.NetworkSourceKey,
                source?.PartSourceKey));
        }

        LineUpRuntimeReadResult? read = null;
        if (!compilation.Plan.Items.IsDefaultOrEmpty)
        {
            read = await valueReader.ReadAsync(compilation.Plan, cancellationToken);
            var valuesBySource = read.Values
                .GroupBy(value => value.SourceKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var item in compilation.Plan.Items)
            {
                if (!valuesBySource.TryGetValue(item.SourceKey, out var values) || values.Length != 1 ||
                    values[0].Quality != LineUpRuntimeQuality.Good)
                {
                    var code = values is { Length: 1 } && !string.IsNullOrWhiteSpace(values[0].ErrorCode)
                        ? values[0].ErrorCode!
                        : "RuntimeValueNotGood";
                    // 读值层失败（IGW/PLC 不通、tag 未注册读 Bad）一律 Warning 不拦监控开启：
                    // 结构完整性由编译层 issue 与下方 RuntimeRequiredSourceMissing（Blocking）把守，
                    // 在线读值失败只反映运行环境状态，不应把 Resolved/开监控卡死在 422。
                    issues.Add(Issue(
                        code,
                        item.OwnerSourceKey,
                        Warning,
                        item.NetworkSourceKey,
                        item.PartSourceKey));
                }
            }
            if (read.Status != LineUpRuntimeReadStatus.Completed)
            {
                issues.Add(Issue("RuntimeReadIncomplete", string.Empty, Warning));
            }
        }

        foreach (var source in built.Sources.Where(source => IsRequired(source.Role)))
        {
            if (!compilation.Plan.Items.Any(item =>
                    string.Equals(item.SourceKey, source.SourceKey, StringComparison.Ordinal)))
            {
                issues.Add(Issue(
                    "RuntimeRequiredSourceMissing",
                    source.OwnerSourceKey,
                    Blocking,
                    source.NetworkSourceKey,
                    source.PartSourceKey));
            }
        }

        issues = OrderIssues(issues).ToList();
        var canEnable = !issues.Any(IsBlocking) &&
                        built.Sources.Any(source => IsRequired(source.Role));
        var goodCount = read?.Values.Count(value => value.Quality == LineUpRuntimeQuality.Good) ?? 0;
        var totalCount = compilation.Plan.Items.Length;
        if (!canEnable)
        {
            return new(
                bindingId.ToString(CultureInfo.InvariantCulture),
                "NeedsReview",
                goodCount,
                totalCount,
                false,
                issues);
        }

        var validationJson = BuildValidationJson(built.DefinitionRevision, issues);
        if (!await repository.SaveValidatedAsync(
                bindingId,
                context.PublishedRevision.Id,
                built.Json,
                validationJson,
                built.DefinitionRevision,
                DateTime.UtcNow,
                cancellationToken))
        {
            throw Error("RuntimeBindingConflict", "The published GRAPH binding changed during validation.");
        }

        compiler.Invalidate(bindingId);
        bindingCache.Invalidate(bindingId);
        return new(
            bindingId.ToString(CultureInfo.InvariantCulture),
            "Resolved",
            goodCount,
            totalCount,
            true,
            issues);
    }

    public async Task<LineUpMonitoringStatusDto> SetMonitoringAsync(
        long bindingId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0)
        {
            throw Error("RuntimeBindingInvalid", "The runtime binding identity is invalid.");
        }
        if (enabled && !runtimeGate.IsEnabled)
        {
            throw Error("LineUpMonitorDisabled", "The server-wide LineUp monitor gate is disabled.");
        }

        var status = await repository.LoadBindingAsync(bindingId, cancellationToken)
                     ?? throw Error("RuntimeBindingUnavailable", "The GRAPH station binding is unavailable.");
        if (!enabled)
        {
            if (!await repository.SetMonitoringAsync(
                    bindingId,
                    status.PublishedRevisionId ?? 0,
                    status.ValidationRevision ?? string.Empty,
                    false,
                    cancellationToken))
            {
                throw Error("RuntimeBindingConflict", "The runtime binding changed while monitoring was disabled.");
            }
            compiler.Invalidate(bindingId);
            bindingCache.Invalidate(bindingId);
            return new(bindingId.ToString(CultureInfo.InvariantCulture), status.BindingStatus, false, runtimeGate.IsEnabled);
        }

        if (!IsValidated(status))
        {
            throw Error("RuntimeBindingNotValidated", "Validate the current published GRAPH binding before enabling monitoring.");
        }

        var context = await repository.LoadContextAsync(bindingId, cancellationToken);
        if (context is null)
        {
            await InvalidateAsync(status, status.ValidationRevision!, cancellationToken);
            throw Error("RuntimeBindingStale", "The current GRAPH InstanceDB or published revision is unavailable.");
        }
        var built = await BuildAsync(context, cancellationToken);
        if (!built.CanValidate ||
            !string.Equals(status.ValidationRevision, built.DefinitionRevision, StringComparison.Ordinal) ||
            !JsonEquals(status.RuntimeBindingJson!, built.Json))
        {
            await InvalidateAsync(status, status.ValidationRevision!, cancellationToken);
            throw Error("RuntimeBindingStale", "The published GRAPH or PLC metadata changed after validation.");
        }
        if (!await repository.SetMonitoringAsync(
                bindingId,
                context.PublishedRevision.Id,
                built.DefinitionRevision,
                true,
                cancellationToken))
        {
            throw Error("RuntimeBindingConflict", "The runtime binding changed while monitoring was enabled.");
        }

        return new(bindingId.ToString(CultureInfo.InvariantCulture), context.Binding.BindingStatus, true, runtimeGate.IsEnabled);
    }

    private async Task<LineUpGraphRuntimeContext> LoadContextAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0)
        {
            throw Error("RuntimeBindingInvalid", "The runtime binding identity is invalid.");
        }

        return await repository.LoadContextAsync(bindingId, cancellationToken)
               ?? throw Error(
                   "RuntimeBindingUnavailable",
                   "The GRAPH station binding, published revision, or exact InstanceDB is unavailable.");
    }

    private async Task<BuiltRuntimeBinding> BuildAsync(
        LineUpGraphRuntimeContext context,
        CancellationToken cancellationToken)
    {
        LineUpPublishedGraphDefinition definition;
        try
        {
            definition = projector.Project(context);
        }
        catch (LineUpPublishedGraphProjectionException exception)
        {
            return BuiltRuntimeBinding.Empty([Issue(exception.Code, string.Empty, Blocking)]);
        }

        if (!ValidInstanceDb(context))
        {
            return BuiltRuntimeBinding.Empty([Issue("InstanceDbInvalid", string.Empty, Blocking)]);
        }

        var issues = new List<LineUpRuntimeBindingIssueDto>();
        var tags = new List<LineUpRuntimeBindingTag>();
        var sources = new List<LineUpRuntimeSourceVariable>();
        var operands = new List<LineUpRuntimeBindingStaticOperand>();
        var steps = definition.Sequences
            .SelectMany(sequence => sequence.Steps.Select(step => new SequenceStep(sequence, step)))
            .OrderBy(item => item.Sequence.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.Step.Number)
            .ThenBy(item => item.Step.SourceKey, StringComparer.Ordinal)
            .ToArray();
        await BindStepExecutionAsync(context, steps, tags, sources, issues, cancellationToken);

        if (definition.ParserSchemaVersion < 3)
        {
            issues.Add(Issue("RuntimeNetworkParserSchemaUnsupported", string.Empty, Warning));
        }
        else
        {
            await BindNetworkPartsAsync(
                context,
                definition,
                tags,
                sources,
                operands,
                issues,
                cancellationToken);
        }

        var networkCompilation = definition.ParserSchemaVersion >= 3
            ? new LineUpLadNetworkCompiler().Compile(definition, tags, operands)
            : new LineUpLadNetworkCompilation([], []);
        issues.AddRange(networkCompilation.Issues);

        var orderedTags = tags
            .OrderBy(tag => tag.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(tag => tag.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(tag => tag.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(tag => tag.PartSourceKey, StringComparer.Ordinal)
            .ToArray();
        var orderedSources = sources
            .OrderBy(source => source.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(source => source.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(source => source.PartSourceKey, StringComparer.Ordinal)
            .ToArray();
        var orderedOperands = operands
            .OrderBy(item => item.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.PartSourceKey, StringComparer.Ordinal)
            .ToArray();
        var orderedIssues = OrderIssues(issues
            .Where(issue => !ToleratedNetworkWarningCodes.Contains(issue.Code))).ToArray();
        var definitionRevision = DefinitionRevision(
            context, orderedTags, orderedOperands, orderedIssues, false, networkCompilation.Networks);
        var canValidate = steps.Length > 0 &&
                          orderedTags.Count(tag => string.Equals(tag.Role, "StepExecution", StringComparison.Ordinal)) == steps.Length &&
                          !orderedIssues.Any(IsBlocking);
        if (!canValidate)
        {
            return new(definitionRevision, string.Empty, orderedTags, orderedSources, orderedOperands,
                networkCompilation.Networks, orderedIssues, false);
        }

        var sequences = definition.Sequences
            .OrderBy(sequence => sequence.SequenceSourceKey, StringComparer.Ordinal)
            .Select(sequence => new LineUpRuntimeBindingSequence(
                sequence.SequenceSourceKey,
                sequence.Steps
                    .OrderBy(step => step.Number)
                    .ThenBy(step => step.SourceKey, StringComparer.Ordinal)
                    .Select(step => step.SourceKey)
                    .ToArray()))
            .ToArray();
        var staticIssues = orderedIssues.Select(issue => new LineUpRuntimeBindingStaticIssue(
            issue.Severity,
            issue.Code,
            issue.OwnerSourceKey,
            issue.NetworkSourceKey,
            issue.PartSourceKey)).ToArray();
        var document = new LineUpRuntimeBindingDocument(
            RuntimeBindingSchema,
            context.Binding.Id,
            context.Asset.Id,
            context.PublishedRevision.Id,
            context.PublishedRevision.SourceHash,
            context.PublishedRevision.ParserSchemaVersion,
            definitionRevision,
            context.Asset.PlcProjectId,
            context.InstanceDb.Id,
            context.InstanceDb.BlockName!.Trim(),
            false,
            sequences,
            orderedTags,
            orderedOperands,
            staticIssues,
            networkCompilation.Networks);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumRuntimeBindingBytes)
        {
            var tooLarge = OrderIssues(orderedIssues.Append(
                Issue("RuntimeBindingTooLarge", string.Empty, Blocking))).ToArray();
            return new(
                DefinitionRevision(context, orderedTags, orderedOperands, tooLarge, false,
                    networkCompilation.Networks),
                string.Empty,
                orderedTags,
                orderedSources,
                orderedOperands,
                networkCompilation.Networks,
                tooLarge,
                false);
        }

        return new(definitionRevision, json, orderedTags, orderedSources, orderedOperands,
            networkCompilation.Networks, orderedIssues, true);
    }

    private async Task BindStepExecutionAsync(
        LineUpGraphRuntimeContext context,
        IReadOnlyList<SequenceStep> steps,
        List<LineUpRuntimeBindingTag> tags,
        List<LineUpRuntimeSourceVariable> sources,
        List<LineUpRuntimeBindingIssueDto> issues,
        CancellationToken cancellationToken)
    {
        var paths = steps.Select(item => item.Step.ExecutionPath!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        LineUpVariableLookupData lookup;
        try
        {
            lookup = await metadataRepository.LoadVariableDataAsync(
                context.Asset.PlcProjectId,
                context.InstanceDb.Id,
                paths,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            issues.Add(Issue("InstanceDbMetadataUnavailable", string.Empty, Blocking));
            return;
        }

        var blocks = lookup.Blocks.Where(block =>
                block.Id == context.InstanceDb.Id &&
                block.ProjectId == context.Asset.PlcProjectId &&
                string.Equals(block.BlockName?.Trim(), context.InstanceDb.BlockName!.Trim(), StringComparison.Ordinal) &&
                string.Equals(block.ProgrammingLanguage?.Trim(), "DB", StringComparison.Ordinal) &&
                string.Equals(block.BlockType?.Trim(), "InstanceDB", StringComparison.Ordinal))
            .ToArray();
        if (blocks.Length != 1)
        {
            issues.Add(Issue("InstanceDbMetadataChanged", string.Empty, Blocking));
            return;
        }

        foreach (var item in steps)
        {
            var path = item.Step.ExecutionPath!;
            var matches = lookup.Interfaces.Where(row =>
                    row.Id > 0 && row.BlockId == context.InstanceDb.Id &&
                    string.Equals(row.VariablePath?.Trim(), path, StringComparison.Ordinal))
                .OrderBy(row => row.Id)
                .ToArray();
            if (matches.Length == 0)
            {
                issues.Add(Issue("RuntimeInterfaceMissing", item.Step.SourceKey, Blocking));
                continue;
            }
            if (matches.Length != 1)
            {
                issues.Add(Issue("RuntimeInterfaceAmbiguous", item.Step.SourceKey, Blocking));
                continue;
            }
            var match = matches[0];
            if (!IsBoolean(match.DataType))
            {
                issues.Add(Issue("RuntimeInterfaceTypeInvalid", item.Step.SourceKey, Blocking));
                continue;
            }

            AddTagAndSource(
                tags,
                sources,
                item.Sequence.SequenceSourceKey,
                "StepExecution",
                item.Step.SourceKey,
                item.Step.SourceKey,
                item.Step.SourceKey,
                "InstanceDb",
                item.Step.DisplayName,
                LineUpRuntimeVariableRole.StepActive,
                item.Step.Number,
                context.InstanceDb,
                match,
                path);
        }
    }

    private async Task BindNetworkPartsAsync(
        LineUpGraphRuntimeContext context,
        LineUpPublishedGraphDefinition definition,
        List<LineUpRuntimeBindingTag> tags,
        List<LineUpRuntimeSourceVariable> sources,
        List<LineUpRuntimeBindingStaticOperand> operands,
        List<LineUpRuntimeBindingIssueDto> issues,
        CancellationToken cancellationToken)
    {
        var parts = definition.Sequences
            .SelectMany(sequence => sequence.Steps.Concat(sequence.Transitions)
                .SelectMany(owner => owner.LogicNetworks.SelectMany(network => network.Parts
                    .Where(part => string.Equals(part.PartName, "Access", StringComparison.Ordinal))
                    .Select(part => new NetworkPart(sequence, owner, network, part)))))
            .OrderBy(item => item.Sequence.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.Owner.SourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.Network.SourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.Part.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var globalParts = new List<NetworkPart>();
        foreach (var item in parts)
        {
            if (item.Network.Role is not ("TransitionCondition" or "StepInterlock" or "StepSupervision"))
            {
                issues.Add(NetworkIssue("RuntimeNetworkRoleUnsupported", item, Warning));
                continue;
            }
            if (item.Part.Scope is "LiteralConstant" or "TypedConstant")
            {
                operands.Add(new(
                    item.Sequence.SequenceSourceKey,
                    RuntimeRole(item.Network.Role),
                    item.Owner.SourceKey,
                    item.Network.SourceKey,
                    item.Part.SourceKey,
                    item.Part.Scope,
                    item.Part.ConstantValue));
                continue;
            }
            if (!string.Equals(item.Part.Scope, "GlobalVariable", StringComparison.Ordinal) ||
                item.Part.SymbolPath.Count < 2 ||
                item.Part.SymbolPath.Any(string.IsNullOrWhiteSpace))
            {
                issues.Add(NetworkIssue("RuntimeNetworkScopeUnsupported", item, Warning));
                continue;
            }
            globalParts.Add(item);
        }
        if (globalParts.Count == 0) return;

        var requests = globalParts.Select(item => new LineUpGraphSymbolLookupRequest(
                item.Part.SymbolPath[0].Trim(),
                string.Join('.', item.Part.SymbolPath.Skip(1).Select(value => value.Trim()))))
            .Distinct()
            .OrderBy(item => item.BlockName, StringComparer.Ordinal)
            .ThenBy(item => item.VariablePath, StringComparer.Ordinal)
            .ToArray();
        LineUpGraphSymbolLookupData lookup;
        try
        {
            lookup = await metadataRepository.LoadGraphSymbolDataAsync(
                context.Asset.PlcProjectId,
                requests,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            foreach (var item in globalParts)
                issues.Add(NetworkIssue("RuntimeNetworkMetadataUnavailable", item, Warning));
            return;
        }

        foreach (var item in globalParts)
        {
            var blockName = item.Part.SymbolPath[0].Trim();
            var variablePath = string.Join('.', item.Part.SymbolPath.Skip(1).Select(value => value.Trim()));
            var results = lookup.Symbols.Where(result =>
                    string.Equals(result.BlockName, blockName, StringComparison.Ordinal) &&
                    string.Equals(result.VariablePath, variablePath, StringComparison.Ordinal))
                .ToArray();
            if (results.Length == 0)
            {
                // 2026-09-11 跨 PLC 通信块（如 DB182_PLC1<->PLC2）：引用对端工程的块，本 PLC 读不到，
                // 归类为外部引用（容忍码）而非 BlockMissing；名字含特殊字符还会毒杀整批网关读（见毒批隔离）。
                issues.Add(NetworkIssue(
                    LineUpNetworkSymbolFallback.IsExternalReferenceBlockName(blockName)
                        ? "RuntimeNetworkExternalReference"
                        : "RuntimeNetworkBlockMissing",
                    item,
                    Warning));
                continue;
            }
            if (results.Length != 1)
            {
                issues.Add(NetworkIssue("RuntimeNetworkBlockAmbiguous", item, Warning));
                continue;
            }

            var candidates = results[0].Blocks.Where(block =>
                    block.ProjectId == context.Asset.PlcProjectId &&
                    string.Equals(block.BlockName?.Trim(), blockName, StringComparison.Ordinal))
                .ToArray();
            var supported = candidates.Where(IsSupportedNamedDb).OrderBy(block => block.Id).ToArray();
            if (supported.Length == 0)
            {
                issues.Add(NetworkIssue(
                    candidates.Length == 0 ? "RuntimeNetworkBlockMissing" : "RuntimeNetworkBlockUnsupported",
                    item,
                    Warning));
                continue;
            }
            if (supported.Length != 1)
            {
                issues.Add(NetworkIssue("RuntimeNetworkBlockAmbiguous", item, Warning));
                continue;
            }

            var block = supported[0];
            var interfaces = results[0].Interfaces.Where(row =>
                    row.Id > 0 && row.BlockId == block.Id &&
                    string.Equals(row.VariablePath?.Trim(), variablePath, StringComparison.Ordinal))
                .OrderBy(row => row.Id)
                .ToArray();
            if (interfaces.Length == 0)
            {
                // 2026-09-11 深层符号回退（与运行时 LineUpMonitorRuntimeMetadataValidator.ResolveGraphOutcome 同口径）：
                // ①数组同名展开 X[i]→X.X[i]——GRAPH XML 把「成员 X 内同名数组 X[i]」压成一个带索引组件，
                //   接口表与 PLC 真实可读路径均为双层 X.X[i]（实测 Status.Robot.Robot[1].HomeComplete Good）；
                // ②最长前缀容器行 + LAD 消费者推断类型（喂触点=Bool）——接口表常只展开到 UDT 容器层，
                //   老网关 S7 Plus 可按完整符号路径直读深层叶子（实测 DB281…SafetyZone6 Good）。
                // 两级都失败才落 InterfaceMissing（容忍码，不展示）。
                var fallback = LineUpNetworkSymbolFallback.ResolveDeepLeaf(
                    results[0].Interfaces,
                    block.Id,
                    variablePath,
                    LineUpNetworkSymbolFallback.InferAccessDataType(item.Network, item.Part));
                if (fallback is not null)
                {
                    AddTagAndSource(
                        tags,
                        sources,
                        item.Sequence.SequenceSourceKey,
                        RuntimeRole(item.Network.Role),
                        item.Owner.SourceKey,
                        item.Network.SourceKey,
                        item.Part.SourceKey,
                        item.Part.Scope,
                        item.Part.SourceKey,
                        RuntimeVariableRole(item.Network.Role),
                        null,
                        block,
                        fallback.Value.Interface,
                        fallback.Value.BoundVariablePath);
                    continue;
                }

                issues.Add(NetworkIssue("RuntimeNetworkInterfaceMissing", item, Warning));
                continue;
            }
            if (interfaces.Length != 1)
            {
                issues.Add(NetworkIssue("RuntimeNetworkInterfaceAmbiguous", item, Warning));
                continue;
            }
            var match = interfaces[0];
            if (string.IsNullOrWhiteSpace(match.DataType))
            {
                issues.Add(NetworkIssue("RuntimeNetworkInterfaceTypeMissing", item, Warning));
                continue;
            }

            AddTagAndSource(
                tags,
                sources,
                item.Sequence.SequenceSourceKey,
                RuntimeRole(item.Network.Role),
                item.Owner.SourceKey,
                item.Network.SourceKey,
                item.Part.SourceKey,
                item.Part.Scope,
                item.Part.SourceKey,
                RuntimeVariableRole(item.Network.Role),
                null,
                block,
                match,
                variablePath);
        }
    }

    private static void AddTagAndSource(
        List<LineUpRuntimeBindingTag> tags,
        List<LineUpRuntimeSourceVariable> sources,
        string sequenceSourceKey,
        string roleName,
        string ownerSourceKey,
        string networkSourceKey,
        string partSourceKey,
        string scope,
        string displayName,
        LineUpRuntimeVariableRole role,
        int? stepNumber,
        LineUpBlockLookup block,
        LineUpBlockInterfaceLookup match,
        string variablePath)
    {
        var blockName = block.BlockName!.Trim();
        var dataType = match.DataType!.Trim();
        var canonicalPath = $"{blockName}.{variablePath}";
        tags.Add(new(
            sequenceSourceKey,
            roleName,
            ownerSourceKey,
            networkSourceKey,
            partSourceKey,
            scope,
            block.Id,
            blockName,
            match.Id,
            variablePath,
            canonicalPath,
            dataType,
            null,
            500));
        sources.Add(new(
            ownerSourceKey,
            networkSourceKey,
            partSourceKey,
            displayName,
            role,
            stepNumber,
            block.Id,
            blockName,
            match.Id,
            variablePath,
            canonicalPath,
            dataType,
            null,
            true));
    }

    internal static string DefinitionRevision(
        LineUpGraphRuntimeContext context,
        IEnumerable<LineUpRuntimeBindingTag> tags,
        IEnumerable<LineUpRuntimeBindingStaticOperand> operands,
        IEnumerable<LineUpRuntimeBindingIssueDto> issues,
        bool persistSamples = false,
        IEnumerable<LineUpRuntimeLogicNetworkDefinition>? compiledNetworks = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, "lineup-runtime-definition-v4");
        AppendField(hash, context.Binding.Id.ToString(CultureInfo.InvariantCulture));
        AppendField(hash, context.Asset.Id.ToString(CultureInfo.InvariantCulture));
        AppendField(hash, context.PublishedRevision.Id.ToString(CultureInfo.InvariantCulture));
        AppendField(hash, context.PublishedRevision.SourceHash);
        AppendField(hash, context.PublishedRevision.ParserSchemaVersion.ToString(CultureInfo.InvariantCulture));
        AppendField(hash, context.Asset.PlcProjectId.ToString(CultureInfo.InvariantCulture));
        AppendField(hash, context.InstanceDb.Id.ToString(CultureInfo.InvariantCulture));
        AppendField(hash, context.InstanceDb.BlockName);
        AppendField(hash, persistSamples ? "true" : "false");
        var orderedTags = tags.OrderBy(tag => tag.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(tag => tag.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(tag => tag.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(tag => tag.PartSourceKey, StringComparer.Ordinal).ToArray();
        AppendCollection(hash, "tags", orderedTags.Length);
        foreach (var tag in orderedTags)
        {
            AppendField(hash, "tag");
            AppendField(hash, tag.SequenceSourceKey);
            AppendField(hash, tag.Role);
            AppendField(hash, tag.OwnerSourceKey);
            AppendField(hash, tag.NetworkSourceKey);
            AppendField(hash, tag.PartSourceKey);
            AppendField(hash, tag.Scope);
            AppendField(hash, tag.ResolvedBlockId.ToString(CultureInfo.InvariantCulture));
            AppendField(hash, tag.ResolvedBlockName);
            AppendField(hash, tag.ResolvedBlockInterfaceId.ToString(CultureInfo.InvariantCulture));
            AppendField(hash, tag.VariablePath);
            AppendField(hash, tag.CanonicalPlcPath);
            AppendField(hash, tag.DataType);
            AppendField(hash, tag.NumericAddress);
            AppendField(hash, tag.ExpectedScanMs.ToString(CultureInfo.InvariantCulture));
        }
        var orderedOperands = operands.OrderBy(item => item.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.PartSourceKey, StringComparer.Ordinal).ToArray();
        AppendCollection(hash, "operands", orderedOperands.Length);
        foreach (var operand in orderedOperands)
        {
            AppendField(hash, "operand");
            AppendField(hash, operand.SequenceSourceKey);
            AppendField(hash, operand.Role);
            AppendField(hash, operand.OwnerSourceKey);
            AppendField(hash, operand.NetworkSourceKey);
            AppendField(hash, operand.PartSourceKey);
            AppendField(hash, operand.Scope);
            AppendField(hash, operand.ConstantValue);
        }
        var orderedIssues = OrderIssues(issues).ToArray();
        AppendCollection(hash, "issues", orderedIssues.Length);
        foreach (var issue in orderedIssues)
        {
            AppendField(hash, "issue");
            AppendField(hash, issue.Severity);
            AppendField(hash, issue.Code);
            AppendField(hash, issue.OwnerSourceKey);
            AppendField(hash, issue.NetworkSourceKey);
            AppendField(hash, issue.PartSourceKey);
        }
        var orderedNetworks = compiledNetworks?.OrderBy(item => item.SequenceSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.NetworkSourceKey, StringComparer.Ordinal).ToArray();
        AppendCollection(hash, "compiledNetworks", orderedNetworks?.Length);
        foreach (var network in orderedNetworks ?? [])
        {
            AppendField(hash, "network");
            AppendField(hash, network.SequenceSourceKey);
            AppendField(hash, network.OwnerSourceKey);
            AppendField(hash, network.NetworkSourceKey);
            AppendField(hash, network.Role);
            AppendField(hash, network.ProgrammingLanguage);
            AppendField(hash, network.CoilPartSourceKey);
            var orderedParts = network.Parts.IsDefault
                ? null
                : network.Parts.OrderBy(item => item.UId)
                    .ThenBy(item => item.SourceKey, StringComparer.Ordinal).ToArray();
            AppendCollection(hash, "parts", orderedParts?.Length);
            foreach (var part in orderedParts ?? [])
            {
                AppendField(hash, "part");
                AppendField(hash, part.SourceKey);
                AppendField(hash, part.UId.ToString(CultureInfo.InvariantCulture));
                AppendField(hash, part.PartName);
                AppendField(hash, part.Scope);
                AppendCollection(hash, "symbolPath", part.SymbolPath.IsDefault ? null : part.SymbolPath.Length);
                if (!part.SymbolPath.IsDefault)
                    foreach (var component in part.SymbolPath) AppendField(hash, component);
                AppendField(hash, part.ConstantValue);
                AppendField(hash, part.Negated ? "true" : "false");
                AppendCollection(hash, "attributes", part.Attributes?.Count);
                foreach (var attribute in (part.Attributes ?? ImmutableSortedDictionary<string, string>.Empty)
                             .OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    AppendField(hash, attribute.Key);
                    AppendField(hash, attribute.Value);
                }
                AppendCollection(hash, "templateValues", part.TemplateValues?.Count);
                foreach (var template in (part.TemplateValues ?? ImmutableSortedDictionary<string, string>.Empty)
                             .OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    AppendField(hash, template.Key);
                    AppendField(hash, template.Value);
                }
                var orderedPins = part.Pins.IsDefault
                    ? null
                    : part.Pins.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                AppendCollection(hash, "pins", orderedPins?.Length);
                foreach (var pin in orderedPins ?? [])
                {
                    AppendField(hash, pin.Name);
                    AppendField(hash, pin.Direction);
                }
            }
            var orderedWires = network.Wires.IsDefault
                ? null
                : network.Wires.OrderBy(item => item.UId)
                    .ThenBy(item => item.SourceKey, StringComparer.Ordinal).ToArray();
            AppendCollection(hash, "wires", orderedWires?.Length);
            foreach (var wire in orderedWires ?? [])
            {
                AppendField(hash, "wire");
                AppendField(hash, wire.SourceKey);
                AppendField(hash, wire.UId.ToString(CultureInfo.InvariantCulture));
                var endpoints = CanonicalWireEndpoints(wire.Endpoints);
                AppendCollection(hash, "endpoints", wire.Endpoints.IsDefault ? null : endpoints.Length);
                foreach (var endpoint in endpoints)
                {
                    AppendField(hash, endpoint.Kind);
                    AppendField(hash, endpoint.UId?.ToString(CultureInfo.InvariantCulture));
                    AppendField(hash, endpoint.Name);
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ImmutableArray<LineUpRuntimeLogicEndpointDefinition> CanonicalWireEndpoints(
        ImmutableArray<LineUpRuntimeLogicEndpointDefinition> endpoints)
    {
        if (endpoints.IsDefaultOrEmpty) return [];
        return endpoints.Take(1).Concat(endpoints.Skip(1)
                .OrderBy(endpoint => endpoint.Kind, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.UId)
                .ThenBy(endpoint => endpoint.Name, StringComparer.Ordinal))
            .ToImmutableArray();
    }

    private static void AppendCollection(IncrementalHash hash, string label, int? count)
    {
        AppendField(hash, label);
        AppendField(hash, count?.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendField(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData([0]);
            return;
        }
        hash.AppendData([1]);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string BuildValidationJson(
        string definitionRevision,
        IReadOnlyList<LineUpRuntimeBindingIssueDto> issues)
    {
        var ordered = OrderIssues(issues).Select(SafeIssue).ToArray();
        var blockingCount = ordered.Count(IsBlocking);
        var warningCount = ordered.Count(issue => string.Equals(issue.Severity, Warning, StringComparison.Ordinal));
        var included = new List<LineUpRuntimeBindingIssueDto>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            included.Add(ordered[index]);
            var candidate = ValidationJson(definitionRevision, ordered.Length, blockingCount, warningCount,
                index + 1 < ordered.Length, included);
            if (Encoding.UTF8.GetByteCount(candidate) > MaximumRuntimeValidationBytes)
            {
                included.RemoveAt(included.Count - 1);
                break;
            }
        }
        var json = ValidationJson(
            definitionRevision,
            ordered.Length,
            blockingCount,
            warningCount,
            included.Count < ordered.Length,
            included);
        while (Encoding.UTF8.GetByteCount(json) > MaximumRuntimeValidationBytes && included.Count > 0)
        {
            included.RemoveAt(included.Count - 1);
            json = ValidationJson(definitionRevision, ordered.Length, blockingCount, warningCount, true, included);
        }
        return json;
    }

    private static string ValidationJson(
        string definitionRevision,
        int totalCount,
        int blockingCount,
        int warningCount,
        bool truncated,
        IReadOnlyList<LineUpRuntimeBindingIssueDto> issues) => JsonSerializer.Serialize(new
    {
        schema = "lineup.graph-runtime-validation.v1",
        definitionRevision,
        totalCount,
        blockingCount,
        warningCount,
        truncated,
        issues
    }, JsonOptions);

    private static LineUpRuntimeBindingIssueDto SafeIssue(LineUpRuntimeBindingIssueDto issue) => issue with
    {
        Code = Safe(issue.Code, 128),
        OwnerSourceKey = Safe(issue.OwnerSourceKey, 512),
        Severity = string.Equals(issue.Severity, Blocking, StringComparison.Ordinal) ? Blocking : Warning,
        NetworkSourceKey = Safe(issue.NetworkSourceKey, 512),
        PartSourceKey = Safe(issue.PartSourceKey, 512)
    };

    private static string Safe(string? value, int maximumCharacters)
    {
        var safe = value ?? string.Empty;
        if (safe.Length <= maximumCharacters) return safe;
        var truncated = safe[..maximumCharacters];
        return truncated.Length > 0 && char.IsHighSurrogate(truncated[^1])
            ? truncated[..^1]
            : truncated;
    }

    private async Task ResetAsync(LineUpGraphRuntimeContext context, CancellationToken cancellationToken)
    {
        if (!await repository.ResetAsync(context.Binding.Id, context.PublishedRevision.Id, cancellationToken))
        {
            throw Error("RuntimeBindingConflict", "The runtime binding changed while stale validation was cleared.");
        }
    }

    private async Task InvalidateAsync(
        LineUpGraphRuntimeBindingStatusData status,
        string expectedDefinitionRevision,
        CancellationToken cancellationToken)
    {
        if (status.PublishedRevisionId is > 0 && !string.IsNullOrWhiteSpace(expectedDefinitionRevision))
        {
            await repository.InvalidateAsync(
                status.Id,
                status.PublishedRevisionId.Value,
                expectedDefinitionRevision,
                cancellationToken);
            compiler.Invalidate(status.Id);
            bindingCache.Invalidate(status.Id);
        }
    }

    private async Task InvalidateAsync(
        LineUpGraphRuntimeBindingData binding,
        string fallbackDefinitionRevision,
        CancellationToken cancellationToken)
    {
        var expected = binding.ValidationRevision ?? TryDefinitionRevision(binding.RuntimeBindingJson) ?? fallbackDefinitionRevision;
        if (!string.IsNullOrWhiteSpace(expected))
        {
            await repository.InvalidateAsync(
                binding.Id,
                binding.PublishedRevisionId,
                expected,
                cancellationToken);
            compiler.Invalidate(binding.Id);
            bindingCache.Invalidate(binding.Id);
        }
    }

    private static string? TryDefinitionRevision(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonNode.Parse(json)?["definitionRevision"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool JsonEquals(string left, string right)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidInstanceDb(LineUpGraphRuntimeContext context) =>
        context.Binding.InstanceDbBlockId is > 0 &&
        context.InstanceDb.Id == context.Binding.InstanceDbBlockId &&
        context.InstanceDb.ProjectId == context.Asset.PlcProjectId &&
        string.Equals(context.InstanceDb.ProgrammingLanguage, "DB", StringComparison.Ordinal) &&
        string.Equals(context.InstanceDb.BlockType, "InstanceDB", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(context.InstanceDb.BlockName);

    private static bool IsSupportedNamedDb(LineUpBlockLookup block) =>
        string.Equals(block.ProgrammingLanguage?.Trim(), "DB", StringComparison.Ordinal) &&
        block.BlockType?.Trim() is { } type &&
        (string.Equals(type, "DB", StringComparison.Ordinal) ||
         string.Equals(type, "GlobalDB", StringComparison.Ordinal) ||
         string.Equals(type, "InstanceDB", StringComparison.Ordinal));

    private static bool IsBoolean(string? dataType) =>
        string.Equals(dataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(dataType?.Trim(), "Boolean", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequired(LineUpRuntimeVariableRole? role) => role == LineUpRuntimeVariableRole.StepActive;

    private static bool IsBlocking(LineUpRuntimeBindingIssueDto issue) =>
        string.Equals(issue.Severity, Blocking, StringComparison.Ordinal);

    private static bool IsValidated(LineUpGraphRuntimeBindingStatusData status) =>
        string.Equals(status.BindingStatus, "Resolved", StringComparison.Ordinal) &&
        status.PublishedRevisionId is > 0 &&
        !string.IsNullOrWhiteSpace(status.RuntimeBindingJson) &&
        !string.IsNullOrWhiteSpace(status.RuntimeValidationJson) &&
        !string.IsNullOrWhiteSpace(status.ValidationRevision) &&
        status.LastValidatedAt is not null;

    private static string RuntimeRole(string role) => role switch
    {
        "TransitionCondition" => "TransitionCondition",
        "StepInterlock" => "Interlock",
        "StepSupervision" => "Supervision",
        _ => "Unknown"
    };

    private static LineUpRuntimeVariableRole RuntimeVariableRole(string role) => role switch
    {
        "TransitionCondition" => LineUpRuntimeVariableRole.TransitionCondition,
        "StepInterlock" => LineUpRuntimeVariableRole.Interlock,
        "StepSupervision" => LineUpRuntimeVariableRole.Supervision,
        _ => LineUpRuntimeVariableRole.AutoMode
    };

    private static LineUpRuntimeBindingIssueDto NetworkIssue(
        string code,
        NetworkPart item,
        string severity) => Issue(
        code,
        item.Owner.SourceKey,
        severity,
        item.Network.SourceKey,
        item.Part.SourceKey);

    private static LineUpRuntimeBindingIssueDto Issue(
        string code,
        string? owner,
        string severity,
        string? network = null,
        string? part = null) => new(
        code,
        owner ?? string.Empty,
        severity,
        network ?? string.Empty,
        part ?? string.Empty);

    private static IOrderedEnumerable<LineUpRuntimeBindingIssueDto> OrderIssues(
        IEnumerable<LineUpRuntimeBindingIssueDto> issues) => issues
        .Distinct()
        .OrderBy(issue => issue.Severity, StringComparer.Ordinal)
        .ThenBy(issue => issue.Code, StringComparer.Ordinal)
        .ThenBy(issue => issue.OwnerSourceKey, StringComparer.Ordinal)
        .ThenBy(issue => issue.NetworkSourceKey, StringComparer.Ordinal)
        .ThenBy(issue => issue.PartSourceKey, StringComparer.Ordinal);

    private static LineUpImportException Error(string code, string summary) =>
        new(code, string.Empty, summary);

    private sealed record SequenceStep(
        LineUpPublishedSequenceDefinition Sequence,
        LineUpPublishedNodeDefinition Step);

    private sealed record NetworkPart(
        LineUpPublishedSequenceDefinition Sequence,
        LineUpPublishedNodeDefinition Owner,
        LineUpPublishedLogicNetworkDefinition Network,
        LineUpPublishedLogicPartDefinition Part);

    private sealed record BuiltRuntimeBinding(
        string DefinitionRevision,
        string Json,
        IReadOnlyList<LineUpRuntimeBindingTag> Tags,
        IReadOnlyList<LineUpRuntimeSourceVariable> Sources,
        IReadOnlyList<LineUpRuntimeBindingStaticOperand> StaticOperands,
        IReadOnlyList<LineUpRuntimeLogicNetworkDefinition> CompiledNetworks,
        IReadOnlyList<LineUpRuntimeBindingIssueDto> Issues,
        bool CanValidate)
    {
        public static BuiltRuntimeBinding Empty(IReadOnlyList<LineUpRuntimeBindingIssueDto> issues) =>
            new(string.Empty, string.Empty, [], [], [], [], issues, false);
    }
}
