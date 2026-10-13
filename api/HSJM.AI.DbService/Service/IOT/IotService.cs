using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;

namespace HSJM.AI.DbService.Service.IOT
{
    /// <summary>
    /// IOT Service
    /// </summary>
    public class IotService : IIotService, IAutoInject
    {

        private readonly RequestToHttpHelper _requestToHttpHelper;
        public IotService(RequestToHttpHelper requestToHttpHelper)
        {
            _requestToHttpHelper = requestToHttpHelper;
        }

        #region 双网关适配层（新 IGW /Tag/* 契约）

        /// <summary>网关类型探测结果缓存有效期（秒）</summary>
        private const int GatewayDetectionCacheSeconds = 300;

        /// <summary>网关类型探测缓存：规范化 baseUrl → (类型, 过期时间)。静态跨实例共享（本服务为注入实例）。</summary>
        private static readonly ConcurrentDictionary<string, (GatewayFlavor Flavor, DateTime ExpiresAtUtc)> _gatewayFlavorCache =
            new ConcurrentDictionary<string, (GatewayFlavor, DateTime)>();

        /// <summary>清空网关类型探测缓存（单元测试 / 手动强制重探用）。</summary>
        public static void ClearGatewayDetectionCache()
        {
            _gatewayFlavorCache.Clear();
        }

        /// <summary>
        /// 探测网关类型。swagger 优先，失败回退路由存在性探测（GET /Tag/AddTags → 405 = 新 IGW），
        /// 全部失败按老网关处理（老网关调用失败会走原 RegFailed 语义，不会误发新契约）。
        /// </summary>
        public async Task<GatewayFlavor> DetectGatewayAsync(string host)
        {
            var baseUrl = DcsIgwConsts.NormalizeGatewayBaseUrl(host);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return GatewayFlavor.Legacy;
            }

