using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 一次旧设备清理的删除结果：Description 为可读描述；IsGraphCapable = 位于「通用_S7Plus」通道或逻辑名为 S7Plus_{ip}
    /// （老 GRAPH Plus 设备）——删掉它意味着项目 GRAPH 步位需要在 IGW 通道下重新注册。
    /// </summary>
    /// <summary>旧设备清理范围：All = S1500_{ip} + S7Plus_{ip}（DB/IO 注册用）；GraphOnly = 仅 S7Plus_{ip}（GRAPH 注册用——
    /// 标量旧设备留给 DB/IO 注册清理，其删除会触发该任务的增量→全量升级，GRAPH 抢先删掉会让 DB 侧失去这一信号）</summary>
    public enum IgwLegacyDeviceScope
    {
        All,
        GraphOnly,
    }

    public sealed record IgwLegacyDeviceRemoval(string Description, string ChannelName, string LogicalName, bool IsGraphCapable);

    /// <summary>
    /// 清理项目通道设备的结果（I-3 / M-4）：Removed = 已删除（或复查已不存在）的设备；SkippedUnmanaged = 逻辑名匹配但没有
    /// dcsManagedBy=bulk 标记、未删除的设备（含描述与逻辑名）；ChannelFound / ChannelDeleted = 通道是否存在 / 是否因变空被删除。
    /// </summary>
    public sealed record IgwProjectDeviceCleanup(IReadOnlyList<IgwLegacyDeviceRemoval> Removed, IReadOnlyList<IgwLegacyDeviceRemoval> SkippedUnmanaged,
        bool ChannelFound, bool ChannelDeleted);

    /// <summary>只读查询通道内项目设备的结果（I-3）：ChannelFound = 通道是否存在；Devices = 逻辑名匹配的设备（通道不存在时为空）</summary>
    public sealed record IgwProjectDeviceScan(bool ChannelFound, IReadOnlyList<IgwLegacyDeviceRemoval> Devices);

    /// <summary>
    /// IGW 批量点位注册客户端（POST /api/v1/project/bulk/tags）：批量支持探测（带缓存）、
    /// 带退避重试的批量写入、旧设备清理（sub-project B 分批切通道/S1500_Plus 拓扑后遗留的
    /// 通用/通用_S7Plus 通道下旧 S1500_{ip}/S7Plus_{ip} 设备）。
    /// 读写工程树经 <see cref="IgwProjectApi"/> 助手（原 PLC_IgwGraphPlusChannelService 的搬组逻辑已随 task-7 删除）。
    /// </summary>
    public sealed class IgwBulkClient : ISelfScopedAutoInject, IDisposable
    {
        /// <summary>批量写入最多重试次数（首次尝试之外再试 3 次，共 4 次）</summary>
        private const int MaxRetries = 3;

        /// <summary>IGW 批量接口写入设备时加的归属标记键 / 值（IGW 1.6.77+，ProjectBulkTagMetadata.ManagedByKey）</summary>
        public const string ManagedBySettingKey = "dcsManagedBy";
        public const string ManagedByBulk = "bulk";

        /// <summary>批量支持探测缓存有效期</summary>
        private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromMinutes(5);

        /// <summary>探测 / 工程树 tree/entity 单次读请求超时（每次调用各自计时，不是整趟遍历共享一个预算）</summary>
        private static readonly TimeSpan DefaultTreeRequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>单次 deleteEntities mutation 超时（含 If-Match 冲突重试的整个 3 次循环）</summary>
        private static readonly TimeSpan DefaultMutationTimeout = TimeSpan.FromSeconds(60);

        /// <summary>批量写入整体超时（大项目 20 万点位落库 + 运行态重建设备可能耗时较久）</summary>
        private static readonly TimeSpan BulkClientTimeout = TimeSpan.FromMinutes(10);

        /// <summary>默认退避延迟：2s / 4s / 8s（测试用内部构造函数可覆盖，避免单测实际等待）</summary>
        private static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays = new[]
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        };

        /// <summary>探测结果缓存：Key=规范化后的 baseUrl（scheme/host 已小写化）；只缓存成功结果，探测失败不缓存</summary>
        private static readonly ConcurrentDictionary<string, (bool Supported, DateTime ExpiresAtUtc)> ProbeCache = new();

        private readonly ILogger<IgwBulkClient> _logger;
        private readonly HttpClient _http;
        private readonly IReadOnlyList<TimeSpan> _retryDelays;
        private readonly TimeSpan _treeRequestTimeout;
        private readonly TimeSpan _mutationTimeout;

        private static readonly JsonSerializerOptions ResponseJsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public IgwBulkClient(ILogger<IgwBulkClient> logger, HttpMessageHandler handler = null)
            : this(logger, handler, DefaultRetryDelays)
        {
        }

        /// <summary>供单测使用：注入自定义 <see cref="HttpMessageHandler"/> 桩 + 可缩短的退避延迟 / 超时。</summary>
        internal IgwBulkClient(
            ILogger<IgwBulkClient> logger,
            HttpMessageHandler handler,
            IReadOnlyList<TimeSpan> retryDelays,
            TimeSpan? treeRequestTimeout = null,
            TimeSpan? mutationTimeout = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryDelays = retryDelays is { Count: > 0 } ? retryDelays : DefaultRetryDelays;
            _treeRequestTimeout = treeRequestTimeout ?? DefaultTreeRequestTimeout;
            _mutationTimeout = mutationTimeout ?? DefaultMutationTimeout;
            _http = handler != null
                ? new HttpClient(handler, disposeHandler: false) { Timeout = BulkClientTimeout }
                : new HttpClient { Timeout = BulkClientTimeout };
        }

        /// <summary>供单测清空静态探测缓存，避免用例间互相污染。</summary>
        internal static void ResetCacheForTests() => ProbeCache.Clear();

        public void Dispose() => _http.Dispose();

        /// <summary>
        /// 校验并规范化 iotUrl：空/空白直接拒绝（调用方配置缺失应尽早暴露，而不是拼出无意义的
        /// "http:///api/..." 请求）；scheme/host 统一小写，保证同一网关无论怎么书写大小写都
        /// 命中同一条探测缓存（HTTP scheme 与 host 本身大小写不敏感）。
        /// </summary>
        private static string NormalizeBaseUrlOrThrow(string iotUrl)
        {
            if (string.IsNullOrWhiteSpace(iotUrl))
            {
                throw new ArgumentException("iotUrl 不能为空（IGW 基址缺失，无法定位批量接口）", nameof(iotUrl));
            }
            // 与注册身份（IgwRegistrationIdentity.IgwBaseUrl）同一规则：scheme/host 小写、单个尾斜杠
            return IgwRegistrationIdentity.NormalizeIgwBaseUrl(iotUrl);
        }

        /// <summary>
        /// 探测目标 IGW 是否支持批量注册接口。
        /// 不用 GET：IGW 部署时常见 <c>/api/{**path}</c> 静态资源兜底路由，未命中的 GET 会被兜底捕获返回
        /// 404，即便新版 IGW 真的挂了这条路由也测不出来。改用 <c>POST {base}api/v1/project/bulk/tags</c>，
        /// body 固定 <c>{}</c>、不带 If-Match：新版 IGW 的校验（<c>ProjectBulkTagValidator.Validate</c>）在真正写库
        /// /重建运行态之前执行，空请求体会被判定为通道名/设备名无效等校验失败，返回 400
        /// <c>invalid_items</c>（或反序列化阶段的 <c>invalid_request</c>），且不产生任何副作用 —— 据此判定"支持批量"；
        /// 路由不存在（旧版 IGW / 非 IGW 网关）→ 404，或同路径只允许 GET → 405 → 不支持。其它状态码/超时/网络异常 → 抛
        /// <see cref="InvalidOperationException"/>（不缓存失败，允许调用方稍后重探）。结果按规范化 baseUrl 缓存 5 分钟。
        /// </summary>
        public async Task<bool> SupportsBulkAsync(string iotUrl, CancellationToken ct)
        {
            var baseUrl = NormalizeBaseUrlOrThrow(iotUrl);
            if (ProbeCache.TryGetValue(baseUrl, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            {
                return cached.Supported;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(_treeRequestTimeout);
            try
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}api/v1/project/bulk/tags")
                {
                    Content = content,
                };
                // 故意不带 If-Match：探测只是校验请求本身能否命中新路由，不涉及任何修订号语义
                using var response = await _http.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

                // 404：路由不存在；405：旧版 IGW 同路径只有 GET 兜底路由 / 反代只放行 GET —— 均判定为不支持批量
                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    ProbeCache[baseUrl] = (false, DateTime.UtcNow.Add(ProbeCacheTtl));
                    return false;
                }
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    var errorCode = TryExtractErrorCode(body);
                    if (string.Equals(errorCode, "invalid_items", StringComparison.Ordinal) ||
                        string.Equals(errorCode, "invalid_request", StringComparison.Ordinal))
                    {
                        ProbeCache[baseUrl] = (true, DateTime.UtcNow.Add(ProbeCacheTtl));
                        return true;
                    }
                }
                throw new InvalidOperationException(
                    $"IGW 批量接口探测返回意外状态 HTTP {(int)response.StatusCode}（{baseUrl}）：{IgwProjectApi.Truncate(body, 300)}，无法判定是否支持批量注册");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"IGW 批量接口探测超时（{baseUrl}），无法判定是否支持批量注册");
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"IGW 批量接口探测失败（{baseUrl}）：{ex.Message}", ex);
            }
        }

        /// <summary>从探测响应体提取 error 字段（非法/非 JSON body 返回 null，不抛异常）。</summary>
        private static string TryExtractErrorCode(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var errorEl) &&
                    errorEl.ValueKind == JsonValueKind.String)
                {
                    return errorEl.GetString();
                }
            }
            catch (JsonException)
            {
                // 非 JSON 响应体（如反代错误页）：不是我们能判定的错误码，按未知状态处理
            }
            return null;
        }

        /// <summary>
        /// 批量写入（POST /api/v1/project/bulk/tags）。任意 HTTP 状态都解析成 <see cref="IgwBulkResponse"/> 返回，
        /// 不因业务错误状态抛异常。重试规则（2s/4s/8s 退避，最多 3 次，与状态码/请求体解耦到可判定为止）：
        /// <list type="bullet">
        /// <item>409（revision_conflict）→ 重试。本客户端从不发送 If-Match（<see cref="IgwBulkRequest"/> 不携带修订号），
        /// IGW 因此正常不会返回 409；保留重试只是防御性的，不依赖它。</item>
        /// <item>503 → 无条件重试。IGW 的 503（<c>persistence_failed</c>）响应体没有 <c>retryable</c> 字段——
        /// 503 本身已经是"可重试"的状态码语义（存储层繁忙/加锁），不看 body 内容。</item>
        /// <item>500 → 仅当响应体显式带 <c>retryable:true</c> 才重试；<c>runtime_apply_failed</c> 永不重试
        /// （即使误带 retryable，也显式排除——见下方关于该错误码的说明）。</item>
        /// <item>400/413 → 不重试，错误原文进 <see cref="IgwBulkResponse.Detail"/>。</item>
        /// </list>
        /// 重试耗尽仍失败则返回最后一次的响应（不抛异常）。
        /// <para>
        /// ⚠️ 500 <c>runtime_apply_failed</c> 语义：IGW 已经把这次批量变更提交到持久化存储（修订号已推进，
        /// <see cref="IgwBulkResponse.Revision"/> 有效），只是运行态重建设备/驱动会话失败。调用方**不能**把
        /// 它当成"这次请求完全没有生效"——台账/日志应记录为"已写入但运行态未生效"，而不是当作纯粹失败重试整单。
        /// </para>
        /// <para>
        /// 传输层异常区分两种情形：连接级失败（<see cref="HttpRequestException"/>，如连接被拒绝/
        /// 重置；通常请求尚未送达，但也可能在发出后才被重置）→ 按同样次数重试（批量写入是幂等 upsert，重复提交安全）；请求已经发出、只是响应没能在超时内送达（HttpClient 自身超时触发的
        /// <see cref="OperationCanceledException"/>，通常包一层 <see cref="TimeoutException"/>）→ **不重试**，直接
        /// 向上抛出——此时无法确定 IGW 是否已经处理了这次写入，重试有把同一批点位重复落库的风险。
        /// </para>
        /// </summary>
        public async Task<IgwBulkResponse> BulkUpsertAsync(string iotUrl, IgwBulkRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var baseUrl = NormalizeBaseUrlOrThrow(iotUrl);
            var url = $"{baseUrl}api/v1/project/bulk/tags";
            var json = JsonSerializer.Serialize(request, IgwBulkJson.Options);

            for (var attempt = 1; attempt <= MaxRetries + 1; attempt++)
            {
                int httpStatus;
                string body;
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
                    httpStatus = (int)response.StatusCode;
                    body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw; // 调用方主动取消，不算传输失败，不重试
                    }
                    // HttpClient 自身超时（请求已发出，响应未在超时内送达）：无法判定 IGW 是否已处理，不重试
                    _logger.LogWarning(ex,
                        "[IgwBulkClient] 批量写入请求已发送但超时（第 {Attempt} 次尝试，请求是否生效未知，不重试直接失败）{Url}",
                        attempt, url);
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    // 连接级失败（连接被拒绝/重置/DNS 失败等）：多数情况下请求尚未送达 IGW，但连接也可能在请求发出后
                    // 才被重置（IGW 可能已处理）。重试仍然安全：批量写入是幂等 upsert（replaceGroups + 同名覆盖），
                    // 重复提交同一请求结果相同。
                    _logger.LogWarning(ex, "[IgwBulkClient] 批量写入连接失败（第 {Attempt}/{Max} 次尝试，请求未送达）{Url}",
                        attempt, MaxRetries + 1, url);
                    if (attempt > MaxRetries)
                    {
                        throw;
                    }
                    await DelayAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }

                var parsed = ParseBulkResponse(httpStatus, body);
                if (httpStatus == 200)
                {
                    return parsed;
                }

                var retryable = httpStatus == 409
                    || httpStatus == 503
                    || (httpStatus == 500 && parsed.Retryable == true &&
                        !string.Equals(parsed.Error, "runtime_apply_failed", StringComparison.Ordinal));
                if (!retryable || attempt > MaxRetries)
                {
                    return parsed;
                }
                _logger.LogWarning(
                    "[IgwBulkClient] 批量写入失败（第 {Attempt}/{Max} 次尝试，HTTP {Status} {Error}），退避重试",
                    attempt, MaxRetries + 1, httpStatus, parsed.Error);
                await DelayAsync(attempt, ct).ConfigureAwait(false);
            }

            // 理论不可达（循环内每条路径都会 return/throw），仅为编译器满足返回值要求
            throw new InvalidOperationException("IgwBulkClient.BulkUpsertAsync 重试循环异常退出");
        }

        private Task DelayAsync(int attempt, CancellationToken ct)
        {
            var index = attempt - 1;
            var delay = index >= 0 && index < _retryDelays.Count ? _retryDelays[index] : _retryDelays[^1];
            return Task.Delay(delay, ct);
        }

        /// <summary>响应体原始形态（200 成功体与 400/409/413/500/503 错误体合并解析）。
        /// details 是 [{index,reason}]（invalid_items 校验错误列表），与 detail（普通错误字符串）分别处理，
        /// 最终合并进 <see cref="IgwBulkResponse.Detail"/>。</summary>
        private sealed class RawBulkResponse
        {
            public bool Applied { get; set; }
            public string Revision { get; set; }
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Deleted { get; set; }
            public bool DeviceRestarted { get; set; }
            public JsonElement Timing { get; set; }
            public string Error { get; set; }
            public string Detail { get; set; }
            public List<RawValidationDetail> Details { get; set; }
            public bool? Retryable { get; set; }
        }

        private sealed class RawValidationDetail
        {
            public int Index { get; set; }
            public string Reason { get; set; }
        }

        private static IgwBulkResponse ParseBulkResponse(int httpStatus, string body)
        {
            var response = new IgwBulkResponse { HttpStatus = httpStatus };
            if (string.IsNullOrWhiteSpace(body))
            {
                return response;
            }

            RawBulkResponse raw;
            try
            {
                raw = JsonSerializer.Deserialize<RawBulkResponse>(body, ResponseJsonOpts);
            }
            catch (JsonException)
            {
                response.Detail = IgwProjectApi.Truncate(body, 500);
                return response;
            }
            if (raw == null)
            {
                return response;
            }

            response.Applied = raw.Applied;
            response.Revision = raw.Revision;
            response.Created = raw.Created;
            response.Updated = raw.Updated;
            response.Deleted = raw.Deleted;
            response.DeviceRestarted = raw.DeviceRestarted;
            response.Timing = raw.Timing.ValueKind switch
            {
                JsonValueKind.Undefined => null,
                JsonValueKind.Null => null,
                JsonValueKind.String => raw.Timing.GetString(),
                _ => raw.Timing.GetRawText(),
            };
            response.Error = raw.Error;
            response.Retryable = raw.Retryable;

            var detail = raw.Detail;
            if (raw.Details is { Count: > 0 })
            {
                var detailsText = string.Join("; ", raw.Details.Select(d =>
                    d.Index >= 0 ? $"[{d.Index}] {d.Reason}" : d.Reason));
                detail = string.IsNullOrEmpty(detail) ? detailsText : $"{detail}; {detailsText}";
            }
            response.Detail = detail;

            if (raw.Details is { Count: > 0 })
            {
                response.ItemErrorIndexes = raw.Details.Where(d => d.Index >= 0).Select(d => d.Index).Distinct().ToList();
            }

            return response;
        }

        /// <summary>
        /// 清理「通用」/「通用_S7Plus」通道下遗留的旧设备：dcsLogicalDevice ∈ {S1500_{ip}, S7Plus_{ip}}
        /// 且 endpoint 主机部分等于 <paramref name="plcIp"/> 的设备（S1500_{ip} 是老单请求 AddTags 落地的标量设备，
        /// S7Plus_{ip} 是 GRAPH Plus 拓扑设备；切到批量注册后二者均由本客户端另行按新命名规则重建）。
        /// 无匹配设备时不发起任何 mutation 请求。工程树读取（summary/tree/entity）与删除 mutation 各自独立计时
        /// （分别 <see cref="DefaultTreeRequestTimeout"/> / <see cref="DefaultMutationTimeout"/>，不是整趟遍历共享一个预算）。
        /// 删除请求失败时重读该设备所在通道的设备清单：设备已不存在（如 DB/IO 注册与 GRAPH 注册并发、对方先删）→ 视为成功，
        /// 记 Information 日志；仍存在才算失败。
        /// 任一设备删除失败（含超时）→ 抛 <see cref="InvalidOperationException"/> 列出全部失败项：旧设备残留会导致
        /// 新旧别名同时解析到同一 dcsLogicalDevice 前缀，读链报"别名不唯一"，因此清理失败必须让整个注册任务失败，
        /// 不能静默忽略。返回值仅在全部成功时才包含已删除设备的可读描述列表。
        /// </summary>
        public async Task<IReadOnlyList<string>> RemoveLegacyDevicesAsync(string iotUrl, string plcIp, CancellationToken ct) =>
            (await RemoveLegacyDevicesDetailedAsync(iotUrl, plcIp, ct).ConfigureAwait(false)).Select(x => x.Description).ToList();

        /// <summary>
        /// 同 <see cref="RemoveLegacyDevicesAsync"/>，返回结构化结果（所在通道名 / 逻辑名），供注册任务判断本次是否删掉了
        /// 可承载 GRAPH 的旧设备（<see cref="IgwLegacyDeviceRemoval.IsGraphCapable"/>）而需要触发 GRAPH 重注册。
        /// </summary>
        public Task<IReadOnlyList<IgwLegacyDeviceRemoval>> RemoveLegacyDevicesDetailedAsync(string iotUrl, string plcIp, CancellationToken ct) =>
            RemoveLegacyDevicesAsync(iotUrl, plcIp, IgwLegacyDeviceScope.All, ct);

        /// <summary>按 <paramref name="scope"/> 选择要清理的旧逻辑设备名（其余语义同 <see cref="RemoveLegacyDevicesAsync(string, string, CancellationToken)"/>）</summary>
        public async Task<IReadOnlyList<IgwLegacyDeviceRemoval>> RemoveLegacyDevicesAsync(string iotUrl, string plcIp,
            IgwLegacyDeviceScope scope, CancellationToken ct)
        {
            var baseUrl = NormalizeBaseUrlOrThrow(iotUrl);
            var results = new List<IgwLegacyDeviceRemoval>();
            var ip = (plcIp ?? string.Empty).Trim();
            if (ip.Length == 0)
            {
                return results;
            }

            var legacyLogicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DcsIgwConsts.GraphPlusDeviceLogicalName(ip),              // S7Plus_{ip}
            };
            if (scope == IgwLegacyDeviceScope.All)
            {
                legacyLogicalNames.Add(IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, ip)); // S1500_{ip}
            }

            var rootId = await WithTimeoutAsync(_treeRequestTimeout, ct,
                t => IgwProjectApi.GetRootIdAsync(_http, baseUrl, t)).ConfigureAwait(false);
            var channels = await WithTimeoutAsync(_treeRequestTimeout, ct,
                t => IgwProjectApi.GetChildrenAsync(_http, baseUrl, "project", rootId, t)).ConfigureAwait(false);
            var legacyChannels = channels.Where(c =>
                string.Equals(c.Name, "通用", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "通用_S7Plus", StringComparison.OrdinalIgnoreCase)).ToList();

            var matches = new List<(IgwProjectApi.NodeInfo Device, string Logical, string Endpoint, string ChannelId, string ChannelName)>();
            foreach (var channel in legacyChannels)
            {
                var devices = await WithTimeoutAsync(_treeRequestTimeout, ct,
                    t => IgwProjectApi.GetChildrenAsync(_http, baseUrl, "channel", channel.Id, t)).ConfigureAwait(false);
                foreach (var device in devices.Where(d => string.Equals(d.Kind, "device", StringComparison.OrdinalIgnoreCase)))
                {
                    var entity = await WithTimeoutAsync(_treeRequestTimeout, ct,
                        t => IgwProjectApi.GetEntityAsync(_http, baseUrl, "device", device.Id, t)).ConfigureAwait(false);
                    var logical = entity?.Settings != null &&
                                  entity.Settings.TryGetValue(DcsIgwConsts.DcsLogicalDeviceSettingKey, out var lv)
                        ? lv
                        : null;
                    if (logical == null || !legacyLogicalNames.Contains(logical))
                    {
                        continue;
                    }
                    var host = ParseEndpointHost(entity?.Endpoint);
                    if (!string.Equals(host, ip, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    matches.Add((device, logical, entity?.Endpoint, channel.Id, channel.Name));
                }
            }

            var failures = new List<string>();
            foreach (var match in matches)
            {
                var description = $"{match.Device.Name}（逻辑名 {match.Logical}，endpoint {match.Endpoint}）";
                var removal = new IgwLegacyDeviceRemoval(description, match.ChannelName, match.Logical,
                    string.Equals(match.ChannelName, "通用_S7Plus", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(match.Logical, DcsIgwConsts.GraphPlusDeviceLogicalName(ip), StringComparison.OrdinalIgnoreCase));
                var error = await DeleteEntityVerifiedAsync(baseUrl, "device", match.Device.Id, "channel", match.ChannelId, ct).ConfigureAwait(false);
                if (error == null)
                {
                    _logger.LogInformation("[IgwBulkClient] 已清理旧设备 {Description}", description);
                    results.Add(removal);
                    continue;
                }
                _logger.LogWarning("[IgwBulkClient] 清理旧设备失败 {Description}：{Error}", description, error);
                failures.Add($"{description}：{error}");
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"清理旧设备失败 {failures.Count}/{matches.Count} 个（旧别名残留会导致读链解析冲突，本次注册终止）：{string.Join("；", failures)}");
            }

            return results;
        }

        /// <summary>
        /// 清理本项目在指定 IGW 通道下的设备（I-3 注册身份变化 / M-4 删除项目）：只删 settings.dcsLogicalDevice ∈
        /// {S7_{ip}, S1500_{ip}, S7Plus_{ip}} 且带 dcsManagedBy=bulk 的设备；逻辑名匹配但无该标记的设备不删、放进
        /// SkippedUnmanaged（可能是现场手建或老版本 IGW 建的）；其它设备不动。deleteChannelIfEmpty=true 且删完后通道没有任何
        /// 子节点 → 删除通道。通道不存在 → ChannelFound=false，不报错。任一删除失败 → 抛 InvalidOperationException（列出全部失败项）。
        /// </summary>
        public async Task<IgwProjectDeviceCleanup> RemoveProjectDevicesAsync(string iotUrl, string channelName, string plcIp,
            bool deleteChannelIfEmpty, CancellationToken ct)
        {
            var baseUrl = NormalizeBaseUrlOrThrow(iotUrl);
            var lookup = await FindProjectDeviceMatchesAsync(baseUrl, channelName, plcIp, ct).ConfigureAwait(false);
            if (lookup.Channel == null)
            {
                return new IgwProjectDeviceCleanup(Array.Empty<IgwLegacyDeviceRemoval>(), Array.Empty<IgwLegacyDeviceRemoval>(), false, false);
            }
            var channel = lookup.Channel;

            var removed = new List<IgwLegacyDeviceRemoval>();
            var skipped = new List<IgwLegacyDeviceRemoval>();
            var failures = new List<string>();
            foreach (var match in lookup.Matches)
            {
                if (!match.ManagedByBulk)
                {
                    skipped.Add(match.Device);
                    continue;
                }
                var error = await DeleteEntityVerifiedAsync(baseUrl, "device", match.DeviceId, "channel", channel.Id, ct).ConfigureAwait(false);
                if (error == null)
                {
                    _logger.LogInformation("[IgwBulkClient] 已清理项目设备 {Description}", match.Device.Description);
                    removed.Add(match.Device);
                }
                else
                {
                    failures.Add($"{match.Device.Description}：{error}");
                }
            }
            if (failures.Count > 0)
            {
                throw new InvalidOperationException($"清理通道 {channel.Name} 中的项目设备失败 {failures.Count} 个：{string.Join("；", failures)}");
            }

            var channelDeleted = false;
            if (deleteChannelIfEmpty)
            {
                var remaining = await WithTimeoutAsync(_treeRequestTimeout, ct,
                    t => IgwProjectApi.GetChildrenAsync(_http, baseUrl, "channel", channel.Id, t)).ConfigureAwait(false);
                if (remaining.Count == 0)
                {
                    var error = await DeleteEntityVerifiedAsync(baseUrl, "channel", channel.Id, "project", lookup.RootId, ct).ConfigureAwait(false);
                    if (error != null)
                    {
                        throw new InvalidOperationException($"删除空通道 {channel.Name} 失败：{error}");
                    }
                    channelDeleted = true;
                }
            }
            return new IgwProjectDeviceCleanup(removed, skipped, true, channelDeleted);
        }

        /// <summary>
        /// 只读查询（I-3）：指定 IGW 通道下 settings.dcsLogicalDevice ∈ {S7_{ip}, S1500_{ip}, S7Plus_{ip}} 的设备（不论有无批量标记）。
        /// 通道不存在 → ChannelFound=false、空清单。树读取失败 → 抛出（超时为 <see cref="TimeoutException"/>）。
        /// </summary>
        public async Task<IgwProjectDeviceScan> FindProjectDevicesAsync(string iotUrl, string channelName, string plcIp,
            CancellationToken ct)
        {
            var lookup = await FindProjectDeviceMatchesAsync(NormalizeBaseUrlOrThrow(iotUrl), channelName, plcIp, ct).ConfigureAwait(false);
            return new IgwProjectDeviceScan(lookup.Channel != null, lookup.Matches.Select(m => m.Device).ToList());
        }

        private sealed record ProjectDeviceMatch(string DeviceId, IgwLegacyDeviceRemoval Device, bool ManagedByBulk);

        private sealed record ProjectDeviceLookup(string RootId, IgwProjectApi.NodeInfo Channel, IReadOnlyList<ProjectDeviceMatch> Matches);

        /// <summary>按通道名（忽略大小写）找通道，列出逻辑名属于 {S7_, S1500_, S7Plus_}{ip} 的设备；通道不存在 → Channel=null</summary>
        private async Task<ProjectDeviceLookup> FindProjectDeviceMatchesAsync(string baseUrl, string channelName, string plcIp, CancellationToken ct)
        {
            var channelTrimmed = (channelName ?? string.Empty).Trim();
            var ip = (plcIp ?? string.Empty).Trim();
            if (channelTrimmed.Length == 0)
            {
                return new ProjectDeviceLookup(null, null, Array.Empty<ProjectDeviceMatch>());
            }
            var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ip.Length > 0)
            {
                logicalNames.Add(IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7, ip));
                logicalNames.Add(IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, ip));
                logicalNames.Add(IgwTagNaming.LogicalDeviceName(IgwDeviceKind.Graph, ip));
            }
            var graphLogical = IgwTagNaming.LogicalDeviceName(IgwDeviceKind.Graph, ip);

            var rootId = await WithTimeoutAsync(_treeRequestTimeout, ct,
                t => IgwProjectApi.GetRootIdAsync(_http, baseUrl, t)).ConfigureAwait(false);
            var channels = await WithTimeoutAsync(_treeRequestTimeout, ct,
                t => IgwProjectApi.GetChildrenAsync(_http, baseUrl, "project", rootId, t)).ConfigureAwait(false);
            var channel = channels.FirstOrDefault(c => string.Equals(c.Name, channelTrimmed, StringComparison.OrdinalIgnoreCase));
            if (channel == null)
            {
                return new ProjectDeviceLookup(rootId, null, Array.Empty<ProjectDeviceMatch>());
            }

            var matches = new List<ProjectDeviceMatch>();
            var devices = await WithTimeoutAsync(_treeRequestTimeout, ct,
                t => IgwProjectApi.GetChildrenAsync(_http, baseUrl, "channel", channel.Id, t)).ConfigureAwait(false);
            foreach (var device in devices.Where(d => string.Equals(d.Kind, "device", StringComparison.OrdinalIgnoreCase)))
            {
                var entity = await WithTimeoutAsync(_treeRequestTimeout, ct,
                    t => IgwProjectApi.GetEntityAsync(_http, baseUrl, "device", device.Id, t)).ConfigureAwait(false);
                string logical = null;
                string managedBy = null;
                entity?.Settings?.TryGetValue(DcsIgwConsts.DcsLogicalDeviceSettingKey, out logical);
                entity?.Settings?.TryGetValue(ManagedBySettingKey, out managedBy);
                if (logical == null || !logicalNames.Contains(logical))
                {
                    continue;
                }
                var description = $"{channel.Name}/{device.Name}（逻辑名 {logical}，endpoint {entity?.Endpoint}）";
                var isGraph = string.Equals(logical, graphLogical, StringComparison.OrdinalIgnoreCase);
                matches.Add(new ProjectDeviceMatch(device.Id, new IgwLegacyDeviceRemoval(description, channel.Name, logical, isGraph),
                    string.Equals(managedBy, ManagedByBulk, StringComparison.OrdinalIgnoreCase)));
            }
            return new ProjectDeviceLookup(rootId, channel, matches);
        }

        /// <summary>
        /// 删除一个实体（设备 / 通道）；删除请求失败时重读父级子节点清单，实体已不存在视为成功（并发任务已删）。
        /// 返回 null = 成功，否则为错误描述。
        /// </summary>
        private async Task<string> DeleteEntityVerifiedAsync(string baseUrl, string entityKind, string entityId,
            string parentKind, string parentId, CancellationToken ct)
        {
            string error;
            try
            {
                var mutate = await WithTimeoutAsync(_mutationTimeout, ct,
                    t => IgwProjectApi.MutateWithRetryAsync(_http, baseUrl, "deleteEntities", entityKind,
                        new[] { entityId }, null, null, t)).ConfigureAwait(false);
                if (mutate.Ok)
                {
                    return null;
                }
                error = mutate.Error;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = ex.Message;
            }

            try
            {
                var current = await WithTimeoutAsync(_treeRequestTimeout, ct,
                    t => IgwProjectApi.GetChildrenAsync(_http, baseUrl, parentKind, parentId, t)).ConfigureAwait(false);
                if (!current.Any(d => string.Equals(d.Id, entityId, StringComparison.Ordinal)))
                {
                    _logger.LogInformation("[IgwBulkClient] {Kind} {Id} 删除请求失败（{Error}），但复查已不存在（可能被并发任务清理），视为成功",
                        entityKind, entityId, error);
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = $"{error}；复查清单失败：{ex.Message}";
            }
            return error;
        }

        /// <summary>用独立于其它调用的 <paramref name="timeout"/> 包一次异步调用；超时（且不是调用方自己取消）时
        /// 转换成 <see cref="TimeoutException"/>，调用方主动取消（<paramref name="ct"/> 已请求取消）时原样透传。</summary>
        private static async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> action)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);
            try
            {
                return await action(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"IGW 请求超过 {timeout.TotalSeconds:0.#}s 未响应");
            }
        }

        /// <summary>从 endpoint 字符串解析主机部分：兼容 "ip"、"ip:port"、"opc.tcp://host:port"、"host/path" 等形态。</summary>
        private static string ParseEndpointHost(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return string.Empty;
            }

            var trimmed = endpoint.Trim();
            var schemeIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
            {
                trimmed = trimmed.Substring(schemeIdx + 3);
            }
            var slashIdx = trimmed.IndexOf('/');
            if (slashIdx >= 0)
            {
                trimmed = trimmed.Substring(0, slashIdx);
            }
            var colonIdx = trimmed.LastIndexOf(':');
            if (colonIdx >= 0 && int.TryParse(trimmed.Substring(colonIdx + 1), out _))
            {
                trimmed = trimmed.Substring(0, colonIdx);
            }
            return trimmed.Trim();
        }
    }
}
