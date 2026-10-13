using System.Net;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>I-3：注册身份（IGW 基址 / PLC IP / 通道名）变化 → 清旧位置 + 强制全量；同 IP / 同通道冲突 fail safe。</summary>
public class IgwRegistrationIdentityTests
{
    private const string Channel = "线体A_PLC1";
    private const string NewIp = "10.0.0.2";

    private static MemoryRegistrationStore PopulatedStore(string channel = Channel)
    {
        var store = new MemoryRegistrationStore
        {
            ChannelName = channel,
            Data = new IgwRegistrationData(new[] { "DB1", "DB2" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0), SymbolicRow("DB2", "Motor.Speed") }, new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7Plus", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.Io, "IO", "S7", IgwLedgerStatus.Registered);
        return store;
    }

    private static IgwRegistrationArgs ChangeDb2() => new(new[] { "DB2" }, Array.Empty<string>(), false, false, null);

    [Fact]
    public async Task Ip_change_removes_old_ip_devices_in_same_channel_and_forces_full()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        var s7 = igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        var s7Plus = igw.AddDevice(Channel, "S7Plus", "S1500_" + Ip, Ip + ":102");
        var graphDevice = igw.AddDevice(Channel, "GRAPH", "S7Plus_" + Ip, Ip + ":102");
        igw.AddDevice(Channel, "其它", "OpcUa_" + Ip, Ip + ":4840");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, Channel);
        var store = PopulatedStore();
        var graph = new List<(long, string)>();