            if (_gatewayFlavorCache.TryGetValue(baseUrl, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            {
                return cached.Flavor;
            }

            var flavor = await ProbeGatewayFlavorAsync(baseUrl).ConfigureAwait(false);
            _gatewayFlavorCache[baseUrl] = (flavor, DateTime.UtcNow.AddSeconds(GatewayDetectionCacheSeconds));
            return flavor;
        }

        private async Task<GatewayFlavor> ProbeGatewayFlavorAsync(string baseUrl)
        {
            // 探测 1：swagger 文档 paths（最精确，且能区分"有 swagger 但无 /Tag/*"的老网关）
            try
            {
                var swagger = await _requestToHttpHelper.GetAsync<SwaggerPathsDto>(new HttpRequestModel
                {
                    Host = baseUrl,
                    Path = "swagger/v1/swagger.json"
                }).ConfigureAwait(false);

                if (swagger != null && swagger.IsSuccess && swagger.BackResult != null && swagger.BackResult.Paths != null)
                {
                    foreach (var path in swagger.BackResult.Paths.Keys)
                    {
                        if (path == null)
                        {
                            continue;
                        }
                        if (path.IndexOf("/tag/addtags", StringComparison.OrdinalIgnoreCase) >= 0
                            || path.IndexOf("/tag/readtagsaslist", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return GatewayFlavor.NewIgw;
                        }
                    }
                    foreach (var path in swagger.BackResult.Paths.Keys)
                    {
                        if (path != null && path.IndexOf("/driverinfo/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return GatewayFlavor.Legacy;
                        }
                    }
                }
            }
            catch
            {
                // swagger 探测失败（连接拒绝/超时/非 JSON）→ 继续路由存在性探测
            }

            // 探测 2：路由存在性。GET 一个仅支持 POST 的 /Tag 路由：
            // 新 IGW（ASP.NET Core 路由）→ 405 MethodNotAllowed；老网关/SPA 兜底 → 200 或 404。
            try
            {
                var probe = await _requestToHttpHelper.GetAsync<object>(new HttpRequestModel
                {
                    Host = baseUrl,
                    Path = "Tag/AddTags"
                }).ConfigureAwait(false);

                if (probe != null && probe.HttpStatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    return GatewayFlavor.NewIgw;
                }
            }
            catch
            {
                // 探测失败 → 老网关回退
            }

            return GatewayFlavor.Legacy;
        }

        /// <summary>swagger 文档的最小投影（只取 paths 键集合做路由判定）。</summary>
        private sealed class SwaggerPathsDto
        {
            public Dictionary<string, object> Paths { get; set; }
        }

        /// <summary>热重载重试参数：503「网关正在热重载」是瞬时窗口，重试即可恢复（3 次 × 600ms）。</summary>
        private const int IgwReloadRetryAttempts = 3;
        private const int IgwReloadRetryDelayMs = 600;

        /// <summary>
        /// POST + 热重载重试：仅当 HTTP 503 或错误体含「网关正在热重载」时重试（最多 3 次 × 600ms），其余错误立即返回。
        /// </summary>
        private async Task<HttpResponseResultModel<E>> PostIgwWithReloadRetryAsync<E>(HttpRequestModel requestModel)
        {
            HttpResponseResultModel<E> res = null;
            for (var attempt = 0; attempt < IgwReloadRetryAttempts; attempt++)
            {
                res = await _requestToHttpHelper.PostAsync<E>(requestModel).ConfigureAwait(false);
                if (res != null && res.IsSuccess)
                {
                    return res;
                }
                var reloading = res != null
                    && (res.HttpStatusCode == HttpStatusCode.ServiceUnavailable
                        || (res.ErrorMessage ?? string.Empty).Contains("热重载", StringComparison.Ordinal));
                if (!reloading)
                {
                    return res;
                }
                if (attempt < IgwReloadRetryAttempts - 1)
                {
                    await Task.Delay(IgwReloadRetryDelayMs).ConfigureAwait(false);
                }
            }
            return res;
        }

        /// <summary>
        /// 新 IGW：POST /Tag/AddTags。整批语义：失败时整批 isSuccess=false（errorMessage 如 "索引 0 的 IP 不能为空"）；
        /// 成功时 backResult 为 null，这里按入参顺序合成 IsGood=true 明细，保持与老接口消费方（按索引对位）同构。
        /// </summary>
        public async Task<HttpResponseResultModel<List<ManualAddTagItemsResult>>> ManualAddTagItemsV2(string host, List<DcsTagUpdateModel> tags)
        {
            var response = new HttpResponseResultModel<List<ManualAddTagItemsResult>>();
            if (tags == null || tags.Count == 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "变量为空";
                return response;
            }

            var baseUrl = DcsIgwConsts.NormalizeGatewayBaseUrl(host);
            var requestModel = new HttpRequestModel { Host = baseUrl, Path = "Tag/AddTags", Data = tags };
            // 2026-09-17：AddTags 是整工程应用级操作，工程涨到 20 万+标签后单批应用实测超 100s
            // （HttpClient 默认超时把任务打死在 17:28 的 205K/278K 处）——显式放宽到 10 分钟。
            requestModel.TimeOut = 600_000;
            try
            {
                // IGW 响应壳 DcsResponse{httpStatusCode,isSuccess,backResult,errorMessage} 与 HttpResponseResultModel 字段名同构，
                // 复用现有反序列化路径：外层是 HTTP 包装，BackResult 即网关自己的响应壳。
                var res = await PostIgwWithReloadRetryAsync<HttpResponseResultModel<string>>(requestModel).ConfigureAwait(false);

                var envelope = res.BackResult;
                if (envelope == null)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = res.IsSuccess
                        ? $"新IGW无有效返回(HTTP {(int)res.HttpStatusCode})"
                        : $"新IGW注册HTTP失败({(int)res.HttpStatusCode}): {TruncateError(res.ErrorMessage)}";
                }
                else if (!envelope.IsSuccess)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = envelope.ErrorMessage;
                }
                else
                {
                    response.IsSuccess = true;
                    response.BackResult = tags
                        .Select(t => new ManualAddTagItemsResult { IsGood = true })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                WriteLogHelper.WriteLogsAsync($"新IGW注册点位失败[{baseUrl}]: {ex.Message}");
            }
            return response;
        }

        /// <summary>
        /// 新 IGW：POST /Tag/ReadTagsAsList。按 1000 上限内部分批并合并为单一按序对齐结果；
        /// 任一批失败则整体失败（新 IGW 读接口本就是"任一点点位不存在整批报错"的语义）。
        /// </summary>
        public async Task<HttpResponseResultModel<List<PLC_ReadTagResult>>> ReadTagsAsList(
            string host, List<string> tagNames, bool keepPolling = false, TimeSpan? requestTimeout = null)
        {
            var response = new HttpResponseResultModel<List<PLC_ReadTagResult>>();
            if (tagNames == null || tagNames.Count == 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "变量为空";
                return response;
            }

            var baseUrl = DcsIgwConsts.NormalizeGatewayBaseUrl(host);
            var merged = new List<PLC_ReadTagResult>(tagNames.Count);

            try
            {
                for (var offset = 0; offset < tagNames.Count; offset += DcsIgwConsts.ReadBatchLimit)
                {
                    var batchCount = Math.Min(DcsIgwConsts.ReadBatchLimit, tagNames.Count - offset);
                    var batch = tagNames.GetRange(offset, batchCount);

                    var requestModel = new HttpRequestModel
                    {
                        Host = baseUrl,
                        Path = IgwReadPath.TagsAsList(keepPolling),
                        Data = batch,
                    };
                    if (requestTimeout.HasValue)
                    {
                        requestModel.TimeOut = ToRequestTimeoutMs(requestTimeout.Value);
                    }
                    var res = await PostIgwWithReloadRetryAsync<HttpResponseResultModel<List<DcsTagReadItem>>>(requestModel)
                        .ConfigureAwait(false);

                    var envelope = res.BackResult;
                    if (envelope == null)
                    {
                        // HTTP 层失败（网关不可达/网关进程无响应）= 整体失败，保持原语义
                        response.IsSuccess = false;
                        response.ErrorMessage = res.IsSuccess
                            ? $"新IGW读值无有效返回(HTTP {(int)res.HttpStatusCode})"
                            : $"新IGW读值HTTP失败({(int)res.HttpStatusCode}): {TruncateError(res.ErrorMessage)}";
                        return response;
                    }
                    if (!envelope.IsSuccess)
                    {
                        // 2026-09-18 批内业务失败（典型=「点位不存在」/Bad 质量整批报错）不再一损俱损：
                        // 该批全部标记 IsGood=false 后继续后续批，调用方按行级 IsGood 降级
                        // （此前任一批失败丢弃全部已读批，混合批次里 1 个缺失点位会把整批 Good 值陪葬——
                        // LineUp 绑定 Degraded、诊断采 0 信号的放大器）。网关级故障仍走上方整体失败。
                        // 2026-10-09 检查清单 #13：非 keepPolling 场景进一步做坏点隔离——批内一个过期
                        // 别名曾让同批其余正常点（如报警阶段同批最多 199 个有效报警位）全部失去值。
                        // keepPolling（设备监控 100ms 轮询）保持整批标坏的原语义：下一轮本来就会重读，
                        // 不做定位重试以免拖慢轮询节奏。
                        if (!keepPolling)
                        {
                            await ReadBatchIsolatingBadPointsAsync(
                                baseUrl, batch, envelope.ErrorMessage ?? string.Empty, requestTimeout, merged)
                                .ConfigureAwait(false);
                            continue;
                        }
                        foreach (var name in batch)
                        {
                            merged.Add(new PLC_ReadTagResult
                            {
                                IsGood = false,
                                TagName = name,
                                ErrorMessage = TruncateError(envelope.ErrorMessage),
                            });
                        }
                        continue;
                    }

                    merged.AddRange(AlignReadItems(batch, envelope.BackResult));
                }

                response.IsSuccess = true;
                response.BackResult = merged;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                // HttpClient 超时抛 TaskCanceledException（OperationCanceledException 子类）：标 408，
                // 调用方据此判定"IGW 那边还在排队/现场读"，不要立即原样重发把队列越堵越长
                if (ex is OperationCanceledException or TimeoutException)
                {
                    response.HttpStatusCode = HttpStatusCode.RequestTimeout;
                }
                WriteLogHelper.WriteLogsAsync($"新IGW读取点位失败[{baseUrl}]: {ex.Message}");
            }
            return response;
        }

        /// <summary>
        /// 坏点隔离的额外请求预算（含热重载重试）。权衡同 IgwDiagnosticReader.MaxRequestsPerBatch：
        /// 报文只报一个坏点，坏点多时定位式重试退化为接近逐点请求；超预算的剩余点整段标坏
        ///（附原因），用"部分点位读不到"换"响应时间有界"。常见场景（1~3 个过期别名）开销极小。
        /// </summary>
        private const int BadPointIsolationMaxRequests = 8;

        /// <summary>
        /// 批内业务失败后的坏点隔离（2026-10-09 检查清单 #13，复用 IgwDiagnosticReader 的定位策略）：
        /// ①先用已到达的失败报文定位坏点（不花请求）——「点位不存在」等内容性坏点标坏后，剩余点
        /// 重发一批即可全部读到值；②定位不了 → 对半拆分重发逐步缩小；③通讯故障/超时是设备级的，
        /// 整段标坏并保留原报文（调用方 HasTransientCommFailure 的补读语义不变）；④请求预算
        /// <see cref="BadPointIsolationMaxRequests"/>，超预算整段标坏。本批全部落行（好行按值、坏行带原因）。
        /// </summary>
        private async Task ReadBatchIsolatingBadPointsAsync(
            string baseUrl, List<string> batch, string firstError, TimeSpan? requestTimeout, List<PLC_ReadTagResult> merged)
        {
            var requests = 0;
            var pending = new Stack<List<string>>();

            LocateBadPointAndSplit(batch, firstError, pending, merged);
            while (pending.Count > 0)
            {
                var segment = pending.Pop();
                if (segment.Count == 0)
                {
                    continue;
                }
                if (requests >= BadPointIsolationMaxRequests)
                {
                    MarkSegmentBad(segment, merged, "坏点定位次数超限");
                    continue;
                }
                requests++;

                var requestModel = new HttpRequestModel
                {
                    Host = baseUrl,
                    Path = IgwReadPath.TagsAsList(keepPolling: false),
                    Data = segment,
                };
                if (requestTimeout.HasValue)
                {
                    requestModel.TimeOut = ToRequestTimeoutMs(requestTimeout.Value);
                }
                try
                {
                    var res = await PostIgwWithReloadRetryAsync<HttpResponseResultModel<List<DcsTagReadItem>>>(requestModel)
                        .ConfigureAwait(false);
                    var envelope = res?.BackResult;
                    if (res == null || !res.IsSuccess || envelope == null)
                    {
                        // 隔离读取期间网关整体失败：本段标坏（保留已成功合并的行），不再让外层整体失败
                        MarkSegmentBad(segment, merged,
                            res?.ErrorMessage ?? $"新IGW读值无有效返回(HTTP {(int)(res?.HttpStatusCode ?? 0)})");
                        continue;
                    }
                    if (envelope.IsSuccess)
                    {
                        merged.AddRange(AlignReadItems(segment, envelope.BackResult));
                        continue;
                    }
                    LocateBadPointAndSplit(segment, envelope.ErrorMessage ?? string.Empty, pending, merged);
                }
                catch (Exception ex)
                {
                    MarkSegmentBad(segment, merged, ex.Message);
                }
            }
        }

        /// <summary>
        /// 解析失败报文并分流：定位到坏点 → 坏行落结果、剩余段入栈（设备级故障整段标坏）；
        /// 定位不了 → 单点标坏、多点对半拆分入栈。与 IgwDiagnosticReader 的同名策略语义一致。
        /// </summary>
        private static void LocateBadPointAndSplit(
            List<string> segment, string message, Stack<List<string>> pending, List<PLC_ReadTagResult> merged)
        {
            if (IgwLegacyReadErrorParser.TryLocate(message, segment, out var badAlias, out var status))
            {
                merged.Add(new PLC_ReadTagResult
                {
                    TagName = badAlias,
                    IsGood = false,
                    ErrorMessage = TruncateError(message),
                });
                var rest = segment.Where(a => !string.Equals(a, badAlias, StringComparison.Ordinal)).ToList();
                if (status is PLC_SignalReadStatus.CommFailure or PLC_SignalReadStatus.Timeout)
                {
                    // 设备级故障：本段其余点大概率同样失败，整段标坏并保留原报文——
                    // 调用方（ReadIgwTagsWithRetryCoreAsync.HasTransientCommFailure）按报文触发整批补读
                    MarkSegmentBad(rest, merged, TruncateError(message));
                    return;
                }
                if (rest.Count > 0)
                {
                    pending.Push(rest);
                }
                return;
            }
            if (segment.Count == 1)
            {
                merged.Add(new PLC_ReadTagResult
                {
                    TagName = segment[0],
                    IsGood = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(message)
                        ? "IGW 读取失败（无错误信息）"
                        : TruncateError(message),
                });
                return;
            }
            var half = segment.Count / 2;
            pending.Push(segment.GetRange(half, segment.Count - half));
            pending.Push(segment.GetRange(0, half));
        }

        private static void MarkSegmentBad(List<string> segment, List<PLC_ReadTagResult> merged, string error)
        {
            foreach (var name in segment)
            {
                merged.Add(new PLC_ReadTagResult
                {
                    TagName = name,
                    IsGood = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(error) ? "IGW 读取失败" : TruncateError(error),
                });
            }
        }

        /// <summary>
        /// 请求超时 → HttpRequestModel.TimeOut 毫秒数。30000 是 RequestToHttpHelper 的"未设置"哨兵值
        /// （等于它时不覆盖 HttpClient 默认 100 s），所以要避开它。
        /// </summary>
        internal static int ToRequestTimeoutMs(TimeSpan timeout)
        {
            var ms = (int)Math.Max(1, Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            return ms == 30000 ? 30001 : ms;
        }

        /// <summary>
        /// 把一批 backResult:[{key,value}] 对齐到请求名列表（索引对齐）。
        /// key 正常回显请求名；防御性兜底：精确匹配失败时按 ".{请求名}" 后缀匹配（容忍网关回显物理全路径）。
        /// </summary>
        private static List<PLC_ReadTagResult> AlignReadItems(List<string> requestedNames, List<DcsTagReadItem> items)
        {
            var byKey = new Dictionary<string, DcsTagReadItem>(StringComparer.OrdinalIgnoreCase);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.Key) && !byKey.ContainsKey(item.Key.Trim()))
                    {
                        byKey[item.Key.Trim()] = item;
                    }
                }
            }

