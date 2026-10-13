using Hangfire.Common;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>
/// M-1 fix round 2：待补跑记录归属本机队列——他机记录只并入不覆盖、清除只清本机队列的记录；
/// 异常路径另行入队失败时范围回到闸门；启动恢复清除走落库锁；排队任务只算 Enqueued/Processing；空结果按 Failed 上报。
/// </summary>
public class IgwPendingOwnershipTests
{
    private const string OtherQueue = "local_other_machine";

    private static long _nextProjectId = 890_000;

    private static long NewProjectId() => Interlocked.Increment(ref _nextProjectId);

    private static IgwRegistrationArgs Args(string block, string taskId = null) =>
        new(new[] { block }, Array.Empty<string>(), false, false, taskId);

    private static string[] Blocks(string json) => IgwPendingArgsCodec.Deserialize(json).ChangedBlocks.OrderBy(x => x).ToArray();

    [Fact]
    public void Resolve_write_overwrites_own_record_unions_into_other_owner_and_adopts_orphans()
    {
        var mine = IgwPendingArgsCodec.Serialize(Args("DB2"));
        var theirs = IgwPendingArgsCodec.Serialize(Args("DB1"));

        Assert.Equal((mine, "local_me"), IgwPendingRecordRules.ResolveWrite(null, null, mine, "local_me", IgwPendingArgsCodec.Merge));
        Assert.Equal((mine, "local_me"), IgwPendingRecordRules.ResolveWrite(theirs, "local_me", mine, "local_me", IgwPendingArgsCodec.Merge));

        var merged = IgwPendingRecordRules.ResolveWrite(theirs, OtherQueue, mine, "local_me", IgwPendingArgsCodec.Merge);
        Assert.Equal(OtherQueue, merged.Queue); // 保留原所属队列，由其所属机器恢复
        Assert.Equal(new[] { "DB1", "DB2" }, Blocks(merged.Json));

        var orphan = IgwPendingRecordRules.ResolveWrite(theirs, null, mine, "local_me", IgwPendingArgsCodec.Merge);
        Assert.Equal("local_me", orphan.Queue);
        Assert.Equal(new[] { "DB1", "DB2" }, Blocks(orphan.Json));
    }

    [Fact]
    public void Codec_merge_unions_and_tolerates_invalid_existing_json()
    {
        var merged = IgwPendingArgsCodec.Merge(
            IgwPendingArgsCodec.Serialize(new IgwRegistrationArgs(new[] { "DB1" }, new[] { "DB9" }, true, false, null)),
            IgwPendingArgsCodec.Serialize(new IgwRegistrationArgs(new[] { "DB2" }, Array.Empty<string>(), false, true, null)));
        var args = IgwPendingArgsCodec.Deserialize(merged);
        Assert.Equal(new[] { "DB1", "DB2" }, args.ChangedBlocks.OrderBy(x => x));
        Assert.Equal(new[] { "DB9" }, args.DeletedBlocks);
        Assert.True(args.IoRefreshed);
        Assert.True(args.Full);

        var mine = IgwPendingArgsCodec.Serialize(Args("DB2"));
        Assert.Equal(new[] { "DB2" }, Blocks(IgwPendingArgsCodec.Merge("{broken", mine)));
    }

    [Fact]
    public void Clear_sql_only_clears_the_given_queue()
    {
        Assert.Contains("WHERE projectid = @Id AND pendingqueue = @Queue", PLC_IgwRegistrationStateService.ClearPendingSql);
        Assert.Contains("pendingargsjson = NULL", PLC_IgwRegistrationStateService.ClearPendingSql);
    }

