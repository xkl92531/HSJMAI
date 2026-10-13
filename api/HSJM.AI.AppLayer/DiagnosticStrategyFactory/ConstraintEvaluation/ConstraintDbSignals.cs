using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory
{
    internal delegate Task<IReadOnlyList<PLC_ExactReadResult>> ExactReader(
        long projectId, IReadOnlyCollection<PLC_ExactReadRequest> requests, CancellationToken ct);

    /// <summary>
    /// 一个 PLC 的门控 DB 源读数（与 <see cref="PlcLiveSignals"/> 分开存放）：只给门控规则的 Source 预填，
    /// 不进 PLC 的共享实时信号，避免非门控规则的后缀兜底借到这些 DB 值（评审 Important）。
    /// </summary>
    internal sealed class DbSignalSet
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, SignalUnknownReason> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Contains(string symbol) => Values.ContainsKey(symbol) || Statuses.ContainsKey(symbol);

        /// <summary>
        /// 为给定信号预填 Good 布尔值或未采到原因；本集合没有的信号、调用方字典里已有的信号不动
        /// （PlcLiveSignals.Resolve 会跳过两本字典里已有的名字）。
        /// </summary>
        public void Fill(IEnumerable<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> goodValues, Dictionary<string, SignalUnknownReason> unknownSignals)
        {
            foreach (var name in (signals ?? Enumerable.Empty<PLC_IOConstraintSignalEntity>()).Select(s => s?.SignalName))
            {
                if (string.IsNullOrWhiteSpace(name) || goodValues.ContainsKey(name) || unknownSignals.ContainsKey(name))
                    continue;
                if (Values.TryGetValue(name, out var raw))
                {
                    if (PlcLiveSignals.TryConvertToBool(raw, out var value))
                        goodValues[name] = value;
                    else
                        unknownSignals[name] = new SignalUnknownReason(SignalUnknownReason.NotBoolean, $"{name}={raw}");
                }
                else if (Statuses.TryGetValue(name, out var reason))
                {
                    unknownSignals[name] = reason;
                }
            }
        }
    }

    /// <summary>
    /// FB 程序体证明的急停门控（spec 2026-09-30 §4.4）：Source 常是 DB 变量，不在 IO 点表。
    /// 经精确读补进该 PLC 的门控 DB 读数集合；读不到按原因记入 Statuses，绝不当成通过。
    /// </summary>
    internal static class ConstraintDbSignals
    {
        /// <summary>"DB名.成员路径" → 精确读请求（RequestKey = 原符号）；不含 '.' 返回 null。</summary>
        public static PLC_ExactReadRequest Request(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            var dot = symbol.IndexOf('.');
            if (dot <= 0 || dot == symbol.Length - 1) return null;
            return new PLC_ExactReadRequest(symbol, symbol.Substring(0, dot), symbol.Substring(dot + 1));
        }

        /// <summary>构造不出精确读请求的门控源（不在点表、名字里没有 '.'）的未采到说明。</summary>
        public const string UnrequestableDetail = "门控源不可读取";

        /// <summary>
        /// 精确读补进门控源。构造不出请求的名字直接记为未参与读取（NotRead）：绝不留给后缀兜底去借别的点的值。
        /// </summary>
        public static async Task MergeAsync(long projectId, IEnumerable<string> symbols, DbSignalSet store, ExactReader read, CancellationToken ct)
        {
            var names = symbols
                .Where(s => !string.IsNullOrWhiteSpace(s) && !store.Contains(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in names.Where(n => Request(n) == null))
                store.Statuses[name] = SignalUnknownReason.FromStatus(PLC_SignalReadStatus.NotRead, UnrequestableDetail);
            var requests = names.Select(Request).Where(r => r != null).ToArray();
            if (requests.Length == 0) return;
            IReadOnlyList<PLC_ExactReadResult> results;
            try
            {
                results = await read(projectId, requests, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                foreach (var request in requests)
                    store.Statuses[request.RequestKey] = SignalUnknownReason.FromStatus(
                        PLC_SignalReadStatus.CommFailure, "DB 变量读取失败：" + ex.Message);
                return;
            }
            Apply(requests, results, store.Values, store.Statuses);
        }

        public static void Apply(IReadOnlyCollection<PLC_ExactReadRequest> requests, IReadOnlyList<PLC_ExactReadResult> results,
            IDictionary<string, object> values, IDictionary<string, SignalUnknownReason> statuses)
        {
            foreach (var request in requests)
            {
                var result = results?.FirstOrDefault(r => r.RequestKey == request.RequestKey);
                if (result != null && result.Quality == PLC_ExactReadQuality.Good && result.Value != null)
                {
                    values[request.RequestKey] = result.Value;
                }
                else if (result == null)
                {
                    statuses[request.RequestKey] = SignalUnknownReason.FromStatus(PLC_SignalReadStatus.Missing, "DB 变量未返回");
                }
                else
                {
                    statuses[request.RequestKey] = SignalUnknownReason.FromStatus(
                        FailureStatus(result.ErrorCode), result.ErrorCode ?? "DB 变量读取质量异常");
                }
            }
        }

        /// <summary>PLC_VariableAccessService 精确读实际产出的错误码 → 诊断读取状态（显式映射，优先于包含匹配）。</summary>
        static readonly Dictionary<string, PLC_SignalReadStatus> KnownErrorCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PlcExactReadFailed"] = PLC_SignalReadStatus.CommFailure,
            ["PlcExactProjectReadFailed"] = PLC_SignalReadStatus.CommFailure,
            ["PlcExactMetadataReadFailed"] = PLC_SignalReadStatus.CommFailure,
            ["PlcExactTagMissing"] = PLC_SignalReadStatus.SymbolNotFound,
            ["PlcExactResponseMissing"] = PLC_SignalReadStatus.Missing,
            ["PlcExactProjectMissing"] = PLC_SignalReadStatus.Missing,
            ["PlcExactTagAmbiguous"] = PLC_SignalReadStatus.DeviceError,
            ["IgwLedgerUnavailableAmbiguous"] = PLC_SignalReadStatus.DeviceError,
        };

        /// <summary>精确读错误码 → 诊断读取状态：已知错误码显式映射；其余按忽略大小写的包含匹配兜底（按顺序取第一档）。</summary>
        internal static PLC_SignalReadStatus FailureStatus(string errorCode)
        {
            if (string.IsNullOrEmpty(errorCode)) return PLC_SignalReadStatus.BadQuality;
            if (KnownErrorCodes.TryGetValue(errorCode, out var known)) return known;
            bool Has(string part) => errorCode.Contains(part, StringComparison.OrdinalIgnoreCase);
            if (Has("Missing") || Has("NotFound")) return PLC_SignalReadStatus.SymbolNotFound;
            if (Has("Timeout")) return PLC_SignalReadStatus.Timeout;
            if (Has("Comm") || Has("Unavailable") || Has("Offline") || Has("Ledger")) return PLC_SignalReadStatus.CommFailure;
            return PLC_SignalReadStatus.BadQuality;
        }
    }
}
