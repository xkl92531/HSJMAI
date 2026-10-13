using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed class LineUpRuntimeValueReader : ILineUpRuntimeValueReader, IScopedAutoInject
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private readonly Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, CancellationToken,
        Task<IReadOnlyList<PLC_ExactReadResult>>> read;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan timeout;

    public LineUpRuntimeValueReader(IPLC_VariableAccessService variableAccessService)
        : this(
            (projectId, requests, cancellationToken) =>
                // keepPolling: true —— 设备监控 100ms 一轮持续盯同一批点，要让它们留在 IGW 的活动窗口里走缓存。
                // 2026-09-22 起 IGW 的 /Tag 读接口默认「只取值、不进轮询」（见 IgwReadPath）：不传 true 的话
                // 每轮都会现场补读 PLC——值是对的但慢，现场十有八九会当成 PLC/网络问题去查。
                // 注意 IGW 是「先判 idle 再 Touch」，所以第一轮仍会现场补读，第二轮起才命中缓存，别当 bug。
                variableAccessService.ReadExactDataAsync(projectId, requests, cancellationToken, keepPolling: true),
            TimeProvider.System,
            DefaultTimeout)
    {
    }

    internal LineUpRuntimeValueReader(
        Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, CancellationToken,
            Task<IReadOnlyList<PLC_ExactReadResult>>> read,
        TimeProvider timeProvider,
        TimeSpan timeout)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
    }

    public async Task<LineUpRuntimeReadResult> ReadAsync(
        LineUpRuntimeReadPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var receivedAt = timeProvider.GetUtcNow();
        if (plan.Items.IsDefaultOrEmpty)
        {
            return Result(plan, LineUpRuntimeReadStatus.Empty, [], receivedAt);
        }

        IReadOnlyList<PLC_ExactReadResult> samples;
        try
        {
            var readTask = read(plan.PlcProjectId, plan.ExactReadRequests, cancellationToken);
            samples = await readTask.WaitAsync(timeout, cancellationToken) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return Failure(plan, "PlcReadTimeout", LineUpRuntimeQuality.Bad, receivedAt);
        }
        catch
        {
            return Failure(plan, "PlcReadFailed", LineUpRuntimeQuality.Bad, receivedAt);
        }

        receivedAt = timeProvider.GetUtcNow();
        if (samples.Count == 0)
        {
            return Failure(plan, "PlcReadEmpty", LineUpRuntimeQuality.Unknown, receivedAt);
        }

        var byRequestKey = samples
            .Where(x => !string.IsNullOrWhiteSpace(x.RequestKey))
            .GroupBy(x => x.RequestKey.Trim(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var values = plan.Items
            .Select(item => Map(item, byRequestKey, plan.MaxSampleAge, receivedAt))
            .ToImmutableArray();
        var goodCount = values.Count(x => x.Quality == LineUpRuntimeQuality.Good);
        var status = goodCount == values.Length
            ? LineUpRuntimeReadStatus.Completed
            : goodCount > 0
                ? LineUpRuntimeReadStatus.Partial
                : LineUpRuntimeReadStatus.Failed;
        return Result(plan, status, values, receivedAt);
    }

    private static LineUpRuntimeValue Map(
        LineUpRuntimeReadItem item,
        IReadOnlyDictionary<string, PLC_ExactReadResult[]> byRequestKey,
        TimeSpan maxSampleAge,
        DateTimeOffset receivedAt)
    {
        if (!byRequestKey.TryGetValue(item.ExactReadRequest.RequestKey, out var matches))
        {
            return Value(item, null, LineUpRuntimeValueKind.Null, LineUpRuntimeQuality.Unknown,
                null, receivedAt, "PlcExactResultMissing");
        }

        if (matches.Length != 1)
        {
            return Value(item, null, LineUpRuntimeValueKind.Null, LineUpRuntimeQuality.Bad,
                null, receivedAt, "PlcExactResultDuplicate");
        }

        var sample = matches[0];
        if (!string.Equals(sample.BlockName?.Trim(), item.ResolvedBlockName, StringComparison.Ordinal) ||
            !string.Equals(sample.VariablePath?.Trim(), item.VariablePath, StringComparison.Ordinal) ||
            !string.Equals(sample.CanonicalPath?.Trim(), item.CanonicalPlcPath, StringComparison.Ordinal))
        {
            return Value(item, null, LineUpRuntimeValueKind.Null, LineUpRuntimeQuality.Bad,
                sample.SourceTimestamp, receivedAt, "PlcExactIdentityMismatch");
        }

        if (sample.Quality != PLC_ExactReadQuality.Good)
        {
            // 2026-10-08 用户拍板：IGW 返回「未知」质量（非 Good 非 Bad）不按数据质量异常处理——
            // Bool 项一律按 false（正常语义）放行，不再触发 SignalBad/步 Unknown/树上数据质量告警；
            // 真正的 Bad 质量仍走异常路径如实上报。非 Bool 项无法安全给默认值，维持原 Unknown 语义。
            if (sample.Quality == PLC_ExactReadQuality.Unknown)
            {
                var normalizedType = item.DataType?.Trim().ToUpperInvariant() ?? string.Empty;
                if (normalizedType is "BOOL" or "BOOLEAN")
                {
                    return Value(item, false, LineUpRuntimeValueKind.Boolean, LineUpRuntimeQuality.Good,
                        sample.SourceTimestamp, receivedAt, null);
                }
            }
            var quality = sample.Quality == PLC_ExactReadQuality.Bad
                ? LineUpRuntimeQuality.Bad
                : LineUpRuntimeQuality.Unknown;
            var errorCode = string.IsNullOrWhiteSpace(sample.ErrorCode)
                ? quality == LineUpRuntimeQuality.Bad ? "PlcExactReadBad" : "PlcExactReadUnknown"
                : sample.ErrorCode;
            return Value(item, null, LineUpRuntimeValueKind.Null, quality,
                sample.SourceTimestamp, receivedAt, errorCode);
        }

        if (!TryConvertValue(sample.Value, item.DataType, out var typedValue, out var valueKind))
        {
            return Value(item, null, LineUpRuntimeValueKind.Null, LineUpRuntimeQuality.Bad,
                sample.SourceTimestamp, receivedAt, "PlcValueTypeMismatch");
        }

        if (valueKind == LineUpRuntimeValueKind.FloatingPoint &&
            typedValue is double floatingPoint && !double.IsFinite(floatingPoint))
        {
            return Value(item, null, LineUpRuntimeValueKind.Null, LineUpRuntimeQuality.Bad,
                sample.SourceTimestamp, receivedAt, "PlcValueNonFinite");
        }

        if (sample.SourceTimestamp is { } sourceTimestamp &&
            receivedAt - sourceTimestamp > maxSampleAge)
        {
            return Value(item, typedValue, valueKind, LineUpRuntimeQuality.Stale,
                sourceTimestamp, receivedAt, "PlcTagStale");
        }

        return Value(item, typedValue, valueKind, LineUpRuntimeQuality.Good,
            sample.SourceTimestamp, receivedAt, null);
    }

    private static LineUpRuntimeReadResult Failure(
        LineUpRuntimeReadPlan plan,
        string errorCode,
        LineUpRuntimeQuality quality,
        DateTimeOffset receivedAt)
    {
        var values = plan.Items.Select(item => Value(
            item,
            null,
            LineUpRuntimeValueKind.Null,
            quality,
            null,
            receivedAt,
            errorCode)).ToImmutableArray();
        return Result(plan, LineUpRuntimeReadStatus.Failed, values, receivedAt);
    }

    private static LineUpRuntimeValue Value(
        LineUpRuntimeReadItem item,
        object value,
        LineUpRuntimeValueKind valueKind,
        LineUpRuntimeQuality quality,
        DateTimeOffset? sourceTimestamp,
        DateTimeOffset receivedAt,
        string errorCode) =>
        new(
            item.CanonicalKey,
            item.OwnerSourceKey,
            item.NetworkSourceKey,
            item.PartSourceKey,
            item.Role,
            value,
            valueKind,
            quality,
            sourceTimestamp,
            receivedAt,
            errorCode);

    private static LineUpRuntimeReadResult Result(
        LineUpRuntimeReadPlan plan,
        LineUpRuntimeReadStatus status,
        ImmutableArray<LineUpRuntimeValue> values,
        DateTimeOffset receivedAt) =>
        new(plan.BindingId, plan.BindingRevision, plan.PlcProjectId, status, values, receivedAt);

    private static bool TryConvertValue(
        object raw,
        string dataType,
        out object value,
        out LineUpRuntimeValueKind kind)
    {
        value = null;
        kind = LineUpRuntimeValueKind.Null;
        if (raw is null)
        {
            return false;
        }

        var normalizedType = dataType?.Trim().ToUpperInvariant() ?? string.Empty;
        try
        {
            switch (normalizedType)
            {
                case "BOOL":
                case "BOOLEAN":
                    if (raw is bool boolean)
                    {
                        value = boolean;
                    }
                    else if (raw is string text && bool.TryParse(text, out var parsedBoolean))
                    {
                        value = parsedBoolean;
                    }
                    else if (TryBinaryBoolean(raw, out var binaryBoolean))
                    {
                        value = binaryBoolean;
                    }
                    else
                    {
                        return false;
                    }
                    kind = LineUpRuntimeValueKind.Boolean;
                    return true;

                case "SINT":
                case "USINT":
                case "INT":
                case "UINT":
                case "DINT":
                case "UDINT":
                case "LINT":
                case "ULINT":
                case "BYTE":
                case "WORD":
                case "DWORD":
                case "LWORD":
                    value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                    kind = LineUpRuntimeValueKind.Integer;
                    return true;

                case "REAL":
                case "LREAL":
                case "DECIMAL":
                    value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    kind = LineUpRuntimeValueKind.FloatingPoint;
                    return true;

                case "TIME":
                case "LTIME":
                    if (raw is TimeSpan duration)
                    {
                        value = duration;
                    }
                    else if (TimeSpan.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                                 CultureInfo.InvariantCulture, out var parsedDuration))
                    {
                        value = parsedDuration;
                    }
                    else
                    {
                        return false;
                    }
                    kind = LineUpRuntimeValueKind.Duration;
                    return true;

                case "DATE":
                case "DATE_AND_TIME":
                case "DT":
                case "LDT":
                    if (!DateTimeOffset.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
                    {
                        return false;
                    }
                    value = dateTime;
                    kind = LineUpRuntimeValueKind.DateTime;
                    return true;

                case "CHAR":
                case "WCHAR":
                case "STRING":
                case "WSTRING":
                    value = Convert.ToString(raw, CultureInfo.InvariantCulture);
                    kind = LineUpRuntimeValueKind.Text;
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        {
            value = null;
            kind = LineUpRuntimeValueKind.Null;
            return false;
        }
    }

    private static bool TryBinaryBoolean(object raw, out bool value)
    {
        var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
        if (string.Equals(text, "1", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "0", StringComparison.Ordinal))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }
}
