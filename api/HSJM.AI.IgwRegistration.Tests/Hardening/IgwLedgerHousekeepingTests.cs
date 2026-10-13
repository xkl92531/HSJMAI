using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.Hardening.HardeningFixture;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>M-5 / R-2：台账清理（**全量**运行清除「台账有、已不是存活 DB 块」的根；启动清软删行的语句）。
/// 2026-09-22 起增量（上传解析）只追加、完全不清理。</summary>
public class IgwLedgerHousekeepingTests
{
    /// <summary>
    /// 2026-09-22 只追加注册：增量（上传解析）这条链路**不清理**已不存活的块——既不发 replaceGroups，
    /// 也不删台账行。只清 IGW 不删台账会让两边失配，只删台账不清 IGW 会丢掉改名映射，所以两件都不做。
    /// 收拾残留归「注册PLC标签」全量（见下一个用例）。
    /// </summary>
    [Fact]
    public async Task Incremental_run_leaves_dead_ledger_roots_alone()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB1", "DB2" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0), ClassicRow("DB2", 2, "V", 0) }, new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        // DB2 存活、本次未变更：台账清理不该动它（不进任何 ReplaceGroups，不进 LastRemoved）
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.Registered);
        // DB9 已被删除（不在存活块里），本次上传的 deleted 参数也没带它（例如上次清除失败/Failed 残留）
        store.Seed(IgwLedgerRootKind.DbBlock, "DB9", "S7Plus", IgwLedgerStatus.Failed);
        var args = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.All(igw.BulkRequests, r => Assert.Empty(r.ReplaceGroups));   // 一个删除请求都不发
        Assert.Contains(store.Rows, r => r.RootName == "DB9");              // 台账行原样留着
        Assert.Empty(store.LastRemoved);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB1").Status);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
    }

    /// <summary>「注册PLC标签」全量仍是收拾残留的入口：死根在两台设备上都清除、台账行删除。</summary>
    [Fact]
    public async Task Full_run_still_clears_ledger_roots_of_blocks_that_no_longer_exist()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB1", "DB2" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0), ClassicRow("DB2", 2, "V", 0) }, new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "DB9", "S7Plus", IgwLedgerStatus.Failed);
        var args = new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), false, true, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.All(new[] { "S7", "S7Plus" }, device =>
            Assert.Contains(igw.BulkRequestsFor(device).SelectMany(r => r.ReplaceGroups), g => g.SequenceEqual(new[] { "DB9" })));
        Assert.DoesNotContain(store.Rows, r => r.RootName == "DB9");
        Assert.Contains((IgwLedgerRootKind.DbBlock, "DB9"), store.LastRemoved);
        Assert.DoesNotContain(store.LastRemoved, x => x.RootName == "DB2");
    }

    [Fact]
    public async Task No_change_incremental_run_leaves_the_dead_ledger_root_alone()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB1" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0) }, new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        // DB9 已不存活，但本次上传既未变更也未声明删除它（例如上次清除失败的残留）
        store.Seed(IgwLedgerRootKind.DbBlock, "DB9", "S7Plus", IgwLedgerStatus.Failed);
        // Io 已刷新以触发本次运行（无变更/删除块时 RunAsync 会直接 Skip），本身与台账清理逻辑无关
        var args = new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), true, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Empty(store.LastRemoved);                            // 只追加：死根不清
        Assert.Contains(store.Rows, r => r.RootName == "DB9");
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB1").Status);
    }

    [Fact]
    public void Purge_statement_hard_deletes_only_soft_deleted_ledger_rows()
    {
        Assert.Equal("DELETE FROM ai_plc_igwregistration WHERE isdeleted = true;", PLC_IgwRegistrationSchemaDdl.PurgeSoftDeletedSql);
    }

    /// <summary>Fix round 1 (H Task 2)：LoadDataAsync 返回 null 必须直接失败，绝不能被当成「项目无存活块」——
    /// 否则 ResolveScope 会把台账里所有已登记的根都当作已不存在而清空整个台账。</summary>
    [Fact]
    public async Task Null_data_from_store_fails_the_run_without_touching_ledger_or_bulk_endpoint()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore { ReturnNullData = true };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        var args = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Failed, outcome.Status);
        Assert.Contains("读取项目数据失败", outcome.Detail);
        Assert.Equal(0, store.LedgerWrites);
        Assert.Empty(store.LastRemoved);
        Assert.Empty(igw.BulkRequests);
        Assert.Single(store.Rows);
        Assert.Equal("DB1", store.Rows[0].RootName);
    }

    /// <summary>Fix round 1 (H Task 2)："含重试失败块 N" 只应计入仍存活的 Failed 块；已不存活的 Failed 块
    /// 不算重试（它会被台账清理当作死根清除，见上面两个测试），不能撑大展示的重试计数。</summary>
    [Fact]
    public async Task Incremental_retry_count_excludes_dead_failed_blocks_from_display()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB1", "DB2" },
                new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0), ClassicRow("DB2", 2, "V", 0) }, new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        // DB2 仍存活、上次失败：应计入重试
        store.Seed(IgwLedgerRootKind.DbBlock, "DB2", "S7", IgwLedgerStatus.Failed);
        // DB9 已不存活、上次失败：不该计入重试（只追加模式下它就留在台账里不动）
        store.Seed(IgwLedgerRootKind.DbBlock, "DB9", "S7Plus", IgwLedgerStatus.Failed);
        var args = new IgwRegistrationArgs(new[] { "DB1" }, Array.Empty<string>(), false, false, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Contains("含重试失败块 1）", outcome.Detail);
        Assert.DoesNotContain("含重试失败块 2）", outcome.Detail);
        Assert.Equal(IgwLedgerStatus.Registered, store.Row("DB2").Status);
        // 只追加：死块不再被清理，但仍然不该计入展示的重试数（上面两条断言）
        Assert.Contains(store.Rows, r => r.RootName == "DB9");
        Assert.Empty(store.LastRemoved);
    }

    /// <summary>
    /// Final fix round：没有任何请求涉及到的死根不能当成"已清除"。块名清洗后为空时规划器分不出根组、不会生成
    /// replaceGroups（此处用一个不可见的控制字符（U+0001）作块名来模拟——它在大多数编辑器/diff 里会显示成空字符串，
    /// 但不能真的改成 ""：台账快照构建器 IgwLedgerSnapshotBuilder 对 RootName 为空的行会提前过滤掉（不进 RootGroupByBlock/
    /// DeviceKindByBlock），那样这行根本不会进入 ReplaceOnly、也就无从触发本测试要验证的 RootOf（block）== null 分支）。
    /// 台账行必须保留、等人工处理——RootOf 只看块名本身，全量注册也会走到同一条空根分支，
    /// 不会重新处理这一行（不是无声删掉，IGW 上的旧组也确实还在）。
    /// </summary>
    [Fact]
    public async Task Dead_ledger_root_without_any_clear_request_is_kept()
    {
        var igw = new FakeIgwServer();
        var store = new MemoryRegistrationStore
        {
            Data = new IgwRegistrationData(new[] { "DB1" }, new List<PlannerDbRow> { ClassicRow("DB1", 1, "V", 0) },
                new List<PlannerIoTag>()),
        };
        store.Seed(IgwLedgerRootKind.DbBlock, "DB1", "S7", IgwLedgerStatus.Registered);
        store.Seed(IgwLedgerRootKind.DbBlock, "", "S7", IgwLedgerStatus.Registered); // 控制字符（非空，不会被快照构建器过滤），清洗后为空 → 无根组、无清除请求
        // 2026-09-22 起只有全量才清理死根（增量只追加），所以这条分支也只在全量下可达
        var args = new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), false, true, null);

        var outcome = await Runner(igw, store).RunAsync(Project(UniqueUrl()), args, CancellationToken.None);

        Assert.Equal(IgwRegistrationStatus.Succeeded, outcome.Status);
        Assert.Empty(store.LastRemoved);
        Assert.Contains(store.Rows, r => r.RootName == "");
        Assert.Contains("1 个根组名无法生成的块未清除，需人工处理", outcome.Detail);
    }
}
