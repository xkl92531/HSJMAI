using System.Net;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>
/// 块换设备（S7Plus → S7）：旧设备上的清除组没写入时台账行记 PendingClear（内容已全部写入，下次只重发旧设备清除）或
/// FailedPendingClear（内容也没全部写入，下次整块重做）——不能记 Registered，否则旧组永远留在旧设备上；读链始终按台账位置解析。
/// 「清除待写入」只由显式 PendingClear / FailedPendingClear 状态或台账旧 DeviceKind 携带，不从普通 Failed 推断（Failed 可能是本设备自身的瞬态失败）。
/// </summary>
public class IgwDeviceMoveTests
{
    /// <summary>DB2 原为 S7Plus（台账 Registered），本次经典偏移齐全 → 归 S7</summary>
    private static MemoryRegistrationStore MovedStore()
    {
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB1", "DB2" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0), ClassicRow("DB2", 2, "Speed", 0) },
                new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7Plus", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.Io, "IO", "S7", IgwLedgerStatus.Registered);
        return store;
    }

    private static bool ClearsDb2(IgwBulkRequest request) => request.ReplaceGroups.Any(g => g.Count == 1 && g[0] == "DB2");

    private static bool TouchesDb2Content(IgwBulkRequest request) => request.Items.Any(i => i.GroupPath[0] == "DB2");

    /// <summary>该请求与 DB2 有关（带 DB2 的点或 DB2 清除组）</summary>
    private static bool TouchesDb2(IgwBulkRequest request) => ClearsDb2(request) || TouchesDb2Content(request);