            var results = new List<PLC_ReadTagResult>(requestedNames.Count);
            foreach (var name in requestedNames)
            {
                var requested = (name ?? string.Empty).Trim();
                DcsTagReadItem matched = null;
                if (requested.Length > 0 && byKey.TryGetValue(requested, out var exact))
                {
                    matched = exact;
                }
                else if (requested.Length > 0)
                {
                    matched = items?.FirstOrDefault(item =>
                        item != null
                        && !string.IsNullOrWhiteSpace(item.Key)
                        && item.Key.Trim().EndsWith("." + requested, StringComparison.OrdinalIgnoreCase));
                }

                if (matched == null)
                {
                    results.Add(new PLC_ReadTagResult
                    {
                        IsGood = false,
                        TagName = requested,
                        ErrorMessage = "网关未返回该标签值",
                    });
                }
                else
                {
                    results.Add(new PLC_ReadTagResult
                    {
                        IsGood = true,
                        TagName = requested,
                        OldTagName = string.Equals(matched.Key?.Trim(), requested, StringComparison.OrdinalIgnoreCase)
                            ? requested
                            : matched.Key?.Trim(),
                        Value = DcsIgwMapper.NormalizeDcsValue(matched.Value),
                    });
                }
            }
            return results;
        }

        /// <summary>D-1：POST /Tag/ReadTagsWithQuality 单次调用（见 IIotService 注释）。</summary>
        public Task<IgwTransportResult<List<DcsTagQualityItem>>> ReadTagsWithQualityAsync(string host, List<string> aliases, TimeSpan? requestTimeout = null) =>
            PostIgwRawAsync<List<DcsTagQualityItem>>(host, "Tag/ReadTagsWithQuality", aliases, requestTimeout);

