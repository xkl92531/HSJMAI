using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HSJM.AI.IDbService;
using Newtonsoft.Json;

namespace HSJM.AI.DbService
{
    /// <summary>单个 IGW 别名的诊断读取结果（D-1）。Value 仅 Status=Good 时有值。</summary>
    internal sealed record IgwAliasReading(object? Value, PLC_SignalReadStatus Status, string? Detail, DateTime? TimestampUtc)
    {
        public static IgwAliasReading Good(object? value, DateTime? timestampUtc) =>
            new(value, PLC_SignalReadStatus.Good, null, timestampUtc);

        /// <summary>
        /// D-1 修复回合 1：强制"Value 有值 ⇔ Status=Good"不变式。即使传入的 status 恰好是 Good
        /// （矛盾数据，典型见旧接口失败报文里解析出 "Good(0x00000000)" 这种自相矛盾的状态——报文本身
        /// 就是一次失败，却把状态解析成了 Good），这里也统一收敛成 DeviceError，绝不允许
        /// Value=null 却 Status=Good（下游一旦按 Status=Good 判定"读到了可信值"就会漏检）。
        /// </summary>
        public static IgwAliasReading Bad(PLC_SignalReadStatus status, string? detail) =>
            new(null, status == PLC_SignalReadStatus.Good ? PLC_SignalReadStatus.DeviceError : status, detail, null);
    }

    /// <summary>
    /// D-1 约束诊断的 IGW 读取器（spec §4.1）：别名按 1000 分批，每批优先 POST /Tag/ReadTagsWithQuality（逐点质量）；
    /// 旧 IGW 返回 404/405 → 该 host 5 分钟内改走 ReadTagsAsList，按报文定位坏点剔除重试、定位不了对半拆分，
    /// 每批 HTTP 请求 ≤ <see cref="MaxRequestsPerBatch"/>；503（热重载）等 1 s 重试 1 次；
    /// 网络异常/超时只影响本批。除调用方取消外不抛异常。一个坏点不会让同批其它点失去值。
    /// </summary>
    internal sealed class IgwDiagnosticReader
    {
        /// <summary>
        /// 旧接口（无 ReadTagsWithQuality）单批（≤1000 别名）定位坏点的最大 HTTP 请求数（含 503 重试）。
        /// 权衡：旧接口一次失败报文只报"一个"坏点，定位式重试+对半拆分在坏符号很多时会退化成接近
        /// 逐点请求；批内坏符号超过 63 个时，本批剩余未定位点直接标 DeviceError("定位坏点次数超限")，
        /// 不再继续逐点定位下去，用"部分点位漏诊"换"诊断响应时间有界"。这是老 IGW 场景下已知的精度/
        /// 时延权衡——新 IGW（1.6.78+ 的 POST /Tag/ReadTagsWithQuality）逐点质量一次请求整批返回，
        /// 不需要定位式重试，天然不受此上限影响。
        /// </summary>
        internal const int MaxRequestsPerBatch = 64;
        internal static readonly TimeSpan QualityUnsupportedTtl = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

        /// <summary>规范化 baseUrl → "新接口不支持"有效期（静态跨实例共享）</summary>
        private static readonly ConcurrentDictionary<string, DateTime> QualityUnsupportedUntilUtc =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly IIotService _iot;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _requestTimeout;
        private readonly bool _useQualityEndpoint;

        /// <param name="delay">503 重试前的等待（测试注入）；默认 Task.Delay</param>
        /// <param name="requestTimeout">单次 HTTP 调用超时；默认 15 s</param>
        /// <param name="useQualityEndpoint">false = 直接走旧接口（端到端验证回退路径用）</param>
        public IgwDiagnosticReader(IIotService iot, Func<TimeSpan, CancellationToken, Task>? delay = null,
            TimeSpan? requestTimeout = null, bool useQualityEndpoint = true)
        {
            _iot = iot ?? throw new ArgumentNullException(nameof(iot));
            _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
            _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
            _useQualityEndpoint = useQualityEndpoint;
        }

        /// <summary>读取别名；结果键 = alias.Trim()（Ordinal），每个非空别名都有一行。</summary>
        public async Task<IReadOnlyDictionary<string, IgwAliasReading>> ReadAsync(
            string host,
            IReadOnlyList<string> aliases,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, IgwAliasReading>(StringComparer.Ordinal);
            var distinct = (aliases ?? Array.Empty<string>())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (distinct.Count == 0)
            {
                return result;
            }

            var baseUrl = DcsIgwConsts.NormalizeGatewayBaseUrl(host) ?? string.Empty;
            for (var offset = 0; offset < distinct.Count; offset += DcsIgwConsts.ReadBatchLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = distinct.GetRange(offset, Math.Min(DcsIgwConsts.ReadBatchLimit, distinct.Count - offset));
                if (_useQualityEndpoint && !IsQualityKnownUnsupported(baseUrl)
                    && await TryReadWithQualityAsync(baseUrl, batch, result, cancellationToken))
                {
                    continue;
                }
                await ReadWithLegacyListAsync(baseUrl, batch, result, cancellationToken);
            }
            return result;
        }

