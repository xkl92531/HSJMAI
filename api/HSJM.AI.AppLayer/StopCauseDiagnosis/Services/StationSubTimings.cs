using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 单工位诊断里深层步骤的累计耗时（2026-09-23）。
    /// 写源追溯、失败路径历史分析散落在求值的多个分支里，调用点很多，逐个包计时会把诊断主流程改乱；
    /// 这里用 AsyncLocal 在工位入口开一个累加器，被测方法自己往里记，工位结束时并入 TimingBreakdown。
    /// 同一步骤可能被并发调用，所以记的是累计值（可能大于墙钟时间）。作用域外调用什么都不记。
    /// </summary>
    public sealed class StationSubTimings : IDisposable
    {
        private static readonly AsyncLocal<StationSubTimings?> CurrentScope = new();

        private readonly StationSubTimings? _parent;
        private readonly ConcurrentDictionary<string, long> _elapsedMs = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _order = new();
        private bool _disposed;

        private StationSubTimings(StationSubTimings? parent) => _parent = parent;

        public static StationSubTimings? Current => CurrentScope.Value;

        public static StationSubTimings Begin()
        {
            var scope = new StationSubTimings(CurrentScope.Value);
            CurrentScope.Value = scope;
            return scope;
        }

        /// <summary>给当前作用域累加一段耗时；没有作用域时什么都不做。</summary>
        public static void Add(string name, long elapsedMs)
        {
            var scope = CurrentScope.Value;
            if (scope == null)
            {
                return;
            }
            if (scope._elapsedMs.TryAdd(name, elapsedMs))
            {
                scope._order.Enqueue(name);
                return;
            }
            scope._elapsedMs.AddOrUpdate(name, elapsedMs, (_, current) => current + elapsedMs);
        }

        /// <summary>计时到 Dispose 为止，累加到 <paramref name="name"/>。</summary>
        public static IDisposable Measure(string name) => new Measurement(name);

        /// <summary>按首次出现顺序返回累计值。</summary>
        public IReadOnlyList<KeyValuePair<string, long>> Snapshot() =>
            _order.Select(name => new KeyValuePair<string, long>(name, _elapsedMs[name])).ToList();

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

        private sealed class Measurement : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private bool _done;

            public Measurement(string name) => _name = name;

            public void Dispose()
            {
                if (_done)
                {
                    return;
                }
                _done = true;
                Add(_name, _stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