        var outcome = await Runner(igw, store, graphRequests: graph, state: state)
            .RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("注册身份变化", outcome.Detail);
        Assert.Equal(new[] { s7.Id, s7Plus.Id, graphDevice.Id }.OrderBy(x => x), igw.Deleted.Where(d => d.Kind == "device").Select(d => d.Id).OrderBy(x => x));
        Assert.DoesNotContain(igw.Deleted, d => d.Kind == "channel");
        Assert.Null(Assert.Single(store.RowBlockRequests)); // 强制全量
        Assert.Single(graph);                              // 删掉了 GRAPH 设备 → 请求 GRAPH 重注册
        Assert.Equal(NewIp, state.State.RegisteredPlcIp);
    }

    [Fact]
    public async Task Channel_change_deletes_old_channel_once_empty()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice("旧通道", "S7", "S7_" + Ip, Ip + ":102");
        var oldChannelId = igw.Channels.Single(c => c.Name == "旧通道").Id;
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains(("channel", oldChannelId), igw.Deleted);
        Assert.Equal("新通道", state.State.RegisteredChannel);
    }

    [Fact]
    public async Task Devices_without_bulk_marker_are_reported_not_deleted()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102", managedBy: null);

        var outcome = await Runner(igw, PopulatedStore(), state: MemoryProjectStateStore.WithIdentity(url, Ip, Channel))
            .RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status); // 旧 IP 的逻辑名与新身份不同 → 只提示
        Assert.Empty(igw.Deleted);
        Assert.Contains("跳过无批量标记设备 1 个", outcome.Detail);
    }

    [Fact]
    public async Task Cleanup_failure_on_same_igw_fails_before_any_request()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer { FailDeletes = true };
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, Channel);

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("清理旧位置失败", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Equal(Ip, state.State.RegisteredPlcIp); // 身份不变，下次继续清理
    }

    [Fact]
    public async Task Unreachable_old_igw_is_skipped_with_a_note()
    {
        var oldUrl = "http://old-igw.local:48020";
        var igw = new FakeIgwServer();
        igw.UnreachableHosts.Add("old-igw.local");
        var state = MemoryProjectStateStore.WithIdentity(oldUrl, Ip, Channel);
        var newUrl = UniqueUrl();

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(newUrl), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("旧 IGW", outcome.Detail);
        Assert.Equal(IgwRegistrationIdentity.NormalizeIgwBaseUrl(newUrl), state.State.RegisteredIotBaseUrl);
    }

    [Fact]
    public async Task Registered_peer_with_same_ip_on_same_igw_blocks_registration()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        var state = new MemoryProjectStateStore();
        state.Peers.Add(new IgwPeerProjectRow
        {
            ProjectId = 7, ProjectName = "旧项目", IotUrl = url, PlcIp = Ip,
            RegisteredIdentity = IgwRegistrationIdentity.Create(url, Ip, "别的通道"),
        });

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("注册身份冲突", outcome.Detail);
        Assert.Contains("旧项目(7)", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
    }

    [Fact]
    public async Task Unregistered_peer_with_same_ip_only_warns()
    {
        var url = UniqueUrl();
        var state = new MemoryProjectStateStore();
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 8, ProjectName = "副本", IotUrl = url, PlcIp = Ip });

        var outcome = await Runner(new FakeIgwServer(), PopulatedStore(), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("副本(8)", outcome.Detail);
        Assert.Single(state.SavedIdentities);
    }

    [Fact]
    public void Conflicts_detect_same_channel_on_same_igw()
    {
        var current = IgwRegistrationIdentity.Create("http://a:1", "10.0.0.1", "通道X");
        var result = IgwIdentityConflicts.Evaluate(current, new[]
        {
            new IgwPeerProjectRow { ProjectId = 2, ProjectName = "P2", RegisteredIdentity = IgwRegistrationIdentity.Create("http://A:1/", "10.0.0.9", "通道x") },
            new IgwPeerProjectRow { ProjectId = 3, ProjectName = "P3", RegisteredIdentity = IgwRegistrationIdentity.Create("http://b:1", "10.0.0.1", "通道X") },
        });

        Assert.Contains(result.Blocking, b => b.Contains("P2(2)") && b.Contains("通道"));
        Assert.DoesNotContain(result.Blocking, b => b.Contains("P3(3)"));
    }

    [Fact]
    public async Task Old_channel_registered_by_another_project_is_kept_even_when_empty()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice("旧通道", "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");
        state.Peers.Add(new IgwPeerProjectRow
        {
            ProjectId = 9, ProjectName = "邻居", IotUrl = url, PlcIp = "10.0.0.9",
            RegisteredIdentity = IgwRegistrationIdentity.Create(url, "10.0.0.9", "旧通道"),
        });

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Single(igw.Deleted, d => d.Kind == "device");
        Assert.DoesNotContain(igw.Deleted, d => d.Kind == "channel");
        Assert.Contains(igw.Channels, c => c.Name == "旧通道");
    }

    [Fact]
    public async Task Project_device_cleanup_never_touches_other_devices_or_non_empty_channel()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        var own = igw.AddDevice("共享通道", "S7Plus", "S1500_" + Ip, Ip + ":102");
        var unmanaged = igw.AddDevice("共享通道", "S7", "S7_" + Ip, Ip + ":102", managedBy: null);
        var otherIp = igw.AddDevice("共享通道", "别人S7", "S7_10.0.0.9", "10.0.0.9:102");
        var elsewhere = igw.AddDevice("另一通道", "S7Plus", "S1500_" + Ip, Ip + ":102");

        var cleanup = await Client(igw).RemoveProjectDevicesAsync(url, "共享通道", Ip, deleteChannelIfEmpty: true, CancellationToken.None);

        Assert.True(cleanup.ChannelFound);
        Assert.False(cleanup.ChannelDeleted);
        Assert.Equal(new[] { ("device", own.Id) }, igw.Deleted);
        Assert.Single(cleanup.Removed);
        Assert.Single(cleanup.SkippedUnmanaged);
        Assert.Contains(igw.Channels.Single(c => c.Name == "共享通道").Devices, d => d.Id == unmanaged.Id);
        Assert.Contains(igw.Channels.Single(c => c.Name == "共享通道").Devices, d => d.Id == otherIp.Id);
        Assert.Contains(igw.Channels.Single(c => c.Name == "另一通道").Devices, d => d.Id == elsewhere.Id);
    }

    [Fact]
    public async Task Project_device_cleanup_reports_missing_channel_without_error()
    {
        var igw = new FakeIgwServer();

        var cleanup = await Client(igw).RemoveProjectDevicesAsync(UniqueUrl(), "不存在", Ip, deleteChannelIfEmpty: true, CancellationToken.None);

        Assert.False(cleanup.ChannelFound);
        Assert.Empty(igw.Deleted);
    }

    [Fact]
    public async Task Unreachable_old_igw_with_identity_change_requests_graph_once()
    {
        var oldUrl = "http://old-igw.local:48020";
        var igw = new FakeIgwServer();
        igw.UnreachableHosts.Add("old-igw.local");
        var state = MemoryProjectStateStore.WithIdentity(oldUrl, Ip, Channel);
        var store = PopulatedStore();
        store.HasGraphTargets = true;
        store.Seed(IgwLedgerRootKind.Graph, "DB_GRAPH", "GRAPH", IgwLedgerStatus.Registered);
        var graph = new List<(long, string)>();

        var outcome = await Runner(igw, store, graphRequests: graph, state: state)
            .RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Single(graph);
        Assert.Contains("注册身份变化，新位置需重建 GRAPH 设备", outcome.Detail);
    }

    [Fact]
    public async Task Same_igw_unmarked_device_with_new_logical_name_fails_and_keeps_identity()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice("旧通道", "S7", "S7_" + Ip, Ip + ":102", managedBy: null); // 同 IP 换通道：新 S7 设备逻辑名也是 S7_{ip}
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("人工删除", outcome.Detail);
        Assert.Contains("S7_" + Ip, outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Empty(state.SavedIdentities);
        Assert.Equal("旧通道", state.State.RegisteredChannel);
    }

    [Fact]
    public async Task Reachable_old_igw_rejecting_deletes_fails_and_keeps_identity()
    {
        var oldUrl = UniqueUrl();
        var igw = new FakeIgwServer { FailDeletes = true };
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(oldUrl, Ip, Channel);

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("拒绝清理旧位置", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Equal(IgwRegistrationIdentity.NormalizeIgwBaseUrl(oldUrl), state.State.RegisteredIotBaseUrl);
    }

    [Fact]
    public async Task Hanging_old_igw_hits_cleanup_deadline_and_is_skipped_with_a_note()
    {
        var oldUrl = "http://hung-igw.local:48020";
        var igw = new FakeIgwServer();
        igw.HangingHosts.Add("hung-igw.local");
        var state = MemoryProjectStateStore.WithIdentity(oldUrl, Ip, Channel);
        var runner = Runner(igw, PopulatedStore(), state: state);
        runner.IdentityCleanupDeadline = TimeSpan.FromMilliseconds(200);

        var outcome = await runner.RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("不可达", outcome.Detail);
        Assert.Contains("未完成", outcome.Detail);
    }

    [Fact]
    public async Task Hanging_deletes_on_same_igw_hit_cleanup_deadline_and_fail()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer { HangDeletes = true };
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, Channel);
        var runner = Runner(igw, PopulatedStore(), state: state);
        runner.IdentityCleanupDeadline = TimeSpan.FromMilliseconds(200);

        var outcome = await runner.RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("清理旧位置失败", outcome.Detail);
        Assert.Contains("未完成", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Equal(Ip, state.State.RegisteredPlcIp);
    }

    [Fact]
    public async Task Conflict_appearing_during_registration_blocks_identity_save()
    {
        var url = UniqueUrl();
        var state = new MemoryProjectStateStore();
        state.LatePeers.Add(new IgwPeerProjectRow
        {
            ProjectId = 11, ProjectName = "并发项目", IotUrl = url, PlcIp = Ip,
            RegisteredIdentity = IgwRegistrationIdentity.Create(url, Ip, "别的通道"),
        });

        var outcome = await Runner(new FakeIgwServer(), PopulatedStore(), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("登记注册身份前发现冲突", outcome.Detail);
        Assert.Contains("并发项目(11)", outcome.Detail);
        Assert.Contains("已写入的数据留在 IGW 新位置", outcome.Detail);
        Assert.Empty(state.SavedIdentities);
    }

    [Fact]
    public async Task Unregistered_peer_configured_on_previous_location_keeps_old_devices()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, Channel);
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 12, ProjectName = "副本", IotUrl = url, PlcIp = Ip, ConfiguredChannel = Channel });

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Empty(igw.Deleted);
        Assert.Contains("未清理旧位置设备", outcome.Detail);
        Assert.Contains("副本(12)", outcome.Detail);
    }

    [Fact]
    public void Registered_elsewhere_but_configured_same_igw_and_ip_still_warns()
    {
        var current = IgwRegistrationIdentity.Create("http://a:1", "10.0.0.1", "通道X");
        var result = IgwIdentityConflicts.Evaluate(current, new[]
        {
            new IgwPeerProjectRow
            {
                ProjectId = 5, ProjectName = "P5", IotUrl = "http://a:1", PlcIp = "10.0.0.1",
                RegisteredIdentity = IgwRegistrationIdentity.Create("http://a:1", "10.0.0.5", "通道Y"),
            },
        });

        Assert.Empty(result.Blocking);
        Assert.Contains(result.Warnings, w => w.Contains("P5(5)"));
    }

    [Fact]
    public async Task Channel_change_with_same_ip_and_occupied_old_location_fails_for_manual_cleanup()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice("旧通道", "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 13, ProjectName = "副本", IotUrl = url, PlcIp = Ip, ConfiguredChannel = "旧通道" });

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("副本(13)", outcome.Detail);
        Assert.Contains("旧通道/S7（逻辑名 S7_" + Ip, outcome.Detail); // 列出仍残留的重名设备
        Assert.Contains("删除 / 合并重复的项目 副本(13)", outcome.Detail);
        Assert.Contains("人工删除通道 旧通道 中上述设备后重新注册", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Empty(igw.Deleted);
        Assert.Empty(state.SavedIdentities);
        Assert.Equal("旧通道", state.State.RegisteredChannel);
    }

    [Fact]
    public async Task Peer_with_unknown_configured_channel_on_previous_igw_and_ip_is_treated_as_occupant()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, Channel);
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 14, ProjectName = "列缺失", IotUrl = url, PlcIp = Ip, ConfiguredChannelUnknown = true });

        var outcome = await Runner(igw, PopulatedStore(), state: state).RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Empty(igw.Deleted);
        Assert.Contains("未清理旧位置设备", outcome.Detail);
        Assert.Contains("列缺失(14)", outcome.Detail);
    }

    [Fact]
    public void Peer_with_known_null_channel_is_not_an_occupant()
    {
        var previous = IgwRegistrationIdentity.Create("http://a:1", Ip, Channel);
        var peer = new IgwPeerProjectRow { ProjectId = 15, ProjectName = "P15", IotUrl = "http://a:1", PlcIp = Ip };

        Assert.Null(IgwIdentityConflicts.FindOccupantOfPreviousLocation(previous, new[] { peer }));
        peer.ConfiguredChannelUnknown = true;
        Assert.Same(peer, IgwIdentityConflicts.FindOccupantOfPreviousLocation(previous, new[] { peer }));
    }

    [Fact]
    public async Task Repeated_failing_runs_after_identity_change_request_graph_only_once()
    {
        var oldUrl = "http://old-igw-graph.local:48020";
        var now = new DateTime(2026, 9, 20, 12, 0, 0);
        var igw = new FakeIgwServer { BulkResponder = _ => (HttpStatusCode.InternalServerError, "{}") };
        igw.UnreachableHosts.Add("old-igw-graph.local");
        var state = MemoryProjectStateStore.WithIdentity(oldUrl, Ip, Channel);
        state.State.RegisteredAt = now.AddDays(-1);
        var store = PopulatedStore();
        store.HasGraphTargets = true;
        store.Seed(IgwLedgerRootKind.Graph, "DB_GRAPH", "GRAPH", IgwLedgerStatus.Registered);
        var graph = new List<(long, string)>();
        var url = UniqueUrl();

        var first = await Runner(igw, store, graphRequests: graph, state: state, now: () => now)
            .RunAsync(Project(url), ChangeDb2(), CancellationToken.None);
        Assert.Equal(IgwRegistrationStatus.Failed, first.Status);
        Assert.Empty(state.SavedIdentities);
        Assert.Single(graph);

        // 触发的 GRAPH 注册失败（写台账前）→ 记录失败时间
        await state.SaveGraphFailureAsync(ProjectId, now.AddMinutes(1), "GRAPH 失败");
        var second = await Runner(igw, store, graphRequests: graph, state: state, now: () => now.AddMinutes(2))
            .RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, second.Status);
        Assert.Single(graph);
        Assert.Contains("不自动重试", second.Detail);
    }

    [Theory]
    [InlineData(false)] // 旧通道还在，只是重名设备已被人工删除（只剩别的设备）
    [InlineData(true)]  // 旧通道已不存在
    public async Task Same_ip_occupied_old_location_without_remaining_devices_proceeds(bool channelGone)
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        if (!channelGone)
        {
            igw.AddDevice("旧通道", "其它", "OpcUa_" + Ip, Ip + ":4840");
        }
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 16, ProjectName = "副本", IotUrl = url, PlcIp = Ip, ConfiguredChannel = "旧通道" });

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Empty(igw.Deleted);
        Assert.NotEmpty(igw.BulkRequests);
        Assert.Contains("副本(16)", outcome.Detail);
        Assert.Contains("旧位置未做任何改动", outcome.Detail);
        Assert.Contains(channelGone ? "旧通道已不存在" : "旧通道中已无与新位置同名的逻辑设备", outcome.Detail);
        Assert.Equal("新通道", Assert.Single(state.SavedIdentities).ChannelName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_peer_channel_with_same_ip_depends_on_remaining_devices(bool remaining)
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        if (remaining)
        {
            igw.AddDevice("旧通道", "S7Plus", "S1500_" + Ip, Ip + ":102");
        }
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 17, ProjectName = "列缺失", IotUrl = url, PlcIp = Ip, ConfiguredChannelUnknown = true });

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Empty(igw.Deleted);
        Assert.Contains("列缺失(17)", outcome.Detail);
        if (remaining)
        {
            Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
            Assert.Contains("旧通道/S7Plus（逻辑名 S1500_" + Ip, outcome.Detail);
            Assert.Empty(igw.BulkRequests);
            Assert.Empty(state.SavedIdentities);
        }
        else
        {
            Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
            Assert.Contains("旧位置未做任何改动", outcome.Detail);
            Assert.Single(state.SavedIdentities);
        }
    }

    [Fact]
    public async Task Same_ip_occupied_old_location_tree_read_failure_fails()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer { FailTreeReads = true };
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, "旧通道");
        state.Peers.Add(new IgwPeerProjectRow { ProjectId = 18, ProjectName = "副本", IotUrl = url, PlcIp = Ip, ConfiguredChannel = "旧通道" });

        var outcome = await Runner(igw, PopulatedStore("新通道"), state: state).RunAsync(Project(url), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("读取 IGW 旧通道 旧通道 失败", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Empty(state.SavedIdentities);
    }

    /// <summary>
    /// 身份变化 + 台账里还有待清除的块（PendingClear / FailedPendingClear）：强制全量后每个块在两台设备上都重发，
    /// 打向新位置的清除组对新建设备是空操作（新设备上本来就没有这些组），台账行最终都回到 Registered。
    /// </summary>
    [Fact]
    public async Task Identity_change_with_pending_clear_rows_registers_everything_at_the_new_location()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102"); // 旧 IP 的设备，身份变化时被清理
        var state = MemoryProjectStateStore.WithIdentity(url, Ip, Channel);
        var store = PopulatedStore();
        store.Row("DB1").Status = IgwLedgerStatus.PendingClear;        // 内容已写入，只剩另一台设备的旧组待清除
        store.Row("DB2").Status = IgwLedgerStatus.FailedPendingClear;  // 内容没写全且有旧组待清除

        var outcome = await Runner(igw, store, state: state).RunAsync(Project(url, NewIp), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("注册身份变化", outcome.Detail);
        Assert.Null(Assert.Single(store.RowBlockRequests));                                      // 强制全量（两个块都重发）
        Assert.All(igw.BulkRequests, r => Assert.StartsWith(NewIp + ":", r.Device.Endpoint));    // 全部打向新位置
        Assert.All(new[] { "S7", "S7Plus" }, device => Assert.Contains(igw.BulkRequestsFor(device).SelectMany(r => r.ReplaceGroups),
            g => g.SequenceEqual(new[] { "DB1" })));                                             // 清除组也重发（新设备上是空操作）
        Assert.All(new[] { "DB1", "DB2", "IO" }, root => Assert.Equal(IgwLedgerStatus.Registered, store.Row(root).Status));
        Assert.Equal(NewIp, Assert.Single(state.SavedIdentities).PlcIp);
    }

    [Fact]
    public async Task Identity_change_completed_in_cooldown_still_requests_graph_once()
    {
        var oldUrl = "http://old-igw-graph2.local:48020";
        var now = new DateTime(2026, 9, 20, 12, 0, 0);
        var igw = new FakeIgwServer();
        igw.UnreachableHosts.Add("old-igw-graph2.local");
        var state = MemoryProjectStateStore.WithIdentity(oldUrl, Ip, Channel);
        state.State.RegisteredAt = now.AddDays(-1);
        state.State.GraphFailedAt = now.AddMinutes(-1); // 身份变化之后的 GRAPH 失败，仍在冷却期内
        state.State.GraphFailure = "GRAPH 失败";
        var store = PopulatedStore();
        store.HasGraphTargets = true;
        store.Seed(IgwLedgerRootKind.Graph, "DB_GRAPH", "GRAPH", IgwLedgerStatus.Registered);
        var graph = new List<(long, string)>();

        var outcome = await Runner(igw, store, graphRequests: graph, state: state, now: () => now)
            .RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Single(state.SavedIdentities);
        Assert.Single(graph);
        Assert.Contains("注册身份变化，新位置需重建 GRAPH 设备", outcome.Detail);
    }
}
