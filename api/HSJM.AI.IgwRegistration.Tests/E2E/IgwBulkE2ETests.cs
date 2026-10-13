using System.Diagnostics;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HSJM.AI.IgwRegistration.Tests.E2E;

/// <summary>
/// 端到端共享状态：临时 IGW 地址、截获请求的 IgwBulkClient、两个 PLC 的内存存储、期望模型、各阶段耗时。
/// 场景按方法名顺序执行（<see cref="ByMethodNameOrderer"/>），后一场景依赖前一场景写入 IGW 的状态。
/// </summary>
public sealed class IgwE2EState : IDisposable
{
    public string Url { get; }
    public RecordingHandler Recorder { get; } = new();
    public IgwBulkClient Client { get; }
    public IgwApi Api { get; }
    public IgwRegistrationConfig Config { get; } = new();
    public ExpectedModel Expected { get; } = new();
    public E2EPlc Plc1 { get; }
    public E2EPlc Plc2 { get; }
    public bool FullDone { get; set; }
    public bool IncrementalDone { get; set; }
    public List<(string Phase, string Plc, long TotalMs, IReadOnlyList<BulkExchange> Exchanges, string Detail)> Phases { get; } = new();

    /// <summary>增量场景的改动（供读别名场景做正/反向校验）</summary>
    public string ChangedBlock { get; set; }
    public string DeletedBlock { get; set; }
    public string DeletedBlockOldAlias { get; set; }
    public PlannerIoTag AddedIo { get; set; }

    public IgwE2EState()
    {
        Url = Environment.GetEnvironmentVariable(IgwE2EFactAttribute.UrlVariable);
        Client = new IgwBulkClient(NullLogger<IgwBulkClient>.Instance, Recorder);
        Api = Url == null ? null : new IgwApi(Url);
        // RFC 5737 文档网段：不可能对应真实 PLC，采集侧只会连接失败
        Plc1 = new E2EPlc("plc1", 9_000_001, "192.0.2.11", "E2E_PLC1");
        Plc2 = new E2EPlc("plc2", 9_000_002, "192.0.2.12", "E2E_PLC2");
    }

    public IgwRegistrationRunner Runner(E2EPlc plc, List<string> logs) =>
        new(Client, plc.Store,
            text => { lock (logs) { logs.Add(text); } return Task.CompletedTask; },
            NullLogger.Instance, Config);

    public void Dispose()
    {
        Client.Dispose();
        Api?.Dispose();
        Recorder.Dispose();
    }
}

public sealed class E2EPlc
{
    public string Fixture { get; }
    public long ProjectId { get; }
    public string Ip { get; }
    public string Channel { get; }
    public E2EMemoryStore Store { get; }
    public IgwRegistrationProjectInfo Info { get; set; }

    public E2EPlc(string fixture, long projectId, string ip, string channel)
    {
        Fixture = fixture;
        ProjectId = projectId;
        Ip = ip;
        Channel = channel;
        Store = new E2EMemoryStore { ChannelName = channel };
    }

    public void LoadFixture(string url)
    {
        var data = RealDataFixture.Load(Fixture);
        Store.DbRows = data.DbRows.ToList();
        Store.IoTags = data.IoTags.ToList();
        Info = new IgwRegistrationProjectInfo(ProjectId, "E2E_" + Fixture, "E2E线体", Fixture.ToUpperInvariant(), url, Ip, 102);
    }
}

/// <summary>
/// HSJM → IGW 批量注册端到端（sub-project B task-9）：真实夹具（plc1/plc2）经生产编排（IgwRegistrationRunner + 内存台账）
/// 推到临时 IGW，再用 IGW 自己的工程树 / DCS 读接口核对。需 tools/igw_bulk_e2e/run_e2e.ps1 启动临时 IGW 并设置 IGW_E2E_URL。
/// </summary>
[Trait("Category", "E2E")]
[TestCaseOrderer(ByMethodNameOrderer.TypeName, ByMethodNameOrderer.AssemblyName)]
public sealed class IgwBulkE2ETests : IClassFixture<IgwE2EState>
{
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromMinutes(30);

    private readonly IgwE2EState _s;
    private readonly ITestOutputHelper _output;

    public IgwBulkE2ETests(IgwE2EState state, ITestOutputHelper output)
    {
        _s = state;
        _output = output;
    }

    private void Log(string line) => E2EResults.Write(_output, line);

    // ================================================================== A 全量