    [Fact]
    public async Task Merge_into_other_machine_record_keeps_union_under_its_owner_and_our_clear_leaves_it()
    {
        var pid = NewProjectId();
        var store = new MemoryPendingStateService();
        store.Records[pid] = (IgwPendingArgsCodec.Serialize(Args("DB0")), OtherQueue);
        var rounds = new List<IgwRegistrationArgs>();

        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1"));
        await PLC_IgwRegistrationJob.RunRoundsAsync(pid, Args("DB1"),
            a =>
            {
                rounds.Add(a);
                if (rounds.Count == 1)
                {
                    Assert.Equal(IgwEnqueueResult.MergedIntoPending, PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB2")));
                }
                else
                {
                    // 补跑轮次执行中：在飞的 DB2 已并入他机记录，所属队列不变
                    Assert.Equal(OtherQueue, store.Records[pid].Queue);
                    Assert.Equal(new[] { "DB0", "DB2" }, Blocks(store.Records[pid].Json));
                }
                return Task.FromResult(new IgwRegistrationOutcome(IgwRegistrationStatus.Succeeded, "ok"));
            },
            store, NullLogger.Instance, (_, _, _) => { }, _ => Task.CompletedTask, _ => Task.CompletedTask);

        Assert.Equal(2, rounds.Count);
        Assert.Contains((pid, MachineQueue.BuildCurrent()), store.Clears); // 我方清除带本机队列条件
        var record = store.Records[pid]; // 他机记录不被我方清除
        Assert.Equal(OtherQueue, record.Queue);
        Assert.Equal(new[] { "DB0", "DB2" }, Blocks(record.Json));
    }

    [Fact]
    public async Task Requeue_failure_returns_extras_to_gate_and_next_successful_run_consumes_them_before_clearing()
    {
        var pid = NewProjectId();
        var store = new MemoryPendingStateService();
        var rounds = new List<IgwRegistrationArgs>();
        var ours = MachineQueue.BuildCurrent();

        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1"));
        await Assert.ThrowsAsync<TimeoutException>(() => PLC_IgwRegistrationJob.RunRoundsAsync(pid, Args("DB1"),
            a =>
            {
                rounds.Add(a);
                if (rounds.Count == 1)
                {
                    PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB2"));
                    return Task.FromResult(new IgwRegistrationOutcome(IgwRegistrationStatus.Succeeded, "ok"));
                }
                throw new TimeoutException("boom");
            },
            store, NullLogger.Instance, (_, _, _) => { },
            extras => PLC_IgwRegistrationJob.RequeueExtrasAsync(pid, extras,
                _ => throw new InvalidOperationException("Hangfire 不可用"), store, NullLogger.Instance),
            _ => Task.CompletedTask));

        // 入队失败：范围回到闸门并落库（本机队列），不丢
        Assert.Equal(new[] { "DB2" }, PLC_IgwRegistrationJob.Gate.GetUnfinishedExtras(pid).ChangedBlocks);
        Assert.Equal(ours, store.Records[pid].Queue);
        Assert.Equal(new[] { "DB2" }, Blocks(store.Records[pid].Json));

        rounds.Clear();
        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB3"));
        await PLC_IgwRegistrationJob.RunRoundsAsync(pid, Args("DB3"),
            a =>
            {
                rounds.Add(a);
                Assert.True(store.Records.ContainsKey(pid)); // 执行完成前记录保留
                return Task.FromResult(new IgwRegistrationOutcome(IgwRegistrationStatus.Succeeded, "ok"));
            },
            store, NullLogger.Instance, (_, _, _) => { }, _ => Task.CompletedTask, _ => Task.CompletedTask);

        Assert.Equal(new[] { "DB2", "DB3" }, Assert.Single(rounds).ChangedBlocks.OrderBy(x => x));
        Assert.False(store.Records.ContainsKey(pid));
        Assert.Null(PLC_IgwRegistrationJob.Gate.GetUnfinishedExtras(pid));
    }

    [Fact]
    public async Task Requeue_success_clears_the_record()
    {
        var pid = NewProjectId();
        var store = new MemoryPendingStateService();
        var enqueued = new List<IgwRegistrationArgs>();

        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1"));
        await Assert.ThrowsAsync<TimeoutException>(() => PLC_IgwRegistrationJob.RunRoundsAsync(pid, Args("DB1"),
            async a =>
            {
                Assert.Equal(IgwEnqueueResult.MergedIntoPending, await PLC_IgwRegistrationJob.EnqueueAsync(pid, new[] { "DB2" },
                    Array.Empty<string>(), false, false, null, store, NullLogger.Instance));
                Assert.True(store.Records.ContainsKey(pid)); // 并入即落库
                throw new TimeoutException("boom");
            },
            store, NullLogger.Instance, (_, _, _) => { },
            extras => PLC_IgwRegistrationJob.RequeueExtrasAsync(pid, extras, a =>
            {
                enqueued.Add(a);
                return Task.FromResult(IgwEnqueueResult.Enqueued);
            }, store, NullLogger.Instance),
            _ => Task.CompletedTask));

        Assert.Equal(new[] { "DB2" }, Assert.Single(enqueued).ChangedBlocks);
        Assert.False(store.Records.ContainsKey(pid));
    }

    [Fact]
    public async Task Recovery_clears_through_the_persist_callback_and_honours_cancellation()
    {
        var store = new MemoryPendingStateService();
        store.Records[31] = (IgwPendingArgsCodec.Serialize(Args("DB5")), "local_x");
        store.Records[32] = ("{broken", "local_x");
        var persisted = new List<long>();

        var recovered = await IgwPendingRegistrationRecovery.RecoverAsync(store, "local_x",
            (_, _) => Task.FromResult(IgwEnqueueResult.Enqueued), NullLogger.Instance,
            persistExtras: pid =>
            {
                persisted.Add(pid);
                return Task.CompletedTask;
            });

        Assert.Equal(1, recovered);
        Assert.Equal(new[] { 31L }, persisted); // 入队后清除交给落库回调（项目锁内写最新快照），不直接清
        Assert.True(store.Records.ContainsKey(31));
        Assert.False(store.Records.ContainsKey(32)); // 无法解析 → 条件清除本队列记录
        Assert.Contains((32L, "local_x"), store.Clears);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var enqueued = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IgwPendingRegistrationRecovery.RecoverAsync(store, "local_x",
            (_, _) =>
            {
                enqueued++;
                return Task.FromResult(IgwEnqueueResult.Enqueued);
            }, NullLogger.Instance, cancellationToken: cts.Token));
        Assert.Equal(0, enqueued);
    }

