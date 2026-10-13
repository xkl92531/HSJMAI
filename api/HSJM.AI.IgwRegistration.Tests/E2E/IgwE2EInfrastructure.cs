using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace HSJM.AI.IgwRegistration.Tests.E2E;

/// <summary>
/// 端到端用例标记：环境变量 IGW_E2E_URL 未设置时跳过（不算失败）。
/// 由 tools/igw_bulk_e2e/run_e2e.ps1 启动临时 IGW 后设置该变量再跑 dotnet test --filter Category=E2E。
/// </summary>
public sealed class IgwE2EFactAttribute : FactAttribute
{
    public const string UrlVariable = "IGW_E2E_URL";

    public IgwE2EFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable)))
        {
            Skip = $"未设置 {UrlVariable}：端到端用例需先用 tools/igw_bulk_e2e/run_e2e.ps1 启动临时 IGW";
        }
    }
}

/// <summary>按方法名排序执行（端到端场景共享同一个临时 IGW，场景之间有先后依赖：A 全量 → B 树 → C 增量 → D 读别名 → E 旧设备 → F 耗时）</summary>
public sealed class ByMethodNameOrderer : ITestCaseOrderer
{
    public const string TypeName = "HSJM.AI.IgwRegistration.Tests.E2E.ByMethodNameOrderer";
    public const string AssemblyName = "HSJM.AI.IgwRegistration.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}

/// <summary>结果记录：写测试输出，并追加到 IGW_E2E_RESULTS 指向的 Markdown 文件（脚本结束时打印）</summary>
public static class E2EResults
{
    private static readonly object Sync = new();

