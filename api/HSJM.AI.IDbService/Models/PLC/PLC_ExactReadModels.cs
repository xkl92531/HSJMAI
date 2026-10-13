#nullable enable

namespace HSJM.AI.IDbService;

/// <summary>
/// 精确 PLC 路径读取请求。BlockName 与 VariablePath 共同组成唯一传输路径。
/// </summary>
public sealed record PLC_ExactReadRequest(
    string RequestKey,
    string BlockName,
    string VariablePath);

public enum PLC_ExactReadQuality
{
    Good,
    Bad,
    Unknown
}

/// <summary>
/// 与 RequestKey 一一对应的安全读取结果。
/// </summary>
public sealed record PLC_ExactReadResult(
    string RequestKey,
    string BlockName,
    string VariablePath,
    string CanonicalPath,
    object? Value,
    PLC_ExactReadQuality Quality,
    DateTimeOffset? SourceTimestamp,
    string? ErrorCode);
