using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IgwRegistration.Tests.Hardening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// IgwRegistrationRunner（PLC_IgwRegistrationJob 的编排核心）单测：假 IGW（HttpMessageHandler 桩）+ 内存台账存储，
/// 不连数据库、不连真实网关。
/// </summary>
public class IgwRegistrationJobTests
{
    private const string Ip = "192.168.1.10";
    private const long ProjectId = 42;

    private static readonly IReadOnlyList<TimeSpan> FastRetryDelays = new[]
    {
        TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
    };

    private static IgwRegistrationProjectInfo Project(string iotUrl, string plcIp = Ip, string line = "线体A", string code = "PLC1", string name = "项目1") =>
        new(ProjectId, name, line, code, iotUrl, plcIp, 102);

    private static string UniqueUrl() => $"http://igw-job-{Guid.NewGuid():N}.local:48020";

    private static IgwRegistrationArgs Full(string taskId = null) => new(Array.Empty<string>(), Array.Empty<string>(), false, true, taskId);

    /// <summary>DB1：经典偏移齐全 → S7；DB2：无偏移 → S7Plus；IO 两点 → S7</summary>
    private static IgwRegistrationData StandardData(int db1Rows = 1) =>
        new(new[] { "DB1", "DB2" },
            Enumerable.Range(0, db1Rows).Select(i => new PlannerDbRow("DB1", 1, "V" + i, "Int", 2m * i, 0m, null))
                .Append(new PlannerDbRow("DB2", 2, "Motor.Speed", "Real", null, null, null))
                .ToList(),
            new List<PlannerIoTag>
            {
                new("表1", "Start", "%I0.0", "Bool", null),
                new("表1", "Lamp", "%Q0.1", "Bool", null),
            });

    private static IgwRegistrationRunner Runner(FakeIgw igw, MemoryRegistrationStore store, IgwRegistrationConfig config = null,
        List<string> logs = null, List<(long ProjectId, string Reason)> graphRequests = null) =>
        new(new IgwBulkClient(NullLogger<IgwBulkClient>.Instance, igw, FastRetryDelays),
            store,
            text => { logs?.Add(text); return Task.CompletedTask; },
            NullLogger.Instance,
            config ?? new IgwRegistrationConfig(),
            (projectId, reason) => { graphRequests?.Add((projectId, reason)); return Task.CompletedTask; });