    private static string ReadAlias(MemoryRegistrationStore store) =>
        IgwReadAliasResolver.ResolveDb(Ip, IgwLedgerSnapshotBuilder.Build(store.Rows), "DB2", "Speed");

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备级校验失败"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"channel_driver_mismatch","detail":"同名通道驱动不同"}""")]
    public async Task Moved_block_stays_pending_clear_until_the_old_device_clear_applies(HttpStatusCode s7PlusStatus, string s7PlusBody)
    {
        var url = UniqueUrl();
        var store = MovedStore();
        var failing = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (s7PlusStatus, s7PlusBody)
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };
        var changeDb2 = new IgwRegistrationArgs(new[] { "DB2" }, Array.Empty<string>(), false, false, null);

        var first = await Runner(failing, store).RunAsync(Project(url), changeDb2, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, first.Status);
        Assert.Contains(failing.BulkRequestsFor("S7"), r => r.Items.Any(i => i.GroupPath[0] == "DB2"));
        Assert.Contains(failing.BulkRequestsFor("S7Plus"), ClearsDb2);
        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.PendingClear, row.Status);     // 不是 Registered，也不是 Rejected（不能停止重试）
        Assert.Equal("S7", row.DeviceKind);                          // 新点已写在 S7 上：读链走新位置
        Assert.Equal(1, row.TagCount);
        Assert.Contains("S7Plus", row.Error);
        Assert.StartsWith("通用.S7_", ReadAlias(store));
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB1").Status); // 不涉及换设备的块不受影响

        // 下次上传（只改了无关的 DB1）清除仍没写入：此时台账 DeviceKind 已是新设备 S7，靠 PendingClear 状态继续要求旧设备清除
        var stillFailing = new FakeIgwServer { BulkResponder = failing.BulkResponder };
        var retry = await Runner(stillFailing, store).RunAsync(Project(url),
            new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null), CancellationToken.None);
        Assert.Equal(IgwRegistrationStatus.Failed, retry.Status);
        // 2026-09-22 只追加注册后三种失败都走同一条路：DB1 没搬过家，不再给它没住过的 S7Plus 发防御性清除，
        // 所以这一轮 S7Plus 上没有前序请求把设备打停，DB2 的清除是真发出去、自己失败的（不再有「未发送」分支）。
        Assert.Contains(stillFailing.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.DoesNotContain(stillFailing.BulkRequests, TouchesDb2Content); // 内容已写入：只重发清除，不重发 DB2 的点（否则 S7 每次上传都重启）
        Assert.Equal(IgwLedgerStatus.PendingClear, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
        Assert.Contains("S7Plus", store.Row("DB2").Error);
        Assert.StartsWith("通用.S7_", ReadAlias(store));

        // 再下次上传：PendingClear 的 DB2 只重发旧设备清除组，写入 → Registered
        var healthy = new FakeIgwServer();
        var second = await Runner(healthy, store).RunAsync(Project(url),
            new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, second.Status);
        Assert.Contains(healthy.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.DoesNotContain(healthy.BulkRequests, TouchesDb2Content);
        var healed = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.Registered, healed.Status);
        Assert.Equal("S7", healed.DeviceKind);
        Assert.Null(healed.Error);
        Assert.StartsWith("通用.S7_", ReadAlias(store));
    }

    [Fact]
    public async Task Block_that_never_lived_on_the_other_device_ignores_a_failed_defensive_clear()
    {
        // DB2 上次就在 S7 且 Registered：S7Plus 上对它的清除只是防御性的，失败不应让 DB2 每次上传都重注册
        var store = MovedStore();
        store.Rows.RemoveAll(r => r.RootName == "DB2");
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.Registered);
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };

        // 2026-09-22 起防御性清除只在全量下发（只追加模式见下一个用例）
        await Runner(igw, store).RunAsync(Project(UniqueUrl()),
            new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), false, true, null), CancellationToken.None);

        Assert.Contains(igw.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
    }

    /// <summary>
    /// 只追加注册：DB2 一直住在 S7、也没欠任何旧组清除 ⇒ 根本不该去碰 S7Plus。
    /// 少发这个防御性清除同时也少了一次失败面——这里 S7Plus 是 503，整轮照样成功。
    /// </summary>
    [Fact]
    public async Task Append_only_does_not_touch_a_device_the_block_never_lived_on()
    {
        var store = MovedStore();
        store.Rows.RemoveAll(r => r.RootName == "DB2");
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.Registered);
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus"
                ? (HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""")
                : (HttpStatusCode.OK, """{"applied":true}"""),
        };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()),
            new IgwRegistrationArgs(new[] { "DB2" }, Array.Empty<string>(), false, false, null), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Empty(igw.BulkRequestsFor("S7Plus"));
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
    }

    // ---------- fix round 4：状态表逐格 ----------

    private static IgwRegistrationArgs Change(params string[] blocks) =>
        new(blocks, Array.Empty<string>(), false, false, null);

    private static (HttpStatusCode, string) Ok() => (HttpStatusCode.OK, """{"applied":true}""");

    private static (HttpStatusCode, string) Busy() => (HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""");

    [Fact]
    public async Task Own_device_failure_does_not_imply_a_pending_clear_on_the_other_device()
    {
        // 上次 DB2 在 S7 上自身 503 失败（Failed，从未在 S7Plus 上）；本次 S7 正常、S7Plus 持续故障 →
        // DB2 Registered（S7Plus 的清除只是防御性的），之后的上传不再重注册 DB2，也不把故障归咎到 S7Plus 旧组
        var store = MovedStore();
        store.Rows.RemoveAll(r => r.RootName == "DB2");
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.Failed);
        Func<IgwBulkRequest, (HttpStatusCode, string)> s7PlusDown = r => r.Device.Name == "S7Plus" ? Busy() : Ok();

        var first = new FakeIgwServer { BulkResponder = s7PlusDown };
        await Runner(first, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.Contains(first.BulkRequestsFor("S7"), r => r.Items.Any(i => i.GroupPath[0] == "DB2")); // Failed 行被重做
        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.Registered, row.Status);
        Assert.Equal("S7", row.DeviceKind);
        Assert.DoesNotContain("旧点可能仍留在", row.Error ?? string.Empty);

        var second = new FakeIgwServer { BulkResponder = s7PlusDown };
        await Runner(second, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.DoesNotContain(second.BulkRequestsFor("S7"), r => r.Items.Any(i => i.GroupPath[0] == "DB2"));
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
    }

    [Fact]
    public async Task Pending_clear_survives_an_own_device_failure_while_the_block_stays_on_its_new_device()
    {
        // DB2 已在 S7（PendingClear：S7Plus 旧组未清）；DB2 本次变更，S7 自身 503 一片未写、S7Plus 仍故障 →
        // FailedPendingClear（变更内容没写入，下次整块重做；读位置沿用台账：S7），清除要求不能因本设备失败而丢失
        var store = MovedStore();
        store.Rows.RemoveAll(r => r.RootName == "DB2");
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.PendingClear);
        var down = new FakeIgwServer { BulkResponder = _ => Busy() };

        await Runner(down, store).RunAsync(Project(UniqueUrl()), Change("DB2"), CancellationToken.None);

        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.FailedPendingClear, row.Status);
        Assert.Equal("S7", row.DeviceKind);
        Assert.StartsWith("S7Plus 设备上的旧组清除未写入", row.Error);
        Assert.StartsWith("通用.S7_", ReadAlias(store));

        // 两台设备恢复 → 整块重做（内容 + 清除）→ Registered
        var healthy = new FakeIgwServer();
        await Runner(healthy, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);
        Assert.Contains(healthy.BulkRequestsFor("S7"), TouchesDb2Content);
        Assert.Contains(healthy.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
    }

    [Fact]
    public async Task Own_failure_with_the_old_clear_applied_heals_on_the_next_run()
    {
        // 换设备 S7Plus → S7：S7 自身 503（一片未写）、S7Plus 旧组清除写入 → 旧点已没了，台账按新位置记 Failed（非 PendingClear）；
        // 下次 S7 正常、S7Plus 故障 → Registered（清除已完成，不再要求 S7Plus）
        var store = MovedStore();
        var s7Down = new FakeIgwServer { BulkResponder = r => r.Device.Name == "S7" ? Busy() : Ok() };

        await Runner(s7Down, store).RunAsync(Project(UniqueUrl()), Change("DB2"), CancellationToken.None);

        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.Failed, row.Status);
        Assert.Equal("S7", row.DeviceKind);
        Assert.StartsWith("通用.S7_", ReadAlias(store));

        var s7PlusDown = new FakeIgwServer { BulkResponder = r => r.Device.Name == "S7Plus" ? Busy() : Ok() };
        await Runner(s7PlusDown, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.Contains(s7PlusDown.BulkRequestsFor("S7"), r => r.Items.Any(i => i.GroupPath[0] == "DB2"));
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
    }

    [Fact]
    public async Task Own_failure_before_anything_moved_keeps_the_old_location_as_the_clear_carrier()
    {
        // 换设备 S7Plus → S7：S7 一片未写、S7Plus 清除也没写 → 旧内容原样在 S7Plus：台账沿用 S7Plus（读链仍走旧位置），记 Failed；
        // 旧 DeviceKind 本身携带「下次要清 S7Plus」——下次 S7 写入但 S7Plus 仍故障 → PendingClear
        var store = MovedStore();
        var down = new FakeIgwServer { BulkResponder = _ => Busy() };

        await Runner(down, store).RunAsync(Project(UniqueUrl()), Change("DB2"), CancellationToken.None);

        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.Failed, row.Status);
        Assert.Equal("S7Plus", row.DeviceKind);
        Assert.StartsWith("通用.S1500_", ReadAlias(store));

        var s7PlusDown = new FakeIgwServer { BulkResponder = r => r.Device.Name == "S7Plus" ? Busy() : Ok() };
        await Runner(s7PlusDown, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.Equal(IgwLedgerStatus.PendingClear, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
        Assert.StartsWith("通用.S7_", ReadAlias(store));
    }

    [Fact]
    public async Task Partially_rejected_moved_block_keeps_its_pending_clear()
    {
        // 换设备 S7Plus → S7，DB2 被拆成两片：首片（带 DB2 清除组）写入、第二片按下标被拒；S7Plus 旧组清除 503 →
        // 本设备部分写入（新位置）+ 旧组未清 → FailedPendingClear（不是 Rejected：否则不再自动重试，S7Plus 旧组永远留着；
        // 也不是 PendingClear：内容没全部写入，下次要整块重做而不是只重发清除）
        var store = MovedStore();
        store.Data = new IgwRegistrationData(new[] { "DB1", "DB2" },
            new List<PlannerDbRow>
            {
                ClassicRow("DB1", 1, "V", 0),
                ClassicRow("DB2", 2, "A", 0),
                ClassicRow("DB2", 2, "B", 2),
                ClassicRow("DB2", 2, "C", 4),
            },
            new List<PlannerIoTag>());
        var igw = new FakeIgwServer
        {
            BulkResponder = r =>
            {
                if (r.Device.Name == "S7Plus")
                {
                    return Busy();
                }
                var index = r.Items.FindIndex(i => i.GroupPath[0] == "DB2" && i.Name == "C");
                return index >= 0
                    ? (HttpStatusCode.BadRequest, $$"""{"error":"invalid_items","details":[{"index":{{index}},"reason":"地址非法"}]}""")
                    : Ok();
            },
        };
        var config = new IgwRegistrationConfig { MaxItemsPerRequest = 2 };

        await Runner(igw, store, config).RunAsync(Project(UniqueUrl()), Change("DB2"), CancellationToken.None);

        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.FailedPendingClear, row.Status);
        Assert.Equal("S7", row.DeviceKind);
        Assert.Equal(2, row.TagCount);
        Assert.StartsWith("S7Plus 设备上的旧组清除未写入", row.Error);
        Assert.Contains("地址非法", row.Error);

        // 下次上传（只改 DB1）：FailedPendingClear 的 DB2 整块重做，S7Plus 清除重发
        var healthy = new FakeIgwServer();
        await Runner(healthy, store, config).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);
        Assert.Contains(healthy.BulkRequestsFor("S7"), TouchesDb2Content);
        Assert.Contains(healthy.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
    }

    [Fact]
    public async Task Pending_clear_rows_alone_do_not_escalate_to_a_full_registration()
    {
        var store = MovedStore();
        store.Rows.Clear();
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.PendingClear);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.PendingClear);
        var igw = new FakeIgwServer();

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.DoesNotContain("自动升级", outcome.Detail);
        Assert.Contains("含重试失败块 0）", outcome.Detail); // DB2（PendingClear）不并入重做
        Assert.Contains("含待清除块 1", outcome.Detail);     // 只重发 S7Plus 上的清除
        Assert.DoesNotContain(igw.BulkRequests, TouchesDb2Content);
        Assert.Contains(igw.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
    }

    [Fact]
    public void Snapshot_tracks_pending_clear_blocks_separately_from_failed_and_registered()
    {
        var rows = new List<PLC_IgwRegistrationEntity>
        {
            new() { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB2", RootGroup = "DB2", DeviceKind = "S7", Status = IgwLedgerStatus.PendingClear },
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Contains("db2", snapshot.PendingClearDbBlocks);
        Assert.Empty(snapshot.FailedPendingClearDbBlocks);
        Assert.Empty(snapshot.FailedDbBlocks);
        Assert.Empty(snapshot.RegisteredRootKinds);
        Assert.Equal(IgwDeviceKind.S7, snapshot.DeviceKindByBlock["DB2"]);
        Assert.StartsWith("通用.S7_", IgwReadAliasResolver.ResolveDb(Ip, snapshot, "DB2", "Speed"));
    }

    [Fact]
    public async Task Rejected_clear_only_unit_is_not_reported_as_a_permanent_rejection()
    {
        // DB2 S7Plus → S7；S7Plus 请求 [DB3 点 + DB2/DB3 清除组]：凡含 DB2 清除组的请求被无下标拒绝，DB3 单独写入成功 →
        // DB2 的纯清除单元被定位为 Rejected，但它随 PendingClear 自动重试，不能列入「IGW 拒绝（不自动重试…）」
        var store = MovedStore();
        store.Data = new IgwRegistrationData(new[] { "DB1", "DB2", "DB3" },
            new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0), ClassicRow("DB2", 2, "Speed", 0), SymbolicRow("DB3", "Motor.Speed") },
            new List<PlannerIoTag>());
        store.Seed(IgwLedgerRootKind.DbBlock, "DB3", "S7Plus", IgwLedgerStatus.Registered);
        var igw = new FakeIgwServer
        {
            BulkResponder = r => r.Device.Name == "S7Plus" && ClearsDb2(r)
                ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备级校验失败"}""")
                : Ok(),
        };

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Change("DB2", "DB3"), CancellationToken.None);

        Assert.Contains("IGW 拒绝根组 1 个", outcome.Detail);     // 确认纯清除单元确实被定位为拒绝
        Assert.DoesNotContain("IGW 拒绝（不自动重试", outcome.Detail);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB3").Status);
        Assert.Equal(IgwLedgerStatus.PendingClear, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
    }

    // ---------- fix round 5：PendingClear 只重发清除；换回本设备的旧组；FailedPendingClear ----------

    /// <summary>DB2 已在 S7（PendingClear，S7Plus 旧组未清），台账记有点数 / 跳过数 / 重名映射</summary>
    private static MemoryRegistrationStore PendingClearStore()
    {
        var store = MovedStore();
        store.Rows.RemoveAll(r => r.RootName == "DB2");
        store.Rows.Add(new PLC_IgwRegistrationEntity
        {
            ProjectId = ProjectId,
            RootKind = IgwLedgerRootKind.DbBlock,
            RootName = "DB2",
            RootGroup = "DB2",
            DeviceKind = "S7",
            TagCount = 7,
            SkippedCount = 3,
            SkipSummary = "unmapped_type×3",
            RenamesJson = """{"speed":"speed_2"}""",
            Status = IgwLedgerStatus.PendingClear,
            Error = "S7Plus 设备上的旧组清除未写入：旧错误",
        });
        return store;
    }

    [Fact]
    public async Task Pending_clear_block_with_a_healthy_own_device_resends_only_the_old_device_clear()
    {
        var store = PendingClearStore();
        var igw = new FakeIgwServer();

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.DoesNotContain(igw.BulkRequests, TouchesDb2Content);          // 不重发 DB2 的点
        Assert.DoesNotContain(igw.BulkRequestsFor("S7"), ClearsDb2);        // 也不替换 S7 上的 DB2（那会删掉已写入的内容并重启设备）
        var clear = Assert.Single(igw.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.Empty(clear.Items);                                          // 独立的纯清除请求
        Assert.Equal(new[] { "DB2" }, clear.ReplaceGroups.Select(g => Assert.Single(g)));
        Assert.DoesNotContain(store.RowBlockRequests, r => r != null && r.Contains("DB2")); // 不加载 DB2 接口行
        Assert.Contains("含重试失败块 0）", outcome.Detail);
        Assert.Contains("含待清除块 1", outcome.Detail);

        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.Registered, row.Status);
        Assert.Null(row.Error);
        Assert.Equal("S7", row.DeviceKind);
        Assert.Equal("DB2", row.RootGroup);
        Assert.Equal(7, row.TagCount);                                      // 点数 / 跳过数 / 重名映射原样沿用
        Assert.Equal(3, row.SkippedCount);
        Assert.Equal("unmapped_type×3", row.SkipSummary);
        Assert.Equal("""{"speed":"speed_2"}""", row.RenamesJson);
        Assert.StartsWith("通用.S7_", ReadAlias(store));
    }

    [Fact]
    public async Task Pending_clear_block_whose_planned_root_differs_from_the_ledger_is_skipped_with_a_note()
    {
        // 台账根组为保留名 IO（规划器不会沿用）→ 本次规划根组与台账不一致：不按可能属于别的块的根组清除，提示全量
        var store = PendingClearStore();
        store.Row("DB2").RootGroup = "IO";
        var igw = new FakeIgwServer();

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.DoesNotContain(igw.BulkRequests, TouchesDb2);
        Assert.Contains("1 个待清除块根组不一致（DB2）", outcome.Detail);
        Assert.Contains("需「注册PLC标签」全量", outcome.Detail);
        Assert.Equal(IgwLedgerStatus.PendingClear, store.Row("DB2").Status);
    }

    [Fact]
    public async Task Permanently_rejected_pending_clear_costs_only_one_clear_request_per_upload()
    {
        // S7Plus 永久拒绝 DB2 的清除（无下标 400）：保持 PendingClear，之后每次上传只多一个 DB2 纯清除请求，DB2 内容一次都不重发
        var store = PendingClearStore();
        Func<IgwBulkRequest, (HttpStatusCode, string)> rejectDb2Clear = r => r.Device.Name == "S7Plus" && ClearsDb2(r)
            ? (HttpStatusCode.BadRequest, """{"error":"invalid_items","detail":"设备级校验失败"}""")
            : Ok();

        for (var upload = 0; upload < 3; upload++)
        {
            var igw = new FakeIgwServer { BulkResponder = rejectDb2Clear };

            var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

            Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
            var db2Requests = igw.BulkRequests.Where(TouchesDb2).ToList();
            var only = Assert.Single(db2Requests);
            Assert.Equal("S7Plus", only.Device.Name);
            Assert.Empty(only.Items);
            Assert.Single(only.ReplaceGroups);
            Assert.Contains("含待清除块 1", outcome.Detail);

            var row = store.Row("DB2");
            Assert.Equal(IgwLedgerStatus.PendingClear, row.Status);
            Assert.Equal("S7", row.DeviceKind);
            Assert.Equal(7, row.TagCount);
            Assert.StartsWith("S7Plus 设备上的旧组清除未写入", row.Error);
            Assert.Contains("设备级校验失败", row.Error);
            Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB1").Status);
        }
    }

    [Fact]
    public async Task Pending_clear_block_that_changes_is_fully_re_registered()
    {
        // PendingClear 的 DB2 本次自身变更：按变更块整块重做（内容 + 两台设备 replaceGroups），不走只清除路径
        var store = PendingClearStore();
        var igw = new FakeIgwServer();

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), Change("DB2"), CancellationToken.None);

        Assert.Contains(igw.BulkRequestsFor("S7"), TouchesDb2Content);
        Assert.Contains(igw.BulkRequestsFor("S7Plus"), ClearsDb2);
        Assert.Contains("含待清除块 0", outcome.Detail);
        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.Registered, row.Status);
        Assert.Equal(1, row.TagCount);                                      // 按本次规划
    }

    [Fact]
    public async Task Block_moved_back_keeps_the_stale_group_on_its_own_device_pending()
    {
        // DB2 台账在 S7Plus 且 PendingClear（此前从 S7 换到 S7Plus，S7 上的旧组没清掉）；本次 DB2 换回 S7：
        // S7 一片都没写入（503），S7Plus 上的清除写入 → S7Plus 已无 DB2，S7 上仍留着更早的旧组 → FailedPendingClear（S7 待清除），
        // 不能记 Failed / Rejected 丢掉「S7 上旧组待清除」
        var store = MovedStore();
        store.Rows.RemoveAll(r => r.RootName == "DB2");
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7Plus", IgwLedgerStatus.PendingClear);
        var s7Down = new FakeIgwServer { BulkResponder = r => r.Device.Name == "S7" ? Busy() : Ok() };

        await Runner(s7Down, store).RunAsync(Project(UniqueUrl()), Change("DB2"), CancellationToken.None);

        Assert.Contains(s7Down.BulkRequestsFor("S7Plus"), ClearsDb2);
        var row = store.Row("DB2");
        Assert.Equal(IgwLedgerStatus.FailedPendingClear, row.Status);
        Assert.Equal("S7", row.DeviceKind);
        Assert.StartsWith("S7 设备上的旧组清除未写入", row.Error);
        Assert.StartsWith("通用.S7_", ReadAlias(store));

        // 下次上传（只改 DB1）：S7 按下标永久拒绝 DB2 的点 → DB2 仍 FailedPendingClear（S7 旧组没被替换，不能停在 Rejected）
        var rejectDb2 = new FakeIgwServer
        {
            BulkResponder = r =>
            {
                var index = r.Items.FindIndex(i => i.GroupPath[0] == "DB2");
                return r.Device.Name == "S7" && index >= 0
                    ? (HttpStatusCode.BadRequest, $$"""{"error":"invalid_items","details":[{"index":{{index}},"reason":"地址非法"}]}""")
                    : Ok();
            },
        };
        var retry = await Runner(rejectDb2, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);

        Assert.Contains("含重试失败块 1）", retry.Detail);
        Assert.Contains("含待清除块 0", retry.Detail);
        Assert.Contains(rejectDb2.BulkRequestsFor("S7"), TouchesDb2Content);
        Assert.Equal(IgwLedgerStatus.FailedPendingClear, store.Row("DB2").Status);
        Assert.StartsWith("S7 设备上的旧组清除未写入", store.Row("DB2").Error);
        Assert.Contains("地址非法", store.Row("DB2").Error);

        // 恢复 → 整块重做写入（S7 旧组随首片替换）→ Registered
        var healthy = new FakeIgwServer();
        await Runner(healthy, store).RunAsync(Project(UniqueUrl()), Change("DB1"), CancellationToken.None);
        Assert.Contains(healthy.BulkRequestsFor("S7"), r => r.Items.Any(i => i.GroupPath[0] == "DB2") && ClearsDb2(r));
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        Assert.Equal("S7", store.Row("DB2").DeviceKind);
    }

    [Fact]
    public void Snapshot_tracks_failed_pending_clear_blocks_and_row_stats()
    {
        var rows = new List<PLC_IgwRegistrationEntity>
        {
            new() { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB2", RootGroup = "DB2", DeviceKind = "S7", Status = IgwLedgerStatus.FailedPendingClear },
            new() { RootKind = IgwLedgerRootKind.DbBlock, RootName = "DB3", RootGroup = "DB3", DeviceKind = "S7Plus", Status = IgwLedgerStatus.PendingClear,
                TagCount = 5, SkippedCount = 2, SkipSummary = "too_deep×2" },
        };

        var snapshot = IgwLedgerSnapshotBuilder.Build(rows);

        Assert.Contains("db2", snapshot.FailedPendingClearDbBlocks);
        Assert.DoesNotContain("DB2", snapshot.PendingClearDbBlocks);
        Assert.DoesNotContain("DB2", snapshot.FailedDbBlocks);
        Assert.Empty(snapshot.RegisteredRootKinds);
        Assert.Equal(new IgwLedgerDbBlockStats(5, 2, "too_deep×2") { DeviceKind = "S7Plus" }, snapshot.DbBlockStats["db3"]);
    }
}