    [IgwE2EFact]
    public async Task A_Full_registration_of_plc1_and_plc2_all_requests_applied()
    {
        using var cts = new CancellationTokenSource(ScenarioTimeout);
        Log($"## E2E {DateTime.Now:yyyy-MM-dd HH:mm:ss}  IGW {_s.Url}");
        Log("");
        Log("### A 全量注册");

        Assert.True(await _s.Client.SupportsBulkAsync(_s.Url, cts.Token), "SupportsBulkAsync 应为 true");
        Log("- SupportsBulkAsync = true");

        // 生产场景：老版本 AddTags 在「通用」通道下留下的 S1500_{ip} 设备，首次批量注册前必须被清掉
        var seed = await _s.Api.AddLegacyS1500TagAsync(_s.Plc1.Ip, "LegacyFlag");
        Assert.True(seed.IsSuccess, $"预置旧设备 AddTags 失败：HTTP {seed.Http} {seed.Error}");
        var legacyName = IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, _s.Plc1.Ip);
        Assert.Contains(await _s.Api.AllDevicesAsync(), d => d.Channel == "通用" && d.Device == legacyName);
        Log($"- 预置旧设备 通用/{legacyName}（/Tag/AddTags）");

        foreach (var plc in new[] { _s.Plc1, _s.Plc2 })
        {
            plc.LoadFixture(_s.Url);
            var logs = new List<string>();
            var before = _s.Recorder.Snapshot().Count;
            var watch = Stopwatch.StartNew();
            var outcome = await _s.Runner(plc, logs).RunAsync(plc.Info,
                new IgwRegistrationArgs(Array.Empty<string>(), Array.Empty<string>(), false, true, null), cts.Token);
            watch.Stop();
            var exchanges = _s.Recorder.Snapshot().Skip(before).ToList();
            _s.Phases.Add(("A 全量", plc.Fixture, watch.ElapsedMilliseconds, exchanges, outcome.Detail));

            Log($"- {plc.Fixture}（{plc.Ip}，通道 {plc.Channel}）：DB 行 {plc.Store.DbRows.Count}、IO {plc.Store.IoTags.Count}；" +
                $"结果 {outcome.Status}，总耗时 {watch.Elapsed.TotalSeconds:0.0}s");
            Log($"  - runner：{outcome.Detail}");
            foreach (var e in exchanges)
            {
                Log($"  - {e.Device}：items {e.Request.Items.Count}，replaceGroups {e.Request.ReplaceGroups.Count} → HTTP {e.Status}" +
                    $" {e.Error} applied={e.Applied} created={e.Created} updated={e.Updated} deleted={e.Deleted}；" +
                    $"客户端 {e.ElapsedMs} ms；服务端 {e.Timing}");
            }

            Assert.All(exchanges, e => Assert.True(e.Status == 200 && e.Applied,
                $"{e.Device} 请求失败：HTTP {e.Status} {e.Error} {e.Detail}"));
            Assert.DoesNotContain(exchanges, e => e.Error == "invalid_items");
            Assert.True(outcome.Status == IgwRegistrationStatus.Succeeded, outcome.Detail);

            // 实际发送 = 独立调用规划器（同输入）的结果：请求数、每请求设备与点数一致
            var data = await plc.Store.LoadDataAsync(plc.ProjectId, true, null);
            var plan = IgwRegistrationPlanner.Plan(new PlannerInput(plc.Channel, plc.Ip, 102, data.DbRows, data.IoTags,
                Array.Empty<string>(), Array.Empty<string>(), false, true, _s.Config, null, data.LiveDbBlocks));
            Assert.Equal(plan.Requests.Select(r => (r.Request.Device.Name, r.Request.Items.Count)),
                exchanges.Select(e => (e.Device, e.Request.Items.Count)));

            foreach (var e in exchanges)
            {
                _s.Expected.Apply(e.Request);
            }

            // 台账：全部块 + IO 都是 Registered
            var ledgerRows = plc.Store.Rows.ToList();
            Assert.All(ledgerRows, r => Assert.True(r.Status == IgwLedgerStatus.Registered, $"{r.RootKind}/{r.RootName}: {r.Error}"));
            Assert.Equal(plan.Blocks.Count + 1, ledgerRows.Count);
            Log($"  - 台账 {ledgerRows.Count} 行全部 Registered；计划点数 S7 {plan.Requests.Where(r => r.Device == IgwDeviceKind.S7).Sum(r => r.Request.Items.Count)}" +
                $"、S7Plus {plan.Requests.Where(r => r.Device == IgwDeviceKind.S7Plus).Sum(r => r.Request.Items.Count)}" +
                $"（IO {plan.IoTagCount}，跳过 {string.Join("、", plan.SkipReasons.Select(kv => $"{kv.Key}×{kv.Value}"))}）");
        }