        /// <summary>D-1：POST /Tag/ReadTagsAsList 单次调用，保留原始 envelope（见 IIotService 注释）。</summary>
        public Task<IgwTransportResult<List<DcsTagReadItem>>> ReadTagsAsListRawAsync(string host, List<string> aliases, TimeSpan? requestTimeout = null) =>
            PostIgwRawAsync<List<DcsTagReadItem>>(host, "Tag/ReadTagsAsList", aliases, requestTimeout);

        /// <summary>单次 POST，不重试不吞异常；2xx → Envelope，非 2xx → RawBody。</summary>
        private async Task<IgwTransportResult<T>> PostIgwRawAsync<T>(string host, string path, List<string> aliases, TimeSpan? requestTimeout = null)
        {
            var baseUrl = DcsIgwConsts.NormalizeGatewayBaseUrl(host);
            var requestModel = new HttpRequestModel { Host = baseUrl, Path = path, Data = aliases };
            if (requestTimeout.HasValue)
            {
                // D-1 修复回合 1：调用方（IgwDiagnosticReader）自己对这次调用有一层 WaitAsync 超时；
                // 不把同样的超时下传到 HttpClient 的话，调用方不再等待了，但 HTTP 请求本体仍会继续跑到
                // HttpClient 默认的 100s 才真正结束（见 RequestToHttpHelper.PostAsync 的 2026-09-17 注释：
                // 只有 TimeOut != 30000（哨兵值，代表"未显式设置"）才会覆盖 client.Timeout）。这里取整并
                // 避开这个哨兵值，确保显式传入的超时一定生效。
                requestModel.TimeOut = ToRequestTimeoutMs(requestTimeout.Value);
            }
            var res = await _requestToHttpHelper
                .PostAsync<HttpResponseResultModel<T>>(requestModel)
                .ConfigureAwait(false);
            return new IgwTransportResult<T>
            {
                HttpStatusCode = res.HttpStatusCode,
                IsHttpSuccess = res.IsSuccess,
                Envelope = res.IsSuccess ? res.BackResult : null,
                RawBody = res.IsSuccess ? null : res.ErrorMessage,
            };
        }

