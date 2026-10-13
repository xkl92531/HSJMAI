#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>报警位所在调用点：程序段 Id（PipelineUnit.Id，归档源时是哈希、不是库 ai_plc_logicunit.id）、块名、
/// 网络号（PipelineUnit.NetworkOrder：归档源时是「根元素序号」，空网络不计数，只供离线评测与本次报告内部对照）、实例名、被调块名。
/// DbNetwork：与库 ai_plc_logicunit.networkid 同口径的程序段编号（PipelineUnit.DbNetworkId），报警清单存这个。</summary>
public sealed record UnderstandingAlarm(CallSiteAlarm Alarm, long UnitId, string BlockName, int? Network, string Instance, string Callee)
{
    public int? DbNetwork { get; init; }
}

/// <summary>RejectReasons 键为「FB|kind|序号」。</summary>
public sealed record UnderstandingReport(IReadOnlyDictionary<string, string> FbStatus, IReadOnlyDictionary<string, string> RejectReasons,
    IReadOnlyList<UnderstandingAlarm> Alarms, IReadOnlyDictionary<string, int> NoteCounts, int RulesAdded, int RulesReplaced)
{
    /// <summary>普通网络 AI：程序段 Id → NetworkAiStatus。</summary>
    public IReadOnlyDictionary<long, string> NetworkStatus { get; init; } = new Dictionary<long, string>();
    /// <summary>普通网络 AI 被拒的结论：「块/网络|operator|序号」→ 原因。</summary>
    public IReadOnlyDictionary<string, string> NetworkRejectReasons { get; init; } = new Dictionary<string, string>();
    /// <summary>超长 FB 分段读取有段失败、但整块仍判 Ok/Rejected 的 FB：块名 → 失败说明（FbContractOutcome.Error）。
    /// 整项目发布可容忍（只少这些段的关系）；按范围重跑据此判失败（审查复核 I-2）。</summary>
    public IReadOnlyDictionary<string, string> FbSegmentFailures { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>普通网络阶段整体出错的说明（只丢网络结果，契约规则与报警清单照常）；正常为 null。</summary>
    public string? NetworkReadError { get; init; }
    /// <summary>被代码判定丢弃的新流程瞬时规则（契约互斥的调用点反例、契约互斥在块内有反例而调用点证不出、普通网络 RefutedByIr），
    /// 每条「块/网络 operator 信号… 原因」，供核对。</summary>
    public IReadOnlyList<string> RefutedRules { get; init; } = [];
}

/// <summary>spec §2：FB 契约 + 调用点代入的产出合并进生成结果；同 Key 以新流程为准，但不把更强的已有规则降级。</summary>
public static class UnderstandingRuleSource
{
    const int MaxDerivedDepth = 4;
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    /// <summary>先读 FB 契约，再读普通网络（networks 为 null 时不读）。</summary>
    /// <param name="ct">读取阶段的取消（可含理解步骤总期限）。</param>
    /// <param name="cancelMerge">合并阶段的取消：只放调用方自己的取消（照常往外抛）；不给时用 ct。</param>
    /// <param name="codeProofDeadline">代码证明（及依赖它的过滤）的期限：到期只停代码证明（CodeProofDeadline），契约代入、
    /// 网络 AI 结果与报警清单照常合并输出。</param>
    public static Task<(Original353ProjectResult Result, UnderstandingReport Report)> ApplyAsync(PipelineSnapshot source,
        Original353ProjectResult generated, FbContractReader reader, string model, CancellationToken ct, NetworkAiReader? networks = null,
        CancellationToken? cancelMerge = null, CancellationToken codeProofDeadline = default)
        => ApplyAsync(source, generated, reader, model, ct,
            networks is null ? null : (contracts, token) => networks.ReadAsync(source, contracts, token), cancelMerge, codeProofDeadline);

    /// <summary>readNetworks 整体出错（取消除外）时只丢网络结果（裁定 F11），契约规则与报警清单照常合并，错误记在 NetworkReadError。</summary>
    internal static async Task<(Original353ProjectResult Result, UnderstandingReport Report)> ApplyAsync(PipelineSnapshot source,
        Original353ProjectResult generated, FbContractReader reader, string model, CancellationToken ct,
        Func<IReadOnlyDictionary<string, FbContractOutcome>, CancellationToken, Task<IReadOnlyDictionary<long, NetworkAiOutcome>>>? readNetworks,
        CancellationToken? cancelMerge = null, CancellationToken codeProofDeadline = default)
    {
        var contracts = await reader.ReadAsync(source, RelevantFbSelector.Select(source), ct);
        IReadOnlyDictionary<long, NetworkAiOutcome>? networkOutcomes = null;
        string? networkError = null;
        if (readNetworks is not null)
        {
            try { networkOutcomes = await readNetworks(contracts, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { networkError = ex.GetType().Name + ": " + ex.Message; }
        }
        // 合并是同步的大段计算（代码证明含可满足性求解）：调用方取消照常往外抛；期限只停代码证明，其余照常合并
        var (result, report) = Merge(source, generated, contracts, model, networkOutcomes, validateNetwork: null,
            ct: cancelMerge ?? ct, proofDeadline: codeProofDeadline);
        return (result, networkError is null ? report : report with { NetworkReadError = networkError });
    }

    public static (Original353ProjectResult Result, UnderstandingReport Report) Merge(PipelineSnapshot source,
        Original353ProjectResult generated, IReadOnlyDictionary<string, FbContractOutcome> contracts, string model,
        IReadOnlyDictionary<long, NetworkAiOutcome>? networkOutcomes = null)
        => Merge(source, generated, contracts, model, networkOutcomes, validateNetwork: null);

    /// <param name="validateNetwork">测试钩子：替换普通网络核对（为 null 时用 NetworkConstraintValidator）。</param>
    /// <param name="prove">测试钩子：替换按程序段的代码证明（为 null 时用 NetworkProofs.Prove）。</param>
    /// <param name="buildProofs">测试钩子：由写入索引建代码证明器（为 null 时直接 new NetworkProofs）。</param>
    /// <remarks>代码证明的失败收住（整计划审查 I-1）：项目级写入索引/证明器/常量输出建不起来 → 关掉代码证明与依赖它的过滤
    /// （计 CodeProofDisabled；调用点互斥判定一律 NotProven，须调用点证明的契约互斥照常丢弃），契约代入、普通网络 AI、报警清单照常；
    /// 单个程序段证明抛异常 → 跳过该段（计 CodeProofFailed）；调用点互斥判定抛异常 → 按 NotProven（只会少产）。取消（ct）照常往外抛。
    /// proofDeadline 到期 → 只停代码证明及依赖它的部分（计 CodeProofDeadline）：已产出的代码证明保留；之后的调用点互斥判定一律 NotProven
    /// （须调用点证明的契约互斥照常丢弃）、普通网络核对改用不做代码判定的核对器、合并后的 SAME_STATE 证伪与常量输出过滤不做；
    /// 契约代入、普通网络 AI 结果与报警清单照常输出。</remarks>
    internal static (Original353ProjectResult Result, UnderstandingReport Report) Merge(PipelineSnapshot source,
        Original353ProjectResult generated, IReadOnlyDictionary<string, FbContractOutcome> contracts, string model,
        IReadOnlyDictionary<long, NetworkAiOutcome>? networkOutcomes,
        Func<AiNetworkReply, IrUnit, IrBlock, IReadOnlyList<ValidatedNetworkConstraint>>? validateNetwork,
        Func<IrUnit, IrBlock, IReadOnlyList<ProvenRelation>>? prove = null, Func<ProjectWrites, NetworkProofs>? buildProofs = null,
        CancellationToken ct = default, CancellationToken proofDeadline = default)
    {
        var blocks = new Dictionary<string, IrBlock>(C);
        IrBlock Block(string name) => blocks.TryGetValue(name, out var b) ? b : blocks[name] = IrBlock.Load(source, name);
        var formalsOf = new Dictionary<string, IReadOnlySet<string>>(C);
        IReadOnlySet<string> Formals(string block) => formalsOf.TryGetValue(block, out var f) ? f
            : formalsOf[block] = Block(block).Interface.Where(m => m.Section is "Input" or "Output" or "InOut")
                .Select(m => m.Name).ToHashSet(C);
        var staticsOf = new Dictionary<string, IReadOnlySet<string>>(C);
        IReadOnlySet<string> Statics(string block) => staticsOf.TryGetValue(block, out var s) ? s
            : staticsOf[block] = Block(block).Interface.Where(m => m.Section == "Static").Select(m => m.Name).ToHashSet(C);
        var localsOf = new Dictionary<string, IReadOnlySet<string>>(C);
        IReadOnlySet<string> Locals(string block) => localsOf.TryGetValue(block, out var l) ? l
            : localsOf[block] = Block(block).Interface.Where(m => m.Section is "Static" or "Temp")
                .Select(m => m.Name).ToHashSet(C);

        // 以程序体里实际的调用为准（不依赖 Callees 列表）；同一份翻译也用于全项目写入计数
        var translated = source.Units.Select(u => (Unit: u, Ir: UnitTranslator.Translate(u, source.Tags))).ToArray();
        var calls = translated
            .SelectMany(x => x.Ir.Statements.Where(s => s.Call is not null).Select(s => (x.Unit, Statement: s, Call: s.Call!)))
            .ToArray();
        var newRules = new Dictionary<long, Dictionary<string, PipelineRule>>();
        var superseded = new Dictionary<long, HashSet<string>>();
        // 调用方块的唯一全局背景实例（报警引脚未接线的多重背景用）：看全部调用点，含 FC 里的（FC 只是不产规则）
        var globalInstanceOf = new Dictionary<string, string?>(C);
        string? GlobalInstanceOf(string block) => globalInstanceOf.TryGetValue(block, out var g) ? g
            : globalInstanceOf[block] = UniqueGlobalInstance(block, calls.Select(c => (c.Unit.BlockName, c.Call)),
                b => Locals(b).Concat(Formals(b)).ToHashSet(C));
        var alarms = new List<UnderstandingAlarm>();
        var notes = new Dictionary<string, int>(StringComparer.Ordinal);
        var refutedRules = new List<string>();
        // FC 块（按块类型，不看块名）里的写入一律不进 Q 写入索引（用户决定：TIA 导出 FC 时部分失败，FC 内容本来就不完整）
        var qWrites = new QWriteIndex(source.Tags, translated.Where(x => !IsFcBlock(x.Unit)).Select(x => x.Ir));

        // 代码证明（spec §7.1）：全项目写入索引含 FC（多算写入只会少产）；证明只在非 FC 程序段里做（用户：FC 完全不管），
        // FC 判定按块类型（PipelineUnit.BlockKind），显式传给 NetworkProofs，被调块是 FC 时它也据此按「可能写任何量」处理
        var fcBlocks = source.Units.Where(IsFcBlock).Select(u => u.BlockName).ToHashSet(C);
        ProjectWrites writes;
        NetworkProofs proofs;
        // 跨块执行顺序的同步（K5，Code:CallOrder）：与按段代码证明同一处产出、同一套写入索引
        CallOrderProof? callOrder = null;
        IReadOnlyDictionary<string, bool> constantBits = new Dictionary<string, bool>(C);
        // 期望状态报警（K4）配置输入的全项目常量解析：依赖写入索引，代码证明关掉（索引为空）时不解析（空索引会把一切当「从不写入」）
        ConfigConstants? configConstants = null;
        IReadOnlySet<string> constantOutputs;
        var codeProofsEnabled = true;
        var proofsStopped = false;
        // 代码证明期限：到期只停代码证明（计一次 CodeProofDeadline）
        bool ProofsStopped()
        {
            if (!proofsStopped && proofDeadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                proofsStopped = true;
                Count("CodeProofDeadline");
            }
            return proofsStopped;
        }
        try
        {
            if (ProofsStopped()) throw new OperationCanceledException(proofDeadline);
            // FC 文字里的间接寻址/指针常数不按区计提及（与 Q 写入索引不计 FC 同一裁定，用户：FC 完全不管）
            writes = new ProjectWrites(translated.Select(x => x.Ir), source.Tags, u => fcBlocks.Contains(u.BlockName));
            // 每个扫描都执行的起点：只认程序循环组织块（导出事件类型 ProgramCycle）；中断/错误/启动 OB、认不出类型的 OB 都不算（审查 I-1）
            var programCycleBlocks = source.Units.Where(u => string.Equals(u.BlockKind?.Trim(), "OB", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(u.OrganizationType?.Trim(), "ProgramCycle", StringComparison.OrdinalIgnoreCase))
                .Select(u => u.BlockName.Trim()).ToHashSet(C);
            // 常量位（含系统存储位按名字/注释的特例，见 ConstantBits）：代码证明当常数、常量输出过滤与期望状态报警配置解析都用它
            constantBits = ConstantBits.Find(translated.Select(x => x.Ir), source.Tags, writes, programCycleBlocks.Contains);
            foreach (var _ in constantBits) Count("ConstantBit");
            proofs = buildProofs?.Invoke(writes)
                ?? new NetworkProofs(source.Tags, qWrites.Count, qWrites.HasUnresolved, writes, name => Block(name), fcBlocks.Contains, constantBits);
            callOrder = new CallOrderProof(translated.Select(x => x.Ir), source.Tags, writes, qWrites.Count, qWrites.HasUnresolved, fcBlocks.Contains);
            configConstants = new ConfigConstants(translated.Select(x => x.Ir), writes, programCycleBlocks.Contains, source.DbInterfaces, constantBits);
            // 常量输出：已知代价（裁定 F9，只会漏）——程序从不写入的 %M 当常量，CPU 按硬件配置置位的时钟存储位也在其中
            // （不能按名字区分），所以只由时钟存储位驱动的输出（如闪烁灯）会被当成常量输出，含它的规则（任何来源）被删
            constantOutputs = ConstantOutputs.Find(translated.Select(x => x.Ir), source.Tags, writes, qWrites.Count, constantBits);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ProofsStopped())
        {
            // 关掉代码证明：空写入索引下每个程序段都不在索引里 → 证明器一律拒绝（NotProven、不产规则）
            if (!proofsStopped) Count("CodeProofDisabled");
            codeProofsEnabled = false;
            callOrder = null;
            configConstants = null;
            constantBits = new Dictionary<string, bool>(C);
            writes = new ProjectWrites([], source.Tags);
            proofs = new NetworkProofs(source.Tags, qWrites.Count, qWrites.HasUnresolved, writes, _ => null, fcBlocks.Contains);
            constantOutputs = new HashSet<string>(C);
        }
        var irById = translated.GroupBy(x => x.Unit.Id).ToDictionary(g => g.Key, g => g.First().Ir);
        var unitById = translated.GroupBy(x => x.Unit.Id).ToDictionary(g => g.Key, g => g.First().Unit);
        // 只收代码证明（Code:*）的互斥：依赖接口初值（Review:*）或 AI 指认（AI:*）的不得用来删已有规则（裁定 F10/G8）
        var provenMutex = new List<(string A, string B, string? Proof)>();
        // 新流程门控（REQUIRES_0_WHEN_FALSE）的 Source 必须是点表里唯一的 Bool 点（spec §7.1 形式校验）：DB 内部变量等读不到实时值、
        // 也不是物理信号，不入库，计 GateSourceNotIo。第一期 FbContract:Gate 不走这里，不受影响
        var uniqueBoolTags = source.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name)).GroupBy(t => t.Name.Trim(), C)
            .Where(g => g.Count() == 1 && string.Equals(g.Single().DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Key).ToHashSet(C);
        bool GateSourceIsIo(PipelineRule r) =>
            r.Operator != "REQUIRES_0_WHEN_FALSE" || !UnderstandingProofs.IsNewFlow(r.Proof)
            || r.Signals.Where(s => s.Role == "Source").All(s => uniqueBoolTags.Contains(s.Name.Trim()));

        // 合并顺序：代码证明 → FB 契约代入 → 普通网络 AI；同段同 Key 先到先得，先写入的代码证明不被后来的同 Key 规则覆盖
        foreach (var (unit, ir) in codeProofsEnabled ? translated : [])
        {
            if (IsFcBlock(unit)) continue;
            ct.ThrowIfCancellationRequested();
            if (ProofsStopped()) break;
            IReadOnlyList<ProvenRelation> provenHere;
            using var proofCancel = CancellationTokenSource.CreateLinkedTokenSource(ct, proofDeadline);
            try
            {
                provenHere = prove?.Invoke(ir, Block(unit.BlockName))
                    ?? [.. proofs.Prove(ir, Block(unit.BlockName), proofCancel.Token), .. callOrder?.Prove(ir) ?? []];
            }
            catch (OperationCanceledException) when (ProofsStopped()) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException) { Count("CodeProofFailed"); continue; }
            foreach (var p in provenHere)
            {
                var rule = NetworkProofs.ToRule(p, model);
                if (!GateSourceIsIo(rule)) { Count("GateSourceNotIo"); continue; }
                if (!newRules.TryGetValue(unit.Id, out var map)) newRules[unit.Id] = map = new(StringComparer.Ordinal);
                if (map.TryAdd(rule.Key, rule)) Count($"CodeProof:{p.Operator}:{p.Proof}");
                if (p.Operator == "AT_MOST_ONE" && UnderstandingProofs.IsCode(p.Proof)) provenMutex.Add((p.Members[0], p.Members[1], p.Proof));
            }
        }

        // 派生前提：调用方块里该输出形参（按根段，含 MO.x / MO[i] 等成员写入）只由本次调用的输出引脚写入一次，
        // 且文本段里没有提到它
        bool SoleWriterIsThisCall(string block, string name, long unitId, string statementId)
        {
            var b = Block(block);
            var root = Root(name);
            var writes = new List<(long Unit, string Statement)>();
            foreach (var u in b.Units)
                foreach (var s in u.Statements)
                {
                    if (s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset
                        && !string.IsNullOrWhiteSpace(s.Target) && C.Equals(Root(s.Target.Trim()), root))
                        writes.Add((u.UnitId, s.Id));
                    if (s.Call is not null)
                        foreach (var target in s.Call.Pins.SelectMany(p => p.Targets))
                            if (!string.IsNullOrWhiteSpace(target) && C.Equals(Root(target.Trim()), root))
                                writes.Add((u.UnitId, s.Id));
                    foreach (var target in s.SideWrites ?? [])
                        if (!string.IsNullOrWhiteSpace(target) && C.Equals(Root(target.Trim()), root))
                            writes.Add((u.UnitId, s.Id));
                }
            if (writes.Count != 1 || writes[0].Unit != unitId || writes[0].Statement != statementId) return false;
            var mention = NameMention(root);
            return !b.Units.SelectMany(u => u.Statements).Any(s => IsText(s) && mention.IsMatch(s.Text!));
        }

        // 第 0 层：真实契约；之后各层：派生契约（按调用方块名）
        var layer = new Dictionary<string, (ValidatedFbContract Contract, string Key)>(C);
        foreach (var (name, outcome) in contracts)
            if (outcome.Status == FbContractStatus.Ok && outcome.Contract is not null)
                layer.TryAdd(name, (outcome.Contract, outcome.CacheKey));
        for (var depth = 0; layer.Count > 0; depth++)
        {
            if (depth > MaxDerivedDepth) { Count("DerivedDepthExceeded"); break; }
            var next = new Dictionary<string, List<(AiContractRelation Relation, string Key, bool RequiresProof)>>(C);
            foreach (var (unit, statement, call) in calls)
            {
                if (!layer.TryGetValue(call.Callee, out var c)) continue;
                ct.ThrowIfCancellationRequested();
                // FC 完全不管（FC 导出不完整，其写入也不进 Q 写入索引）：FC 里的调用点不代入，不产规则、派生关系与报警
                if (IsFcBlock(unit)) { Count("CallerIsFc"); continue; }
                // 调用方块（派生时即中间块）有跳转/返回：调用可能被跳过、输出保持旧值，与调用 EN 有条件同样处理（第一期同口径）
                if (Block(unit.BlockName).HasControlFlow) { Count("CallerHasControlFlow"); continue; }
                var r = CallSiteSubstitution.Apply(c.Contract, Block(call.Callee), call, Formals(unit.BlockName),
                    Locals(unit.BlockName), source.Tags, c.Key, model, GlobalInstanceOf(unit.BlockName), Statics(unit.BlockName),
                    configConstants is null ? null : configConstants.Resolve);
                foreach (var q in r.Superseded)
                {
                    // 与规则同样的全项目唯一写入核对：q 另有写入处或有算不出范围的 Q 区写入时，不能据此证伪
                    if (qWrites.HasUnresolved || qWrites.Count(q) > 1) { Count("SupersedeSkippedQWrite"); continue; }
                    if (!superseded.TryGetValue(unit.Id, out var set)) superseded[unit.Id] = set = new HashSet<string>(C);
                    set.Add(q);
                }
                var requiresProof = r.RequiresCallSiteProof.ToHashSet(StringComparer.Ordinal);
                foreach (var produced in r.Rules)
                {
                    var rule = produced;
                    if (!GateSourceIsIo(rule)) { Count("GateSourceNotIo"); continue; }
                    // AI 指认的瞬时互斥先过代码判定：有反例 → 丢弃；证明成立 → 标为代码证明（依赖接口初值时 Review:CallSiteDefaults）；
                    // 判不了 → 保持 AI 指认。三者的分级相同（用户决定 2026-10-02：新流程瞬时规则直接判故障），这一步只为丢掉反例、
                    // 并让代码证明的互斥参与证伪 SAME_STATE。带延时的规则（到位矛盾等 AfterSettling）不参与（裁定 F16）。
                    // 契约互斥在被调块程序体里有反例（RequiresCallSiteProof）：只可能由本调用点的实参关系成立，调用点证不出就不产
                    var mustProve = requiresProof.Contains(rule.Key);
                    var provenHere = false;
                    if (NeedsIrMutexVerdict(rule) && irById.TryGetValue(unit.Id, out var callerIr))
                    {
                        ProofVerdict verdict;
                        var needsDefaults = false;
                        if (ProofsStopped()) verdict = ProofVerdict.NotProven;
                        else
                            try { verdict = proofs.MutexVerdict(callerIr, Block(unit.BlockName), rule.Signals[0].Name, rule.Signals[1].Name, out needsDefaults); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { Count("CodeProofFailed"); verdict = ProofVerdict.NotProven; }
                        if (verdict == ProofVerdict.Counterexample) { Count("MutexRefutedByIr"); Refuted(unit, rule, "MutexRefutedByIr"); continue; }
                        provenHere = verdict == ProofVerdict.Proven;
                        if (verdict == ProofVerdict.Proven)
                        {
                            var proof = needsDefaults ? UnderstandingProofs.ReviewCallSiteDefaults : UnderstandingProofs.CodeCallSite;
                            rule = rule with
                            {
                                Proof = proof,
                                Trace = rule.Trace is { } t ? t with { Origin = proof, Note = JoinNote(t.Note, "AI 指认（FB 契约），调用点代码判定成立") } : null
                            };
                            Count($"MutexProvenByIr:{proof}");
                            if (UnderstandingProofs.IsCode(proof)) provenMutex.Add((rule.Signals[0].Name, rule.Signals[1].Name, proof));
                        }
                    }
                    if (mustProve && !provenHere)
                    {
                        Count("MutexBodyCounterexampleUnproven");
                        Refuted(unit, rule, "MutexBodyCounterexampleUnproven");
                        continue;
                    }
                    // 全项目唯一写入者：调用输出引脚本身算一处，%Q 位另有写入处即不产；
                    // 项目里有算不出范围的 Q 区写入（间接寻址等）时，任何 %Q 都不能证明唯一
                    var qSignals = rule.Signals.Where(s => s.Role is "Source" or "Target" or "Member" && qWrites.IsQ(s.Name)).ToArray();
                    if (qSignals.Length > 0 && qWrites.HasUnresolved) { Count("UnresolvedQWrite"); continue; }
                    if (qSignals.Any(s => qWrites.Count(s.Name) > 1)) { Count("QWrittenElsewhere"); continue; }
                    if (!newRules.TryGetValue(unit.Id, out var map)) newRules[unit.Id] = map = new(StringComparer.Ordinal);
                    if (!map.TryAdd(rule.Key, rule) && UnderstandingProofs.IsCode(map[rule.Key].Proof)) Count("KeptCodeProof");
                }
                foreach (var a in r.Alarms)
                {
                    // 报警跟随反馈规则的去留：输出 %Q 没通过上面同一道全项目唯一写入检查时，反馈规则已被丢，
                    // 该 %Q 的值不等于本次调用的命令。报警保留，但不带输出（清单里输出为空），不再据输出状态下结论
                    var alarm = a;
                    if (!string.IsNullOrWhiteSpace(a.Output) && qWrites.IsQ(a.Output) && (qWrites.HasUnresolved || qWrites.Count(a.Output) > 1))
                    {
                        alarm = a with { Output = "" };
                        Count("AlarmOutputNotUniqueQ");
                    }
                    alarms.Add(new UnderstandingAlarm(alarm, unit.Id, unit.BlockName, unit.NetworkOrder, call.Instance, call.Callee)
                        { DbNetwork = unit.DbNetworkId });
                }
                foreach (var n in r.Notes) Count(n);
                var callerFormals = Formals(unit.BlockName);
                foreach (var d in r.Derived)
                {
                    if (d.Kind is "mutex" or "gate"
                        && d.InCallerTerms.Outputs.Any(o => callerFormals.Contains(Root(o))
                            && !SoleWriterIsThisCall(unit.BlockName, o, unit.Id, statement.Id)))
                    { Count("DerivedOutputWrittenElsewhere"); continue; }
                    if (!next.TryGetValue(unit.BlockName, out var list)) next[unit.BlockName] = list = [];
                    list.Add((d.InCallerTerms, c.Key, d.RequiresCallSiteProof));
                }
            }
            // 去重时同一关系只要有一处要求调用点证明，就要求（只会少产）
            layer = next.ToDictionary(n => n.Key,
                n => (new ValidatedFbContract(n.Key, DistinctDerived(n.Value.Select(x => x.Relation))
                    .Select(x => new ValidatedRelation(x, false, null)
                    {
                        RequiresCallSiteProof = n.Value.Any(y => y.RequiresProof && SameDerived(y.Relation, x))
                    }).ToArray()), n.Value[0].Key), C);
        }

        // 普通网络 AI（spec §6）：确定性核对每次重跑；FC 不管；块有跳转/返回时语句可能不执行、输出保持旧值，不用（与调用点同口径）；
        // 与契约规则同样过门控 Source 与 %Q 全项目唯一写入核对。每个网络单独收住异常（审查 I-1）：核对的是 AI 回复内容、回复已缓存，
        // 一处异常若冲出 Merge 会让每次发布都回退、丢掉契约规则与报警清单——只丢这个网络（计 NetworkValidateFailed，状态记 Failed）
        var networkStatus = new Dictionary<long, string>();
        var networkRejects = new Dictionary<string, string>(StringComparer.Ordinal);
        if (networkOutcomes is not null)
        {
            var validator = new NetworkConstraintValidator(source.Tags, proofs, constantOutputs,
                NetworkConstraintValidator.UnknownConstants(source.Tags, writes));
            // 代码证明期限到期后改用不做代码判定的核对器（空写入索引上的证明器一律 NotProven；常量输出过滤一并不做）
            NetworkConstraintValidator? unproven = null;
            NetworkConstraintValidator Unproven() => unproven ??= new NetworkConstraintValidator(source.Tags,
                new NetworkProofs(source.Tags, qWrites.Count, qWrites.HasUnresolved, new ProjectWrites([], source.Tags), _ => null, fcBlocks.Contains),
                new HashSet<string>(C), NetworkConstraintValidator.UnknownConstants(source.Tags, new ProjectWrites([], source.Tags)));
            var validateHook = validateNetwork;
            IReadOnlyList<ValidatedNetworkConstraint> ValidateOne(AiNetworkReply reply, IrUnit unit, IrBlock block) =>
                validateHook is not null ? validateHook(reply, unit, block)
                : ProofsStopped() ? Unproven().Validate(reply, unit, block) : validator.Validate(reply, unit, block);
            foreach (var (unitId, outcome) in networkOutcomes.OrderBy(o => o.Key))
            {
                ct.ThrowIfCancellationRequested();
                networkStatus[unitId] = outcome.Status;
                if (outcome.Status != NetworkAiStatus.Ok || outcome.Reply is null
                    || !irById.TryGetValue(unitId, out var ir) || !unitById.TryGetValue(unitId, out var pu)) continue;
                // 先在局部收集，整个网络核对完再提交，异常时不留半截结果
                var accepted = new List<PipelineRule>();
                var localNotes = new List<string>();
                var localRejects = new List<(string Key, string Reason)>();
                var localRefuted = new List<string>();
                try
                {
                    if (IsFcBlock(pu)) { Count("NetworkCallerIsFc"); continue; }
                    if (Block(pu.BlockName).HasControlFlow) { Count("NetworkBlockHasControlFlow"); continue; }
                    var validated = ValidateOne(outcome.Reply, ir, Block(pu.BlockName));
                    for (var i = 0; i < validated.Count; i++)
                    {
                        var v = validated[i];
                        if (v.RejectReason is not null)
                        {
                            localRejects.Add(($"{pu.BlockName}/{pu.NetworkOrder}|{v.Constraint.Operator}|{i}", v.RejectReason));
                            if (v.RejectReason == "RefutedByIr")
                                localRefuted.Add($"{pu.BlockName}/{pu.NetworkOrder} {v.Constraint.Operator} "
                                    + string.Join(",", v.Constraint.Signals.Select(s => s.Name)) + "（普通网络 AI）RefutedByIr");
                            continue;
                        }
                        var rule = NetworkConstraintValidator.ToRule(v, model, outcome.CacheKey);
                        if (!GateSourceIsIo(rule)) { localNotes.Add("NetworkGateSourceNotIo"); continue; }
                        var qSignals = rule.Signals.Where(s => qWrites.IsQ(s.Name)).ToArray();
                        if (qSignals.Length > 0 && qWrites.HasUnresolved) { localNotes.Add("NetworkUnresolvedQWrite"); continue; }
                        if (qSignals.Any(s => qWrites.Count(s.Name) > 1)) { localNotes.Add("NetworkQWrittenElsewhere"); continue; }
                        accepted.Add(rule);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Count("NetworkValidateFailed");
                    networkStatus[unitId] = NetworkAiStatus.Failed;
                    continue;
                }
                foreach (var (key, reason) in localRejects) networkRejects[key] = reason;
                refutedRules.AddRange(localRefuted);
                foreach (var note in localNotes) Count(note);
                foreach (var rule in accepted)
                {
                    if (!newRules.TryGetValue(unitId, out var map)) newRules[unitId] = map = new(StringComparer.Ordinal);
                    if (!map.TryAdd(rule.Key, rule)) { Count("NetworkRuleKeptEarlier"); continue; }
                    Count("NetworkRule:" + rule.Proof);
                    if (rule.Operator == "AT_MOST_ONE" && UnderstandingProofs.IsCode(rule.Proof))
                        for (var a = 0; a < rule.Signals.Count; a++)
                            for (var b = a + 1; b < rule.Signals.Count; b++)
                                provenMutex.Add((rule.Signals[a].Name, rule.Signals[b].Name, rule.Proof));
                }
            }
        }

        var (result, merged) = MergeRules(source, generated,
            newRules.ToDictionary(n => n.Key, n => (IReadOnlyList<PipelineRule>)n.Value.Values.ToArray()),
            superseded.ToDictionary(n => n.Key, n => (IReadOnlySet<string>)n.Value));
        if (merged.KeptStronger > 0) notes["KeptStrongerExisting"] = notes.GetValueOrDefault("KeptStrongerExisting") + merged.KeptStronger;
        if (merged.Superseded > 0) notes["SupersededByProof"] = notes.GetValueOrDefault("SupersededByProof") + merged.Superseded;
        // 合并后的只删不增过滤，作用于全部来源（含旧流程）：被代码证明的互斥证伪的 SAME_STATE、含常量输出的规则
        // 过滤依赖代码证明：抛异常时保留未过滤的结果（计 CodeProofFiltersFailed），不让整步回退
        var (filtered, contradicted, constant) = (result, 0, 0);
        if (!ProofsStopped())
            try { (filtered, contradicted, constant) = RuleFilters.Apply(result, provenMutex, constantOutputs); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Count("CodeProofFiltersFailed"); }
        result = filtered;
        if (contradicted > 0) notes["SameStateContradictsMutex"] = notes.GetValueOrDefault("SameStateContradictsMutex") + contradicted;
        if (constant > 0) notes["ConstantOutputRule"] = notes.GetValueOrDefault("ConstantOutputRule") + constant;

        // 锁存报警位（已知问题第三批，LatchedAlarmBits）：全局 DB 里只被置位/复位写的位，进报警清单（不产规则）。
        // 依赖写入索引判「文字段提到」：代码证明关掉（索引为空）时不找（空索引会把一切当「没提到」），计 LatchedSkippedNoWriteIndex；
        // 自身出错只丢锁存报警（计 LatchedAlarmFailed），契约报警与规则照常
        if (!codeProofsEnabled) Count("LatchedSkippedNoWriteIndex");
        else
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FC 里的置位/复位、FC 里的调用也算（用户 2026-10-02「看 FC」：只用于认报警位，约束生成仍不管 FC）
                var functionBlocks = source.Units.Where(u => string.Equals(u.BlockKind?.Trim(), "FB", StringComparison.OrdinalIgnoreCase))
                    .Select(u => u.BlockName).ToHashSet(C);
                var interfaceXml = source.Units.Where(u => !string.IsNullOrWhiteSpace(u.InterfaceXml)).GroupBy(u => u.BlockName, C)
                    .ToDictionary(g => g.Key, g => g.First().InterfaceXml, C);
                var bits = LatchedAlarmBits.Find(new LatchedAlarmProject(translated.Select(x => x.Ir).ToArray(), source.DbInterfaces,
                    b => Block(b).Interface, writes.MentionedInText)
                {
                    FunctionBlocks = functionBlocks,
                    BlockInterfaceXml = b => interfaceXml.GetValueOrDefault(b),
                }, Count);
                foreach (var bit in bits)
                {
                    if (!unitById.TryGetValue(bit.SetUnitId, out var pu)) continue;
                    alarms.Add(new UnderstandingAlarm(new CallSiteAlarm(bit.Symbol, LatchedAlarmBits.Kind, "", [], null, bit.Evidence) { Text = bit.Text },
                        pu.Id, pu.BlockName, pu.NetworkOrder, bit.Instance, bit.Callee) { DbNetwork = pu.DbNetworkId });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Count("LatchedAlarmFailed"); }
        }

        var fbStatus = new Dictionary<string, string>(C);
        foreach (var (name, outcome) in contracts) fbStatus.TryAdd(name, outcome.Status);
        var rejects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, outcome) in contracts.Where(c => c.Value.Contract is not null))
        {
            var rejected = outcome.Contract!.Rejected;
            for (var i = 0; i < rejected.Count; i++)
                rejects.TryAdd($"{name}|{rejected[i].Relation.Kind}|{i}", rejected[i].RejectReason!);
        }
        var report = new UnderstandingReport(fbStatus, rejects, alarms, notes, merged.Added, merged.Replaced)
            {
                NetworkStatus = networkStatus, NetworkRejectReasons = networkRejects, RefutedRules = refutedRules,
                FbSegmentFailures = contracts.Where(c => c.Value.Status != FbContractStatus.Failed
                        && c.Value.Error?.Contains(FbContractSegmentFailure.Marker, StringComparison.Ordinal) == true)
                    .GroupBy(c => c.Key, C).ToDictionary(g => g.Key, g => g.First().Value.Error!, C)
            };
        return (result, report);

        void Count(string note) => notes[note] = notes.GetValueOrDefault(note) + 1;
        void Refuted(PipelineUnit unit, PipelineRule rule, string reason) =>
            refutedRules.Add($"{unit.BlockName}/{unit.NetworkOrder} {rule.Operator} {string.Join(",", rule.Signals.Select(s => s.Name))}（FB 契约）{reason}");
    }

    /// <summary>契约代入产出的规则要过 IR 互斥判定：AI 指认（AI:FbContract）的两元瞬时互斥。
    /// 带延时的（RequiresSettlingEvidence / AfterSettling，如到位矛盾）不参与（裁定 F16）。</summary>
    internal static bool NeedsIrMutexVerdict(PipelineRule rule) =>
        rule.Operator == "AT_MOST_ONE" && rule.Proof == UnderstandingProofs.AiFbContract && rule.Signals.Count == 2
        && !rule.RequiresSettlingEvidence && rule.Applicability != "AfterSettling";

    static string JoinNote(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a + "；" + b;

    /// <summary>两条派生关系按 DistinctDerived 的去重键相同。</summary>
    static bool SameDerived(AiContractRelation a, AiContractRelation b) => string.Equals(DerivedKey(a), DerivedKey(b), StringComparison.Ordinal);

    static string DerivedKey(AiContractRelation r) => string.Join("\u0001",
        r.Kind.ToUpperInvariant(),
        string.Join("\u0002", r.Inputs.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
        string.Join("\u0002", r.Outputs.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
        r.PositionVar?.ToUpperInvariant() ?? "", r.Alarm?.ToUpperInvariant() ?? "");

    /// <summary>同一调用方块的派生关系去重：按（kind、排序后的输入、排序后的输出、到位变量、报警位）忽略大小写，保留首个。</summary>
    internal static IReadOnlyList<AiContractRelation> DistinctDerived(IEnumerable<AiContractRelation> relations)
    {
        static string Key(AiContractRelation r) => string.Join("\u0001",
            r.Kind.ToUpperInvariant(),
            string.Join("\u0002", r.Inputs.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join("\u0002", r.Outputs.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
            r.PositionVar?.ToUpperInvariant() ?? "", r.Alarm?.ToUpperInvariant() ?? "");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return relations.Where(r => seen.Add(Key(r))).ToArray();
    }

    /// <summary>第 4 条合并规则：新规则按 Key 写入所在程序段；同 Key 时级别不低于旧规则才替换；
    /// 新规则写入（追加或替换）时删除同 Key 的条件反馈（以新流程为准），保留更强旧规则时条件反馈不动；
    /// 重新计算 RuleCount 与 ConditionalFeedbackCount。
    /// superseded（程序段 → 被证伪的 %Q）：先删除该程序段旧规则与条件反馈里 operator 为 IMPLIES/IMPLIES_ANY、Source 为这些 %Q 的条目
    /// （新流程确定该调用点的到位不依赖传感器），再写入新规则；Superseded 为删除条数。</summary>
    public static (Original353ProjectResult Result, (int Added, int Replaced, int KeptStronger, int Superseded) Report) MergeRules(PipelineSnapshot source,
        Original353ProjectResult generated, IReadOnlyDictionary<long, IReadOnlyList<PipelineRule>> newRulesByUnit,
        IReadOnlyDictionary<long, IReadOnlySet<string>>? superseded = null)
    {
        int added = 0, replaced = 0, kept = 0, removed = 0;
        var units = generated.Units.Select(u =>
        {
            if (superseded is not null && superseded.TryGetValue(u.LogicUnitId, out var qs) && qs.Count > 0)
            {
                var q = new HashSet<string>(qs, C);
                bool Hit(PipelineRule r) => r.Operator is "IMPLIES" or "IMPLIES_ANY"
                    && r.Signals.Any(s => s.Role == "Source" && q.Contains(s.Name));
                var keptRules = u.Rules.Where(r => !Hit(r)).ToArray();
                var keptFeedback = u.ConditionalFeedbackRules.Where(f => !Hit(f.Rule)).ToArray();
                removed += u.Rules.Count - keptRules.Length + u.ConditionalFeedbackRules.Count - keptFeedback.Length;
                u = u with { Rules = keptRules, ConditionalFeedbackRules = keptFeedback };
            }
            if (!newRulesByUnit.TryGetValue(u.LogicUnitId, out var list) || list.Count == 0) return u;
            var map = new Dictionary<string, PipelineRule>(StringComparer.Ordinal);
            foreach (var rule in list) map.TryAdd(rule.Key, rule);   // 同段同 Key 只保留一条
            var rules = u.Rules.ToList();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, rule) in map)
            {
                var index = rules.FindIndex(x => x.Key == key);
                if (index < 0) { rules.Add(rule); added++; written.Add(key); }
                else if (Rank(rule, source.Tags) >= Rank(rules[index], source.Tags)) { rules[index] = rule; replaced++; written.Add(key); }
                else kept++;
            }
            return u with
            {
                Rules = rules,
                ConditionalFeedbackRules = u.ConditionalFeedbackRules.Where(f => !written.Contains(f.Rule.Key)).ToArray()
            };
        }).ToArray();
        var result = generated with
        {
            Units = units,
            RuleCount = units.Sum(u => u.Rules.Count),
            ConditionalFeedbackCount = units.Sum(u => u.ConditionalFeedbackRules.Count)
        };
        return (result, (added, replaced, kept, removed));
    }

    /// <summary>块在全项目恰好被调用一次、且那次调用的背景实例是全局 DB（根段不是调用方块的 Static/Temp/形参，非空）时返回该实例名；
    /// 否则 null。只看调用语句本身，不看块名。</summary>
    internal static string? UniqueGlobalInstance(string block, IEnumerable<(string CallerBlock, IrCall Call)> calls,
        Func<string, IReadOnlySet<string>> callerLocalsAndFormals)
    {
        var hits = calls.Where(c => C.Equals(c.Call.Callee, block)).Take(2).ToArray();
        if (hits.Length != 1 || hits[0].Call.InstanceScope == "LocalVariable") return null;   // 导出标明多重背景
        var instance = hits[0].Call.Instance?.Trim();
        if (string.IsNullOrEmpty(instance)) return null;
        return callerLocalsAndFormals(hits[0].CallerBlock).Contains(Root(instance)) ? null : instance;
    }

    static int Rank(PipelineRule rule, IReadOnlyList<PipelineTag> tags) =>
        Original353ActivationPolicy.Classify(rule, tags).Status switch
        {
            "Active" or "ModeCheck" => 3,
            "RequiresRuntimeGuard" => 2,
            _ => 1
        };

    static string Root(string name)
    {
        var cut = name.IndexOfAny(['.', '[']);
        return cut < 0 ? name : name[..cut];
    }

    /// <summary>块类型是 FC（PipelineUnit.BlockKind，取自导出 XML 的 SW.Blocks.FC 或 ai_plc_block_new.blocktype；忽略大小写）。</summary>
    static bool IsFcBlock(PipelineUnit unit) => string.Equals(unit.BlockKind?.Trim(), "FC", StringComparison.OrdinalIgnoreCase);

    /// <summary>文字形式的程序段：纯文本（STL/SCL）、顺控动作、未识别的顺控元素。</summary>
    static bool IsText(IrStatement s) =>
        s.Kind is IrStatementKinds.Source or IrStatementKinds.Action or IrStatementKinds.Unrecognized && !string.IsNullOrEmpty(s.Text);

    const string IdentBefore = "(?<![A-Za-z0-9_])", IdentAfter = "(?![A-Za-z0-9_])";

    static Regex NameMention(string name) =>
        new(IdentBefore + Regex.Escape(name) + IdentAfter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Q 区物理范围（位序号，含两端）。</summary>
    readonly record struct QRange(int Start, int End)
    {
        public bool Overlaps(QRange o) => Start <= o.End && o.Start <= End;
        public int FirstByte => Start / 8;
        public int LastByte => End / 8;
    }

    const RegexOptions Ri = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>输出区绝对地址：Q（英文）或 A（德文），位 / 字节 / 字 / 双字。</summary>
    static readonly Regex QAddress = new(@"^%?[QA](?:X?\s*(\d+)\.([0-7])|([BWD])\s*(\d+))$", Ri);

    /// <summary>位切片：「名.%X3」「名.%B1」「名.%W0」（% 可省）。</summary>
    static readonly Regex Slice = new(@"^(.+)\.%?([XBWD])(\d+)$", Ri);

    /// <summary>IR 目标里的输出区寄存器间接寻址（SymbolReader 写作 QB[AR1,P#0.0]）。</summary>
    static readonly Regex IndirectTarget = new(@"^%?[QA][BWD]?\s*\[[^\]]*P#", Ri);

    /// <summary>非源码文字段（顺控动作、未识别元素）里的输出区间接寻址：任何提及都算。紧跟在 . " # ' 之后的是
    /// 成员访问（"PLC=>Cmd".A[1]、#X.Q[2]），不是绝对输出区。</summary>
    static readonly Regex IndirectText = new(@"(?<![A-Za-z0-9_.""#'])[QA][BWD]?\s*\[", Ri);

    /// <summary>源码（STL/SCL）里的写法：STL 写指令 T / = / S / R / FP / FN（同一行内）后跟操作数，
    /// 或 SCL/调用参数的 := / =>。L、A/U、O 等读指令不算写。</summary>
    const string WritePrefix = @"(?:(?<![A-Za-z0-9_])(?:[TSR]|F[PN])[ \t]+|=>?[ \t]*)";

    /// <summary>源码里的输出区间接寻址写入：T QB[AR1,P#0.0] / = Q [AR1,..]。L QB[..] 是读，不算。</summary>
    static readonly Regex IndirectWriteText = new(WritePrefix + @"%?[QA][BWD]?\s*\[", Ri | RegexOptions.Multiline);

    /// <summary>区未知的寄存器间接（B[AR1,P#0.0]、D[AR2,…]、[AR1,…]：方括号前只有宽度字母或什么都没有，括号里有作为独立记号的
    /// P#/AR1/AR2）——跨区，可能写到 Q 区。目标、源码写指令之后、其它文字段里任何提及三种口径。</summary>
    const string AreaUnknownIndirect = @"(?:[XBWD]|\?\w*)?[ \t]*\[[^\]\r\n]*" + ProjectWrites.Register;
    static readonly Regex AreaUnknownIndirectTarget = new("^" + AreaUnknownIndirect, Ri);
    static readonly Regex AreaUnknownIndirectWriteText = new(WritePrefix + AreaUnknownIndirect, Ri | RegexOptions.Multiline);
    static readonly Regex AreaUnknownIndirectText = new(@"(?<![A-Za-z0-9_.""#'])" + AreaUnknownIndirect, Ri);

    /// <summary>文字里的输出区指针常数（P#Q0.0 BYTE 10、德文 P#A…）：不分读写，按范围算一处写入；换算不出范围记为 HasUnresolved。</summary>
    static readonly Regex QPointerText = new(@"(?<![A-Za-z0-9_#])P#[ \t]*[QA][ \t]*\d+\.[0-7](?:[ \t]+[A-Za-z_]+[ \t]+\d+)?", Ri);

    /// <summary>%Q12.3 / %QX12.3 / %QB12 / %QW12 / %QD12（德文 %A…/AB/AW/AD 同），可不带 %。其它返回 null。</summary>
    static QRange? ParseQ(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = QAddress.Match(text.Trim());
        if (!m.Success) return null;
        if (m.Groups[1].Success)
        {
            if (!int.TryParse(m.Groups[1].Value, out var b) || b > 1_000_000) return null;
            var bit = b * 8 + (m.Groups[2].Value[0] - '0');
            return new QRange(bit, bit);
        }
        if (!int.TryParse(m.Groups[4].Value, out var start) || start > 1_000_000) return null;
        var width = char.ToUpperInvariant(m.Groups[3].Value[0]) switch { 'B' => 1, 'W' => 2, _ => 4 };
        return new QRange(start * 8, (start + width) * 8 - 1);
    }

    static bool LooksLikeOutputArea(string target) =>
        target.StartsWith("%Q", StringComparison.OrdinalIgnoreCase) || target.StartsWith("%A", StringComparison.OrdinalIgnoreCase);

    /// <summary>全项目内 %Q 位的写入处数：赋值/置位/复位目标、调用输出引脚目标（按点名或绝对地址匹配，
    /// 覆盖该位的字节/字/双字标签、同地址别名（含德文 %A 地址）与位切片一并计算）；文字形式的程序段只要提到
    /// 覆盖该位的任一标签名，或以 STL 绝对地址（Q/A 位、QB/QW/QD 等）提到它，就保守算一处写。
    /// 非 %Q 点不受此检查约束。算不出范围的输出区写入（间接寻址、%Q:类型:偏移 等）记为 HasUnresolved。
    /// 指针常数 P#Q…（目标或文字里）按范围计一处写入，范围算不出时 HasUnresolved；区未知的间接写入（B[AR1,…]、P#0.0 …）
    /// 同样 HasUnresolved。调用方不把 FC 里的写入放进来（用户：FC 完全不管）。</summary>
    internal sealed class QWriteIndex
    {
        sealed record TargetWrite(string? Name, IReadOnlyList<QRange> Ranges);

        readonly (string Name, QRange? Range)[] qTags;
        readonly TargetWrite[] writes;
        readonly string[] sourceTexts, otherTexts;
        readonly Dictionary<string, int> cache = new(C);

        public bool HasUnresolved { get; }

        public QWriteIndex(IReadOnlyList<PipelineTag> tags, IEnumerable<IrUnit> units)
        {
            qTags = tags.Where(t => LooksLikeOutputArea(t.Address) || ParseQ(t.Address) is not null)
                .Select(t => (t.Name, ParseQ(t.Address))).ToArray();
            var ws = new List<TargetWrite>(); var src = new List<string>(); var other = new List<string>();
            var unresolved = false;
            // 指针常数（计划 2 遗留，Task 3 裁定）：Q 区按字节/位范围计为一处写入；Q 区范围算不出、区未知 → 不确定；其它区不涉及 Q
            void Pointer(string t)
            {
                if (PlcAddress.ParsePointerConstant(t) is not { } area) { unresolved = true; return; }
                if (area.Name != "Q") return;
                if (area.Start is { } start && area.End is { } end) ws.Add(new TargetWrite(null, [new QRange(start, end)]));
                else unresolved = true;
            }
            void Target(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                var t = raw.Trim();
                if (PlcAddress.IsPointer(t)) { Pointer(t); return; }
                if (IndirectTarget.IsMatch(t) || AreaUnknownIndirectTarget.IsMatch(t)) { unresolved = true; return; }
                var w = Resolve(t);
                if (w.Ranges.Count == 0 && LooksLikeOutputArea(t)) { unresolved = true; return; }
                ws.Add(w);
            }
            foreach (var unit in units)
                foreach (var s in unit.Statements)
                {
                    if (s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset) Target(s.Target);
                    if (s.Call is not null)
                        foreach (var target in s.Call.Pins.SelectMany(p => p.Targets)) Target(target);
                    foreach (var target in s.SideWrites ?? []) Target(target);
                    if (IsText(s))
                    {
                        // 源码只认写指令；顺控动作的操作数本身就是被写的对象，其它文字段保守地照旧
                        var isSource = s.Kind == IrStatementKinds.Source;
                        (isSource ? src : other).Add(s.Text!);
                        if ((isSource ? IndirectWriteText : IndirectText).IsMatch(s.Text!)
                            || (isSource ? AreaUnknownIndirectWriteText : AreaUnknownIndirectText).IsMatch(s.Text!)) unresolved = true;
                        foreach (Match m in QPointerText.Matches(s.Text!)) Pointer(m.Value);
                    }
                }
            writes = ws.ToArray(); sourceTexts = src.ToArray(); otherTexts = other.ToArray(); HasUnresolved = unresolved;
        }

        /// <summary>目标写到哪些 Q 位：绝对地址直接解析；位切片按根段（.% 之前）的标签或地址换算，
        /// 字节标签的 %Xn / %B0 精确到位，其余（字/双字内的切片等）按整个标签范围计；其它按名字匹配。</summary>
        TargetWrite Resolve(string t)
        {
            if (ParseQ(t) is { } direct) return new(t, [direct]);
            var m = Slice.Match(t);
            if (m.Success)
            {
                var root = m.Groups[1].Value;
                var kind = char.ToUpperInvariant(m.Groups[2].Value[0]);
                var n = int.TryParse(m.Groups[3].Value, out var v) ? v : -1;
                var rootTags = qTags.Where(x => C.Equals(x.Name, root)).ToArray();
                var rootRanges = (ParseQ(root) is { } a ? new[] { a } : rootTags.Where(x => x.Range is not null).Select(x => x.Range!.Value))
                    .ToArray();
                if (rootRanges.Length > 0)
                    return new(null, rootRanges.Select(r => Narrow(r, kind, n)).ToArray());
                if (rootTags.Length > 0) return new(root, []);
            }
            return new(t, []);
        }

        static QRange Narrow(QRange tag, char kind, int n)
        {
            var bytes = tag.LastByte - tag.FirstByte + 1;
            if (bytes == 1 && tag.End - tag.Start == 7)
            {
                if (kind == 'X' && n is >= 0 and < 8) return new QRange(tag.Start + n, tag.Start + n);
                if (kind == 'B' && n == 0) return tag;
            }
            return tag;
        }

        public bool IsQ(string signal) => qTags.Any(t => C.Equals(t.Name, signal));

        public int Count(string signal)
        {
            if (cache.TryGetValue(signal, out var cached)) return cached;
            var own = qTags.Where(t => C.Equals(t.Name, signal)).ToArray();
            if (own.Length == 0) return cache[signal] = 0;
            var ranges = own.Where(t => t.Range is not null).Select(t => t.Range!.Value).ToArray();
            var names = qTags.Where(t => t.Range is { } r && ranges.Any(r.Overlaps)).Select(t => t.Name)
                .Append(signal).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(C);
            var count = writes.Count(w => (w.Name is not null && names.Contains(w.Name)) || w.Ranges.Any(r => ranges.Any(r.Overlaps)));
            // 标签名在文字里出现即保守算一处写；绝对地址在源码里只有跟在写指令后（或作 SCL 赋值左边）才算
            var namePatterns = names.Select(n => IdentBefore + Regex.Escape(n) + IdentAfter).ToArray();
            var addresses = ranges.SelectMany(StlPatterns).ToArray();
            var sourcePatterns = namePatterns
                .Concat(addresses.Select(a => WritePrefix + "%?" + a))
                .Concat(addresses.Select(a => IdentBefore + "%?" + a + @"\s*:=")).ToArray();
            var otherPatterns = namePatterns.Concat(addresses.Select(a => IdentBefore + a)).ToArray();
            if (sourcePatterns.Length > 0)
                count += sourceTexts.Count(new Regex(string.Join("|", sourcePatterns), Ri | RegexOptions.Multiline).IsMatch);
            if (otherPatterns.Length > 0)
                count += otherTexts.Count(new Regex(string.Join("|", otherPatterns), Ri).IsMatch);
            return cache[signal] = count;
        }

        /// <summary>STL 里覆盖该范围的绝对地址写法（不含前缀）：Q/A 位（可带 X）与覆盖它的 QB/QW/QD（AB/AW/AD）。</summary>
        static IEnumerable<string> StlPatterns(QRange range)
        {
            for (var b = range.FirstByte; b <= range.LastByte; b++)
            {
                var lo = Math.Max(range.Start, b * 8) - b * 8;
                var hi = Math.Min(range.End, b * 8 + 7) - b * 8;
                yield return $@"[QA]X?\s*{b}\.[{lo}-{hi}](?![0-9])";
            }
            foreach (var (letter, width) in new[] { ("B", 1), ("W", 2), ("D", 4) })
                for (var n = Math.Max(0, range.FirstByte - width + 1); n <= range.LastByte; n++)
                    yield return $@"[QA]{letter}\s*{n}(?![0-9])";
        }
    }
}
