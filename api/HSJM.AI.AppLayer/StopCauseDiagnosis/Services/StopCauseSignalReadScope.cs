using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 一次停机诊断内的实时值读取去重（2026-09-23）。
    /// 现场 IGW 日志：同一工位 GetActiveGraphCandidates 与 GetActiveGraphExecBitFallbackCandidates、
    /// 以及后续锚点求值会在几秒内重复读同一组点；并行排查的工位之间也可能读到同一批 DB 点。
    /// 诊断入口 <see cref="Begin"/> 开一个作用域（AsyncLocal，随子任务流动），
    /// <see cref="StopCauseSignalSnapshotService"/> 的实时读经 <see cref="ReadAsync"/> 过滤：
    /// - 读成功（IsGood）的值在 <see cref="DefaultMaxAge"/> 内复用；
    /// - 正在读的点，并发调用方等同一个请求，不另发；
    /// - 读失败/没返回的点不缓存，下次照常重读（保留原先"失败就再读一次"的自愈能力）。
    /// 作用域外（工位影子巡检、监控页实时读）行为不变。
    /// </summary>
    public sealed class StopCauseSignalReadScope : IDisposable
    {
        /// <summary>成功值的复用时长。停机工位的信号本就不变，这个窗口只是防止长时间诊断用到过旧的值。</summary>
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(15);

        private static readonly AsyncLocal<StopCauseSignalReadScope?> CurrentScope = new();

        private readonly StopCauseSignalReadScope? _parent;
        private readonly TimeSpan _maxAge;
        private readonly Func<DateTime> _clock;
        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private StopCauseSignalReadScope(StopCauseSignalReadScope? parent, TimeSpan maxAge, Func<DateTime> clock)
        {
            _parent = parent;
            _maxAge = maxAge;
            _clock = clock;
        }

        /// <summary>当前异步流上的作用域；不在诊断内为 null。</summary>
        public static StopCauseSignalReadScope? Current => CurrentScope.Value;

        public static StopCauseSignalReadScope Begin(TimeSpan? maxAge = null, Func<DateTime>? clock = null)
        {
            var scope = new StopCauseSignalReadScope(CurrentScope.Value, maxAge ?? DefaultMaxAge, clock ?? (() => DateTime.Now));
            CurrentScope.Value = scope;
            return scope;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (ReferenceEquals(CurrentScope.Value, this))
            {
                CurrentScope.Value = _parent;
            }
        }

        /// <summary>
        /// 读 <paramref name="symbols"/>：可复用的直接取，其余一次交给 <paramref name="reader"/>。
        /// reader 返回的行按 TagName（不区分大小写）对应到符号；返回值只包含有结果的符号（与 reader 语义一致），且是副本。
        /// </summary>
        public async Task<List<PLC_ReadTagResult>> ReadAsync(
            long projectId,
            IReadOnlyList<string> symbols,
            Func<IReadOnlyList<string>, Task<List<PLC_ReadTagResult>>> reader)
        {
            var now = _clock();
            var waits = new List<Task<PLC_ReadTagResult?>>(symbols.Count);
            var owned = new List<(string Symbol, string Key, Entry Entry)>();

            foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = projectId + "\n" + symbol;
                while (true)
                {
                    if (_entries.TryGetValue(key, out var existing) && existing.IsUsable(now, _maxAge))
                    {
                        waits.Add(existing.Result.Task);
                        break;
                    }

                    var entry = new Entry(now);
                    var claimed = existing is null
                        ? _entries.TryAdd(key, entry)
                        : _entries.TryUpdate(key, entry, existing);
                    if (claimed)
                    {
                        owned.Add((symbol, key, entry));
                        waits.Add(entry.Result.Task);
                        break;
                    }
                }
            }

            if (owned.Count > 0)
            {
                List<PLC_ReadTagResult> rows;
                try
                {
                    rows = await reader(owned.Select(o => o.Symbol).ToList()) ?? new List<PLC_ReadTagResult>();
                }
                catch (Exception ex)
                {
                    foreach (var (_, key, entry) in owned)
                    {
                        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                        entry.Result.TrySetException(ex);
                    }
                    throw;
                }

                var bySymbol = rows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.TagName))
                    .GroupBy(row => row.TagName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(r => r.IsGood).First(),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var (symbol, key, entry) in owned)
                {
                    bySymbol.TryGetValue(symbol, out var row);
                    entry.Result.TrySetResult(row is null ? null : Copy(row));
                    if (row is not { IsGood: true })
                    {
                        // 失败不缓存：已经在等这次结果的调用方照样拿到它，之后的调用重新读
                        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                    }
                }
            }

            var results = await Task.WhenAll(waits);
            return results.Where(row => row != null).Select(row => Copy(row!)).ToList();
        }

        private static PLC_ReadTagResult Copy(PLC_ReadTagResult row) => new()
        {
            IsGood = row.IsGood,
            Value = row.Value,
            ErrorMessage = row.ErrorMessage,
            TagName = row.TagName,
            OldTagName = row.OldTagName,
        };

        private sealed class Entry
        {
            public Entry(DateTime startedAt) => StartedAt = startedAt;

            public DateTime StartedAt { get; }

            public TaskCompletionSource<PLC_ReadTagResult?> Result { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>还在读（大家等同一个请求）或读成功且未过期。</summary>
            public bool IsUsable(DateTime now, TimeSpan maxAge) =>
                !Result.Task.IsCompleted || now - StartedAt <= maxAge;
        }
    }
}
