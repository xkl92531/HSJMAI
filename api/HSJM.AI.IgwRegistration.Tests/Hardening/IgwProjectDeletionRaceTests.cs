using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>
/// M-4 竞态（final fix round）：删项目清理与执行中的注册互不打架——
/// 清理任务在注册占位期间推迟重排；注册在写台账 / 登记身份前复查项目是否已删除，已删除则两者都不写。
/// </summary>
public class IgwProjectDeletionRaceTests
{
    private static IgwRegistrationArgs Args(params string[] blocks) =>
        new(blocks, Array.Empty<string>(), false, false, null);

    private static MemoryRegistrationStore PopulatedStore()
    {
        var store = new MemoryRegistrationStore
        {
            ChannelName = "线体A_PLC1",
            Data = new IgwRegistrationData(new[] { "DB1" }, new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0) },
                new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        return store;
    }

    [Fact]
    public void Gate_reports_a_project_occupied_from_reservation_until_completion()
    {
        var gate = new IgwRegistrationQueueGate();
        const long pid = 991001;

        Assert.False(gate.IsOccupied(pid));
        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(pid, Args("DB1")));
        Assert.True(gate.IsOccupied(pid));   // 已入队、尚未开始执行
        Assert.True(gate.TryStart(pid, Args("DB1")));
        Assert.True(gate.IsOccupied(pid));   // 执行中
        Assert.Null(gate.CompleteOrTakePending(pid, Args("DB1")));
        Assert.False(gate.IsOccupied(pid));  // 已释放
    }

    [Fact]
    public void Stale_reservation_is_not_reported_as_occupied()
    {
        var now = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var gate = new IgwRegistrationQueueGate(TimeSpan.FromMinutes(30), () => now);
        const long pid = 991002;
        gate.Reserve(pid, Args("DB1"));

        Assert.True(gate.IsOccupied(pid));
        now = now.AddMinutes(31);
        Assert.False(gate.IsOccupied(pid));
    }

    [Theory]
    [InlineData(true, 0, true)]                                            // 注册在飞 → 推迟
    [InlineData(true, PLC_IgwProjectCleanupJob.MaxDeferrals - 1, true)]
    [InlineData(true, PLC_IgwProjectCleanupJob.MaxDeferrals, false)]       // 推迟次数用完 → 照常清理
    [InlineData(false, 0, false)]                                          // 无注册在飞 → 照常清理
    public void Cleanup_defers_only_while_a_registration_is_in_flight(bool inFlight, int deferrals, bool expected) =>
        Assert.Equal(expected, PLC_IgwProjectCleanupJob.ShouldDefer(inFlight, deferrals));

    [Fact]
    public async Task Project_deleted_during_registration_skips_ledger_and_identity_writes()
    {
        var url = UniqueUrl();
        var store = PopulatedStore();
        store.ProjectRemoved = true; // 本次注册执行期间项目被删除
        var state = new MemoryProjectStateStore();
        var igw = new FakeIgwServer();

        var outcome = await Runner(igw, store, state: state).RunAsync(Project(url), Args("DB1"), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("项目已删除", outcome.Detail);
        Assert.Equal(0, store.LedgerWrites);
        Assert.Empty(state.SavedIdentities);
    }

    [Fact]
    public async Task Live_project_still_writes_ledger_and_identity()
    {
        var url = UniqueUrl();
        var store = PopulatedStore();
        var state = new MemoryProjectStateStore();

        var outcome = await Runner(new FakeIgwServer(), store, state: state).RunAsync(Project(url), Args("DB1"), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.DoesNotContain("项目已删除", outcome.Detail);
        Assert.Equal(1, store.LedgerWrites);
        Assert.Single(state.SavedIdentities);
        Assert.Equal(1, store.ProjectRemovalChecks); // 每次运行只复查一次
    }

    /// <summary>
    /// Final fix round：复查项目是否已删除本身失败时，原来只写日志（页面上看不出来）。
    /// 改为同其它降级说明一样追加进结果详情；按未删除处理，照常写台账 / 登记身份。
    /// </summary>
    [Fact]
    public async Task Project_removal_recheck_failure_is_noted_in_detail_and_treated_as_not_removed()
    {
        var url = UniqueUrl();
        var store = PopulatedStore();
        store.ThrowOnProjectRemovedCheck = true;
        var state = new MemoryProjectStateStore();

        var outcome = await Runner(new FakeIgwServer(), store, state: state).RunAsync(Project(url), Args("DB1"), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("复查项目是否已删除失败", outcome.Detail);
        Assert.Equal(1, store.LedgerWrites);
        Assert.Single(state.SavedIdentities);
    }
}
