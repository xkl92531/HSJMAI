using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>hardening 计划共享夹具：固定项目信息、每用例独立 IGW 地址（避免批量探测静态缓存互相污染）、runner 工厂。</summary>
public static class HardeningFixture
{
    public const string Ip = "10.0.0.1";
    public const long ProjectId = 4242;

    public static readonly IReadOnlyList<TimeSpan> FastRetryDelays = new[]
    {
        TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
    };

    public static string UniqueUrl() => $"http://igw-hardening-{Guid.NewGuid():N}.local:48020";

    public static IgwRegistrationProjectInfo Project(string url, string ip = Ip) =>
        new(ProjectId, "项目H", "线体A", "PLC1", url, ip, 102);

    /// <summary>经典偏移齐全的 Int 行（整块归 S7）</summary>
    public static PlannerDbRow ClassicRow(string block, int number, string path, int offset) =>
        new(block, number, path, "Int", offset, 0m, null);

    /// <summary>无偏移的 Real 行（整块归 S7Plus）</summary>
    public static PlannerDbRow SymbolicRow(string block, string path) =>
        new(block, 0, path, "Real", null, null, null);

    public static IgwBulkClient Client(FakeIgwServer igw) =>
        new(NullLogger<IgwBulkClient>.Instance, igw, FastRetryDelays);

    public static IgwRegistrationRunner Runner(FakeIgwServer igw, MemoryRegistrationStore store,
        IgwRegistrationConfig config = null, List<(long ProjectId, string Reason)> graphRequests = null,
        IIgwProjectStateStore state = null, Func<DateTime> now = null) =>
        new(Client(igw), store, _ => Task.CompletedTask, NullLogger.Instance, config ?? new IgwRegistrationConfig(),
            (projectId, reason) =>
            {
                graphRequests?.Add((projectId, reason));
                return Task.CompletedTask;
            },
            state, now);

    public static GraphIgwRegistrar Registrar(FakeIgwServer igw, MemoryRegistrationStore store,
        IIgwProjectStateStore state, Func<DateTime> now) =>
        new(Client(igw), store, _ => Task.CompletedTask, NullLogger.Instance, new IgwRegistrationConfig(), state, now);
}

/// <summary>内存台账 / 通道列 / 取数（ReplaceLedgerAsync 语义同生产 ReplaceForProjectAsync）</summary>
public sealed class MemoryRegistrationStore : IIgwRegistrationStore
{
    public IgwRegistrationData Data { get; set; } =
        new(Array.Empty<string>(), new List<PlannerDbRow>(), new List<PlannerIoTag>());
    /// <summary>true：LoadDataAsync 返回 null（模拟取数失败），忽略 <see cref="Data"/></summary>
    public bool ReturnNullData { get; set; }
    public string ChannelName { get; set; }
    public List<string> ChannelWrites { get; } = new();
    public List<PLC_IgwRegistrationEntity> Rows { get; } = new();
    public List<(string RootKind, string RootName)> LastRemoved { get; private set; } = new();
    public List<IReadOnlyCollection<string>> RowBlockRequests { get; } = new();
    public int LedgerWrites { get; private set; }
    public bool HasGraphTargets { get; set; }
    /// <summary>true：IsProjectRemovedAsync 返回 true（模拟注册执行期间项目被删除）</summary>
    public bool ProjectRemoved { get; set; }
    public int ProjectRemovalChecks { get; private set; }
    /// <summary>true：IsProjectRemovedAsync 抛异常（模拟复查项目是否已删除本身失败）</summary>
    public bool ThrowOnProjectRemovedCheck { get; set; }

    public PLC_IgwRegistrationEntity Row(string rootName) => Rows.Single(r => r.RootName == rootName);

    public void Seed(string rootKind, string rootName, string deviceKind, string status, string rootGroup = null) =>
        Rows.Add(new PLC_IgwRegistrationEntity
        {
            ProjectId = HardeningFixture.ProjectId,
            RootKind = rootKind,
            RootName = rootName,
            RootGroup = rootGroup ?? rootName,
            DeviceKind = deviceKind,
            Status = status,
        });