        /// <summary>HTTP 层失败时截取响应体做错误信息（响应体可能是含 errorMessage 的 JSON，截断防日志爆炸）。</summary>
        private static string TruncateError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return "无响应体";
            }
            return error.Length <= 300 ? error : error.Substring(0, 300) + "…";
        }

        #endregion

        /// <summary>
        /// 读取变量
        /// </summary>
        /// <param name="readDto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<List<PLC_ReadTagResult>>> ReadTags(string host, PLC_ReadTagParame readDto)
        {
            HttpResponseResultModel<List<PLC_ReadTagResult>> response = new HttpResponseResultModel<List<PLC_ReadTagResult>>();
            var requestModel = new HttpRequestModel { Host = host, Path = "/DriverInfo/ReadTag", Data = readDto };
            try
            {
                if (!readDto.TagList.IsListNullOrEmpty())
                {
                    string str = $"=======开始，总共数量({readDto.TagList.Count})==================================================================================================================\r\n"; 
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    stopwatch.Start();
                    var res = await _requestToHttpHelper.PostAsync<HttpResponseResultModel<List<PLC_ReadTagResult>>>(requestModel).ConfigureAwait(false);
                
                    stopwatch.Stop();
                    str += $"响应时间：{stopwatch.ElapsedMilliseconds}\r\n";
                    str += $"请求参数：{JsonConvert.SerializeObject(readDto)}\r\n";
                    str += $"返回结果：{JsonConvert.SerializeObject(res.BackResult)}\r\n";
                    str += $"=======结束，总共数量({readDto.TagList.Count})==================================================================================================================\r\n";
                    WriteLogHelper.WriteLogsAsync(str, "IOTReadTags");
                    if (stopwatch.ElapsedMilliseconds > 600) {
                        WriteLogHelper.WriteLogsAsync(str, "IOTReadTags读取超600");
                    }
                    // 2026-09-18 修复：与 AddGroupTagItems(2026-09-12) 同款 null 防御——
                    // ioturl 指向新 IGW(48020) 时 /DriverInfo/ReadTag 必 404，PostAsync 返回壳 BackResult=null，
                    // 旧代码把 null 直接赋给 response 返回，调用方 res.IsSuccess 即 NRE
                    // （工位影子巡检整站崩溃的根因之一）。
                    response = res?.BackResult ?? new HttpResponseResultModel<List<PLC_ReadTagResult>>
                    {
                        IsSuccess = false,
                        ErrorMessage = res?.IsSuccess == true
                            ? $"老网关读值无有效返回(HTTP {(int?)res?.HttpStatusCode})"
                            : $"老网关读值HTTP失败({(int?)res?.HttpStatusCode ?? 0}): {TruncateError(res?.ErrorMessage)}"
                    };
                }
                else
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = "变量为空";
                }
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                WriteLogHelper.WriteLogsAsync($"读取变量[{JsonHelper.SerializeObject(readDto)}]" + ex.Message);
            }
            return response;
        }

        /// <summary>
        /// 一次性添加组里面所有变量
        /// </summary>
        /// <param name="host">IOT网关地址</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="groupName">DB块名称</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> AddGroupTagItems(string host, string deviceName, string groupName)
        {
            HttpResponseResultModel<string> response = new HttpResponseResultModel<string>();
            var requestModel = new HttpRequestModel { Host = host, Path = "/DriverInfo/AddGroupTagItems", Data = new { deviceName, groupName } };
            try
            {
                var res = await _requestToHttpHelper.PostAsync<HttpResponseResultModel<string>>(requestModel).ConfigureAwait(false);
                // 2026-09-12 修复：HTTP 层完成但 BackResult 为 null（路由不存在/非老网关协议，典型=ioturl 指向新 IGW 48020
                // 时 /DriverInfo/* 返回 404 空响应）曾把 null 直接返回给调用方，调用方 res.IsSuccess 即 NRE。
                // 现在改为返回带明确错误信息的失败结果，绝不返回 null。
                response = res?.BackResult ?? new HttpResponseResultModel<string>
                {
                    IsSuccess = false,
                    ErrorMessage = $"网关无返回（{host}/DriverInfo/AddGroupTagItems 路由不存在或网关非老网关协议；若 ioturl 指向新 IGW，请改用 ExecBatchRegistrationTagV2——按网关类型自动分派）"
                };
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                WriteLogHelper.WriteLogsAsync($"一次性添加组里面所有变量" + ex.Message);
            }
            return response;
        }

        /// <summary>
        /// 手动添加I点,Q点
        /// </summary>
        /// <param name="host">IOT网关地址</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="tagIems">注册标签列表</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<List<ManualAddTagItemsResult>>> ManualAddTagItems(string host, string deviceName, List<IQTagItem> tagIems)
        {
            HttpResponseResultModel<List<ManualAddTagItemsResult>> response = new HttpResponseResultModel<List<ManualAddTagItemsResult>>();
            tagIems.ForEach(x =>
            {
                // 2026-09-12 防御：Address 为 null 的条目曾在此 NRE（ForEach 在 try 外，异常直接穿透到调用方）
                x.Address = (x.Address ?? string.Empty).Replace("QW", "Q").Replace("QDW", "Q");
            });
            var requestModel = new HttpRequestModel { Host = host, Path = "/DriverInfo/ManualAddTagItemsAsync", Data = new { deviceName, tagInfos = tagIems } };
            try
            {
                var res = await _requestToHttpHelper.PostAsync<HttpResponseResultModel<List<ManualAddTagItemsResult>>>(requestModel).ConfigureAwait(false);
                // 2026-09-12 修复：绝不返回 null（同 AddGroupTagItems；实测 PLC2 ioturl 指向新 IGW 48020 时
                // /DriverInfo/* 404 空响应 → BackResult=null → 调用方 res.IsSuccess NRE）
                response = res?.BackResult ?? new HttpResponseResultModel<List<ManualAddTagItemsResult>>
                {
                    IsSuccess = false,
                    ErrorMessage = $"网关无返回（{host}/DriverInfo/ManualAddTagItemsAsync 路由不存在或网关非老网关协议；若 ioturl 指向新 IGW，请改用 ExecBatchRegistrationTagV2——按网关类型自动分派）"
                };
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                WriteLogHelper.WriteLogsAsync($"一次性添加组里面所有变量" + ex.Message);
            }
            return response;
        }
    }

}