        private static bool IsQualityKnownUnsupported(string baseUrl)
        {
            if (!QualityUnsupportedUntilUtc.TryGetValue(baseUrl, out var until))
            {
                return false;
            }
            if (until > DateTime.UtcNow)
            {
                return true;
            }
            QualityUnsupportedUntilUtc.TryRemove(new KeyValuePair<string, DateTime>(baseUrl, until));
            return false;
        }

        /// <summary>新接口读一批；返回 false 表示该 host 不支持新接口（404/405），调用方改走旧接口。</summary>
        private async Task<bool> TryReadWithQualityAsync(string baseUrl, List<string> batch,
            Dictionary<string, IgwAliasReading> result, CancellationToken ct)
        {
            var call = await CallAsync(() => _iot.ReadTagsWithQualityAsync(baseUrl, batch, _requestTimeout), allowReloadRetry: true, ct);
            if (call.Failure is not null)
            {
                MarkAll(batch, call.Failure, result);
                return true;
            }

            var transport = call.Transport!;
            if (transport.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                QualityUnsupportedUntilUtc[baseUrl] = DateTime.UtcNow + QualityUnsupportedTtl;
                return false;
            }
            if (!transport.IsHttpSuccess || transport.Envelope is null)
            {
                MarkAll(batch, HttpFailure(transport), result);
                return true;
            }
            if (!transport.Envelope.IsSuccess)
            {
                MarkAll(batch, IgwAliasReading.Bad(PLC_SignalReadStatus.DeviceError,
                    "IGW 逐点质量读取失败: " + transport.Envelope.ErrorMessage), result);
                return true;
            }

            var rows = transport.Envelope.BackResult ?? new List<DcsTagQualityItem>();
            for (var i = 0; i < batch.Count; i++)
            {
                var alias = batch[i];
                var row = i < rows.Count && SameAlias(rows[i]?.Key, alias)
                    ? rows[i]
                    : rows.FirstOrDefault(r => SameAlias(r?.Key, alias));
                if (row is null)
                {
                    result[alias] = IgwAliasReading.Bad(PLC_SignalReadStatus.Missing, "IGW 未返回该点");
                }
                else if (row.IsGood)
                {
                    result[alias] = IgwAliasReading.Good(DcsIgwMapper.NormalizeDcsValue(row.Value!), row.TimestampUtc);
                }
                else
                {
                    var detail = (string.IsNullOrWhiteSpace(row.Status) ? "Bad" : row.Status)
                        + "(0x" + row.StatusCode.ToString("X8", CultureInfo.InvariantCulture) + ")"
                        + (string.IsNullOrWhiteSpace(row.Reason) ? string.Empty : " " + row.Reason);
                    result[alias] = new IgwAliasReading(null,
                        PLC_SignalReadStatusMapper.FromIgwReason(false, row.Reason), detail, row.TimestampUtc);
                }
            }
            return true;
        }