    public Task<bool> HasGraphTargetsAsync(long projectId) => Task.FromResult(HasGraphTargets);

    public Task<bool> IsProjectRemovedAsync(long projectId)
    {
        ProjectRemovalChecks++;
        if (ThrowOnProjectRemovedCheck)
        {
            throw new InvalidOperationException("模拟复查项目是否已删除失败");
        }
        return Task.FromResult(ProjectRemoved);
    }

    public Task<string> GetChannelNameAsync(long projectId) => Task.FromResult(ChannelName);

    public Task SetChannelNameAsync(long projectId, string channelName)
    {
        ChannelName = channelName;
        ChannelWrites.Add(channelName);
        return Task.CompletedTask;
    }

    public Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId) => Task.FromResult(IgwLedgerSnapshotBuilder.Build(Rows));

    public Task<IgwRegistrationData> LoadDataAsync(long projectId, bool includeIoTags, IReadOnlyCollection<string> rowBlocks)
    {
        RowBlockRequests.Add(rowBlocks?.ToList());
        if (ReturnNullData)
        {
            return Task.FromResult<IgwRegistrationData>(null);
        }
        var wanted = rowBlocks == null ? null : new HashSet<string>(rowBlocks, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(Data with
        {
            DbRows = Data.DbRows.Where(r => wanted == null || wanted.Contains(r.BlockName)).ToList(),
            IoTags = includeIoTags ? Data.IoTags : new List<PlannerIoTag>(),
        });
    }

    public Task ReplaceLedgerAsync(long projectId, IReadOnlyList<PLC_IgwRegistrationEntity> rows,
        IReadOnlyList<(string RootKind, string RootName)> removedRoots)
    {
        LedgerWrites++;
        LastRemoved = removedRoots.ToList();
        var keys = rows.Select(r => (r.RootKind, r.RootName)).Concat(removedRoots).ToHashSet();
        Rows.RemoveAll(r => keys.Contains((r.RootKind, r.RootName)));
        foreach (var row in rows)
        {
            row.ProjectId = projectId;
            Rows.Add(row);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// 假 IGW：批量探测 / 批量写入 / 工程树（多通道、设备带 dcsLogicalDevice 与 dcsManagedBy）/ deleteEntities（设备、通道）。
/// UnreachableHosts 中的主机一律抛连接异常（模拟旧 IGW 不可达）；HangingHosts 中的主机 / HangDeletes 时的删除请求
/// 一直挂起直到被取消（模拟无响应的 IGW）。
/// </summary>
public sealed class FakeIgwServer : HttpMessageHandler
{
    public sealed class FakeDevice
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Logical { get; init; }
        public string Endpoint { get; init; }
        public string ManagedBy { get; init; }
    }

    public sealed class FakeChannel
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public List<FakeDevice> Devices { get; } = new();
    }

    private int _nextId;

    public HttpStatusCode ProbeStatus { get; set; } = HttpStatusCode.BadRequest;
    public Func<IgwBulkRequest, (HttpStatusCode Status, string Body)> BulkResponder { get; set; } =
        _ => (HttpStatusCode.OK, """{"applied":true,"revision":"r1"}""");
    public List<IgwBulkRequest> BulkRequests { get; } = new();
    public List<string> Requests { get; } = new();
    public List<FakeChannel> Channels { get; } = new();
    public List<(string Kind, string Id)> Deleted { get; } = new();
    public bool FailDeletes { get; set; }
    /// <summary>true：工程树读取（project/summary）返回 500（模拟 IGW 可达但读树失败）</summary>
    public bool FailTreeReads { get; set; }
    public HashSet<string> UnreachableHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HangingHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HangDeletes { get; set; }

    public IReadOnlyList<IgwBulkRequest> BulkRequestsFor(string deviceName) =>
        BulkRequests.Where(r => r.Device.Name == deviceName).ToList();

    public FakeChannel AddChannel(string name)
    {
        var channel = new FakeChannel { Id = "ch-" + (++_nextId), Name = name };
        Channels.Add(channel);
        return channel;
    }

    public FakeDevice AddDevice(string channelName, string deviceName, string logical, string endpoint, string managedBy = "bulk")
    {
        var channel = Channels.FirstOrDefault(c => c.Name == channelName) ?? AddChannel(channelName);
        var device = new FakeDevice
        {
            Id = "dev-" + (++_nextId), Name = deviceName, Logical = logical, Endpoint = endpoint, ManagedBy = managedBy,
        };
        channel.Devices.Add(device);
        return device;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (UnreachableHosts.Contains(request.RequestUri!.Host))
        {
            throw new HttpRequestException("由于目标计算机积极拒绝，无法连接。");
        }
        if (HangingHosts.Contains(request.RequestUri.Host))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri.AbsolutePath;
        lock (Requests)
        {
            Requests.Add($"{request.Method} {path}");
        }

        if (path.EndsWith("api/v1/project/bulk/tags", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            if (body == "{}")
            {
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
            if (FailTreeReads)
            {
                return Json(HttpStatusCode.InternalServerError, """{"error":"internal","detail":"模拟读树失败"}""");
            }
            var response = Json(HttpStatusCode.OK, """{"root":{"id":"root-1","kind":"project","name":"root"}}""");
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rev-1\"");
            return response;
        }
        if (path.Contains("project/tree/project/root-1/children", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(Channels.Select(c => new { id = c.Id, kind = "channel", name = c.Name })));
        }
        const string channelPrefix = "project/tree/channel/";
        if (path.Contains(channelPrefix, StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path.Substring(path.IndexOf(channelPrefix, StringComparison.Ordinal) + channelPrefix.Length).Split('/')[0]);
            var channel = Channels.FirstOrDefault(c => c.Id == id);
            var devices = channel == null ? new List<FakeDevice>() : channel.Devices;
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(devices.Select(d => new { id = d.Id, kind = "device", name = d.Name })));
        }
        const string entityPrefix = "project/entity/device/";
        if (path.Contains(entityPrefix, StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path.Substring(path.IndexOf(entityPrefix, StringComparison.Ordinal) + entityPrefix.Length));
            var device = Channels.SelectMany(c => c.Devices).FirstOrDefault(d => d.Id == id);
            if (device == null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var settings = new Dictionary<string, string> { ["dcsLogicalDevice"] = device.Logical };
            if (device.ManagedBy != null)
            {
                settings["dcsManagedBy"] = device.ManagedBy;
            }
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = device.Id, name = device.Name, endpoint = device.Endpoint, settings }));
        }
        if (path.EndsWith("project/mutations", StringComparison.Ordinal))
        {
            if (HangDeletes)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (FailDeletes)
            {
                return Json(HttpStatusCode.InternalServerError, """{"error":"persistence_failed","detail":"模拟删除失败"}""");
            }
            using var doc = JsonDocument.Parse(body);
            var kind = doc.RootElement.GetProperty("entityKind").GetString();
            foreach (var element in doc.RootElement.GetProperty("entityIds").EnumerateArray())
            {
                var id = element.GetString();
                Deleted.Add((kind, id));
                if (kind == "device")
                {
                    foreach (var channel in Channels)
                    {
                        channel.Devices.RemoveAll(d => d.Id == id);
                    }
                }
                else if (kind == "channel")
                {
                    Channels.RemoveAll(c => c.Id == id);
                }
            }
            return Json(HttpStatusCode.OK, "{}");
        }
        throw new InvalidOperationException($"FakeIgwServer 未预期的请求 {request.Method} {request.RequestUri}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>内存项目级注册状态（IIgwProjectStateStore）</summary>
public sealed class MemoryProjectStateStore : IIgwProjectStateStore
{
    public IgwRegistrationStateRow State { get; set; }
    public List<IgwPeerProjectRow> Peers { get; } = new();
    /// <summary>第二次及以后的 ListPeerProjectsAsync 额外返回的项目（模拟注册过程中另一项目并发登记）</summary>
    public List<IgwPeerProjectRow> LatePeers { get; } = new();
    private int _peerListCalls;
    public List<IgwRegistrationIdentity> SavedIdentities { get; } = new();
    public List<(DateTime? At, string Error)> GraphFailureWrites { get; } = new();

    public static MemoryProjectStateStore WithIdentity(string url, string ip, string channel) => new()
    {
        State = new IgwRegistrationStateRow
        {
            ProjectId = HardeningFixture.ProjectId,
            RegisteredIotBaseUrl = IgwRegistrationIdentity.NormalizeIgwBaseUrl(url),
            RegisteredPlcIp = ip,
            RegisteredChannel = channel,
        },
    };

    public Task<IgwRegistrationStateRow> GetStateAsync(long projectId) => Task.FromResult(State);

    public Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity)
    {
        State ??= new IgwRegistrationStateRow { ProjectId = projectId };
        State.RegisteredIotBaseUrl = identity.IgwBaseUrl;
        State.RegisteredPlcIp = identity.PlcIp;
        State.RegisteredChannel = identity.ChannelName;
        SavedIdentities.Add(identity);
        return Task.CompletedTask;
    }

    public Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string error)
    {
        State ??= new IgwRegistrationStateRow { ProjectId = projectId };
        State.GraphFailedAt = failedAt;
        State.GraphFailure = failedAt == null ? null : error;
        GraphFailureWrites.Add((failedAt, error));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId) =>
        Task.FromResult<IReadOnlyList<IgwPeerProjectRow>>(Interlocked.Increment(ref _peerListCalls) == 1
            ? Peers.ToList()
            : Peers.Concat(LatePeers).ToList());
}

