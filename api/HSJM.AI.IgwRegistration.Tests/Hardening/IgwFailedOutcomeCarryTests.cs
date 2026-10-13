using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>
/// M-1 fix round 1：最后一轮结果 Failed（未抛异常，如批量探测失败）时，合并进来的范围不丢——
/// 结转并保留落库记录，不立即重新入队（避免持续失败时空转），由下一次同项目执行并入；成功后清除记录。
/// </summary>
public class IgwFailedOutcomeCarryTests
{
    private static long _nextProjectId = 880_000;

    private static long NewProjectId() => Interlocked.Increment(ref _nextProjectId);

    private static IgwRegistrationArgs Args(string block, string taskId = null) =>
        new(new[] { block }, Array.Empty<string>(), false, false, taskId);

    private sealed class StateRecorder
    {
        public readonly List<(long ProjectId, string Json)> Saves = new();

        public IPLC_IgwRegistrationStateService Service => Fake.Create<IPLC_IgwRegistrationStateService>((m, a) =>
        {
            if (m.Name == nameof(IPLC_IgwRegistrationStateService.SavePendingAsync))
            {
                lock (Saves)
                {
                    Saves.Add(((long)a[0], (string)a[1]));
                }
                return Task.CompletedTask;
            }
            if (m.Name == nameof(IPLC_IgwRegistrationStateService.ClearPendingAsync))
            {
                lock (Saves)
                {
                    Saves.Add(((long)a[0], null));
                }
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("unexpected " + m.Name);
        });

        public string Last(long projectId) => Saves.Last(s => s.ProjectId == projectId).Json;
    }

    private static Task RunAsync(long projectId, IgwRegistrationArgs args, StateRecorder state, List<IgwRegistrationArgs> requeued,
        Func<IgwRegistrationArgs, int, IgwRegistrationOutcome> outcome, List<IgwRegistrationArgs> rounds)
    {
        return PLC_IgwRegistrationJob.RunRoundsAsync(projectId, args,
            a =>
            {
                rounds.Add(a);
                return Task.FromResult(outcome(a, rounds.Count));
            },
            state.Service, NullLogger.Instance,
            (_, _, _) => { },
            a =>
            {
                requeued.Add(a);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask);
    }

    [Fact]
    public async Task Failed_outcome_after_merge_keeps_union_persisted_without_requeue_and_next_success_consumes_and_clears_it()
    {
        var pid = NewProjectId();
        var state = new StateRecorder();
        var requeued = new List<IgwRegistrationArgs>();
        var rounds = new List<IgwRegistrationArgs>();

        Assert.Equal(IgwEnqueueResult.Enqueued, PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1", "t1")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(pid, Args("DB1", "t1"), state, requeued,
            (a, round) =>
            {
                if (round == 1)
                {
                    // 第一轮执行中另一次上传并入待补跑
                    Assert.Equal(IgwEnqueueResult.MergedIntoPending, PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB2", "t2")));
                    return new IgwRegistrationOutcome(IgwRegistrationStatus.Succeeded, "ok");
                }
                return new IgwRegistrationOutcome(IgwRegistrationStatus.Failed, "IGW 不可达");
            }, rounds));

        Assert.Contains("IGW 不可达", ex.Message);
        Assert.Equal(2, rounds.Count);
        Assert.Empty(requeued); // 不立即重新入队（持续失败时不空转）
        var persisted = IgwPendingArgsCodec.Deserialize(state.Last(pid));
        Assert.NotNull(persisted); // 记录保留，重启后启动恢复可入队
        Assert.Equal(new[] { "DB1", "DB2" }, persisted.ChangedBlocks.OrderBy(x => x));

        // 下一次触发（上传 DB3）：开始时并入结转范围，成功后清除记录
        rounds.Clear();
        Assert.Equal(IgwEnqueueResult.Enqueued, PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB3", "t3")));
        await RunAsync(pid, Args("DB3", "t3"), state, requeued,
            (_, _) => new IgwRegistrationOutcome(IgwRegistrationStatus.Succeeded, "ok"), rounds);

        var single = Assert.Single(rounds);
        Assert.Equal(new[] { "DB1", "DB2", "DB3" }, single.ChangedBlocks.OrderBy(x => x));
        Assert.Contains("t1", single.AllTaskIds);
        Assert.Null(state.Last(pid));
        Assert.Empty(requeued);
        Assert.Null(PLC_IgwRegistrationJob.Gate.GetUnfinishedExtras(pid));
    }

    [Fact]
    public async Task Failed_outcome_without_merge_carries_own_scope_and_repeated_failures_do_not_requeue()
    {
        var pid = NewProjectId();
        var state = new StateRecorder();
        var requeued = new List<IgwRegistrationArgs>();
        var rounds = new List<IgwRegistrationArgs>();
        var failed = new IgwRegistrationOutcome(IgwRegistrationStatus.Failed, "IGW 版本过低");

        for (var i = 1; i <= 3; i++)
        {
            var args = Args("DB" + i);
            PLC_IgwRegistrationJob.Gate.Reserve(pid, args);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(pid, args, state, requeued, (_, _) => failed, rounds));
        }

        Assert.Equal(3, rounds.Count); // 每次触发只跑一轮，没有自旋
        Assert.Equal(new[] { "DB1", "DB2", "DB3" }, rounds[2].ChangedBlocks.OrderBy(x => x));
        Assert.Empty(requeued);
        Assert.Equal(new[] { "DB1", "DB2", "DB3" }, IgwPendingArgsCodec.Deserialize(state.Last(pid)).ChangedBlocks.OrderBy(x => x));
    }

    [Fact]
    public async Task Skipped_outcome_does_not_carry()
    {
        var pid = NewProjectId();
        var state = new StateRecorder();
        var requeued = new List<IgwRegistrationArgs>();
        var rounds = new List<IgwRegistrationArgs>();

        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1"));
        await RunAsync(pid, Args("DB1"), state, requeued,
            (_, _) => new IgwRegistrationOutcome(IgwRegistrationStatus.Skipped, "配置缺失"), rounds);

        Assert.Null(PLC_IgwRegistrationJob.Gate.GetUnfinishedExtras(pid));
        Assert.DoesNotContain(state.Saves, s => s.ProjectId == pid && s.Json != null);
    }

    [Fact]
    public async Task Exception_path_requeues_carried_scope_too()
    {
        var pid = NewProjectId();
        var state = new StateRecorder();
        var requeued = new List<IgwRegistrationArgs>();
        var rounds = new List<IgwRegistrationArgs>();

        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(pid, Args("DB1"), state, requeued,
            (_, _) => new IgwRegistrationOutcome(IgwRegistrationStatus.Failed, "x"), rounds));

        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB2"));
        await Assert.ThrowsAsync<TimeoutException>(() => RunAsync(pid, Args("DB2"), state, requeued,
            (_, _) => throw new TimeoutException("boom"), rounds));

        Assert.Equal(new[] { "DB1" }, Assert.Single(requeued).ChangedBlocks);
    }

