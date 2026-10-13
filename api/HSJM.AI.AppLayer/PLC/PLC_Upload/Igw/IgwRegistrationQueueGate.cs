using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>入队结果</summary>
    public enum IgwEnqueueResult
    {
        /// <summary>已新入队 Hangfire 任务</summary>
        Enqueued,

        /// <summary>同项目已有任务排队或执行中：本次范围已并入「待补跑」，该任务结束后自动以并集补跑一次</summary>
        MergedIntoPending,
    }

    /// <summary>
    /// IGW 注册任务的同项目防重闸门（进程内）：
    /// <list type="bullet">
    /// <item>入队时即占位（修复旧 PLC_TagRegistrationJob「执行时才占位」的入队竞态）；</item>
    /// <item>已占位时再来的请求（第二次上传、按钮、面板手工 Retry 的并发执行）不丢弃，并入待补跑范围；
    /// 正在执行的任务结束时取走待补跑，以「本次 ∪ 待补跑」再跑一次（Full 取或），直到没有待补跑才释放占位；</item>
    /// <item>占位后迟迟没开始执行（如本机没有 worker 消费本机队列）超过阈值视为失效，允许重新入队，避免永久卡死。</item>
    /// </list>
    /// 跨进程由本机队列（MachineQueue）隔离，不在本闸门范围内。未完成的额外范围由 PLC_IgwRegistrationJob 写入注册状态表（M-1），进程重启后由 IgwPendingRegistrationRecoveryService 恢复入队。结果 Failed（未抛异常）的最后一轮范围进入结转，不立即重入队，由下一次同项目执行开始时并入（<see cref="AdoptCarried"/>）。
    /// </summary>
    public sealed class IgwRegistrationQueueGate
    {
        private sealed class Entry
        {
            public bool Started;
            public DateTime ReservedAtUtc;
            public IgwRegistrationArgs Pending;
            /// <summary>正在执行的补跑轮次里「任务自身参数之外」的范围（上一轮取走的待补跑）；该轮结束前仍属未完成（M-1 落库用）</summary>
            public IgwRegistrationArgs InFlightExtra;
        }

        private readonly object _lock = new();
        private readonly Dictionary<long, Entry> _entries = new();
        /// <summary>
        /// 结转范围（M-1 fix round 1）：最后一轮结果为 Failed（未抛异常）时该轮的完整范围，不立即重新入队（避免持续失败时空转），
        /// 由下一次同项目执行（上传 / 按钮 / 面板重试 / 启动恢复）开始时并入本轮。
        /// </summary>
        private readonly Dictionary<long, IgwRegistrationArgs> _carried = new();
        private readonly TimeSpan _staleReservation;
        private readonly Func<DateTime> _utcNow;

        public IgwRegistrationQueueGate(TimeSpan? staleReservation = null, Func<DateTime> utcNow = null)
        {
            _staleReservation = staleReservation ?? TimeSpan.FromMinutes(30);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>占位是否仍然有效（未失效）：已开始执行的占位永远有效；未开始的占位在 <see cref="_staleReservation"/> 内有效。</summary>
        private bool IsActive(Entry entry, DateTime now) => entry.Started || now - entry.ReservedAtUtc < _staleReservation;

        /// <summary>入队前调用：无占位（或占位已失效）→ 占位并返回 Enqueued（调用方随后入队 Hangfire）；否则并入待补跑。</summary>
        public IgwEnqueueResult Reserve(long projectId, IgwRegistrationArgs args)
        {
            lock (_lock)
            {
                var now = _utcNow();
                if (_entries.TryGetValue(projectId, out var entry) && IsActive(entry, now))
                {
                    entry.Pending = IgwRegistrationArgs.Union(entry.Pending, args);
                    return IgwEnqueueResult.MergedIntoPending;
                }
                // 失效占位被替换时保留其待补跑范围，交给新任务一并处理
                _entries[projectId] = new Entry { ReservedAtUtc = now, Pending = entry?.Pending };
                return IgwEnqueueResult.Enqueued;
            }
        }

        /// <summary>
        /// 该项目当前是否有注册任务排队 / 执行中（占位未失效）。删项目清理任务据此推迟，避免与在飞注册互相打架
        /// （清理先删设备、注册随后又建回来）。仅本进程内有效——跨进程由本机队列隔离，与闸门同一范围。
        /// </summary>
        public bool IsOccupied(long projectId)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(projectId, out var entry) && IsActive(entry, _utcNow());
            }
        }

        /// <summary>任务开始执行时调用：已有同项目任务在执行 → 本次范围并入待补跑并返回 false（调用方直接结束）。</summary>
        public bool TryStart(long projectId, IgwRegistrationArgs args)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(projectId, out var entry))
                {
                    if (entry.Started)
                    {
                        entry.Pending = IgwRegistrationArgs.Union(entry.Pending, args);
                        return false;
                    }
                    entry.Started = true;
                    return true;
                }
                _entries[projectId] = new Entry { Started = true, ReservedAtUtc = _utcNow() };
                return true;
            }
        }

        /// <summary>
        /// 任务开始执行（<see cref="TryStart"/> 返回 true）后调用：取走结转范围，并记为本轮的在飞额外范围（本轮完成前仍属未完成）；
        /// 返回值由调用方并入本轮参数。无结转 → null。
        /// </summary>
        public IgwRegistrationArgs AdoptCarried(long projectId)
        {
            lock (_lock)
            {
                if (!_carried.Remove(projectId, out var carried))
                {
                    return null;
                }
                if (_entries.TryGetValue(projectId, out var entry))
                {
                    entry.InFlightExtra = IgwRegistrationArgs.Union(entry.InFlightExtra, carried);
                }
                return carried;
            }
        }

        /// <summary>
        /// 启动恢复用：同项目已有 Hangfire 任务排队 / 执行中时，把恢复出的范围交给它而不另行入队——
        /// 闸门已占位 → 并入待补跑；未占位（重启前入队、尚未开始执行）→ 结转，由该任务开始时并入。
        /// </summary>
        public void CarryOrMerge(long projectId, IgwRegistrationArgs args)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(projectId, out var entry))
                {
                    entry.Pending = IgwRegistrationArgs.Union(entry.Pending, args);
                    return;
                }
                _carried[projectId] = IgwRegistrationArgs.Union(_carried.GetValueOrDefault(projectId), args);
            }
        }

        /// <summary>
        /// 一轮执行结束时调用：有待补跑 → 返回「本轮 ∪ 待补跑」继续执行（仍占位）；否则释放占位并返回 null。
        /// <paramref name="failed"/>=true（本轮结果 Failed）且无待补跑时，本轮完整范围转入结转（M-1 fix round 1），不丢弃。
        /// </summary>
        public IgwRegistrationArgs CompleteOrTakePending(long projectId, IgwRegistrationArgs current, bool failed = false)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(projectId, out var entry) && entry.Pending != null)
                {
                    var next = IgwRegistrationArgs.Union(current, entry.Pending);
                    // 上一轮的 InFlightExtra 已随本轮完成；本次取走的待补跑成为新的在飞额外范围
                    entry.InFlightExtra = entry.Pending;
                    entry.Pending = null;
                    return next;
                }
                _entries.Remove(projectId);
                if (failed && current != null)
                {
                    _carried[projectId] = IgwRegistrationArgs.Union(_carried.GetValueOrDefault(projectId), current);
                }
                return null;
            }
        }

        /// <summary>
        /// 尚未完成的「任务自身参数之外」的范围：执行中补跑轮次取走的范围 ∪ 尚未取走的待补跑 ∪ 结转范围（M-1 落库用）；无 → null。
        /// Hangfire 任务自身参数由 Hangfire 存储持久化，不在此列（但 Failed 轮次结转时整轮范围都在结转里）。
        /// </summary>
        public IgwRegistrationArgs GetUnfinishedExtras(long projectId)
        {
            lock (_lock)
            {
                var carried = _carried.GetValueOrDefault(projectId);
                return _entries.TryGetValue(projectId, out var entry)
                    ? IgwRegistrationArgs.Union(IgwRegistrationArgs.Union(entry.InFlightExtra, entry.Pending), carried)
                    : carried;
            }
        }

        /// <summary>执行异常时释放占位并取出未完成的额外范围（调用方另行入队，不再丢弃）</summary>
        public IgwRegistrationArgs ReleaseAndTakeExtras(long projectId)
        {
            lock (_lock)
            {
                _carried.Remove(projectId, out var carried);
                if (!_entries.Remove(projectId, out var entry))
                {
                    return carried;
                }
                return IgwRegistrationArgs.Union(IgwRegistrationArgs.Union(entry.InFlightExtra, entry.Pending), carried);
            }
        }

        /// <summary>入队失败时释放占位（执行异常请用 ReleaseAndTakeExtras，不丢待补跑）。</summary>
        public void Release(long projectId)
        {
            lock (_lock)
            {
                _entries.Remove(projectId);
            }
        }
    }
}