    [Fact]
    public async Task Persist_after_recovery_marks_persisted_so_empty_extras_clear_the_own_record()
    {
        var pid = NewProjectId();
        var store = new MemoryPendingStateService();
        store.Records[pid] = (IgwPendingArgsCodec.Serialize(Args("DB5")), MachineQueue.BuildCurrent());

        await PLC_IgwRegistrationJob.PersistExtrasAsync(pid, store, NullLogger.Instance, markPersisted: true);

        Assert.False(store.Records.ContainsKey(pid));
    }

    [Fact]
    public void Queued_projects_count_only_active_jobs_of_this_type()
    {
        var active = Job.FromExpression<PLC_IgwRegistrationJob>(x => x.ExecuteAsync(41, null, null, false, false, null));
        var inactive = Job.FromExpression<PLC_IgwRegistrationJob>(x => x.ExecuteAsync(42, null, null, false, false, null));
        var other = Job.FromExpression<IgwPendingOwnershipTests>(x => x.Queued_projects_count_only_active_jobs_of_this_type());

        var result = PLC_IgwRegistrationJob.CollectQueuedProjects(new[] { (active, true), (inactive, false), (other, true), ((Job)null, true) });

        Assert.Equal(new[] { 41L }, result);
    }

    [Theory]
    [InlineData("Enqueued", true)]
    [InlineData("Processing", true)]
    [InlineData("Deleted", false)]
    [InlineData("Succeeded", false)]
    [InlineData(null, false)]
    public void Fetched_job_is_active_only_when_enqueued_or_processing(string state, bool expected)
    {
        Assert.Equal(expected, PLC_IgwRegistrationJob.IsActiveState(state));
    }

    [Fact]
    public async Task Null_outcome_is_reported_as_failed()
    {
        var pid = NewProjectId();
        var reports = new List<string>();
        PLC_IgwRegistrationJob.Gate.Reserve(pid, Args("DB1"));

        await PLC_IgwRegistrationJob.RunRoundsAsync(pid, Args("DB1"), _ => Task.FromResult<IgwRegistrationOutcome>(null),
            new MemoryPendingStateService(), NullLogger.Instance, (_, status, _) => reports.Add(status),
            _ => Task.CompletedTask, _ => Task.CompletedTask);

        Assert.Equal(new[] { "Running", "Failed" }, reports);
    }

    [Fact]
    public async Task Hosted_service_stop_cancels_the_whole_recovery()
    {
        var service = new IgwPendingRegistrationRecoveryService(null, NullLogger<IgwPendingRegistrationRecoveryService>.Instance,
            TimeSpan.FromMinutes(10));
        await service.StartAsync(CancellationToken.None);

        var stop = service.StopAsync(CancellationToken.None);

        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        await stop;
    }

    [Fact]
    public async Task Hosted_service_stop_twice_after_disposal_is_safe()
    {
        var service = new IgwPendingRegistrationRecoveryService(null, NullLogger<IgwPendingRegistrationRecoveryService>.Instance,
            TimeSpan.FromMinutes(10));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var second = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));

        Assert.Null(second);
    }
}
