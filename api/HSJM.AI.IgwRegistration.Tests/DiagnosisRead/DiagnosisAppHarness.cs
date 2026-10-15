using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.IDbService;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>用 DispatchProxy 伪造数据服务构造 DiagnosticStrategyFactoryApp（不连库）。GetListAsync 忽略表达式直接返回预置列表。</summary>
internal sealed class DiagnosisAppHarness
{
    public readonly List<PLC_ProjectEntity> Projects = new();
    public readonly List<PLC_IOConstraintRuleEntity> Rules = new();
    public readonly List<PLC_IOConstraintSignalEntity> Signals = new();
    public readonly List<PLC_IOTagEntity> IoTags = new();
    public readonly List<PLC_BlockEntity> Blocks = new();
    public readonly List<PLC_BlockInterfaceEntity> BlockInterfaces = new();
    public readonly List<PLC_LogicUnitEntity> Units = new();
    public readonly List<PLC_VariableAccessEntity> ExtraAccesses = new();
    public readonly Dictionary<long, Func<Task<PLC_DiagnosticReadResult>>> Reads = new();

    /// <summary>精确读处理器（门控 DB 源补读 / 违反复核），按调用顺序依次取，不足时重复最后一个。</summary>
    public readonly List<Func<IReadOnlyCollection<PLC_ExactReadRequest>, IReadOnlyList<PLC_ExactReadResult>>> ExactReads = new();
    public int ExactReadCalls;

    /// <summary>非 null 时约束规则服务 GetListAsync 抛出该异常（模拟读库失败）</summary>
    public Exception RuleServiceFailure;

    public void AddPlc(long id, string name) =>
        Projects.Add(new PLC_ProjectEntity { Id = id, ProjectName = name, IsDeleted = false });

    public void AddIoTag(long projectId, string table, string tag, string address, string comment = null) =>
        IoTags.Add(new PLC_IOTagEntity
        {
            ProjectId = projectId, TableName = table, TagName = tag, Address = address, Comment = comment,
            DataType = "Bool", IsDeleted = false,
        });

    public PLC_IOConstraintRuleEntity AddRule(long projectId, string op, string name, params (string Signal, string Role)[] signals)
    {
        var rule = ConstraintTestData.Rule(op, name, projectId);
        Rules.Add(rule);
        Signals.AddRange(ConstraintTestData.Signals(rule, signals));
        return rule;
    }

    /// <summary>
    /// 预置某 PLC 的读取结果。与生产 ReadDiagnosticSignalsAsync 一致，每个信号都带裸点名 OldTagName：
    /// 未显式给出时取键 "表名.点名" 首个 '.' 之后的部分（最终评审 Minor 2 起，IO 表里已有的点名不再走后缀兜底，
    /// 缺 OldTagName 的夹具会失真）。
    /// </summary>
    public void SetRead(long projectId, params PLC_DiagnosticSignal[] signals)
    {
        foreach (var signal in signals)
        {
            var dot = signal.TagName?.IndexOf('.') ?? -1;
            if (signal.OldTagName == null && dot >= 0)
            {
                signal.OldTagName = signal.TagName.Substring(dot + 1);
            }
        }
        Reads[projectId] = () => Task.FromResult(new PLC_DiagnosticReadResult
        {
            ProjectId = projectId,
            ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
            Signals = signals,
        });
    }

    public void SetReadFailure(long projectId, Exception exception) =>
        Reads[projectId] = () => Task.FromException<PLC_DiagnosticReadResult>(exception);

    public static PLC_DiagnosticSignal Good(string key, object value, string oldTagName = null) =>
        new() { TagName = key, OldTagName = oldTagName, Status = PLC_SignalReadStatus.Good, Value = value };

    public static PLC_DiagnosticSignal Bad(string key, PLC_SignalReadStatus status, string detail = "test", string oldTagName = null) =>
        new() { TagName = key, OldTagName = oldTagName, Status = status, Detail = detail };

    public DiagnosticStrategyFactoryApp Build()
    {
        var projects = Fake.Create<IPLC_ProjectService>((m, a) =>
            m.Name == "GetListAsync" ? Task.FromResult(Projects) : throw new NotSupportedException("IPLC_ProjectService." + m.Name));
        var rules = Fake.Create<IPLC_IOConstraintRuleService>((m, a) =>
            m.Name == "GetListAsync"
                ? (RuleServiceFailure != null ? Task.FromException<List<PLC_IOConstraintRuleEntity>>(RuleServiceFailure) : Task.FromResult(Rules))
                : throw new NotSupportedException("IPLC_IOConstraintRuleService." + m.Name));
        var signals = Fake.Create<IPLC_IOConstraintSignalService>((m, a) =>
            m.Name == "GetListAsync" ? Task.FromResult(Signals) : throw new NotSupportedException("IPLC_IOConstraintSignalService." + m.Name));
        var tags = Fake.Create<IPLC_IOTagService>((m, a) =>
            m.Name == "GetListAsync" ? Task.FromResult(IoTags) : throw new NotSupportedException("IPLC_IOTagService." + m.Name));
        var variableAccess = Fake.Create<IPLC_VariableAccessService>((m, a) =>
        {
            if (m.Name == "GetListAsync")
            {
                // 每个约束信号都有 VariableAccess 记录 → 不被预过滤剔除
                return Task.FromResult(Signals.Select(s => s.SignalName).Distinct()
                    .Select(n => new PLC_VariableAccessEntity { VariableName = n, LogicUnitId = 1, IsDeleted = false })
                    .Concat(ExtraAccesses).ToList());
            }
            if (m.Name == nameof(IPLC_VariableAccessService.ReadDiagnosticSignalsAsync))
            {
                var projectId = (long)a[0];
                return Reads.TryGetValue(projectId, out var read)
                    ? read()
                    : Task.FromResult(new PLC_DiagnosticReadResult { ProjectId = projectId });
            }
            if (m.Name == nameof(IPLC_VariableAccessService.ReadExactDataAsync))
            {
                var requests = (IReadOnlyCollection<PLC_ExactReadRequest>)a[1];
                var handler = ExactReads.Count == 0 ? null : ExactReads[Math.Min(ExactReadCalls, ExactReads.Count - 1)];
                ExactReadCalls++;
                return Task.FromResult(handler?.Invoke(requests) ?? (IReadOnlyList<PLC_ExactReadResult>)Array.Empty<PLC_ExactReadResult>());
            }
            throw new NotSupportedException("IPLC_VariableAccessService." + m.Name);
        });
        var blocks = Fake.Create<IPLC_BlockService>((m, a) =>
            m.Name == "GetListAsync" ? Task.FromResult(Blocks) : throw new NotSupportedException(m.Name));
        var units = Fake.Create<IPLC_LogicUnitService>((m, a) =>
            m.Name == "GetListAsync" ? Task.FromResult(Units) : throw new NotSupportedException(m.Name));
        var interfaces = Fake.Create<IPLC_BlockInterfaceService>((m, a) =>
            m.Name == "GetListAsync" ? Task.FromResult(BlockInterfaces) : throw new NotSupportedException(m.Name));
        return new DiagnosticStrategyFactoryApp(null, null, units, blocks, variableAccess, rules, signals, tags, projects,
            null, null, null, null, null, blockInterfaceService: interfaces);
    }
}
