using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>M-4：删除项目后只清理其通道里带批量标记、属于本项目 PLC IP 的设备；通道空了才删；共享通道不动。</summary>
public class IgwProjectRemovalCleanerTests
{
    private const string Channel = "线体A_PLC1";

    private sealed class MemoryRemovalStore : IIgwProjectRemovalStore
    {
        public IgwRemovedProjectInfo Info { get; set; }
        public List<IgwPeerProjectRow> Peers { get; } = new();
        public int RecordDeletes { get; private set; }
        public bool FailRecordDeletes { get; set; }

        public Task<IgwRemovedProjectInfo> GetProjectAsync(long projectId) => Task.FromResult(Info);

        public Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId) =>
            Task.FromResult<IReadOnlyList<IgwPeerProjectRow>>(Peers);

        public Task DeleteRegistrationRecordsAsync(long projectId)
        {
            if (FailRecordDeletes)
            {
                throw new InvalidOperationException("数据库不可用");
            }
            RecordDeletes++;
            return Task.CompletedTask;
        }
    }

    private static IgwProjectRemovalCleaner Cleaner(FakeIgwServer igw, MemoryRemovalStore store) =>
        new(Client(igw), store, _ => Task.CompletedTask, NullLogger.Instance);

    private static IgwRemovedProjectInfo Info(string url, string channel = Channel, IgwRegistrationIdentity registered = null) =>
        new(ProjectId, "项目H", url, Ip, channel, registered);

    private static FakeIgwServer IgwWithProjectDevices()
    {
        var igw = new FakeIgwServer();
        igw.AddDevice(Channel, "S7", "S7_" + Ip, Ip + ":102");
        igw.AddDevice(Channel, "S7Plus", "S1500_" + Ip, Ip + ":102");
        igw.AddDevice(Channel, "GRAPH", "S7Plus_" + Ip, Ip + ":102");
        return igw;
    }

    [Fact]
    public async Task Removes_project_devices_and_the_emptied_channel()
    {
        var igw = IgwWithProjectDevices();
        var channelId = igw.Channels.Single().Id;
        var store = new MemoryRemovalStore { Info = Info(UniqueUrl()) };

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(3, igw.Deleted.Count(d => d.Kind == "device"));
        Assert.Contains(("channel", channelId), igw.Deleted);
        Assert.Equal(1, store.RecordDeletes);
    }

    [Fact]
    public async Task Keeps_channel_that_still_has_other_devices()
    {
        var igw = IgwWithProjectDevices();
        igw.AddDevice(Channel, "现场手建", "OpcUa_" + Ip, Ip + ":4840", managedBy: null);
        var store = new MemoryRemovalStore { Info = Info(UniqueUrl()) };

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("保留", outcome.Detail);
        Assert.DoesNotContain(igw.Deleted, d => d.Kind == "channel");
        Assert.Single(igw.Channels.Single().Devices);
    }

    [Fact]
    public async Task Channel_registered_by_another_live_project_is_left_untouched()
    {
        var url = UniqueUrl();
        var igw = IgwWithProjectDevices();
        var store = new MemoryRemovalStore { Info = Info(url) };
        store.Peers.Add(new IgwPeerProjectRow { ProjectId = 9, ProjectName = "新项目", RegisteredIdentity = IgwRegistrationIdentity.Create(url, "10.0.0.9", Channel) });

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Skipped, outcome.Status);
        Assert.Contains("新项目(9)", outcome.Detail);
        Assert.Empty(igw.Requests);
        Assert.Equal(1, store.RecordDeletes);
    }

    [Theory]
    [InlineData("http://10.0.0.5:2500", Channel)]
    [InlineData("http://igw.local:48020", null)]
    public async Task Legacy_gateway_or_never_registered_project_skips_igw(string url, string channel)
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRemovalStore { Info = Info(url, channel) };

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Skipped, outcome.Status);
        Assert.Empty(igw.Requests);
        Assert.Equal(1, store.RecordDeletes);
    }

    [Fact]
    public async Task Igw_failure_fails_and_keeps_records_for_retry()
    {
        var igw = IgwWithProjectDevices();
        igw.FailDeletes = true;
        var store = new MemoryRemovalStore { Info = Info(UniqueUrl()) };

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Equal(0, store.RecordDeletes);
    }

    [Fact]
    public async Task Registered_identity_wins_over_current_project_configuration()
    {
        var url = UniqueUrl();
        var igw = new FakeIgwServer();
        igw.AddDevice("旧通道", "S7", "S7_" + Ip, Ip + ":102");
        var store = new MemoryRemovalStore { Info = Info(url, "新通道", IgwRegistrationIdentity.Create(url, Ip, "旧通道")) };

        await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Single(igw.Deleted, d => d.Kind == "device");
        Assert.Contains(igw.Deleted, d => d.Kind == "channel");
    }

    [Fact]
    public async Task Unregistered_peer_configured_on_the_same_location_is_left_untouched()
    {
        var url = UniqueUrl();
        var igw = IgwWithProjectDevices();
        var store = new MemoryRemovalStore { Info = Info(url) };
        // 加固前注册 / 身份保存失败的存活项目：无登记身份，但当前配置同 IGW + PLC IP + 通道
        store.Peers.Add(new IgwPeerProjectRow { ProjectId = 9, ProjectName = "存活项目", IotUrl = url, PlcIp = Ip, ConfiguredChannel = Channel });

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Skipped, outcome.Status);
        Assert.Contains("存活项目(9)", outcome.Detail);
        Assert.Empty(igw.Requests);
        Assert.Equal(1, store.RecordDeletes);
    }

    [Fact]
    public async Task Peer_with_unknown_channel_on_the_same_igw_and_ip_is_treated_as_occupant()
    {
        var url = UniqueUrl();
        var igw = IgwWithProjectDevices();
        var store = new MemoryRemovalStore { Info = Info(url) };
        store.Peers.Add(new IgwPeerProjectRow { ProjectId = 9, ProjectName = "存活项目", IotUrl = url, PlcIp = Ip, ConfiguredChannelUnknown = true });

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Skipped, outcome.Status);
        Assert.Contains("存活项目(9)", outcome.Detail);
        Assert.Empty(igw.Requests);
        Assert.Equal(1, store.RecordDeletes);
    }

    /// <summary>
    /// 现行行为（钉住）：未登记身份的项目配置在同 IGW + 同通道但不同 PLC IP → 不算占用，照常清理；
    /// 只删本项目 IP 的设备，对方 IP 的设备留下，通道因此保留。
    /// </summary>
    [Fact]
    public async Task Unregistered_peer_on_same_channel_with_other_ip_does_not_block_but_keeps_its_devices()
    {
        var url = UniqueUrl();
        var igw = IgwWithProjectDevices();
        igw.AddDevice(Channel, "S7_对方", "S7_10.0.0.9", "10.0.0.9:102");
        var store = new MemoryRemovalStore { Info = Info(url) };
        store.Peers.Add(new IgwPeerProjectRow { ProjectId = 9, ProjectName = "存活项目", IotUrl = url, PlcIp = "10.0.0.9", ConfiguredChannel = Channel });

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(3, igw.Deleted.Count(d => d.Kind == "device"));
        Assert.DoesNotContain(igw.Deleted, d => d.Kind == "channel");
        Assert.Equal("S7_10.0.0.9", igw.Channels.Single().Devices.Single().Logical);
    }

    [Fact]
    public async Task Record_delete_failure_fails_the_cleanup()
    {
        var igw = IgwWithProjectDevices();
        var store = new MemoryRemovalStore { Info = Info(UniqueUrl()), FailRecordDeletes = true };

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("删除注册记录失败", outcome.Detail);
    }

    [Fact]
    public async Task Channel_already_gone_from_igw_succeeds_and_deletes_records()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRemovalStore { Info = Info(UniqueUrl()) };

        var outcome = await Cleaner(igw, store).RunAsync(ProjectId, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("IGW 上已无该通道", outcome.Detail);
        Assert.Empty(igw.Deleted);
        Assert.Equal(1, store.RecordDeletes);
    }

    /// <summary>删除前已排队的注册任务：项目已软删 → Skipped，不向 IGW 发任何请求（不重建刚清理的通道/设备）</summary>
    [Fact]
    public async Task Registration_of_a_deleted_project_is_skipped_without_igw_calls()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore();

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()) with { IsDeleted = true },
            new IgwRegistrationArgs(null, null, false, true, null), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Skipped, outcome.Status);
        Assert.Contains("已删除", outcome.Detail);
        Assert.Empty(igw.Requests);
    }
}