/// <summary>
/// 内存注册状态表的待补跑列（M-1 fix round 2）：写入按 <see cref="IgwPendingRecordRules.ResolveWrite"/>（与生产 SQL 事务同一规则），
/// 清除只清属于指定队列的记录（同生产 UPDATE ... WHERE pendingqueue = @Queue）。其余方法不支持。
/// </summary>
public sealed class MemoryPendingStateService : IPLC_IgwRegistrationStateService
{
    public Dictionary<long, (string Json, string Queue)> Records { get; } = new();
    public List<(long ProjectId, string Queue)> Clears { get; } = new();

    public Task SavePendingAsync(long projectId, string pendingArgsJson, string queue, Func<string, string, string> mergeWithOtherOwner)
    {
        lock (Records)
        {
            var existing = Records.GetValueOrDefault(projectId);
            Records[projectId] = IgwPendingRecordRules.ResolveWrite(existing.Json, existing.Queue, pendingArgsJson, queue, mergeWithOtherOwner);
        }
        return Task.CompletedTask;
    }

    public Task ClearPendingAsync(long projectId, string queue)
    {
        lock (Records)
        {
            Clears.Add((projectId, queue));
            if (Records.TryGetValue(projectId, out var existing) && existing.Queue == queue)
            {
                Records.Remove(projectId);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IgwRegistrationStateRow>> ListPendingAsync(string queue)
    {
        lock (Records)
        {
            return Task.FromResult<IReadOnlyList<IgwRegistrationStateRow>>(Records
                .Where(r => r.Value.Json != null && r.Value.Queue == queue)
                .Select(r => new IgwRegistrationStateRow { ProjectId = r.Key, PendingArgsJson = r.Value.Json, PendingQueue = r.Value.Queue })
                .ToList());
        }
    }

    public Task EnsureSchemaAsync() => Task.CompletedTask;
    public Task<IgwRegistrationStateRow> GetAsync(long projectId) => throw new NotSupportedException();
    public Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity) => throw new NotSupportedException();
    public Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string error) => throw new NotSupportedException();
    public Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId) => throw new NotSupportedException();
    public Task DeleteAsync(long projectId) => throw new NotSupportedException();
}
