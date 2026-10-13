#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

public sealed record PipelineUnit(long BlockId, string BlockName, long Id, string Language,
    string RawContent, string Bindings, IReadOnlyList<string> Callees, int? NetworkOrder = null,
    string InterfaceXml = "", string BlockKind = "", string SourceFile = "", string UnitKind = "Network")
{
    /// <summary>与库 ai_plc_logicunit.networkid 同口径的程序段编号：块内第几个 SW.Blocks.CompileUnit（从 1 起，
    /// 空网络 / 没有 NetworkSource 的 CompileUnit 也计数，与上传解析器一致）。归档读取时填写；
    /// 库源（NetworkOrder 本身就是 networkid）为 null。不进 Id 与 Fingerprint。</summary>
    public int? CompileUnitOrder { get; init; }

    /// <summary>组织块的事件类型（导出 AttributeList/SecondaryType，如 ProgramCycle、Startup、HardwareInterrupt）；非组织块或库源为 null。
    /// 不进 Id 与 Fingerprint。</summary>
    public string? OrganizationType { get; init; }

    /// <summary>库口径的程序段编号：有 CompileUnitOrder 用它，否则用 NetworkOrder（库源时即 networkid）。</summary>
    public int? DbNetworkId => CompileUnitOrder ?? NetworkOrder;
}
public sealed record PipelineTag(string Name, string Address, string DataType)
{
    /// <summary>点表注释。只用于认系统存储位（ConstantBits 特例）；不参与序列化（不进 Fingerprint 与各缓存键）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Comment { get; init; }
}
public sealed record PipelineSnapshot(long ProjectId, IReadOnlyList<PipelineUnit> Units, IReadOnlyList<PipelineTag> Tags)
{
    /// <summary>全局 DB 名 → 导出里的接口 XML（含起始值）。只有上传归档源填写（库源为空）；只用于期望状态报警配置输入的
    /// 起始值解析（ConfigConstants），不进 Fingerprint。</summary>
    public IReadOnlyDictionary<string, string> DbInterfaces { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Conservative whole-project invalidation, including dependencies and bindings; never reuse stale callees.
    public string Fingerprint() => PipelineHash.Of(JsonSerializer.Serialize(new {
        ProjectId, Units = Units.OrderBy(u => u.Id).Select(u => new {
            u.BlockId, u.BlockName, u.Id, u.Language, u.RawContent, u.Bindings, u.NetworkOrder,u.InterfaceXml,u.BlockKind,u.UnitKind,
            Callees = u.Callees.OrderBy(c => c, StringComparer.Ordinal) }),
        Tags = Tags.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Address, StringComparer.Ordinal)
    }));
}
public static class PipelineHash
{
    public static string Of(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
public sealed record LogicTerm(string Kind, string? Name = null, IReadOnlyList<LogicTerm>? Children = null,string? DataType = null)
{
    public static LogicTerm Signal(string name) => new("Signal", name);
}
public sealed record PipelineAssignment(string Target, LogicTerm Expression);
public sealed record PipelineTimer(string Key, LogicTerm Input, long DelayMs);
public sealed record PipelineCallEvidence(string Callee, string CallUid, IReadOnlyList<long> BodyUnits,
    IReadOnlyDictionary<string,LogicTerm> Outputs, IReadOnlyList<string>? InstancePaths = null,
    IReadOnlyList<PipelineAssignment>? StateUpdates = null);
public sealed record PipelineDescription(long LogicUnitId, string Fingerprint, string Markdown,
    IReadOnlyList<string> Signals, IReadOnlyList<PipelineAssignment> Assignments,
    IReadOnlyList<string> Limitations, string EvidenceJson,
    IReadOnlyList<PipelineTimer>? Timers = null, bool IsTemplate = false,
    IReadOnlyList<PipelineCallEvidence>? Calls = null,
    IReadOnlyList<PipelineAssignment>? StateUpdates = null);
public sealed record PipelineTraceFrame(long TimestampMs, IReadOnlyDictionary<string,bool?> Inputs,
    IReadOnlyDictionary<string,double?>? Scalars = null);
public sealed record PipelineTimerState(long? StartedAtMs);
public sealed record PipelineTraceResult(long TimestampMs, IReadOnlyDictionary<string,bool> Values,
    IReadOnlyDictionary<string,PipelineTimerState> Timers, IReadOnlyDictionary<string,bool>? State = null);
public sealed record PipelineTraceRequest(long LogicUnitId, IReadOnlyList<PipelineTraceFrame> Frames,
    IReadOnlyDictionary<string,bool>? InitialState = null,
    IReadOnlyDictionary<string,PipelineTimerState>? InitialTimers = null);
public sealed record PipelineSignal(string Name, string Role);
public sealed record PipelineRule(string Operator, IReadOnlyList<PipelineSignal> Signals, string Description)
{
    // Optional runtime conditions for a diagnostic expectation. Legacy rules retain their
    // existing behavior; guarded rules must never be evaluated as plain instant IMPLIES.
    public string? Applicability {get;init;}
    public IReadOnlyList<string> BypassSignals {get;init;}=[];
    public bool RequiresSettlingEvidence {get;init;}
    public IReadOnlyList<string> MaskedFeedbackInputs {get;init;}=[];
    // Presentation only; never used by the rule key or Boolean evaluator.
    public string? DisplayLabel {get;init;}
    // Set by FB body contract inference (Original353FbContractRules); never part of the rule key.
    public string? Proof {get;init;}
    // Presentation only: physical inputs traced upstream of a DB gate source.
    public IReadOnlyList<string> UpstreamSignals {get;init;}=[];
    // IMPLIES_ANY 的到位分组（组内「与」、组间「或」）；不进 Key。
    public IReadOnlyList<IReadOnlyList<string>>? TargetGroups {get;init;}
    // 来源追溯（AI 模型、引用语句编号、契约缓存键）；不进 Key。
    public PipelineRuleTrace? Trace {get;init;}
    public string Key => PipelineHash.Of(JsonSerializer.Serialize(new {
        Operator, Signals = Signals.OrderBy(s => s.Role, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
    }));
}
public sealed record PipelineRuleTrace(string Origin, string? Model, IReadOnlyList<string> Evidence, string? CacheKey, string? Note = null);
public sealed record PipelineValidation(bool Success, string? ErrorCode = null);
public sealed record PipelineUnitResult(long LogicUnitId, string Status, string EvaluationPoint,
    string? Description, IReadOnlyList<PipelineRule> Rules);
public sealed record PipelineItem(long LogicUnitId, string Status, int Rules = 0, string? ErrorCode = null);
public sealed record PipelineRun(Guid RunId, long ProjectId, string Status, string SourceFingerprint,
    IReadOnlyList<PipelineItem> Items, IReadOnlyDictionary<string, long> TimingMs,
    string? ErrorCode = null);
public sealed class PipelineException(string code) : Exception(code) { public string Code { get; } = code; }

public interface IPipelineStore
{
    Task<PipelineSnapshot> LoadSourceAsync(long project, CancellationToken token);
    Task<IReadOnlyDictionary<string, PipelineDescription>> LoadDescriptionsAsync(long project, CancellationToken token);
    Task SaveDescriptionsAsync(long project, IReadOnlyList<PipelineDescription> descriptions, CancellationToken token);
    Task<bool> IsCompleteAsync(long project, long lu, string generationKey, CancellationToken token);
    Task CommitAsync(long project, Guid run, PipelineDescription description, string generationKey,
        IReadOnlyList<PipelineRule> rules, CancellationToken token);
    Task SaveRunAsync(PipelineRun run, CancellationToken token);
}
public interface IPipelineInference
{
    string Version { get; }
    Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    Task<IReadOnlyList<PipelineRule>> InferAsync(PipelineDescription description, CancellationToken token);
}
public sealed record PipelineCommit(PipelineDescription Description,string GenerationKey,IReadOnlyList<PipelineRule> Rules);
public interface IPipelineBatchStore
{
    Task<IReadOnlyDictionary<long,string>> LoadCompletionsAsync(long project,CancellationToken token);
    Task CommitBatchAsync(long project,Guid run,IReadOnlyList<PipelineCommit> batch,CancellationToken token);
}
public static class PipelineSettings
{
    public static void Validate(bool enabled, string connection, string expectedDatabase)
    {
        if (!enabled) throw new PipelineException("IndependentPipelineDisabled");
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(expectedDatabase))
            throw new PipelineException("ExplicitTestConnectionRequired");
        var configured = new NpgsqlConnectionStringBuilder(connection);
        if (!expectedDatabase.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(configured.Database, expectedDatabase, StringComparison.Ordinal))
            throw new PipelineException("TestDatabaseMismatch");
    }
}
