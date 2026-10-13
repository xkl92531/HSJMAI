using System.Net;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>R-1：IGW 按内容拒绝的根组被隔离、记 Rejected 且不自动重试；瞬态失败照常重试。</summary>
public class IgwRegistrationIsolationTests
{
    /// <summary>DB1 → S7（经典偏移）；DB2 / DB3 / DB4 → S7Plus（符号式），同一 S7Plus 请求</summary>
    private static IgwRegistrationData Data() =>
        new(new[] { "DB1", "DB2", "DB3", "DB4" },
            new List<PlannerDbRow>
            {
                ClassicRow("DB1", 1, "V", 0),
                SymbolicRow("DB2", "Motor.Speed"),
                SymbolicRow("DB3", "Motor.Speed"),
                SymbolicRow("DB4", "Motor.Speed"),
            },
            new List<PlannerIoTag>());

    private static IgwRegistrationArgs Full() => new(Array.Empty<string>(), Array.Empty<string>(), false, true, null);

    private static bool HasItemsOf(IgwBulkRequest request, string root) => request.Items.Any(i => i.GroupPath[0] == root);

    [Fact]
    public async Task Rejection_without_item_index_is_bisected_to_the_offending_root()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => HasItemsOf(r, "DB3")
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备级校验：DB3.Motor.Speed 数组定义非法"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("IGW 拒绝根组 1 个（DB3）", outcome.Detail);
        // 原请求 [DB2 DB3 DB4 +DB1 清除组] → [DB2 DB3] 被拒 → [DB2] 成功、[DB3] 被拒 → [DB4 DB1] 成功
        Assert.Equal(5, igw.BulkRequestsFor("S7Plus").Count);
        Assert.Contains("隔离重发 4 次", outcome.Detail);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB4").Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB1").Status);
        var db3 = store.Row("DB3");
        Assert.Equal(IgwLedgerStatus.Rejected, db3.Status);
        Assert.Equal(0, db3.TagCount);
        Assert.Contains("数组定义非法", db3.Error);
        // DB2、DB4 的点确实写进了 IGW（被拒的 DB3 没有拖累它们）
        var applied = igw.BulkRequestsFor("S7Plus").Where(r => !HasItemsOf(r, "DB3")).SelectMany(r => r.Items).Select(i => i.GroupPath[0]).ToList();
        Assert.Contains("DB2", applied);
        Assert.Contains("DB4", applied);
    }

    [Fact]
    public async Task Rejection_with_item_index_resends_the_rest_once_without_bisecting()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r =>
            {
                var index = r.Items.FindIndex(i => i.GroupPath[0] == "DB3");
                return index >= 0
                    ? (HttpStatusCode.BadRequest, $$"""{"error":"invalid_items","details":[{"index":{{index}},"reason":"地址非法"}]}""")
                    : (HttpStatusCode.OK, """{"applied":true}""");
            },
        };
        var store = new MemoryRegistrationStore { Data = Data() };

        await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(2, igw.BulkRequestsFor("S7Plus").Count);
        Assert.Equal(IgwLedgerStatus.Rejected, store.Row("DB3").Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB4").Status);
    }

    [Fact]
    public async Task Rejected_block_is_not_retried_on_next_upload_but_transiently_failed_block_is()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore { Data = Data() };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7Plus", IgwLedgerStatus.Rejected);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB3", "S7Plus", IgwLedgerStatus.Failed);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB4", "S7Plus", IgwLedgerStatus.Registered);
        var args = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Equal(new[] { "DB1", "DB3" }, Assert.Single(store.RowBlockRequests).OrderBy(x => x));
        var roots = igw.BulkRequests.SelectMany(r => r.Items).Select(i => i.GroupPath[0]).Distinct().ToList();
        Assert.Contains("DB3", roots);
        Assert.DoesNotContain("DB2", roots);
        Assert.Equal(IgwLedgerStatus.Rejected, store.Row("DB2").Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB3").Status);
    }

    [Fact]
    public async Task Rejected_block_is_resent_when_it_changes()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore { Data = Data() };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7Plus", IgwLedgerStatus.Rejected);
        var args = new IgwRegistrationArgs(new[] { "DB2" }, Array.Empty<string>(), false, false, null);

        await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Contains(igw.BulkRequests.SelectMany(r => r.Items), i => i.GroupPath[0] == "DB2");
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
    }

    [Fact]
    public async Task Device_level_rejection_stops_the_device_without_bisecting()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.BadRequest, """{"error":"channel_driver_mismatch","detail":"同名通道驱动不同"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };
        var config = new IgwRegistrationConfig { MaxItemsPerRequest = 1 }; // S7Plus 拆成 3 个请求

        var outcome = await Runner(igw, store, config).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Single(igw.BulkRequestsFor("S7Plus"));
        // 与内容无关的拒绝不是根组的问题：一律瞬态 Failed（下次注册自动重试），首个请求的根组带 IGW 错误原文，其余「未发送」
        var roots = store.Rows.Where(r => r.RootName is "DB2" or "DB3" or "DB4").ToList();
        Assert.Equal(3, roots.Count);
        Assert.All(roots, r => Assert.Equal(IgwLedgerStatus.Failed, r.Status));
        var first = Assert.Single(roots, r => !r.Error.Contains("未发送"));
        Assert.Contains("channel_driver_mismatch", first.Error);
        Assert.Contains("同名通道驱动不同", first.Error);
        Assert.DoesNotContain("IGW 拒绝根组", outcome.Detail);
    }

    [Theory]
    [InlineData("template_instance_not_supported")]
    [InlineData("invalid_request")]
    [InlineData("invalid_if_match")]
    public async Task Other_device_level_errors_are_transient_failures(string error)
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.BadRequest, $$"""{"error":"{{error}}","detail":"设备级"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };

        await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Single(igw.BulkRequestsFor("S7Plus"));
        Assert.All(new[] { "DB2", "DB3", "DB4" }, name =>
        {
            Assert.Equal(IgwLedgerStatus.Failed, store.Row(name).Status);
            Assert.Contains(error, store.Row(name).Error);
        });
    }

    // ---------- 无下标拒绝：同一原请求没有任何根组写入成功 → 不记永久拒绝 ----------

    [Fact]
    public async Task No_index_rejection_is_not_committed_as_rejected_when_the_budget_runs_out()
    {
        // 设备一律拒绝（无下标）；预算 4 只够二分到 [DB2]、[DB3] 单独被拒，[DB4 DB1] 因预算用完没能定位——
        // 原请求没有任何根组写入成功 → DB2、DB3 也不能判为内容问题，记瞬态 Failed 并带 IGW 错误原文
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备当前状态拒绝"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };

        var outcome = await Runner(igw, store, new IgwRegistrationConfig { MaxIsolationRequests = 4 })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Equal(5, igw.BulkRequestsFor("S7Plus").Count); // 原请求 + [DB2 DB3] + [DB2] + [DB3] + [DB4 DB1]
        Assert.DoesNotContain(store.Rows, r => r.Status == IgwLedgerStatus.Rejected);
        Assert.DoesNotContain("IGW 拒绝根组", outcome.Detail);
        Assert.All(new[] { "DB2", "DB3", "DB4" }, name =>
        {
            Assert.Equal(IgwLedgerStatus.Failed, store.Row(name).Status);
            Assert.Contains("设备当前状态拒绝", store.Row(name).Error);
        });
    }

    [Fact]
    public async Task No_index_rejection_of_many_roots_with_default_budget_is_never_committed_as_rejected()
    {
        // 40 个 S7Plus 根组同一请求、设备一律拒绝：默认预算不够逐个拒绝全部根组，也不能留下 Rejected
        var blocks = Enumerable.Range(1, 40).Select(i => $"DBS{i:00}").ToList();
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备当前状态拒绝"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore
        {
            Data = new(blocks, blocks.Select(b => SymbolicRow(b, "Motor.Speed")).ToList(), new List<PlannerIoTag>()),
        };

        await Runner(igw, store).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(1 + 64, igw.BulkRequestsFor("S7Plus").Count); // 原请求 + 默认预算 64 次二分重发，用完即止
        Assert.Equal(40, store.Rows.Count(r => r.RootKind == IgwLedgerRootKind.DbBlock));
        Assert.All(store.Rows.Where(r => r.RootKind == IgwLedgerRootKind.DbBlock), r =>
        {
            Assert.Equal(IgwLedgerStatus.Failed, r.Status);
            Assert.Contains("设备当前状态拒绝", r.Error);
        });
    }

    [Fact]
    public async Task No_index_rejection_is_rejected_only_when_another_root_of_the_same_request_applied()
    {
        // S7Plus：[DB2 DB3 +DB1 清除组] [DB4]；含 DB3 或 DB4 点的请求一律被拒（无下标）
        // DB3 与已写入的 DB2 同属一个原请求 → Rejected；DB4 单独成请求、无任何写入成功的证据 → 瞬态 Failed
        var igw = new FakeIgwServer
        {
            BulkResponder = r => HasItemsOf(r, "DB3") || HasItemsOf(r, "DB4")
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"定义非法"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };

        var outcome = await Runner(igw, store, new IgwRegistrationConfig { MaxItemsPerRequest = 2 })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal(IgwLedgerStatus.Rejected, store.Row("DB3").Status);
        Assert.Contains("定义非法", store.Row("DB3").Error);
        Assert.Equal(IgwLedgerStatus.Failed, store.Row("DB4").Status);
        Assert.Contains("定义非法", store.Row("DB4").Error);
        Assert.Contains("IGW 拒绝根组 1 个（DB3）", outcome.Detail);
    }

    [Fact]
    public async Task Bisection_sub_attempt_failures_log_at_debug_with_one_warning_summary_per_device()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => HasItemsOf(r, "DB3")
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"DB3 非法"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };
        var logger = new ListLogger();
        var runner = new IgwRegistrationRunner(Client(igw), store, _ => Task.CompletedTask, logger, new IgwRegistrationConfig());

        await runner.RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        // 原请求失败 1 条 + 设备汇总 1 条；二分子请求的 2 次失败（[DB2 DB3]、[DB3]）只记 Debug
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("隔离重发汇总") && w.Message.Contains("S7Plus"));
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("请求失败")));
    }

    [Fact]
    public async Task Rejected_io_row_does_not_trigger_io_resend_on_incremental_upload()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore
        {
            Data = Data() with { IoTags = new List<PlannerIoTag> { new("表1", "Start", "%I0.0", "Bool", null) } },
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB4", "S7Plus", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.Io, "IO", "S7", IgwLedgerStatus.Rejected);
        var args = new IgwRegistrationArgs(new[] { "DB4" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Contains("IO 刷新 否", outcome.Detail);
        Assert.DoesNotContain(igw.BulkRequests.SelectMany(r => r.Items), i => i.GroupPath[0] == "IO");
        Assert.Equal(IgwLedgerStatus.Rejected, store.Rows.Single(r => r.RootKind == IgwLedgerRootKind.Io).Status);
    }

    [Fact]
    public async Task Exhausted_isolation_budget_leaves_roots_transiently_failed()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备级校验失败"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };

        await Runner(igw, store, new IgwRegistrationConfig { MaxIsolationRequests = 0 })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Single(igw.BulkRequestsFor("S7Plus"));
        Assert.All(new[] { "DB2", "DB3", "DB4" }, name =>
        {
            Assert.Equal(IgwLedgerStatus.Failed, store.Row(name).Status);
            Assert.Contains("预算", store.Row(name).Error);
        });
    }

    [Fact]
    public async Task Ledger_with_only_rejected_rows_does_not_escalate_to_full()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore { Data = Data() };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7Plus", IgwLedgerStatus.Rejected);
        var args = new IgwRegistrationArgs(new[] { "DB4" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.DoesNotContain("自动升级", outcome.Detail);
        Assert.Equal(new[] { "DB4" }, Assert.Single(store.RowBlockRequests));
    }

    // ---------- 大根组跨多个请求分片（replaceGroups 只在首片） ----------

    /// <summary>DB2 三点、DB3 一点（均 S7Plus）；MaxItemsPerRequest=2 → S7Plus 请求 [DB2×2 + DB2/DB1 清除组] [DB2×1 + DB3×1 + DB3 清除组]</summary>
    private static IgwRegistrationData SlicedData() =>
        new(new[] { "DB1", "DB2", "DB3" },
            new List<PlannerDbRow>
            {
                ClassicRow("DB1", 1, "V", 0),
                SymbolicRow("DB2", "A"),
                SymbolicRow("DB2", "B"),
                SymbolicRow("DB2", "C"),
                SymbolicRow("DB3", "A"),
            },
            new List<PlannerIoTag>());

    private static bool ReplacesRoot(IgwBulkRequest request, string root) => request.ReplaceGroups.Any(g => g[0] == root);

    [Fact]
    public async Task Rejected_first_slice_keeps_later_slices_of_that_root_unsent()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => ReplacesRoot(r, "DB2")
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"DB2 定义非法"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = SlicedData() };

        var outcome = await Runner(igw, store, new IgwRegistrationConfig { MaxItemsPerRequest = 2 })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        // 首片之后，DB2 的点一个都没再发出（否则会追加进一个从未被替换的组）
        var requests = igw.BulkRequestsFor("S7Plus");
        Assert.All(requests, r => Assert.True(!HasItemsOf(r, "DB2") || ReplacesRoot(r, "DB2")));
        // 首片原请求里只有 DB1 的纯清除组写入成功——不算设备接受内容的证据 → DB2 记瞬态 Failed 而非 Rejected
        Assert.Equal(IgwLedgerStatus.Failed, store.Row("DB2").Status);
        Assert.StartsWith("同一请求没有任何带点的根组写入成功", store.Row("DB2").Error);
        Assert.Contains("DB2 定义非法", store.Row("DB2").Error);
        Assert.Equal(0, store.Row("DB2").TagCount);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB3").Status);
        Assert.Contains(requests, r => HasItemsOf(r, "DB3"));
    }

    [Fact]
    public async Task Failed_first_slice_after_exhausted_budget_keeps_later_slices_unsent()
    {
        var igw = new FakeIgwServer
        {
            BulkResponder = r => ReplacesRoot(r, "DB2")
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"DB2 定义非法"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = SlicedData() };

        await Runner(igw, store, new IgwRegistrationConfig { MaxItemsPerRequest = 2, MaxIsolationRequests = 0 })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        var requests = igw.BulkRequestsFor("S7Plus");
        Assert.Equal(2, requests.Count);
        Assert.False(HasItemsOf(requests[1], "DB2"));
        Assert.True(HasItemsOf(requests[1], "DB3"));
        Assert.Equal(IgwLedgerStatus.Failed, store.Row("DB2").Status);
        Assert.Contains("预算", store.Row("DB2").Error);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB3").Status);
    }

    [Fact]
    public async Task Root_is_registered_only_when_all_of_its_slices_applied()
    {
        // 首片（带 DB2 清除组）成功；第二片中 DB2 的点被 IGW 按下标拒绝 → DB2 Rejected，DB3 剔除 DB2 后重发成功
        var igw = new FakeIgwServer
        {
            BulkResponder = r =>
            {
                var index = r.Items.FindIndex(i => i.GroupPath[0] == "DB2");
                return index >= 0 && !ReplacesRoot(r, "DB2")
                    ? (HttpStatusCode.BadRequest, $$"""{"error":"invalid_items","details":[{"index":{{index}},"reason":"地址非法"}]}""")
                    : (HttpStatusCode.OK, """{"applied":true}""");
            },
        };
        var store = new MemoryRegistrationStore { Data = SlicedData() };

        var outcome = await Runner(igw, store, new IgwRegistrationConfig { MaxItemsPerRequest = 2 })
            .RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Equal(3, igw.BulkRequestsFor("S7Plus").Count);
        Assert.Equal(IgwLedgerStatus.Rejected, store.Row("DB2").Status);
        Assert.Equal(2, store.Row("DB2").TagCount); // 只算已写入的首片 2 点，不是规划的 3 点
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB3").Status);
    }

    // ---------- 二分到底全部被拒：按设备级失败处理 ----------

    [Fact]
    public async Task Bisection_rejecting_every_root_is_treated_as_a_device_level_failure()
    {
        // 地址模式混用等由设备现有状态引起的拒绝：每个根组单独发也被拒 → 不逐个记 Rejected（下次照常重试），停发该设备。
        // 与真实 IGW 一致：只有带点的请求被拒，纯清除请求（DB1 在 S7Plus 上的清除组）返回 200——它不能证明设备接受内容
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus" && r.Items.Count > 0
                ? (HttpStatusCode.BadRequest, """{"error":"mixed_siemens_address_mode","detail":"设备已有经典地址点"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var store = new MemoryRegistrationStore { Data = Data() };
        var config = new IgwRegistrationConfig { MaxItemsPerRequest = 2 }; // S7Plus：[DB2 DB3 +DB1 清除组] [DB4]

        var outcome = await Runner(igw, store, config).RunAsync(Project(UniqueUrl()), Full(), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("mixed_siemens_address_mode", outcome.Detail);
        Assert.Contains("设备已有经典地址点", outcome.Detail);
        Assert.DoesNotContain("IGW 拒绝根组", outcome.Detail);
        // 原请求 [DB2 DB3 +DB1 清除组] → [DB2] 被拒、[DB3 DB1] 被拒 → [DB3] 被拒：带点的根组逐个都被拒 → 停发；
        // 剩下的纯清除 [DB1] 与 DB4 所在的后续请求不再发送
        Assert.Equal(4, igw.BulkRequestsFor("S7Plus").Count);
        Assert.DoesNotContain(igw.BulkRequestsFor("S7Plus"), r => HasItemsOf(r, "DB4"));
        Assert.All(new[] { "DB2", "DB3" }, name =>
        {
            Assert.Equal(IgwLedgerStatus.Failed, store.Row(name).Status);
            Assert.StartsWith("原请求 2 个根组逐个单独发送均被拒绝，判定为设备级问题", store.Row(name).Error); // 说明在前，截断不丢
            Assert.Contains("设备已有经典地址点", store.Row(name).Error);
        });
        Assert.Equal(IgwLedgerStatus.Failed, store.Row("DB4").Status);
        Assert.Contains("未发送", store.Row("DB4").Error);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB1").Status);
    }
}

/// <summary>记录日志级别与格式化消息的测试用 logger</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