    /// <summary>台账已有 DB1 / DB2 / IO / GRAPH 行（「已完成过首次注册」的项目）</summary>
    private static void SeedPopulatedLedger(MemoryRegistrationStore store)
    {
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB1", RootGroup = "DB1", DeviceKind = "S7", Status = IgwLedgerStatus.Registered,
        });
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB2", RootGroup = "DB2", DeviceKind = "S7Plus", Status = IgwLedgerStatus.Registered,
        });
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.Io, RootName = "IO", RootGroup = "IO", DeviceKind = "S7", Status = IgwLedgerStatus.Registered,
        });
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.Graph, RootName = "ST10" + IgwLedgerSnapshotBuilder.GraphRootNameSeparator + "DB161_HomeGraph",
            RootGroup = "ST10", DeviceKind = "Graph", Status = IgwLedgerStatus.Registered,
        });
    }

    private static IgwRegistrationArgs ChangeDb2() => new(new[] { "DB2" }, Array.Empty<string>(), false, false, null);

    // ------------------------------------------------------------------ 成功路径

    [Fact]
    public async Task Full_registration_sends_s7_then_s7plus_writes_ledger_and_sets_channel_once()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };
        // 静态事件被其它并行测试类触发：只记本项目，且用线程安全集合（旧写法并发 Add 会丢写入 / 与断言枚举冲突）
        var invalidated = new System.Collections.Concurrent.ConcurrentQueue<long>();
        void OnInvalidated(long id)
        {
            if (id == ProjectId)
            {
                invalidated.Enqueue(id);
            }
        }
        IgwAliasCacheInvalidation.Invalidated += OnInvalidated;
        try
        {
            var logs = new List<string>();
            var url = UniqueUrl();
            var outcome = await Runner(igw, store, logs: logs).RunAsync(Project(url), Full(), CancellationToken.None);

            Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
            Assert.Equal(new[] { "S7", "S7Plus" }, igw.BulkRequests.Select(r => r.Device.Name));
            Assert.All(igw.BulkRequests, r => Assert.Equal("线体A_PLC1", r.Channel.Name));
            var s7 = igw.BulkRequests[0];
            Assert.Contains(s7.Items, i => i.Address == "I0.0" && i.GroupPath.SequenceEqual(new[] { "IO", "表1" }));
            Assert.Contains(s7.Items, i => i.Address == "DB1.DBW0");
            Assert.Contains(igw.BulkRequests[1].Items, i => i.Address == "DB2.Motor.Speed");

            Assert.Equal(3, store.Rows.Count);
            Assert.All(store.Rows, r => Assert.Equal(IgwLedgerStatus.Registered, r.Status));
            var db1 = store.Rows.Single(r => r.RootName == "DB1");
            Assert.Equal("S7", db1.DeviceKind);
            Assert.Equal("DB1", db1.RootGroup);
            Assert.Equal(1, db1.TagCount);
            Assert.Equal("S7Plus", store.Rows.Single(r => r.RootName == "DB2").DeviceKind);
            var io = store.Rows.Single(r => r.RootKind == IgwLedgerRootKind.Io);
            Assert.Equal("IO", io.RootGroup);
            Assert.Equal(2, io.TagCount);

            Assert.Equal(new[] { "线体A_PLC1" }, store.ChannelWrites);
            Assert.Contains(ProjectId, invalidated);
            Assert.Contains(logs, l => l.Contains("S7：请求 1/1 成功") && l.Contains("S7Plus：请求 1/1 成功"));

            // 第二次运行：通道名已在项目列里，不再回写
            var second = await Runner(igw, store).RunAsync(Project(url), Full(), CancellationToken.None);
            Assert.Equal(IgwRegistrationStatus.Succeeded, second.Status);
            Assert.Single(store.ChannelWrites);
            Assert.Equal(3, store.Rows.Count); // 替换而不是累加
        }
        finally
        {
            IgwAliasCacheInvalidation.Invalidated -= OnInvalidated;
        }
    }

    [Fact]
    public async Task Stored_channel_name_wins_over_generated_one()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData(), ChannelName = "老通道名" };

        await Runner(igw, store).RunAsync(Project(UniqueUrl(), line: "新产线"), Full(), CancellationToken.None);

        Assert.All(igw.BulkRequests, r => Assert.Equal("老通道名", r.Channel.Name));
        Assert.Empty(store.ChannelWrites);
    }

    // ------------------------------------------------------------------ 配置缺失 / 版本过低

    public static IEnumerable<object[]> MissingConfigCases() => new[]
    {
        new object[] { "", Ip, "线体A", "PLC1", "项目1", "IotUrl" },
        new object[] { "http://192.168.6.75:2500/", Ip, "线体A", "PLC1", "项目1", "老网关" },
        new object[] { "URL", "", "线体A", "PLC1", "项目1", "plcip" },
        new object[] { "URL", Ip, "", "", " ", "通道名" },
    };

    [Theory]
    [MemberData(nameof(MissingConfigCases))]
    public async Task Missing_configuration_is_skipped_without_bulk_requests(string iotUrl, string plcIp, string line, string code,
        string name, string expectedReason)
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };
        var url = iotUrl == "URL" ? UniqueUrl() : iotUrl;

        var outcome = await Runner(igw, store).RunAsync(Project(url, plcIp, line, code, name), Full("task-1"), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Skipped, outcome.Status);
        Assert.Contains(expectedReason, outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Empty(store.Rows);
        Assert.Equal(0, store.LedgerWrites);
    }

    [Fact]
    public async Task Unreachable_igw_fails_instead_of_being_skipped_and_job_throws()
    {
        // I-2：IGW 宕机 / 网络不通不能被当成「不是 IGW」静默跳过——探测抛网络异常 → Failed，Hangfire 任务抛出
        var igw = new FakeIgw { ProbeThrows = true };
        var store = new MemoryRegistrationStore { Data = StandardData() };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("探测失败", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Equal(0, store.LedgerWrites);
        var ex = Assert.Throws<InvalidOperationException>(() => PLC_IgwRegistrationJob.EnsureNotFailed(outcome));
        Assert.Contains("探测失败", ex.Message);
    }

    [Fact]
    public async Task Old_igw_fails_with_upgrade_hint_and_job_throws()
    {
        var igw = new FakeIgw { ProbeStatus = HttpStatusCode.MethodNotAllowed };

        var outcome = await Runner(igw, new MemoryRegistrationStore { Data = StandardData() })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("IGW 版本过低", outcome.Detail);
        Assert.Throws<InvalidOperationException>(() => PLC_IgwRegistrationJob.EnsureNotFailed(outcome));
        PLC_IgwRegistrationJob.EnsureNotFailed(new IgwRegistrationOutcome(IgwRegistrationStatus.Skipped, "x")); // Skipped 不抛
    }

    // ------------------------------------------------------------------ I-1 过渡安全：增量自动升级全量 + GRAPH 重注册

    [Fact]
    public async Task Incremental_with_empty_ledger_escalates_to_full()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };
        var graph = new List<(long, string)>();

        var outcome = await Runner(igw, store, graphRequests: graph).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Null(Assert.Single(store.RowBlockRequests)); // 全量取数
        Assert.Contains("全量", outcome.Detail);
        Assert.Contains("自动升级", outcome.Detail);
        var s7 = igw.BulkRequests.Single(r => r.Device.Name == "S7");
        Assert.Contains(s7.Items, i => i.Address == "DB1.DBW0"); // 未变更块同一轮补注册
        Assert.Contains(s7.Items, i => i.Address == "I0.0");     // IO 一并注册
        Assert.Equal(new[] { "DB1", "DB2", "IO" }, store.Rows.Select(r => r.RootName).OrderBy(x => x));
        Assert.Empty(graph); // 项目无 GRAPH 目标
    }

    [Fact]
    public async Task Legacy_graph_device_removed_escalates_to_full_and_requests_graph_registration()
    {
        var igw = new FakeIgw { LegacyChannelName = "通用_S7Plus" };
        igw.LegacyDevices.Add(("dev-g", "旧GRAPH设备", "S7Plus_" + Ip, Ip + ":102"));
        var store = new MemoryRegistrationStore { Data = StandardData() };
        SeedPopulatedLedger(store);
        var graph = new List<(long ProjectId, string Reason)>();

        var outcome = await Runner(igw, store, graphRequests: graph).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("dev-g", igw.DeletedDeviceIds);
        Assert.Null(Assert.Single(store.RowBlockRequests));
        Assert.Contains(igw.BulkRequests.SelectMany(r => r.Items), i => i.Address == "DB1.DBW0");
        var request = Assert.Single(graph);
        Assert.Equal(ProjectId, request.ProjectId);
        Assert.Contains("清理删除了", request.Reason);
    }

    [Fact]
    public async Task Legacy_scalar_device_removed_escalates_to_full_without_graph_request_when_graph_ledger_present()
    {
        var igw = new FakeIgw();
        igw.LegacyDevices.Add(("dev-1", "旧设备", "S1500_" + Ip, Ip + ":102"));
        var store = new MemoryRegistrationStore { Data = StandardData(), HasGraphTargets = true };
        SeedPopulatedLedger(store);
        var graph = new List<(long, string)>();

        var outcome = await Runner(igw, store, graphRequests: graph).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Null(Assert.Single(store.RowBlockRequests));
        Assert.Empty(graph);
    }

    [Fact]
    public async Task Missing_graph_ledger_with_graph_targets_requests_graph_registration_without_escalating()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData(), HasGraphTargets = true };
        SeedPopulatedLedger(store);
        store.Rows.RemoveAll(r => r.RootKind == IgwLedgerRootKind.Graph);
        var graph = new List<(long ProjectId, string Reason)>();

        var outcome = await Runner(igw, store, graphRequests: graph).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(new[] { "DB2" }, Assert.Single(store.RowBlockRequests)); // 仍是增量
        Assert.Equal(ProjectId, Assert.Single(graph).ProjectId);
    }

    [Fact]
    public async Task Normal_incremental_with_populated_ledger_is_unchanged()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData(), HasGraphTargets = true };
        SeedPopulatedLedger(store);
        var graph = new List<(long, string)>();

        var outcome = await Runner(igw, store, graphRequests: graph).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(new[] { "DB2" }, Assert.Single(store.RowBlockRequests));
        Assert.DoesNotContain("自动升级", outcome.Detail);
        Assert.Equal("DB2.Motor.Speed", Assert.Single(igw.BulkRequests.SelectMany(r => r.Items)).Address);
        Assert.Empty(graph);
    }

    // ------------------------------------------------------------------ N-1 失败根在重试时补注册

    [Fact]
    public async Task Retry_after_failed_first_full_run_escalates_to_full()
    {
        // 首次（升级为全量的）注册全部失败（503 耗尽重试）：台账只有 Failed 行 → Hangfire 重试（增量参数）仍按全量跑
        var igw = new FakeIgw { BulkResponder = _ => (HttpStatusCode.ServiceUnavailable, """{"error":"persistence_failed"}""") };
        var store = new MemoryRegistrationStore { Data = StandardData() };

        var first = await Runner(igw, store).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);
        Assert.Equal(IgwRegistrationStatus.Failed, first.Status);
        Assert.All(store.Rows, r => Assert.Equal(IgwLedgerStatus.Failed, r.Status));

        igw.BulkResponder = _ => (HttpStatusCode.OK, """{"applied":true}""");
        igw.BulkRequests.Clear();
        store.RowBlockRequests.Clear();
        var retry = await Runner(igw, store).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, retry.Status);
        Assert.Null(Assert.Single(store.RowBlockRequests)); // 全量
        var addresses = igw.BulkRequests.SelectMany(r => r.Items).Select(i => i.Address).ToList();
        Assert.Contains("DB1.DBW0", addresses);
        Assert.Contains("DB2.Motor.Speed", addresses);
        Assert.Contains("I0.0", addresses);
        Assert.All(store.Rows, r => Assert.Equal(IgwLedgerStatus.Registered, r.Status));
    }

    [Fact]
    public async Task Retry_after_partially_failed_full_run_re_registers_failed_blocks_and_io()
    {
        // 首次全量：S7 设备 503 重试耗尽（DB1 + IO Failed，瞬态），S7Plus 成功（DB2 Registered）；重试以增量参数（只变更 DB2）→
        // 失败的 DB1 并入变更范围、IO 视为刷新，全部重新注册
        var igw = new FakeIgw
        {
            BulkResponder = req => req.Device.Name == "S7"
                ? (HttpStatusCode.ServiceUnavailable, """{"error":"persistence_failed","detail":"数据库忙"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = StandardData() };
        var first = await Runner(igw, store).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);
        Assert.Equal(IgwRegistrationStatus.Failed, first.Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Rows.Single(r => r.RootName == "DB2").Status);
        Assert.Equal(IgwLedgerStatus.Failed, store.Rows.Single(r => r.RootName == "DB1").Status);

        igw.BulkResponder = _ => (HttpStatusCode.OK, """{"applied":true}""");
        igw.BulkRequests.Clear();
        store.RowBlockRequests.Clear();
        var retry = await Runner(igw, store).RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, retry.Status);
        Assert.Equal(new[] { "DB1", "DB2" }, Assert.Single(store.RowBlockRequests).OrderBy(x => x)); // 增量，但含失败块
        var addresses = igw.BulkRequests.SelectMany(r => r.Items).Select(i => i.Address).ToList();
        Assert.Contains("DB1.DBW0", addresses);
        Assert.Contains("I0.0", addresses);
        // 只追加注册：DB2 上一轮已写成功、指纹未变 → 整根跳过。调用方说"它变了"，但内容一模一样，
        // 重发只会让 IGW 白重启一次设备，这正是本次改造要省掉的开销。
        Assert.DoesNotContain("DB2.Motor.Speed", addresses);
        Assert.All(store.Rows, r => Assert.Equal(IgwLedgerStatus.Registered, r.Status));
    }

    [Fact]
    public async Task Graph_request_failure_does_not_fail_the_registration()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData(), HasGraphTargets = true };
        var runner = new IgwRegistrationRunner(new IgwBulkClient(NullLogger<IgwBulkClient>.Instance, igw, FastRetryDelays), store,
            _ => Task.CompletedTask, NullLogger.Instance, new IgwRegistrationConfig(),
            (_, _) => throw new InvalidOperationException("入队失败"));

        var outcome = await runner.RunAsync(Project(UniqueUrl()), ChangeDb2(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("GRAPH", outcome.Detail);
    }

    [Fact]
    public async Task Old_igw_without_bulk_endpoint_fails_with_upgrade_message()
    {
        var igw = new FakeIgw { ProbeStatus = HttpStatusCode.NotFound };
        var store = new MemoryRegistrationStore { Data = StandardData() };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("升级", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
    }

    // ------------------------------------------------------------------ 失败语义

    [Fact]
    public async Task Bulk_400_with_item_index_rejects_only_the_offending_root()
    {
        // S7 请求含 IO + DB1（根组沿用台账 DB1_OLD）+ DB2 的清除组；IGW 指出 DB1_OLD 下的点非法 → 只拒绝 DB1，
        // 其余根组剔除 DB1 后重发成功（R-1：不再整台设备停发）
        var igw = new FakeIgw
        {
            BulkResponder = req =>
            {
                var index = req.Items.FindIndex(i => i.GroupPath.Count > 0 && i.GroupPath[0] == "DB1_OLD");
                return req.Device.Name == "S7" && index >= 0
                    ? (HttpStatusCode.BadRequest, $$"""{"error":"invalid_items","details":[{"index":{{index}},"reason":"地址非法"}]}""")
                    : (HttpStatusCode.OK, """{"applied":true,"revision":"r2"}""");
            },
        };
        var ledger = new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB1", RootGroup = "DB1_OLD", DeviceKind = "S7",
            Status = IgwLedgerStatus.Registered, RenamesJson = """{"V0":"V0_2"}""",
        };
        var store = new MemoryRegistrationStore { Data = StandardData(db1Rows: 2) };
        store.Rows.Add(ledger);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("invalid_items", outcome.Detail);
        Assert.Contains("地址非法", outcome.Detail);
        Assert.Equal(2, igw.BulkRequests.Count(r => r.Device.Name == "S7")); // 原请求 + 剔除 DB1 后重发

        var db1 = store.Rows.Single(r => r.RootName == "DB1");
        Assert.Equal(IgwLedgerStatus.Rejected, db1.Status);
        // DB1 在 S7 上一个请求都没写入（旧注册原样留在 IGW）：沿用台账旧值，读链继续按旧注册读
        Assert.Equal("S7", db1.DeviceKind);
        Assert.Equal("DB1_OLD", db1.RootGroup);
        Assert.Equal("""{"V0":"V0_2"}""", db1.RenamesJson);
        Assert.Equal(IgwLedgerStatus.Registered, store.Rows.Single(r => r.RootKind == IgwLedgerRootKind.Io).Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Rows.Single(r => r.RootName == "DB2").Status);
    }

    [Fact]
    public async Task Legacy_cleanup_failure_fails_before_any_bulk_request()
    {
        var igw = new FakeIgw { FailLegacyDelete = true };
        igw.LegacyDevices.Add(("dev-1", "旧设备", "S1500_" + Ip, Ip + ":102"));
        var store = new MemoryRegistrationStore { Data = StandardData() };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("清理旧设备失败", outcome.Detail);
        Assert.Empty(igw.BulkRequests);
        Assert.Equal(0, store.LedgerWrites);
    }

    [Fact]
    public async Task Legacy_devices_are_removed_and_reported()
    {
        var igw = new FakeIgw();
        igw.LegacyDevices.Add(("dev-1", "旧设备", "S1500_" + Ip, Ip + ":102"));
        var logs = new List<string>();

        var outcome = await Runner(igw, new MemoryRegistrationStore { Data = StandardData() }, logs: logs)
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("dev-1", igw.DeletedDeviceIds);
        Assert.Contains("删除旧设备 1 个", outcome.Detail);
        Assert.Contains(logs, l => l.Contains("删除旧设备 1 个"));
    }

    // ------------------------------------------------------------------ 增量

    /// <summary>
    /// 2026-09-22 只追加注册：上传解析报上来的「已删除块」不再去清 IGW，台账行也保留。
    /// 用户拍板——PLC 里删掉的东西留在 IGW 上不管它，要收拾走「注册PLC标签」全量（见下一个用例）。
    /// </summary>
    [Fact]
    public async Task Incremental_delete_leaves_the_gateway_and_the_ledger_alone()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB9", RootGroup = "DB9", DeviceKind = "S7", Status = IgwLedgerStatus.Registered,
        });
        // FC7 从未注册过（不在台账）：不应出现在 replaceGroups 里
        var args = new IgwRegistrationArgs(Array.Empty<string>(), new[] { "DB9", "FC7" }, false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.All(igw.BulkRequests, r => Assert.Empty(r.ReplaceGroups));
        Assert.Contains(store.Rows, r => r.RootName == "DB9");  // 台账行保留：不清 IGW 就不能删台账，否则两边失配
        Assert.Empty(store.LastRemoved);
    }

    /// <summary>「注册PLC标签」全量仍会按台账清除已删除块的根组并移除台账行。</summary>
    [Fact]
    public async Task Full_delete_sends_replace_groups_only_requests_and_removes_ledger_root()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB9", RootGroup = "DB9", DeviceKind = "S7", Status = IgwLedgerStatus.Registered,
        });
        var args = new IgwRegistrationArgs(Array.Empty<string>(), new[] { "DB9", "FC7" }, false, true, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.All(new[] { "S7", "S7Plus" }, device =>
            Assert.Contains(igw.BulkRequests.Where(r => r.Device.Name == device).SelectMany(r => r.ReplaceGroups),
                g => g.SequenceEqual(new[] { "DB9" })));
        Assert.DoesNotContain(store.Rows, r => r.RootName == "DB9");
        Assert.Contains((IgwLedgerRootKind.DbBlock, "DB9"), store.LastRemoved);
    }

    /// <summary>
    /// 2026-09-22 只追加注册：变更块直接 upsert，既不替换自己那台设备上的旧组，
    /// 也不再给它从未住过的另一台设备发防御性清除——于是整轮只剩 DB2 落在 S7Plus 上的那一个请求。
    /// </summary>
    [Fact]
    public async Task Incremental_change_registers_only_changed_block_without_any_replace()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };
        // 台账已有 DB1（项目完成过首次注册）：否则增量会自动升级为全量（I-1）
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB1", RootGroup = "DB1", DeviceKind = "S7", Status = IgwLedgerStatus.Registered,
        });
        var args = new IgwRegistrationArgs(new[] { "DB2", "FB5" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(new[] { "S7Plus" }, igw.BulkRequests.Select(r => r.Device.Name));
        Assert.Empty(Assert.Single(igw.BulkRequests).ReplaceGroups);
        Assert.Equal("DB2.Motor.Speed", Assert.Single(igw.BulkRequests[0].Items).Address);
        Assert.Equal(new[] { "DB1", "DB2" }, store.Rows.Select(r => r.RootName).OrderBy(x => x));
    }

    [Fact]
    public async Task Previous_block_roots_from_ledger_are_preserved()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB_A" },
                new List<PlannerDbRow> { new("DB_A", 2, "V", "Int", 0m, 0m, null) }, new List<PlannerIoTag>()),
        };
        store.Rows.Add(new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB.A", RootGroup = "DB_A", DeviceKind = "S7", Status = IgwLedgerStatus.Registered });
        store.Rows.Add(new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB_A", RootGroup = "DB_A_2", DeviceKind = "S7", Status = IgwLedgerStatus.Registered });
        var args = new IgwRegistrationArgs(new[] { "DB_A" }, new[] { "DB.A" }, false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        var item = igw.BulkRequests.SelectMany(r => r.Items).Single();
        Assert.Equal(new[] { "DB_A_2" }, item.GroupPath);   // 本用例的正题：根组沿用台账，不随清洗结果漂移
        // 只追加注册：deleted 参数里的 "DB.A" 不再触发清除，它的台账行也留着
        Assert.All(igw.BulkRequests, r => Assert.Empty(r.ReplaceGroups));
        var row = Assert.Single(store.Rows, r => r.RootName == "DB_A");
        Assert.Equal("DB_A_2", row.RootGroup);
        Assert.Contains(store.Rows, r => r.RootName == "DB.A");
    }

    [Fact]
    public async Task Runtime_apply_failure_records_new_state_but_fails()
    {
        var igw = new FakeIgw
        {
            BulkResponder = req => req.Device.Name == "S7Plus"
                ? (HttpStatusCode.InternalServerError, """{"error":"runtime_apply_failed","detail":"设备重建失败","revision":"r9"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = StandardData() };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Equal(2, igw.BulkRequests.Count); // 500 runtime_apply_failed 不重试
        var db2 = store.Rows.Single(r => r.RootName == "DB2");
        Assert.Equal("S7Plus", db2.DeviceKind);
        Assert.Equal(IgwLedgerStatus.Failed, db2.Status);
        Assert.Contains("运行态", db2.Error);
    }

    // ------------------------------------------------------------------ 入队防重 + 补跑

    [Fact]
    public void Gate_reserves_at_enqueue_and_merges_second_request_into_pending_rerun()
    {
        var gate = new IgwRegistrationQueueGate();
        var first = new IgwRegistrationArgs(new[] { "DB1" }, new[] { "DB8" }, false, false, "t1");
        var second = new IgwRegistrationArgs(new[] { "DB2" }, new[] { "DB9" }, true, true, "t2");

        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(ProjectId, first));
        Assert.Equal(IgwEnqueueResult.MergedIntoPending, gate.Reserve(ProjectId, second)); // 入队时即占位
        Assert.True(gate.TryStart(ProjectId, first));

        var rerun = gate.CompleteOrTakePending(ProjectId, first);
        Assert.NotNull(rerun);
        Assert.Equal(new[] { "DB1", "DB2" }, rerun.ChangedBlocks.OrderBy(x => x));
        Assert.Equal(new[] { "DB8", "DB9" }, rerun.DeletedBlocks.OrderBy(x => x));
        Assert.True(rerun.IoRefreshed);
        Assert.True(rerun.Full);
        Assert.Equal("t2", rerun.TaskId);

        Assert.Null(gate.CompleteOrTakePending(ProjectId, rerun)); // 补跑一次后释放
        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(ProjectId, first));
    }

    [Fact]
    public void Gate_merges_enqueue_while_running_and_concurrent_execution()
    {
        var gate = new IgwRegistrationQueueGate();
        var a = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, "t1");
        var b = new IgwRegistrationArgs(new[] { "DB2" }, Array.Empty<string>(), false, false, "t2");
        var c = new IgwRegistrationArgs(new[] { "DB3" }, Array.Empty<string>(), false, false, null);

        gate.Reserve(ProjectId, a);
        Assert.True(gate.TryStart(ProjectId, a));
        Assert.Equal(IgwEnqueueResult.MergedIntoPending, gate.Reserve(ProjectId, b));
        Assert.False(gate.TryStart(ProjectId, c)); // 面板手工 Retry 等并发执行：并入补跑

        var rerun = gate.CompleteOrTakePending(ProjectId, a);
        Assert.Equal(new[] { "DB1", "DB2", "DB3" }, rerun.ChangedBlocks.OrderBy(x => x));
        Assert.False(rerun.Full);
        Assert.Equal("t2", rerun.TaskId);
    }

    [Fact]
    public void Gate_release_and_stale_reservation_do_not_block_forever()
    {
        var now = new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc);
        var gate = new IgwRegistrationQueueGate(TimeSpan.FromMinutes(30), () => now);
        var a = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(ProjectId, a));
        gate.Release(ProjectId); // 入队失败 / 执行异常
        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(ProjectId, a));

        // 已占位但一直没开始执行（如本机没有 worker 消费队列）：超时后重新入队
        Assert.Equal(IgwEnqueueResult.MergedIntoPending, gate.Reserve(ProjectId, a));
        now = now.AddMinutes(31);
        Assert.Equal(IgwEnqueueResult.Enqueued, gate.Reserve(ProjectId, a));
    }

    // ------------------------------------------------------------------ Fix round 1

    [Fact]
    public async Task Incremental_run_loads_rows_only_for_changed_blocks_and_unchanged_blocks_keep_roots()
    {
        // 未变更块 DB.A 台账根组 DB_A；本次新增块 DB_A（清洗后同名）。只加载 DB_A 的行，
        // 但 LiveDbBlocks 含 DB.A → DB.A 的根组被保留，新块让路为 DB_A_2
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB.A", "DB_A" },
                new List<PlannerDbRow>
                {
                    new("DB.A", 1, "Old", "Int", 0m, 0m, null),
                    new("DB_A", 2, "New", "Int", 0m, 0m, null),
                },
                new List<PlannerIoTag>()),
        };
        store.Rows.Add(new PLC_IgwRegistrationEntity { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB.A", RootGroup = "DB_A", DeviceKind = "S7", Status = IgwLedgerStatus.Registered });
        var args = new IgwRegistrationArgs(new[] { "DB_A" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(new[] { "DB_A" }, Assert.Single(store.RowBlockRequests));
        var item = igw.BulkRequests.SelectMany(r => r.Items).Single();
        Assert.Equal("DB2.DBW0", item.Address);
        Assert.Equal(new[] { "DB_A_2" }, item.GroupPath);
        Assert.All(igw.BulkRequests, r => Assert.DoesNotContain(r.ReplaceGroups, g => g.SequenceEqual(new[] { "DB_A" })));
        Assert.Equal("DB_A", store.Rows.Single(r => r.RootName == "DB.A").RootGroup); // 未变更块台账不动
        Assert.Equal("DB_A_2", store.Rows.Single(r => r.RootName == "DB_A").RootGroup);
    }

    [Fact]
    public async Task Full_run_loads_rows_for_all_blocks()
    {
        var igw = new FakeIgw();
        var store = new MemoryRegistrationStore { Data = StandardData() };

        await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Null(Assert.Single(store.RowBlockRequests));
        Assert.Equal(new[] { "DB1", "DB2" }, store.Rows.Where(r => r.RootKind == IgwLedgerRootKind.DbBlock).Select(r => r.RootName).OrderBy(x => x));
    }

    [Fact]
    public async Task First_registration_failure_records_zero_tag_count()
    {
        var igw = new FakeIgw
        {
            // 只有含 DB2 点的请求被拒（内容型）：二分后 DB2 单独被拒，同一原请求里带点的 DB3 写入成功
            // （设备接受内容的证据）→ DB2 Rejected。纯清除组写入成功不算证据；若没有其它带点的根组写入成功，
            // 按设备级不确定记瞬态 Failed，见 Hardening 用例
            BulkResponder = req => req.Device.Name == "S7Plus" && req.Items.Any(i => i.GroupPath[0] == "DB2")
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"坏点"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var standard = StandardData();
        var store = new MemoryRegistrationStore
        {
            Data = new(standard.LiveDbBlocks.Append("DB3").ToList(),
                standard.DbRows.Append(new PlannerDbRow("DB3", 3, "Motor.Speed", "Real", null, null, null)).ToList(),
                standard.IoTags),
        };

        await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        var db2 = store.Rows.Single(r => r.RootName == "DB2");
        Assert.Equal(IgwLedgerStatus.Rejected, db2.Status);
        Assert.Equal("S7Plus", db2.DeviceKind);
        Assert.Equal(0, db2.TagCount);
        Assert.Equal(1, store.Rows.Single(r => r.RootName == "DB1").TagCount);
        Assert.Equal(IgwLedgerStatus.Registered, store.Rows.Single(r => r.RootName == "DB3").Status);
    }

    [Fact]
    public void Merged_task_ids_are_all_kept_for_progress_reporting()
    {
        var gate = new IgwRegistrationQueueGate();
        var a = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, "t1");
        var b = new IgwRegistrationArgs(new[] { "DB2" }, Array.Empty<string>(), false, false, "t2");
        var c = new IgwRegistrationArgs(new[] { "DB3" }, Array.Empty<string>(), false, false, "t3");
        var d = new IgwRegistrationArgs(new[] { "DB4" }, Array.Empty<string>(), false, false, null);

        gate.Reserve(ProjectId, a);
        gate.TryStart(ProjectId, a);
        gate.Reserve(ProjectId, b);
        gate.Reserve(ProjectId, c);
        gate.Reserve(ProjectId, d);

        var rerun = gate.CompleteOrTakePending(ProjectId, a);

        Assert.Equal("t3", rerun.TaskId);
        Assert.Equal(new[] { "t1", "t2", "t3" }, rerun.AllTaskIds.OrderBy(x => x));
    }

    [Fact]
    public void Bulk_client_resolves_from_container_like_scrutor_self_scoped_registration()
    {
        // PLC_IgwRegistrationJob 构造注入 IgwBulkClient：public 构造函数的 HttpMessageHandler 参数有默认值，容器未注册时应取 null
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IgwBulkClient>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IgwBulkClient>());
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>假 IGW：批量探测 / 批量写入 / 工程树（旧设备清理）</summary>
    private sealed class FakeIgw : HttpMessageHandler
    {
        public HttpStatusCode ProbeStatus { get; set; } = HttpStatusCode.BadRequest;
        public Func<IgwBulkRequest, (HttpStatusCode Status, string Body)> BulkResponder { get; set; } =
            _ => (HttpStatusCode.OK, """{"applied":true,"revision":"r1"}""");
        public List<IgwBulkRequest> BulkRequests { get; } = new();
        public List<string> Requests { get; } = new();
        public List<(string Id, string Name, string Logical, string Endpoint)> LegacyDevices { get; } = new();
        public HashSet<string> DeletedDeviceIds { get; } = new();
        public bool FailLegacyDelete { get; set; }
        public bool ProbeThrows { get; set; }
        public string LegacyChannelName { get; set; } = "通用";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");

            if (path.EndsWith("api/v1/project/bulk/tags", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (body == "{}")
                {
                    if (ProbeThrows)
                    {
                        throw new HttpRequestException("由于目标计算机积极拒绝，无法连接。");
                    }
                    return ProbeStatus == HttpStatusCode.BadRequest
                        ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_items","details":[{"index":-1,"reason":"通道名无效"}]}""")
                        : new HttpResponseMessage(ProbeStatus);
                }
                var bulk = JsonSerializer.Deserialize<IgwBulkRequest>(body, IgwBulkJson.Options);
                BulkRequests.Add(bulk);
                var (status, responseBody) = BulkResponder(bulk);
                return Json(status, responseBody);
            }
            if (path.EndsWith("project/summary", StringComparison.Ordinal))
            {
                var response = Json(HttpStatusCode.OK, """{"root":{"id":"root-1","kind":"project","name":"root"}}""");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rev-1\"");
                return response;
            }
            if (path.Contains("project/tree/project/root-1/children", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, LegacyDevices.Count == 0
                    ? "[]"
                    : $$"""[{"id":"ch-1","kind":"channel","name":"{{LegacyChannelName}}"}]""");
            }
            if (path.Contains("project/tree/channel/ch-1/children", StringComparison.Ordinal))
            {
                var items = LegacyDevices.Select(d => $$"""{"id":"{{d.Id}}","kind":"device","name":"{{d.Name}}"}""");
                return Json(HttpStatusCode.OK, "[" + string.Join(",", items) + "]");
            }
            const string entityPrefix = "project/entity/device/";
            if (path.Contains(entityPrefix, StringComparison.Ordinal))
            {
                var id = path.Substring(path.IndexOf(entityPrefix, StringComparison.Ordinal) + entityPrefix.Length);
                var device = LegacyDevices.Single(d => d.Id == id);
                return Json(HttpStatusCode.OK,
                    $$$"""{"id":"{{{id}}}","endpoint":"{{{device.Endpoint}}}","settings":{"dcsLogicalDevice":"{{{device.Logical}}}"}}""");
            }
            if (path.EndsWith("project/mutations", StringComparison.Ordinal))
            {
                if (FailLegacyDelete)
                {
                    return Json(HttpStatusCode.InternalServerError, """{"error":"persistence_failed","detail":"模拟删除失败"}""");
                }
                using var doc = JsonDocument.Parse(body);
                foreach (var el in doc.RootElement.GetProperty("entityIds").EnumerateArray())
                {
                    DeletedDeviceIds.Add(el.GetString());
                }
                return Json(HttpStatusCode.OK, "{}");
            }
            throw new InvalidOperationException($"FakeIgw 未预期的请求 {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