        Assert.DoesNotContain(await _s.Api.AllDevicesAsync(), d => d.Channel == "通用" && d.Device == legacyName);
        Log($"- 旧设备 通用/{legacyName} 已被 runner 的旧设备清理删除");
        _s.FullDone = true;
    }

    // ================================================================== B 工程树

    [IgwE2EFact]
    public async Task B_Tree_matches_the_plan()
    {
        Assert.True(_s.FullDone, "A 全量注册未完成");
        Log("");
        Log("### B 工程树核对");
        await AssertTreeMatchesExpectedAsync("B");

        // /api/v1/project/tags?groupId：抽一个有直属点的根组，直属点名集合与期望一致
        var channels = await _s.Api.ChannelsAsync();
        var channel = channels.Single(c => c.Name == _s.Plc1.Channel);
        foreach (var device in (await _s.Api.TreeChildrenAsync("channel", channel.Id)).Where(n => n.Kind == "device"))
        {
            var roots = await _s.Api.TreeChildrenAsync("device", device.Id);
            var sample = roots.FirstOrDefault(r => r.Kind == "group" && r.DirectTagCount > 0);
            if (sample == null)
            {
                continue;
            }
            var actual = (await _s.Api.GroupTagsAsync(sample.Id)).Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var expected = _s.Expected.Device(_s.Plc1.Channel, device.Name).Values
                .Where(i => i.GroupPath.Count == 1 && i.GroupPath[0] == sample.Name)
                .Select(i => i.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, actual);
            Log($"- tags?groupId：{_s.Plc1.Channel}/{device.Name}/{sample.Name} 直属点 {actual.Count} 个与期望一致");
        }

        var (_, total) = await _s.Api.SummaryAsync();
        Assert.Equal(_s.Expected.TotalTags, total);
        Log($"- summary.totalTagCount = {total} = 期望 {_s.Expected.TotalTags}");
    }

    // ================================================================== C 增量

    [IgwE2EFact]
    public async Task C_Incremental_change_delete_and_io_refresh()
    {
        Assert.True(_s.FullDone, "A 全量注册未完成");
        using var cts = new CancellationTokenSource(ScenarioTimeout);
        Log("");
        Log("### C 增量（改 1 块、删 1 块、IO 刷新）");
        var plc = _s.Plc1;
        var store = plc.Store;
        var ledgerBefore = await store.GetLedgerAsync(plc.ProjectId);

        var rowsByBlock = store.DbRows.GroupBy(r => r.BlockName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var blocks = store.LiveBlocks();
        var changed = blocks.First(b => ledgerBefore.DeviceKindByBlock.TryGetValue(b, out var k) && k == IgwDeviceKind.S7Plus
                                        && rowsByBlock[b].Count >= 3);
        var deleted = blocks.FirstOrDefault(b => ledgerBefore.DeviceKindByBlock.TryGetValue(b, out var k) && k == IgwDeviceKind.S7
                                                 && !string.Equals(b, changed, StringComparison.OrdinalIgnoreCase))
                      ?? blocks.First(b => !string.Equals(b, changed, StringComparison.OrdinalIgnoreCase));
        var changedRoot = ledgerBefore.RootGroupByBlock[changed];
        var deletedRoot = ledgerBefore.RootGroupByBlock[deleted];
        var deletedKind = ledgerBefore.DeviceKindByBlock[deleted];
        var dropped = rowsByBlock[changed][0];
        var droppedOldAlias = IgwReadAliasResolver.ResolveDb(plc.Ip, ledgerBefore, changed, dropped.VariablePath);
        _s.DeletedBlockOldAlias = IgwReadAliasResolver.ResolveDb(plc.Ip, ledgerBefore, deleted, rowsByBlock[deleted][0].VariablePath);
        var added = new PlannerDbRow(changed, dropped.BlockNumber, "E2E_Added", "Int", null, null, "e2e 新增行");

        // 改块：删首行、在块末尾加一行；删块：去掉全部行（不再是存活块）；IO：删首个点、加一个新点
        var newRows = new List<PlannerDbRow>(store.DbRows.Count);
        var lastOfChanged = rowsByBlock[changed][^1];
        foreach (var row in store.DbRows)
        {
            if (ReferenceEquals(row, dropped) || string.Equals(row.BlockName, deleted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            newRows.Add(row);
            if (ReferenceEquals(row, lastOfChanged))
            {
                newRows.Add(added);
            }
        }
        store.DbRows = newRows;
        var droppedIo = store.IoTags[0];
        _s.AddedIo = new PlannerIoTag("E2E表", "E2E_IoAdded", "%I900.0", "Bool", "e2e 新增 IO");
        store.IoTags = store.IoTags.Skip(1).Append(_s.AddedIo).ToList();
        _s.ChangedBlock = changed;
        _s.DeletedBlock = deleted;
        Log($"- 改块 {changed}（S7Plus，根组 {changedRoot}）：删行 {dropped.VariablePath}，加行 E2E_Added；" +
            $"删块 {deleted}（{deletedKind}，根组 {deletedRoot}）；IO 删 {droppedIo.TableName}/{droppedIo.TagName}，加 E2E表/E2E_IoAdded");

        var logs = new List<string>();
        var before = _s.Recorder.Snapshot().Count;
        var watch = Stopwatch.StartNew();
        var outcome = await _s.Runner(plc, logs).RunAsync(plc.Info,
            new IgwRegistrationArgs(new[] { changed }, new[] { deleted }, true, false, null), cts.Token);
        watch.Stop();
        var exchanges = _s.Recorder.Snapshot().Skip(before).ToList();
        _s.Phases.Add(("C 增量", plc.Fixture, watch.ElapsedMilliseconds, exchanges, outcome.Detail));
        Log($"- 结果 {outcome.Status}，总耗时 {watch.Elapsed.TotalSeconds:0.0}s；runner：{outcome.Detail}");
        foreach (var e in exchanges)
        {
            Log($"  - {e.Device}：items {e.Request.Items.Count}，replaceGroups [{string.Join(", ", e.Request.ReplaceGroups.Select(g => string.Join("/", g)))}]" +
                $" → HTTP {e.Status} {e.Error} created={e.Created} updated={e.Updated} deleted={e.Deleted}；客户端 {e.ElapsedMs} ms；服务端 {e.Timing}");
        }
        Assert.All(exchanges, e => Assert.True(e.Status == 200 && e.Applied, $"{e.Device}：HTTP {e.Status} {e.Error} {e.Detail}"));
        Assert.True(outcome.Status == IgwRegistrationStatus.Succeeded, outcome.Detail);

        // 删块在两台设备上都带 replaceGroups（设备可能变过，两边都清）
        foreach (var device in new[] { IgwTagNaming.S7DeviceName, IgwTagNaming.S7PlusDeviceName })
        {
            Assert.Contains(exchanges, e => e.Device == device
                && e.Request.ReplaceGroups.Any(g => g.Count == 1 && g[0] == deletedRoot));
        }
        foreach (var e in exchanges)
        {
            _s.Expected.Apply(e.Request);
        }

        // 树：全部设备（含未改动的 PLC2）逐点与期望一致
        await AssertTreeMatchesExpectedAsync("C");

        // 明确断言：删块根组在两台设备上都不在；改块新增行在、被删行不在；IO 新点在、被删点不在
        var devices = await DevicesOfAsync(plc.Channel);
        foreach (var (name, id) in devices)
        {
            var roots = (await _s.Api.TreeChildrenAsync("device", id)).Select(n => n.Name).ToList();
            Assert.DoesNotContain(deletedRoot, roots);
        }
        var s7PlusTags = (await _s.Api.DeviceTagsDeepAsync(devices.Single(d => d.Name == IgwTagNaming.S7PlusDeviceName).Id)).Tags;
        Assert.Contains(s7PlusTags, t => t.GroupPath.Count >= 1 && t.GroupPath[0] == changedRoot && t.Name == "E2E_Added");
        var droppedPath = AliasToPath(droppedOldAlias, IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, plc.Ip));
        Assert.DoesNotContain(s7PlusTags, t => string.Join("", t.GroupPath.Append(t.Name)) == droppedPath);
        var s7Tags = (await _s.Api.DeviceTagsDeepAsync(devices.Single(d => d.Name == IgwTagNaming.S7DeviceName).Id)).Tags;
        Assert.Contains(s7Tags, t => t.GroupPath.SequenceEqual(new[] { IgwTagNaming.IoRootGroup, "E2E表" }) && t.Name == "E2E_IoAdded" && t.Address == "I900.0");
        Assert.DoesNotContain(s7Tags, t => t.GroupPath.Count == 2 && t.GroupPath[0] == IgwTagNaming.IoRootGroup
                                          && t.GroupPath[1] == IgwTagNaming.SanitizeName(droppedIo.TableName)
                                          && t.Name == IgwTagNaming.SanitizeName(droppedIo.TagName));

        // 台账：删块行已移除；改块与 IO 行 Registered
        var ledgerAfter = await store.GetLedgerAsync(plc.ProjectId);
        Assert.False(ledgerAfter.DeviceKindByBlock.ContainsKey(deleted), "删块台账行应被移除");
        Assert.Equal(changedRoot, ledgerAfter.RootGroupByBlock[changed]);
        Assert.All(store.Rows.Where(r => r.RootName == changed || r.RootKind == IgwLedgerRootKind.Io),
            r => Assert.Equal(IgwLedgerStatus.Registered, r.Status));
        Log($"- 核对通过：{deletedRoot} 在 S7/S7Plus 上均已删除；{changedRoot} 含 E2E_Added、不含被删行；IO 新点在、旧点不在；台账已更新");
        _s.IncrementalDone = true;
    }

    // ================================================================== D 读别名

    [IgwE2EFact]
    public async Task D_Read_aliases_resolve_through_ReadTagsAsList()
    {
        Assert.True(_s.FullDone, "A 全量注册未完成");
        using var cts = new CancellationTokenSource(ScenarioTimeout);
        Log("");
        Log("### D 读别名（/Tag/ReadTagsAsList）");

        // GRAPH：给 PLC1 注册一个假 GRAPH 实例 DB（生产 GraphIgwRegistrar 路径，同一台账）
        const string graphDb = "DB_E2E_Graph";
        var logs = new List<string>();
        var graph = new GraphIgwRegistrar(_s.Client, _s.Plc1.Store, t => { logs.Add(t); return Task.CompletedTask; },
            NullLogger.Instance, _s.Config);
        var before = _s.Recorder.Snapshot().Count;
        var graphResult = await graph.RegisterAsync(new GraphIgwProjectInfo(_s.Plc1.ProjectId, _s.Plc1.Info.ProjectName,
                _s.Plc1.Info.ProductionLineName, _s.Plc1.Info.PlcCode, _s.Url, _s.Plc1.Ip, 102),
            new[] { new GraphIgwDbInput(graphDb, "OP010", 15, true, Array.Empty<string>()) }, cts.Token);
        var graphExchanges = _s.Recorder.Snapshot().Skip(before).ToList();
        _s.Phases.Add(("D GRAPH", _s.Plc1.Fixture, graphExchanges.Sum(e => e.ElapsedMs), graphExchanges, graphResult.Summary));
        Assert.True(graphResult.ByDb[graphDb].Registered, graphResult.Summary);
        foreach (var e in graphExchanges)
        {
            _s.Expected.Apply(e.Request);
        }
        var graphDevice = (await DevicesOfAsync(_s.Plc1.Channel)).Single(d => d.Name == IgwTagNaming.GraphDeviceName);
        var graphEntity = await _s.Api.DeviceEntityAsync(graphDevice.Id);
        Assert.Equal(IgwTagNaming.LogicalDeviceName(IgwDeviceKind.Graph, _s.Plc1.Ip), graphEntity.Settings["dcsLogicalDevice"]);
        Log($"- GRAPH 注册 {graphDb}（工站 OP010）：{graphResult.Summary}");

        var snap1 = await _s.Plc1.Store.GetLedgerAsync(_s.Plc1.ProjectId);
        var snap2 = await _s.Plc2.Store.GetLedgerAsync(_s.Plc2.ProjectId);
        var samples = new List<(string Label, string Alias)>();

        // 只抽真正注册进 IGW 的点（规划器按设计跳过的行——未展开 UDT、Time、PD_TEL 报文等——本来就没有物理点）
        bool Registered(E2EPlc plc, IgwDeviceKind kind, string alias)
        {
            var deviceName = kind switch
            {
                IgwDeviceKind.S7 => IgwTagNaming.S7DeviceName,
                IgwDeviceKind.S7Plus => IgwTagNaming.S7PlusDeviceName,
                _ => IgwTagNaming.GraphDeviceName,
            };
            var key = AliasToPath(alias, IgwTagNaming.LogicalDeviceName(kind, plc.Ip)).ToLowerInvariant();
            return _s.Expected.Device(plc.Channel, deviceName).ContainsKey(key);
        }

        void AddDbSample(string label, E2EPlc plc, IgwLedgerSnapshot snap, Func<PlannerDbRow, bool> predicate)
        {
            var row = plc.Store.DbRows.FirstOrDefault(r => snap.DeviceKindByBlock.TryGetValue(r.BlockName, out var kind) && predicate(r)
                && Registered(plc, kind, IgwReadAliasResolver.ResolveDb(plc.Ip, snap, r.BlockName, r.VariablePath)));
            if (row == null)
            {
                Log($"- （{label}：{plc.Fixture} 无符合条件的行，跳过）");
                return;
            }
            samples.Add(($"{label} [{plc.Fixture} {row.BlockName}.{row.VariablePath}]",
                IgwReadAliasResolver.ResolveDb(plc.Ip, snap, row.BlockName, row.VariablePath)));
        }

        bool IsKind(IgwLedgerSnapshot snap, PlannerDbRow r, IgwDeviceKind kind) =>
            snap.DeviceKindByBlock.TryGetValue(r.BlockName, out var k) && k == kind;

        AddDbSample("S7 经典地址", _s.Plc1, snap1, r => IsKind(snap1, r, IgwDeviceKind.S7));
        AddDbSample("S7 经典地址", _s.Plc2, snap2, r => IsKind(snap2, r, IgwDeviceKind.S7));
        AddDbSample("S7Plus 符号", _s.Plc1, snap1, r => IsKind(snap1, r, IgwDeviceKind.S7Plus) && !r.VariablePath.Contains('['));
        AddDbSample("S7Plus 深路径", _s.Plc2, snap2, r => IsKind(snap2, r, IgwDeviceKind.S7Plus) && r.VariablePath.Count(c => c == '.') >= 3);
        AddDbSample("S7Plus 数组元素", _s.Plc1, snap1, r => IsKind(snap1, r, IgwDeviceKind.S7Plus) && r.VariablePath.Contains('['));
        AddDbSample("S7Plus 数组元素", _s.Plc2, snap2, r => IsKind(snap2, r, IgwDeviceKind.S7Plus) && r.VariablePath.Contains('['));
        AddDbSample("块根组改名", _s.Plc1, snap1, r => snap1.RootGroupByBlock.TryGetValue(r.BlockName, out var root)
                                                   && root != IgwTagNaming.SanitizeName(r.BlockName));
        AddDbSample("块根组改名", _s.Plc2, snap2, r => snap2.RootGroupByBlock.TryGetValue(r.BlockName, out var root)
                                                   && root != IgwTagNaming.SanitizeName(r.BlockName));
        if (_s.IncrementalDone)
        {
            samples.Add(($"增量新增行 [{_s.ChangedBlock}.E2E_Added]", IgwReadAliasResolver.ResolveDb(_s.Plc1.Ip, snap1, _s.ChangedBlock, "E2E_Added")));
            samples.Add(("增量新增 IO [E2E表/E2E_IoAdded]", IgwReadAliasResolver.ResolveIo(_s.Plc1.Ip, snap1, _s.AddedIo.TableName, _s.AddedIo.TagName)));
        }
        foreach (var (plc, snap) in new[] { (_s.Plc1, snap1), (_s.Plc2, snap2) })
        {
            var io = plc.Store.IoTags.First(t => Registered(plc, IgwDeviceKind.S7, IgwReadAliasResolver.ResolveIo(plc.Ip, snap, t.TableName, t.TagName)));
            samples.Add(($"IO [{plc.Fixture} {io.TableName}/{io.TagName}]", IgwReadAliasResolver.ResolveIo(plc.Ip, snap, io.TableName, io.TagName)));
            var dbRename = snap.DbRenames.FirstOrDefault();
            if (dbRename.Key.Block != null)
            {
                samples.Add(($"DB 重名改名 [{plc.Fixture} {dbRename.Key.Block}.{dbRename.Key.VariablePath} → {dbRename.Value}]",
                    IgwReadAliasResolver.ResolveDb(plc.Ip, snap, dbRename.Key.Block, dbRename.Key.VariablePath)));
            }
            var ioRename = snap.IoRenames.FirstOrDefault();
            if (ioRename.Key.Table != null)
            {
                samples.Add(($"IO 重名改名 [{plc.Fixture} {ioRename.Key.Table}/{ioRename.Key.Tag} → {ioRename.Value}]",
                    IgwReadAliasResolver.ResolveIo(plc.Ip, snap, ioRename.Key.Table, ioRename.Key.Tag)));
            }
        }
        samples.Add(("GRAPH EXEC_BITS 容器", IgwReadAliasResolver.ResolveGraph(_s.Plc1.Ip, snap1, graphDb, IgwReadAliasResolver.GraphExecBitsMember, null)));
        samples.Add(("GRAPH S_NO", IgwReadAliasResolver.ResolveGraph(_s.Plc1.Ip, snap1, graphDb, IgwReadAliasResolver.GraphStepNoMember, null)));

        var failures = new List<string>();
        foreach (var (label, alias) in samples)
        {
            var read = await _s.Api.ReadTagsAsListAsync(alias);
            var resolved = read.IsSuccess || (read.Error?.StartsWith("点位质量异常", StringComparison.Ordinal) ?? false);
            Log($"- {(resolved ? "OK " : "FAIL")} {label}：`{alias}` → HTTP {read.Http} isSuccess={read.IsSuccess} " +
                $"{(read.IsSuccess ? "value=" + read.Back : read.Error)}（{read.ElapsedMs} ms）");
            if (!resolved)
            {
                failures.Add($"{label} {alias}: {read.Error}");
            }
        }

        // 附加信息（不断言）：EXEC_BITS 单元素别名（读链不用，只观察 IGW 数组元素解析）
        var element = IgwReadAliasResolver.ResolveGraph(_s.Plc1.Ip, snap1, graphDb, IgwReadAliasResolver.GraphExecBitsMember, null) + "[3]";
        var elementRead = await _s.Api.ReadTagsAsListAsync(element);
        Log($"- （信息）`{element}` → isSuccess={elementRead.IsSuccess} {elementRead.Error}");

        // 反向：被删块的旧别名必须是「点位不存在」（证明上面的判定能区分）
        if (_s.DeletedBlockOldAlias != null)
        {
            var gone = await _s.Api.ReadTagsAsListAsync(_s.DeletedBlockOldAlias);
            Log($"- 反向：已删块旧别名 `{_s.DeletedBlockOldAlias}` → isSuccess={gone.IsSuccess} {gone.Error}");
            Assert.False(gone.IsSuccess);
            Assert.StartsWith("点位不存在", gone.Error);
        }

        Assert.True(failures.Count == 0, "以下别名未能解析：\n" + string.Join("\n", failures));
    }

    // ================================================================== E 旧设备清理

    [IgwE2EFact]
    public async Task E_Remove_legacy_devices_only_touches_the_target_ip()
    {
        Assert.True(_s.FullDone, "A 全量注册未完成");
        using var cts = new CancellationTokenSource(ScenarioTimeout);
        Log("");
        Log("### E 旧设备清理（RemoveLegacyDevicesAsync）");
        const string ipA = "192.0.2.201";
        const string ipB = "192.0.2.202";
        // 两类旧设备：老 AddTags 标量设备 通用/S1500_{ip}、老 GRAPH Plus 拓扑 通用_S7Plus/S7Plus_{ip}（经管理面 mutation 构造）
        var created = new List<(string Channel, string Logical, string Ip, string Id)>();
        foreach (var ip in new[] { ipA, ipB })
        {
            foreach (var (channel, logical) in new[]
                     {
                         ("通用", IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, ip)),
                         ("通用_S7Plus", IgwTagNaming.LogicalDeviceName(IgwDeviceKind.Graph, ip)),
                     })
            {
                created.Add((channel, logical, ip, await _s.Api.CreateLegacyDeviceAsync(channel, logical, ip)));
            }
        }
        var before = await _s.Api.AllDevicesAsync();
        foreach (var c in created)
        {
            Assert.Contains(before, d => d.DeviceId == c.Id && d.Channel == c.Channel && d.Device == c.Logical);
        }
        Log($"- 经 /api/v1/project/mutations 预置 {string.Join("、", created.Select(c => $"{c.Channel}/{c.Logical}"))}；共 {before.Count} 台设备");

        var watch = Stopwatch.StartNew();
        var removed = await _s.Client.RemoveLegacyDevicesAsync(_s.Url, ipA, cts.Token);
        watch.Stop();
        Log($"- RemoveLegacyDevicesAsync({ipA}) → [{string.Join("；", removed)}]（{watch.ElapsedMilliseconds} ms）");
        Assert.Equal(2, removed.Count);

        var after = await _s.Api.AllDevicesAsync();
        var removedIds = created.Where(c => c.Ip == ipA).Select(c => c.Id).ToHashSet();
        Assert.Equal(before.Where(d => !removedIds.Contains(d.DeviceId)).OrderBy(d => d.DeviceId),
            after.OrderBy(d => d.DeviceId));
        foreach (var c in created.Where(c => c.Ip == ipB))
        {
            Assert.Contains(after, d => d.DeviceId == c.Id);
        }

        // 幂等：再次清理无匹配 → 返回空
        Assert.Empty(await _s.Client.RemoveLegacyDevicesAsync(_s.Url, ipA, cts.Token));

        // E2E 通道下全部设备的点位一个不少（与期望模型逐点比对）
        await AssertTreeMatchesExpectedAsync("E");
        Log($"- {ipA} 的 2 台旧设备已删除；{ipB} 的旧设备与全部 E2E 设备保持不变（{after.Count} 台）");

        Assert.Equal(2, (await _s.Client.RemoveLegacyDevicesAsync(_s.Url, ipB, cts.Token)).Count);
    }

    // ================================================================== F 耗时

    [IgwE2EFact]
    public void F_Timing_summary()
    {
        Log("");
        Log("### F 耗时汇总");
        Log("| 阶段 | PLC | 总耗时 s | 请求数 | 点数 | 请求明细（设备 items 客户端ms / 服务端 timing） |");
        Log("|---|---|---|---|---|---|");
        foreach (var (phase, plc, totalMs, exchanges, _) in _s.Phases)
        {
            var detail = string.Join("<br>", exchanges.Select(e => $"{e.Device} {e.Request.Items.Count} {e.ElapsedMs}ms / {e.Timing}"));
            Log($"| {phase} | {plc} | {totalMs / 1000.0:0.0} | {exchanges.Count} | {exchanges.Sum(e => e.Request.Items.Count)} | {detail} |");
        }
        Assert.NotEmpty(_s.Phases);
    }

    // ================================================================== helpers

    private async Task<List<(string Name, string Id)>> DevicesOfAsync(string channelName)
    {
        var channel = (await _s.Api.ChannelsAsync()).Single(c => c.Name == channelName);
        return (await _s.Api.TreeChildrenAsync("channel", channel.Id))
            .Where(n => n.Kind == "device").Select(n => (n.Name, n.Id)).ToList();
    }

    /// <summary>点路径（组…+点名，"" 连接）← 读别名 通用.{逻辑设备}.{组…}.{点}（逻辑设备名含 IP 的 '.'，按前缀剥离；其余段内不含 '.'，清洗保证）</summary>
    private static string AliasToPath(string alias, string logicalDevice)
    {
        var prefix = "通用." + logicalDevice + ".";
        Assert.StartsWith(prefix, alias);
        return string.Join("", alias[prefix.Length..].Split('.'));
    }

    /// <summary>
    /// 两个 E2E 通道下每台设备：设备集合、endpoint、dcsLogicalDevice、根组集合（DB 树接口）与期望一致；
    /// 运行中工程的完整子树与期望逐点一致（组路径 / 点名 / 地址 / 类型 / 扫描周期 / 数组维度），根组递归点数一致。
    /// </summary>
    private async Task AssertTreeMatchesExpectedAsync(string phase)
    {
        var channels = await _s.Api.ChannelsAsync();
        foreach (var plc in new[] { _s.Plc1, _s.Plc2 })
        {
            var channel = channels.SingleOrDefault(c => c.Name == plc.Channel);
            Assert.True(channel != null, $"通道 {plc.Channel} 不存在");
            var devices = (await _s.Api.TreeChildrenAsync("channel", channel.Id)).Where(n => n.Kind == "device").ToList();
            var expectedDevices = _s.Expected.DeviceKeys
                .Where(k => k.StartsWith(plc.Channel + "/", StringComparison.OrdinalIgnoreCase))
                .Select(k => k[(plc.Channel.Length + 1)..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(expectedDevices, devices.Select(d => d.Name).OrderBy(x => x, StringComparer.Ordinal).ToList());

            foreach (var device in devices)
            {
                var kind = device.Name switch
                {
                    IgwTagNaming.S7DeviceName => IgwDeviceKind.S7,
                    IgwTagNaming.S7PlusDeviceName => IgwDeviceKind.S7Plus,
                    _ => IgwDeviceKind.Graph,
                };
                var entity = await _s.Api.DeviceEntityAsync(device.Id);
                Assert.Equal($"{plc.Ip}:102", entity.Endpoint);
                Assert.Equal(IgwTagNaming.LogicalDeviceName(kind, plc.Ip), entity.Settings.GetValueOrDefault("dcsLogicalDevice"));

                var expected = _s.Expected.Device(plc.Channel, device.Name);
                var expectedRoots = expected.Values.GroupBy(i => i.GroupPath[0], StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

                // DB 树接口（SQLite 工程库）：根组集合
                var treeRoots = (await _s.Api.TreeChildrenAsync("device", device.Id)).Where(n => n.Kind == "group")
                    .Select(n => n.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
                Assert.Equal(expectedRoots.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(), treeRoots);

                // 运行中工程：逐点比对 + 根组递归点数
                var (tags, rootTotals) = await _s.Api.DeviceTagsDeepAsync(device.Id);
                var problems = new List<string>();
                var actualByKey = new Dictionary<string, ActualTag>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    if (!actualByKey.TryAdd(ExpectedModel.ItemKey(tag.GroupPath, tag.Name), tag))
                    {
                        problems.Add($"重复点 {string.Join("/", tag.GroupPath)}/{tag.Name}");
                    }
                }
                foreach (var (key, item) in expected)
                {
                    if (!actualByKey.TryGetValue(key, out var actual))
                    {
                        problems.Add($"缺少 {string.Join("/", item.GroupPath)}/{item.Name}");
                        continue;
                    }
                    if (!actual.GroupPath.SequenceEqual(item.GroupPath) || actual.Name != item.Name
                        || actual.Address != item.Address
                        || !string.Equals(actual.DataType, item.DataType, StringComparison.OrdinalIgnoreCase)
                        || actual.ScanRateMs != item.ScanRateMs
                        || (item.Array == null) != (actual.ArrayDimensions == null)
                        || (item.Array != null && !item.Array.Dimensions.SequenceEqual(actual.ArrayDimensions)))
                    {
                        problems.Add($"不一致 {string.Join("/", item.GroupPath)}/{item.Name}：期望 {item.Address} {item.DataType} {item.ScanRateMs}ms，" +
                                     $"实际 {string.Join("/", actual.GroupPath)}/{actual.Name} {actual.Address} {actual.DataType} {actual.ScanRateMs}ms");
                    }
                }
                foreach (var key in actualByKey.Keys.Where(k => !expected.ContainsKey(k)))
                {
                    var extra = actualByKey[key];
                    problems.Add($"多余 {string.Join("/", extra.GroupPath)}/{extra.Name}");
                }
                foreach (var (root, count) in expectedRoots)
                {
                    if (!rootTotals.TryGetValue(root, out var actualCount) || actualCount != count)
                    {
                        problems.Add($"根组 {root} 点数：期望 {count}，实际 {(rootTotals.TryGetValue(root, out var c) ? c : -1)}");
                    }
                }
                Assert.True(problems.Count == 0,
                    $"[{phase}] {plc.Channel}/{device.Name} 与期望不一致 {problems.Count} 处：\n" + string.Join("\n", problems.Take(20)));
                Log($"- [{phase}] {plc.Channel}/{device.Name}：endpoint {entity.Endpoint}，dcsLogicalDevice {entity.Settings["dcsLogicalDevice"]}，" +
                    $"根组 {treeRoots.Count}，点 {tags.Count}（逐点一致）");
            }
        }
    }
}