        /// <summary>旧接口读一批：成功全 Good；失败按报文定位坏点剔除重试，定位不了对半拆分；请求数 ≤ 64。</summary>
        private async Task ReadWithLegacyListAsync(string baseUrl, List<string> batch,
            Dictionary<string, IgwAliasReading> result, CancellationToken ct)
        {
            var requests = 0;
            var pending = new Stack<List<string>>();
            pending.Push(batch);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var segment = pending.Pop();
                if (segment.Count == 0)
                {
                    continue;
                }
                if (requests >= MaxRequestsPerBatch)
                {
                    MarkRemaining(segment, pending, IgwAliasReading.Bad(PLC_SignalReadStatus.DeviceError, "定位坏点次数超限"), result);
                    return;
                }

                var call = await CallAsync(() => _iot.ReadTagsAsListRawAsync(baseUrl, segment, _requestTimeout),
                    allowReloadRetry: requests + 1 < MaxRequestsPerBatch, ct);
                requests += call.Requests;
                if (call.Failure is not null)
                {
                    MarkRemaining(segment, pending, call.Failure, result);
                    return;
                }

                var transport = call.Transport!;
                if (!transport.IsHttpSuccess || transport.Envelope is null)
                {
                    MarkRemaining(segment, pending, HttpFailure(transport), result);
                    return;
                }

                var envelope = transport.Envelope;
                if (envelope.IsSuccess)
                {
                    MapListRows(segment, envelope.BackResult, result);
                    continue;
                }

                var message = envelope.ErrorMessage ?? string.Empty;
                if (IgwLegacyReadErrorParser.TryLocate(message, segment, out var badAlias, out var status))
                {
                    result[badAlias] = IgwAliasReading.Bad(status, message);
                    var rest = segment.Where(a => !string.Equals(a, badAlias, StringComparison.Ordinal)).ToList();
                    if (status == PLC_SignalReadStatus.CommFailure || status == PLC_SignalReadStatus.Timeout)
                    {
                        // D-1 修复回合 1：定位到的坏点是"通讯中断/超时"——这类故障是设备级的，本段乃至本批
                        // 其余未定位点大概率会以同样的方式失败（同一台断线/超时的设备）。不再继续逐点定位
                        // （否则最坏要把 §64 请求上限全部耗光，还会被误标成"定位坏点次数超限"而不是真实原因），
                        // 直接把本段剩余点 + pending 里还没处理的段都标成同一状态。已解析的点位（badAlias 本身、
                        // 以及此前已定位/已成功的其它段）保持不变。
                        var deviceDetail = "设备级故障（" + status + "），判定批内其余未定位点位同一故障，不再逐点定位。首个定位点报文：" + message;
                        MarkRemaining(rest, pending, IgwAliasReading.Bad(status, deviceDetail), result);
                        return;
                    }
                    pending.Push(rest);
                    continue;
                }
                if (segment.Count == 1)
                {
                    result[segment[0]] = IgwAliasReading.Bad(PLC_SignalReadStatus.DeviceError,
                        message.Length == 0 ? "IGW 读取失败（无错误信息）" : message);
                    continue;
                }

                var half = segment.Count / 2;
                pending.Push(segment.GetRange(half, segment.Count - half));
                pending.Push(segment.GetRange(0, half));
            }
        }

        private sealed record CallResult<T>(IgwTransportResult<T>? Transport, IgwAliasReading? Failure, int Requests);

