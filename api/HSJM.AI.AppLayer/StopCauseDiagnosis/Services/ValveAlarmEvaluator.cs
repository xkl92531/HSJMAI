#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>阀门报警的读取请求构造与激活判定（纯函数）。</summary>
    public static class ValveAlarmEvaluator
    {
        private const string IoKeyPrefix = "io::";
        private const char IoKeySeparator = '\u0001';

        /// <summary>IO 点用独立前缀键（避免与工位快照的裸点名结果串用）；DB 成员用原名；其它不可读返回 null。</summary>
        public static string? ReadKey(ValveAlarmSignalRef signal)
        {
            if (!string.IsNullOrWhiteSpace(signal.TableName))
                return IoKeyPrefix + signal.TableName + IoKeySeparator + signal.Name;
            return IsDbPath(signal.Name) ? signal.Name : null;
        }

        /// <summary>为报警位、输出信号与反馈信号构造去重的精确读取请求（键 = <see cref="ReadKey"/>）。</summary>
        public static Dictionary<string, PLC_ExactReadRequest> BuildRequests(IEnumerable<ValveAlarmDefinition> definitions)
        {
            var list = definitions.ToList();
            var requests = BuildAlarmRequests(list);
            foreach (var (key, request) in BuildSignalRequests(list)) requests.TryAdd(key, request);
            return requests;
        }

        /// <summary>只读报警位的请求（终审 I-1：先读报警位，激活的再补读关联信号）。</summary>
        public static Dictionary<string, PLC_ExactReadRequest> BuildAlarmRequests(IEnumerable<ValveAlarmDefinition> definitions)
        {
            var requests = new Dictionary<string, PLC_ExactReadRequest>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in definitions) AddDbPath(requests, AlarmReadPath(d));
            return requests;
        }

        /// <summary>报警的输出与到位反馈信号的请求。</summary>
        public static Dictionary<string, PLC_ExactReadRequest> BuildSignalRequests(IEnumerable<ValveAlarmDefinition> definitions)
        {
            var requests = new Dictionary<string, PLC_ExactReadRequest>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in definitions)
            {
                if (d.Output != null) Add(requests, d.Output);
                foreach (var f in d.Feedbacks) Add(requests, f);
            }
            return requests;
        }

        /// <summary>报警位读到 Good 且为 true（位切片按父成员取位）。</summary>
        public static bool IsActive(ValveAlarmDefinition d, IReadOnlyDictionary<string, PLC_ReadTagResult> reads) => AlarmValue(d, reads) == true;

        /// <summary>报警位的值：读不到 / 质量坏 / 认不出为 null。</summary>
        private static bool? AlarmValue(ValveAlarmDefinition d, IReadOnlyDictionary<string, PLC_ReadTagResult> reads)
        {
            if (!reads.TryGetValue(AlarmReadPath(d), out var row) || row == null || !row.IsGood) return null;
            return TrySplitBitSlice(d.ReadVariable ?? d.AlarmVariable, out _, out var bit)
                ? BitOf(row.Value, bit)
                : StopCauseAstRuntimeEvaluationService.CoerceBoolean(row.Value);
        }

        /// <summary>报警位读到 Good 且为 true 即激活（位切片按父成员取位）；读不到 / 质量坏 / 非布尔计入 FailedCount（不当成无报警）。</summary>
        public static (List<ActiveValveAlarmDto> Active, int FailedCount) Evaluate(
            IEnumerable<ValveAlarmDefinition> definitions,
            IReadOnlyDictionary<string, PLC_ReadTagResult> reads)
        {
            var active = new List<ActiveValveAlarmDto>();
            var failed = 0;
            foreach (var d in definitions)
            {
                var value = AlarmValue(d, reads);
                if (value is null)
                {
                    failed++;
                    continue;
                }
                if (value == false) continue;

                active.Add(new ActiveValveAlarmDto
                {
                    AlarmVariable = d.AlarmVariable,
                    Kind = d.Kind,
                    Direction = d.Direction,
                    Text = d.Text,
                    ValveInstance = d.ValveInstance,
                    BlockName = d.BlockName,
                    NetworkId = d.NetworkId,
                    OutputSignal = d.Output == null ? null : SignalValue(d.Output, reads),
                    Feedbacks = d.Feedbacks.Select(f => SignalValue(f, reads)).ToList(),
                    FeedbackGroups = d.FeedbackGroups.Select(g => g.ToList()).ToList(),
                });
            }
            return (active, failed);
        }

        private static readonly System.Text.RegularExpressions.Regex BitSlice = new(
            @"^(?<parent>.+)\.%X(?<bit>\d{1,2})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>报警位是字/字节成员的位切片（DB.Word.%X3，锁存报警位清单可能有）：读整个父成员，取第 n 位。</summary>
        public static bool TrySplitBitSlice(string path, out string parent, out int bit)
        {
            var m = BitSlice.Match(path?.Trim() ?? string.Empty);
            parent = m.Success ? m.Groups["parent"].Value : string.Empty;
            bit = m.Success ? int.Parse(m.Groups["bit"].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
            return m.Success && bit < 64;
        }

        /// <summary>报警位实际要读的路径：位切片读父成员，其余读自己。</summary>
        public static string AlarmReadPath(string alarmVariable) =>
            TrySplitBitSlice(alarmVariable, out var parent, out _) ? parent : alarmVariable;

        /// <summary>定义的读取路径：有解析器写法（ReadVariable）用它，否则用清单写法；位切片读父成员。</summary>
        public static string AlarmReadPath(ValveAlarmDefinition d) => AlarmReadPath(d.ReadVariable ?? d.AlarmVariable);

        /// <summary>位串值（整数，或十进制 / 16#… 文本）的第 bit 位（%X0 为最低位）；布尔、负数、认不出的值为 null（计读取失败）。</summary>
        public static bool? BitOf(object? value, int bit)
        {
            ulong? raw = value switch
            {
                byte b => b, ushort us => us, uint ui => ui, ulong ul => ul,
                sbyte sb when sb >= 0 => (ulong)sb, short sh when sh >= 0 => (ulong)sh,
                int i when i >= 0 => (ulong)i, long l when l >= 0 => (ulong)l,
                string text => ParseBits(text),
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je when je.TryGetUInt64(out var u) => u,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => ParseBits(je.GetString() ?? string.Empty),
                _ => null,
            };
            return raw is ulong r && bit is >= 0 and < 64 ? ((r >> bit) & 1UL) == 1UL : null;
        }

        private static ulong? ParseBits(string text)
        {
            var t = text.Trim();
            var hash = t.LastIndexOf("16#", StringComparison.OrdinalIgnoreCase);
            if (hash >= 0)
                return ulong.TryParse(t[(hash + 3)..].Replace("_", string.Empty), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : null;
            return ulong.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        private static ValveAlarmSignalValueDto SignalValue(ValveAlarmSignalRef signal, IReadOnlyDictionary<string, PLC_ReadTagResult> reads)
        {
            var key = ReadKey(signal);
            bool? value = null;
            if (key != null && reads.TryGetValue(key, out var row) && row is { IsGood: true })
                value = StopCauseAstRuntimeEvaluationService.CoerceBoolean(row.Value);
            return new ValveAlarmSignalValueDto { Name = signal.Name, Address = signal.Address, Value = value };
        }

        private static void Add(Dictionary<string, PLC_ExactReadRequest> requests, ValveAlarmSignalRef signal)
        {
            if (!string.IsNullOrWhiteSpace(signal.TableName))
            {
                var key = ReadKey(signal)!;
                requests.TryAdd(key, new PLC_ExactReadRequest(key, signal.TableName!, signal.Name));
                return;
            }
            AddDbPath(requests, signal.Name);
        }

        private static void AddDbPath(Dictionary<string, PLC_ExactReadRequest> requests, string path)
        {
            if (!IsDbPath(path)) return;
            var dot = path.IndexOf('.');
            requests.TryAdd(path, new PLC_ExactReadRequest(path, path[..dot], path[(dot + 1)..]));
        }

        private static bool IsDbPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("#", StringComparison.Ordinal)) return false;
            // 形如 I1106.5 / %Q3000.1 的绝对地址（点表里没有的）不是 DB 路径：不发请求（发了也只会拆成 I1106 / 5 读成坏质量）
            if (AbsoluteAddress.IsMatch(path.Trim())) return false;
            var dot = path.IndexOf('.');
            return dot > 0 && dot < path.Length - 1;
        }

        private static readonly System.Text.RegularExpressions.Regex AbsoluteAddress = new(
            @"^%?[IQMEA][XBWD]?\d+(\.\d+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
