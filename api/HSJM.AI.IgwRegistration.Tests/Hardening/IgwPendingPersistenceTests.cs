using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>M-1：闸门「未完成的额外范围」、待补跑编解码、启动恢复。</summary>
public class IgwPendingPersistenceTests
{
    private static IgwRegistrationArgs Args(string block, string taskId = null) =>
        new(new[] { block }, Array.Empty<string>(), false, false, taskId);

    [Fact]
    public void Unfinished_extras_cover_the_in_flight_rerun_until_it_completes()
    {
        var gate = new IgwRegistrationQueueGate();
        gate.Reserve(1, Args("DB1"));
        Assert.True(gate.TryStart(1, Args("DB1")));
        Assert.Null(gate.GetUnfinishedExtras(1));

        gate.Reserve(1, Args("DB2"));
        Assert.Equal(new[] { "DB2" }, gate.GetUnfinishedExtras(1).ChangedBlocks);

        var rerun = gate.CompleteOrTakePending(1, Args("DB1"));
        Assert.Equal(new[] { "DB1", "DB2" }, rerun.ChangedBlocks.OrderBy(x => x));
        Assert.Equal(new[] { "DB2" }, gate.GetUnfinishedExtras(1).ChangedBlocks); // 补跑轮次执行中，DB2 仍未完成

        gate.Reserve(1, Args("DB3"));
        Assert.Equal(new[] { "DB2", "DB3" }, gate.GetUnfinishedExtras(1).ChangedBlocks.OrderBy(x => x));

        var second = gate.CompleteOrTakePending(1, rerun);
        Assert.Contains("DB3", second.ChangedBlocks);
        Assert.Equal(new[] { "DB3" }, gate.GetUnfinishedExtras(1).ChangedBlocks);

        Assert.Null(gate.CompleteOrTakePending(1, second));
        Assert.Null(gate.GetUnfinishedExtras(1));
    }

    [Fact]
    public void Release_and_take_extras_returns_in_flight_and_pending_and_frees_the_gate()
    {
        var gate = new IgwRegistrationQueueGate();
        gate.Reserve(1, Args("DB1"));
        gate.TryStart(1, Args("DB1"));
        gate.Reserve(1, Args("DB2"));
        gate.CompleteOrTakePending(1, Args("DB1"));
        gate.Reserve(1, Args("DB3"));

        var extras = gate.ReleaseAndTakeExtras(1);

        Assert.Equal(new[] { "DB2", "DB3" }, extras.ChangedBlocks.OrderBy(x => x));
        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(1, Args("DB4")));
    }

    [Fact]
    public void Codec_round_trips_scope_without_task_ids()
    {
        var args = new IgwRegistrationArgs(new[] { "DB1", "DB2" }, new[] { "DB9" }, true, false, "t1");

        var decoded = IgwPendingArgsCodec.Deserialize(IgwPendingArgsCodec.Serialize(args));

        Assert.Equal(new[] { "DB1", "DB2" }, decoded.ChangedBlocks);
        Assert.Equal(new[] { "DB9" }, decoded.DeletedBlocks);
        Assert.True(decoded.IoRefreshed);
        Assert.False(decoded.Full);
        Assert.Null(decoded.TaskId);
        Assert.Null(IgwPendingArgsCodec.Serialize(null));
        Assert.Null(IgwPendingArgsCodec.Deserialize("not json"));
        Assert.Null(IgwPendingArgsCodec.Deserialize(" "));
    }

    [Fact]
    public async Task Recovery_enqueues_persisted_scopes_and_clears_them()
    {
        var rows = new List<IgwRegistrationStateRow>
        {
            new() { ProjectId = 11, PendingArgsJson = IgwPendingArgsCodec.Serialize(Args("DB5")), PendingQueue = "local_x" },
            new() { ProjectId = 12, PendingArgsJson = "{broken", PendingQueue = "local_x" },
            new() { ProjectId = 13, PendingArgsJson = IgwPendingArgsCodec.Serialize(Args("DB6")), PendingQueue = "local_x" },
        };
        var saves = new List<(long ProjectId, string Json)>();
        string queriedQueue = null;
        var service = Fake.Create<IPLC_IgwRegistrationStateService>((m, a) =>
        {
            if (m.Name == nameof(IPLC_IgwRegistrationStateService.ListPendingAsync))
            {
                queriedQueue = (string)a[0];
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
        var enqueued = new List<(long ProjectId, IgwRegistrationArgs Args)>();

        var recovered = await IgwPendingRegistrationRecovery.RecoverAsync(service, "local_x", (pid, args) =>
        {
            if (pid == 13)
            {
                throw new InvalidOperationException("Hangfire 不可用");
            }
            enqueued.Add((pid, args));
            return Task.FromResult(IgwEnqueueResult.Enqueued);
        }, NullLogger.Instance);

        Assert.Equal("local_x", queriedQueue);
        Assert.Equal(1, recovered);
        Assert.Equal(new[] { "DB5" }, Assert.Single(enqueued).Args.ChangedBlocks);
        Assert.Contains((11L, (string)null), saves); // 已入队 → 清除
        Assert.Contains((12L, (string)null), saves); // 无法解析 → 清除
        Assert.DoesNotContain(saves, s => s.ProjectId == 13); // 入队失败 → 保留，下次启动再试
    }
}