        /// <summary>一次调用（可含 1 次 503 重试）；异常折叠为 Failure，调用方取消时抛出。</summary>
        private async Task<CallResult<T>> CallAsync<T>(Func<Task<IgwTransportResult<T>>> send, bool allowReloadRetry,
            CancellationToken ct)
        {
            var requests = 0;
            while (true)
            {
                requests++;
                IgwTransportResult<T>? transport;
                try
                {
                    transport = await send().WaitAsync(_requestTimeout, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    return new(null, IgwAliasReading.Bad(PLC_SignalReadStatus.Timeout,
                        "IGW 读取超时（>" + _requestTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s）"), requests);
                }
                catch (TaskCanceledException ex)
                {
                    return new(null, IgwAliasReading.Bad(PLC_SignalReadStatus.Timeout, "IGW 读取超时: " + ex.Message), requests);
                }
                catch (JsonException ex)
                {
                    // D-1 修复回合 1：2xx 响应体本身解析失败（JsonReaderException/JsonSerializationException，
                    // 均派生自 JsonException）说明 IGW 确实响应了、只是内容有问题——这是设备/接口侧的数据问题，
                    // 不是"连不上"，不能归为 CommFailure("IGW 不可达")，否则会被当成瞬时网络故障误导排查方向。
                    return new(null, IgwAliasReading.Bad(PLC_SignalReadStatus.DeviceError,
                        "IGW 返回内容无法解析（" + ex.GetType().Name + "）: " + ex.Message), requests);
                }
                catch (Exception ex)
                {
                    return new(null, IgwAliasReading.Bad(PLC_SignalReadStatus.CommFailure,
                        "IGW 不可达: " + ex.GetType().Name + ": " + ex.Message), requests);
                }

                if (transport is null)
                {
                    return new(null, IgwAliasReading.Bad(PLC_SignalReadStatus.CommFailure, "IGW 无响应"), requests);
                }
                if (transport.HttpStatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    if (allowReloadRetry && requests == 1)
                    {
                        await _delay(ReloadRetryDelay, ct);
                        continue;
                    }
                    return new(null, IgwAliasReading.Bad(PLC_SignalReadStatus.CommFailure,
                        "IGW 正在热重载（HTTP 503），重试后仍不可用"), requests);
                }
                return new(transport, null, requests);
            }
        }

        private static IgwAliasReading HttpFailure<T>(IgwTransportResult<T> transport)
        {
            if (transport.IsHttpSuccess)
            {
                return IgwAliasReading.Bad(PLC_SignalReadStatus.DeviceError, "IGW 返回内容无法解析（HTTP " + (int)transport.HttpStatusCode + "）");
            }
            var code = (int)transport.HttpStatusCode;
            var body = transport.RawBody ?? string.Empty;
            if (body.Length > 300)
            {
                body = body.Substring(0, 300) + "…";
            }
            var status = code == 0 || code >= 500 ? PLC_SignalReadStatus.CommFailure : PLC_SignalReadStatus.DeviceError;
            return IgwAliasReading.Bad(status, ("IGW HTTP " + code + ": " + body).TrimEnd(' ', ':'));
        }

        private static void MapListRows(List<string> segment, List<DcsTagReadItem>? rows, Dictionary<string, IgwAliasReading> result)
        {
            rows ??= new List<DcsTagReadItem>();
            for (var i = 0; i < segment.Count; i++)
            {
                var alias = segment[i];
                var row = i < rows.Count && SameAlias(rows[i]?.Key, alias)
                    ? rows[i]
                    : rows.FirstOrDefault(r => SameAlias(r?.Key, alias));
                result[alias] = row is null
                    ? IgwAliasReading.Bad(PLC_SignalReadStatus.Missing, "IGW 未返回该点")
                    : IgwAliasReading.Good(DcsIgwMapper.NormalizeDcsValue(row.Value!), null);
            }
        }

        private static bool SameAlias(string? key, string alias) =>
            string.Equals(key?.Trim(), alias, StringComparison.OrdinalIgnoreCase);

        private static void MarkAll(IEnumerable<string> aliases, IgwAliasReading reading, Dictionary<string, IgwAliasReading> result)
        {
            foreach (var alias in aliases)
            {
                result[alias] = reading;
            }
        }

        private static void MarkRemaining(List<string> segment, Stack<List<string>> pending, IgwAliasReading reading,
            Dictionary<string, IgwAliasReading> result)
        {
            MarkAll(segment, reading, result);
            while (pending.Count > 0)
            {
                MarkAll(pending.Pop(), reading, result);
            }
        }
    }

    /// <summary>
    /// IGW 旧读接口（ReadTagsAsList）失败报文解析（纯函数）：只报第一个坏点，格式见 IGW DcsTagOperations.ReadMany /
    /// DcsTagAliasResolver——「点位质量异常:{别名} {名称}({0x........})」「点位不存在:{别名}」「无该点位读取权限:{别名}」
    /// 「DCS 点位别名不唯一:{别名}」。别名可能含空格，按段内别名前缀最长匹配。
    /// </summary>
    internal static class IgwLegacyReadErrorParser
    {
        private const string QualityPrefix = "点位质量异常:";
        private const string MissingPrefix = "点位不存在:";
        private const string DeniedPrefix = "无该点位读取权限:";
        private const string AmbiguousPrefix = "DCS 点位别名不唯一:";

        private static readonly Regex StatusPattern =
            new(@"^(?<name>[A-Za-z_]+)\s*(\((?<hex>0x[0-9A-Fa-f]{1,8})\))?", RegexOptions.CultureInvariant);

        public static bool TryLocate(string message, IReadOnlyList<string> segment, out string alias, out PLC_SignalReadStatus status)
        {
            alias = string.Empty;
            status = PLC_SignalReadStatus.DeviceError;
            if (string.IsNullOrWhiteSpace(message) || segment is null || segment.Count == 0)
            {
                return false;
            }

            var text = message.Trim();
            if (TryExact(text, MissingPrefix, segment, out alias))
            {
                status = PLC_SignalReadStatus.NotRegistered;
                return true;
            }
            if (TryExact(text, DeniedPrefix, segment, out alias))
            {
                status = PLC_SignalReadStatus.AccessDenied;
                return true;
            }
            if (TryExact(text, AmbiguousPrefix, segment, out alias))
            {
                status = PLC_SignalReadStatus.DeviceError;
                return true;
            }
            if (!text.StartsWith(QualityPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var rest = text.Substring(QualityPrefix.Length);
            string? best = null;
            foreach (var candidate in segment)
            {
                if (rest.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase)
                    && (best is null || candidate.Length > best.Length))
                {
                    best = candidate;
                }
            }
            if (best is null)
            {
                return false;
            }

            alias = best;
            var match = StatusPattern.Match(rest.Substring(best.Length + 1).Trim());
            if (match.Success && match.Groups["hex"].Success
                && uint.TryParse(match.Groups["hex"].Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                status = PLC_SignalReadStatusMapper.FromStatusCode(code);
            }
            else
            {
                status = PLC_SignalReadStatusMapper.FromStatusName(match.Success ? match.Groups["name"].Value : null);
            }
            return true;
        }

        private static bool TryExact(string text, string prefix, IReadOnlyList<string> segment, out string alias)
        {
            alias = string.Empty;
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            var named = text.Substring(prefix.Length).Trim();
            foreach (var candidate in segment)
            {
                if (string.Equals(candidate, named, StringComparison.OrdinalIgnoreCase))
                {
                    alias = candidate;
                    return true;
                }
            }
            return false;
        }
    }
}