    [Fact]
    public void Carry_or_merge_goes_to_pending_when_reserved_and_to_carried_otherwise()
    {
        var gate = new IgwRegistrationQueueGate();
        gate.CarryOrMerge(1, Args("DB1"));
        Assert.Equal(new[] { "DB1" }, gate.GetUnfinishedExtras(1).ChangedBlocks);

        gate.Reserve(2, Args("DB0"));
        gate.CarryOrMerge(2, Args("DB2"));
        Assert.True(gate.TryStart(2, Args("DB0")));
        Assert.Null(gate.AdoptCarried(2));
        Assert.Equal(new[] { "DB0", "DB2" }, gate.CompleteOrTakePending(2, Args("DB0")).ChangedBlocks.OrderBy(x => x));

        Assert.True(gate.TryStart(1, Args("DB9")));
        Assert.Equal(new[] { "DB1" }, gate.AdoptCarried(1).ChangedBlocks);
        Assert.Equal(new[] { "DB1" }, gate.GetUnfinishedExtras(1).ChangedBlocks); // 在飞，完成前仍未完成
        Assert.Null(gate.CompleteOrTakePending(1, Args("DB9")));
        Assert.Null(gate.GetUnfinishedExtras(1));
    }

    [Fact]
    public async Task Recovery_hands_scope_to_an_already_queued_job_instead_of_enqueuing()
    {
        var rows = new List<IgwRegistrationStateRow>
        {
            new() { ProjectId = 21, PendingArgsJson = IgwPendingArgsCodec.Serialize(Args("DB7")), PendingQueue = "local_x" },
            new() { ProjectId = 22, PendingArgsJson = IgwPendingArgsCodec.Serialize(Args("DB8")), PendingQueue = "local_x" },
        };
        var saves = new List<(long ProjectId, string Json)>();
        var service = Fake.Create<IPLC_IgwRegistrationStateService>((m, a) =>
        {
            if (m.Name == nameof(IPLC_IgwRegistrationStateService.ListPendingAsync))
            {
                return Task.FromResult<IReadOnlyList<IgwRegistrationStateRow>>(rows);
            }
            if (m.Name == nameof(IPLC_IgwRegistrationStateService.SavePendingAsync))
            {
                saves.Add(((long)a[0], (string)a[1]));
                return Task.CompletedTask;
            }
            if (m.Name == nameof(IPLC_IgwRegistrationStateService.ClearPendingAsync))
            {
                saves.Add(((long)a[0], null));
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("unexpected " + m.Name);
        });
        var enqueued = new List<long>();
        var handed = new List<(long ProjectId, IgwRegistrationArgs Args)>();

        var recovered = await IgwPendingRegistrationRecovery.RecoverAsync(service, "local_x", (pid, _) =>
            {
                enqueued.Add(pid);
                return Task.FromResult(IgwEnqueueResult.Enqueued);
            }, NullLogger.Instance,
            hasQueuedJob: pid => pid == 21,
            handToQueuedJob: (pid, args) => handed.Add((pid, args)));

        Assert.Equal(2, recovered);
        Assert.Equal(new[] { 22L }, enqueued);
        Assert.Equal(new[] { "DB7" }, Assert.Single(handed).Args.ChangedBlocks);
        Assert.DoesNotContain(saves, s => s.ProjectId == 21); // 交给排队任务：记录保留到它成功后清除
        Assert.Contains((22L, (string)null), saves);
    }
}