    public static void Write(ITestOutputHelper output, string line)
    {
        output?.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("IGW_E2E_RESULTS");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        lock (Sync)
        {
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}

/// <summary>一次批量写入的请求/响应记录（由 <see cref="RecordingHandler"/> 截获 IgwBulkClient 的真实 HTTP 交互）</summary>
public sealed record BulkExchange(string Channel, string Device, IgwBulkRequest Request, int Status, string Error, string Detail,
    bool Applied, string Timing, int Created, int Updated, int Deleted, long ElapsedMs, string RawResponse);

/// <summary>
/// 截获 IgwBulkClient 的批量写入：记录请求体（反序列化回 <see cref="IgwBulkRequest"/>，用于推导期望树）、状态码、
/// 响应 error/timing 与客户端侧耗时。探测请求（body = {}）不记录。
/// </summary>
public sealed class RecordingHandler : DelegatingHandler
{
    private readonly List<BulkExchange> _exchanges = new();

    public RecordingHandler()
        : base(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
    }

    public IReadOnlyList<BulkExchange> Snapshot()
    {
        lock (_exchanges)
        {
            return _exchanges.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isBulk = request.Method == HttpMethod.Post
                     && request.RequestUri!.AbsolutePath.EndsWith("/api/v1/project/bulk/tags", StringComparison.Ordinal);
        var body = isBulk && request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
        var watch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        watch.Stop();
        if (!isBulk || body == "{}")
        {
            return response;
        }

        await response.Content.LoadIntoBufferAsync();
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var bulk = JsonSerializer.Deserialize<IgwBulkRequest>(body, IgwBulkJson.Options);
        string error = null, detail = null, timing = null;
        var applied = false;
        int created = 0, updated = 0, deleted = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            error = Str(root, "error");
            detail = root.TryGetProperty("details", out var details) ? details.GetRawText() : Str(root, "detail");
            timing = Str(root, "timing");
            applied = root.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.True;
            created = Int(root, "created");
            updated = Int(root, "updated");
            deleted = Int(root, "deleted");
        }
        catch (JsonException)
        {
            detail = raw.Length > 500 ? raw[..500] : raw;
        }

        lock (_exchanges)
        {
            _exchanges.Add(new BulkExchange(bulk.Channel?.Name, bulk.Device?.Name, bulk, (int)response.StatusCode, error, detail,
                applied, timing, created, updated, deleted, watch.ElapsedMilliseconds, raw.Length > 2000 ? raw[..2000] : raw));
        }
        return response;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

/// <summary>
/// 内存注册存储：与生产 DbStore 同语义——LiveDbBlocks 始终是项目全部块（按块出现顺序），DbRows 只含 rowBlocks 范围，
/// ReplaceLedgerAsync 同 PLC_IgwRegistrationService.ReplaceForProjectAsync（按 (RootKind, RootName) 替换 + 移除），
/// GetLedgerAsync 走生产同款 <see cref="IgwLedgerSnapshotBuilder"/>。
/// </summary>
public sealed class E2EMemoryStore : IIgwRegistrationStore
{
    public string ChannelName { get; set; }
    public List<PlannerDbRow> DbRows { get; set; } = new();
    public List<PlannerIoTag> IoTags { get; set; } = new();
    public List<PLC_IgwRegistrationEntity> Rows { get; } = new();

    public IReadOnlyList<string> LiveBlocks() =>
        DbRows.Select(r => r.BlockName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public Task<bool> HasGraphTargetsAsync(long projectId) => Task.FromResult(false);

    /// <summary>端到端夹具里的项目始终存活</summary>
    public Task<bool> IsProjectRemovedAsync(long projectId) => Task.FromResult(false);

    public Task<string> GetChannelNameAsync(long projectId) => Task.FromResult(ChannelName);

    public Task SetChannelNameAsync(long projectId, string channelName)
    {
        ChannelName = channelName;
        return Task.CompletedTask;
    }

    public Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId)
    {
        lock (Rows)
        {
            return Task.FromResult(IgwLedgerSnapshotBuilder.Build(Rows.ToList()));
        }
    }

    public Task<IgwRegistrationData> LoadDataAsync(long projectId, bool includeIoTags, IReadOnlyCollection<string> rowBlocks)
    {
        var wanted = rowBlocks == null ? null : new HashSet<string>(rowBlocks, StringComparer.OrdinalIgnoreCase);
        var rows = DbRows.Where(r => wanted == null || wanted.Contains(r.BlockName)).ToList();
        // 生产取数会过叶子行过滤（容器行丢弃）；夹具本身已是叶子行，这里同样过一遍保持一致
        rows = IgwLeafRowFilter.FilterLeaves(rows, r => r.BlockName, r => r.VariablePath);
        return Task.FromResult(new IgwRegistrationData(LiveBlocks(), rows,
            includeIoTags ? IoTags.ToList() : new List<PlannerIoTag>()));
    }

    public Task ReplaceLedgerAsync(long projectId, IReadOnlyList<PLC_IgwRegistrationEntity> rows,
        IReadOnlyList<(string RootKind, string RootName)> removedRoots)
    {
        lock (Rows)
        {
            var keys = rows.Select(r => (r.RootKind, r.RootName)).Concat(removedRoots).ToHashSet();
            Rows.RemoveAll(r => keys.Contains((r.RootKind, r.RootName)));
            foreach (var row in rows)
            {
                row.ProjectId = projectId;
                Rows.Add(row);
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// 期望模型：按 IGW 批量接口语义（replaceGroups 先整棵删除，再按「组路径 + 点名」大小写不敏感 upsert）
/// 回放实际发送且返回 200 的请求，得到每台设备应有的点位集合。
/// </summary>
public sealed class ExpectedModel
{
    private readonly Dictionary<string, Dictionary<string, IgwBulkItem>> _devices = new(StringComparer.OrdinalIgnoreCase);

    public static string DeviceKey(string channel, string device) => channel + "/" + device;

    public static string ItemKey(IEnumerable<string> groupPath, string name) =>
        string.Join(IgwTagNaming.KeySeparator, groupPath.Append(name)).ToLowerInvariant();

    public void Apply(IgwBulkRequest request)
    {
        var key = DeviceKey(request.Channel.Name, request.Device.Name);
        if (!_devices.TryGetValue(key, out var items))
        {
            items = new Dictionary<string, IgwBulkItem>(StringComparer.Ordinal);
            _devices[key] = items;
        }
        foreach (var group in request.ReplaceGroups)
        {
            var prefix = string.Join(IgwTagNaming.KeySeparator, group).ToLowerInvariant() + IgwTagNaming.KeySeparator;
            foreach (var k in items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                items.Remove(k);
            }
        }
        foreach (var item in request.Items)
        {
            items[ItemKey(item.GroupPath, item.Name)] = item;
        }
    }

    public IReadOnlyDictionary<string, IgwBulkItem> Device(string channel, string device) =>
        _devices.TryGetValue(DeviceKey(channel, device), out var items)
            ? items
            : new Dictionary<string, IgwBulkItem>();

    public IEnumerable<string> DeviceKeys => _devices.Keys;

    public long TotalTags => _devices.Values.Sum(d => (long)d.Count);
}

/// <summary>IGW 中一个点位的实际配置（从 /api/v1/project/children/device/{id}?deep=true 读出）</summary>
public sealed record ActualTag(IReadOnlyList<string> GroupPath, string Name, string Address, string DataType, int ScanRateMs,
    string ArrayElementType, int[] ArrayDimensions);

/// <summary>IGW 工程树 / DCS 兼容接口的只读助手（测试专用，直接走 REST，不依赖 HSJM 代码）</summary>
public sealed class IgwApi : IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public IgwApi(string baseUrl)
    {
        BaseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public void Dispose() => _http.Dispose();

    public sealed record Node(string Id, string Kind, string Name, bool HasChildren, int DirectTagCount);

    public async Task<JsonDocument> GetJsonAsync(string relative)
    {
        using var response = await _http.GetAsync(BaseUrl + relative);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {relative} → HTTP {(int)response.StatusCode}: {Trim(body)}");
        return JsonDocument.Parse(body);
    }

    public async Task<(string RootId, long TotalTagCount)> SummaryAsync()
    {
        using var doc = await GetJsonAsync("api/v1/project/summary");
        return (doc.RootElement.GetProperty("root").GetProperty("id").GetString(),
            doc.RootElement.GetProperty("totalTagCount").GetInt64());
    }

    public async Task<List<Node>> TreeChildrenAsync(string kind, string id)
    {
        using var doc = await GetJsonAsync($"api/v1/project/tree/{kind}/{Uri.EscapeDataString(id)}/children");
        return doc.RootElement.EnumerateArray().Select(e => new Node(
            e.GetProperty("id").GetString(), e.GetProperty("kind").GetString(), e.GetProperty("name").GetString(),
            e.TryGetProperty("hasChildren", out var h) && h.GetBoolean(),
            e.TryGetProperty("directTagCount", out var c) ? c.GetInt32() : 0)).ToList();
    }

    /// <summary>全部通道（name → id）</summary>
    public async Task<List<Node>> ChannelsAsync()
    {
        var (rootId, _) = await SummaryAsync();
        return (await TreeChildrenAsync("project", rootId)).Where(n => n.Kind == "channel").ToList();
    }

    public async Task<(string Endpoint, Dictionary<string, string> Settings)> DeviceEntityAsync(string deviceId)
    {
        using var doc = await GetJsonAsync($"api/v1/project/entity/device/{Uri.EscapeDataString(deviceId)}");
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in s.EnumerateObject())
            {
                settings[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            }
        }
        return (doc.RootElement.TryGetProperty("endpoint", out var ep) ? ep.GetString() : null, settings);
    }

    /// <summary>GET /api/v1/project/tags?groupId=…（只含该组直属点位，分页取全）</summary>
    public async Task<List<(string Name, string Address)>> GroupTagsAsync(string groupId)
    {
        var result = new List<(string, string)>();
        for (var offset = 0; ; offset += 10_000)
        {
            using var doc = await GetJsonAsync($"api/v1/project/tags?groupId={Uri.EscapeDataString(groupId)}&offset={offset}&limit=10000");
            var items = doc.RootElement.GetProperty("items");
            foreach (var t in items.EnumerateArray())
            {
                result.Add((t.GetProperty("name").GetString(), t.GetProperty("address").GetString()));
            }
            if (offset + items.GetArrayLength() >= doc.RootElement.GetProperty("total").GetInt64() || items.GetArrayLength() == 0)
            {
                return result;
            }
        }
    }

    /// <summary>
    /// 运行中工程里该设备的完整子树（/api/v1/project/children/device/{id}?deep=true），展开为点位清单；
    /// 同时返回根组名 → 递归点数（来自同一份子树）。
    /// </summary>
    public async Task<(List<ActualTag> Tags, Dictionary<string, long> RootTotals)> DeviceTagsDeepAsync(string deviceId)
    {
        using var response = await _http.GetAsync(BaseUrl + $"api/v1/project/children/device/{Uri.EscapeDataString(deviceId)}?deep=true",
            HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode, $"GET children/device/{deviceId}?deep=true → HTTP {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 256 });
        var tags = new List<ActualTag>();
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var t in doc.RootElement.GetProperty("tags").EnumerateArray())
        {
            tags.Add(ToTag(Array.Empty<string>(), t));
        }
        foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
        {
            var before = tags.Count;
            Walk(g, new List<string>(), tags);
            totals[g.GetProperty("name").GetString()] = tags.Count - before;
        }
        return (tags, totals);
    }

    private static void Walk(JsonElement group, List<string> parent, List<ActualTag> into)
    {
        var path = new List<string>(parent) { group.GetProperty("name").GetString() };
        if (group.TryGetProperty("tags", out var tags))
        {
            foreach (var t in tags.EnumerateArray())
            {
                into.Add(ToTag(path, t));
            }
        }
        if (group.TryGetProperty("subGroups", out var subs))
        {
            foreach (var s in subs.EnumerateArray())
            {
                Walk(s, path, into);
            }
        }
    }

    private static ActualTag ToTag(IReadOnlyList<string> path, JsonElement t)
    {
        string elementType = null;
        int[] dims = null;
        if (t.TryGetProperty("array", out var arr) && arr.ValueKind == JsonValueKind.Object)
        {
            elementType = arr.TryGetProperty("elementType", out var et) ? et.GetString() : null;
            dims = arr.TryGetProperty("dimensions", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : null;
        }
        return new ActualTag(path.ToList(), t.GetProperty("name").GetString(),
            t.TryGetProperty("address", out var a) ? a.GetString() : null,
            t.TryGetProperty("dataType", out var dt) ? dt.GetString() : null,
            t.TryGetProperty("scanRateMs", out var sr) && sr.ValueKind == JsonValueKind.Number ? sr.GetInt32() : 0,
            elementType, dims);
    }

    /// <summary>DCS 兼容读取 POST /Tag/ReadTagsAsList（单个别名）→ (isSuccess, errorMessage, backResult 原文)</summary>
    public async Task<(int Http, bool IsSuccess, string Error, string Back, long ElapsedMs)> ReadTagsAsListAsync(params string[] aliases)
    {
        var watch = Stopwatch.StartNew();
        using var content = new StringContent(JsonSerializer.Serialize(aliases), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(BaseUrl + "Tag/ReadTagsAsList", content);
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return ((int)response.StatusCode,
            root.TryGetProperty("isSuccess", out var ok) && ok.ValueKind == JsonValueKind.True,
            root.TryGetProperty("errorMessage", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null,
            root.TryGetProperty("backResult", out var back) ? back.GetRawText() : null,
            watch.ElapsedMilliseconds);
    }

    /// <summary>DCS 兼容 POST /Tag/AddTags（S1500 通讯类型 → 通用 通道下 S1500_{ip} 设备，dcsLogicalDevice = S1500_{ip}）</summary>
    public async Task<(int Http, bool IsSuccess, string Error)> AddLegacyS1500TagAsync(string ip, string tagName)
    {
        // DcsCommunicationType.S1500 = 4，DcsTagDataType.Boolean = 1（DCS 兼容接口枚举按数值序列化）
        var payload = new[]
        {
            new { ip, port = 102, communicationType = 4, dbAddress = "DB1.DBX0.0", dataType = 1, tagName, group = "" },
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(BaseUrl + "Tag/AddTags", content);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return ((int)response.StatusCode,
            doc.RootElement.TryGetProperty("isSuccess", out var ok) && ok.ValueKind == JsonValueKind.True,
            doc.RootElement.TryGetProperty("errorMessage", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null);
    }

    /// <summary>
    /// 经 POST /api/v1/project/mutations（If-Match = summary 的 ETag）在指定名称的通道下建一台旧式 DCS 设备
    /// （settings.dcsLogicalDevice = 逻辑名，endpoint = ip:102，一个经典地址点）；通道不存在时先建通道（Siemens.S7）。
    /// 注意：/Tag/AddTags 会把新设备放进「第一个含 DCS 托管设备的同驱动通道」（批量注册后即 HSJM 项目通道），
    /// 无法再落到「通用」，所以旧设备用管理面 mutation 直接构造。
    /// </summary>
    public async Task<string> CreateLegacyDeviceAsync(string channelName, string logicalName, string ip)
    {
        var channel = (await ChannelsAsync()).FirstOrDefault(c => c.Name == channelName);
        string channelId;
        if (channel == null)
        {
            channelId = Guid.NewGuid().ToString("N");
            await MutateAsync("upsertEntity", "channel", null,
                JsonSerializer.Serialize(new { id = channelId, name = channelName, driverId = "Siemens.S7" }));
        }
        else
        {
            channelId = channel.Id;
        }
        var deviceId = Guid.NewGuid().ToString("N");
        var device = new
        {
            id = deviceId,
            name = logicalName,
            endpoint = $"{ip}:102",
            settings = new Dictionary<string, string> { ["dcsLogicalDevice"] = logicalName, ["model"] = "S7-1500" },
        };
        // upsertEntity/device 不接受带点位/组的文档：先建空设备，再往设备根层加一个点
        await MutateAsync("upsertEntity", "device", channelId, JsonSerializer.Serialize(device));
        var tag = new { id = Guid.NewGuid().ToString("N"), name = "LegacyFlag", address = "DB1.DBX0.0", dataType = "bool", access = "readWrite", scanRateMs = 1000 };
        await MutateAsync("upsertEntity", "tag", deviceId, JsonSerializer.Serialize(tag));
        return deviceId;
    }

    private async Task MutateAsync(string kind, string entityKind, string targetId, string documentJson)
    {
        using var probe = await _http.GetAsync(BaseUrl + "api/v1/project/summary");
        var etag = probe.Headers.ETag?.Tag ?? probe.Headers.GetValues("ETag").FirstOrDefault();
        var payload = JsonSerializer.Serialize(new { kind, entityKind, entityIds = Array.Empty<string>(), targetId, documentJson });
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "api/v1/project/mutations")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"mutation {kind}/{entityKind} → HTTP {(int)response.StatusCode}: {Trim(body)}");
    }

    /// <summary>全部设备：(通道名, 设备 id, 设备名)</summary>
    public async Task<List<(string Channel, string DeviceId, string Device)>> AllDevicesAsync()
    {
        var result = new List<(string, string, string)>();
        foreach (var channel in await ChannelsAsync())
        {
            foreach (var device in (await TreeChildrenAsync("channel", channel.Id)).Where(n => n.Kind == "device"))
            {
                result.Add((channel.Name, device.Id, device.Name));
            }
        }
        return result;
    }

    private static string Trim(string s) => s == null ? null : s.Length > 300 ? s[..300] : s;
}
