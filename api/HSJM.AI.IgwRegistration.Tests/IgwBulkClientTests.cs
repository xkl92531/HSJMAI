using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// IgwBulkClient 单测：批量支持探测（含缓存）、批量写入重试、旧设备清理。
/// 用自定义 HttpMessageHandler 桩记录请求并返回预设响应，不发起真实网络调用。
/// </summary>
public class IgwBulkClientTests
{
    /// <summary>测试用最短退避延迟（真实值 2s/4s/8s 太慢，注入毫秒级避免拖慢用例）。</summary>
    private static readonly IReadOnlyList<TimeSpan> FastRetryDelays = new[]
    {
        TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5),
    };

    private static IgwBulkClient CreateClient(StubHttpMessageHandler handler) =>
        new(NullLogger<IgwBulkClient>.Instance, handler, FastRetryDelays);

    /// <summary>每个探测测试用独立域名，避免静态缓存跨用例互相污染（比只调用 ResetCacheForTests 更稳妥）。</summary>
    private static string UniqueBaseUrl() => $"http://igw-probe-{Guid.NewGuid():N}.local:48020";

    // ---------------------------------------------------------------- SupportsBulkAsync
    // 探测改为 POST {base}api/v1/project/bulk/tags，body="{}"，不带 If-Match（详见 IgwBulkClient 的
    // SupportsBulkAsync 文档注释：GET 会被 IGW 部署常见的 /api/{**path} 静态资源兜底路由吞掉，
    // 即便新版 IGW 真的有这条路由也测不出来）。

    [Fact]
    public async Task SupportsBulkAsync_400InvalidItems_ReturnsTrueAndCaches_SendsEmptyBodyNoIfMatch()
    {
        var baseUrl = UniqueBaseUrl();
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_items","details":[{"index":-1,"reason":"通道名无效"}]}"""));
        var client = CreateClient(handler);

        var first = await client.SupportsBulkAsync(baseUrl, CancellationToken.None);
        var second = await client.SupportsBulkAsync(baseUrl, CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Single(handler.Requests); // 第二次命中缓存，未发起新请求
        var probeRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, probeRequest.Method);
        Assert.Equal("{}", probeRequest.Body);
        Assert.Null(probeRequest.IfMatch);
    }

    [Fact]
    public async Task SupportsBulkAsync_400InvalidRequest_ReturnsTrue()
    {
        var baseUrl = UniqueBaseUrl();
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_request","detail":"请求体为空"}"""));
        var client = CreateClient(handler);

        Assert.True(await client.SupportsBulkAsync(baseUrl, CancellationToken.None));
    }

    [Fact]
    public async Task SupportsBulkAsync_404_ReturnsFalseAndCaches()
    {
        var baseUrl = UniqueBaseUrl();
        var handler = new StubHttpMessageHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var first = await client.SupportsBulkAsync(baseUrl, CancellationToken.None);
        var second = await client.SupportsBulkAsync(baseUrl, CancellationToken.None);

        Assert.False(first);
        Assert.False(second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SupportsBulkAsync_405_ReturnsFalseAndCaches()
    {
        // 旧版 IGW：同路径只有 GET 兜底路由（或反代只放行 GET）→ POST 405，同样判定为不支持批量
        var baseUrl = UniqueBaseUrl();
        var handler = new StubHttpMessageHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        var client = CreateClient(handler);

        var first = await client.SupportsBulkAsync(baseUrl, CancellationToken.None);
        var second = await client.SupportsBulkAsync(baseUrl, CancellationToken.None);

        Assert.False(first);
        Assert.False(second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SupportsBulkAsync_UnexpectedStatus_ThrowsAndDoesNotCache()
    {
        var baseUrl = UniqueBaseUrl();
        // 400 但 error 既不是 invalid_items 也不是 invalid_request：视为无法判定，不是"支持批量"的证据
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.BadRequest, """{"error":"name_conflict","detail":"x"}"""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SupportsBulkAsync(baseUrl, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SupportsBulkAsync(baseUrl, CancellationToken.None));

        // 失败不缓存：两次调用都真实发起了请求
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SupportsBulkAsync_CacheKeyIsCaseInsensitiveForSchemeAndHost()
    {
        var host = $"IGW-Probe-{Guid.NewGuid():N}.LOCAL";
        var handler = new StubHttpMessageHandler((req, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var lower = await client.SupportsBulkAsync($"http://{host.ToLowerInvariant()}:48020", CancellationToken.None);
        var upper = await client.SupportsBulkAsync($"HTTP://{host.ToUpperInvariant()}:48020", CancellationToken.None);

        Assert.False(lower);
        Assert.False(upper);
        Assert.Single(handler.Requests); // scheme/host 小写化后命中同一条缓存
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SupportsBulkAsync_NullOrEmptyIotUrl_ThrowsArgumentException(string iotUrl)
    {
        var handler = new StubHttpMessageHandler((req, _) => throw new InvalidOperationException("不应发起任何请求"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SupportsBulkAsync(iotUrl, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------- BulkUpsertAsync

    private static IgwBulkRequest SampleRequest() => new()
    {
        Channel = new IgwBulkChannel { Name = "通用" },
        Device = new IgwBulkDevice { Name = "S7_10.0.0.5", Endpoint = "10.0.0.5:102" },
        Items = new List<IgwBulkItem>
        {
            new() { GroupPath = new List<string> { "OP010" }, Name = "Tag1", Address = "%I0.0", DataType = "bool" },
        },
    };

    [Fact]
    public async Task BulkUpsertAsync_NullOrEmptyIotUrl_ThrowsArgumentException()
    {
        var handler = new StubHttpMessageHandler((req, _) => throw new InvalidOperationException("不应发起任何请求"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.BulkUpsertAsync(" ", SampleRequest(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BulkUpsertAsync_200_DeserializesSuccessBody()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("bulk/tags", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {"applied":true,"revision":"rev-7","channelId":"ch-1","deviceId":"dev-1",
                     "created":3,"updated":1,"deleted":0,"deviceRestarted":true,"timing":"parse=1ms;apply=2ms"}
                    """);
            }
            throw new InvalidOperationException($"未预期的请求 {req.Method} {req.RequestUri}");
        });
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-ok.local:48020", SampleRequest(), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("rev-7", result.Revision);
        Assert.Equal(3, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Deleted);
        Assert.True(result.DeviceRestarted);
        Assert.Equal("parse=1ms;apply=2ms", result.Timing);
        Assert.Equal(200, result.HttpStatus);
        Assert.Null(result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BulkUpsertAsync_400InvalidItems_DoesNotRetryAndCarriesDetail()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.BadRequest, """
                {"error":"invalid_items","details":[{"index":0,"reason":"地址不能为空"}]}
                """));
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-badreq.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("invalid_items", result.Error);
        Assert.Equal(400, result.HttpStatus);
        Assert.Contains("地址不能为空", result.Detail);
        Assert.Single(handler.Requests); // 400 不可重试，只发一次请求
    }

    [Fact]
    public async Task BulkUpsertAsync_413TooManyItems_DoesNotRetry()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.RequestEntityTooLarge, """{"error":"too_many_items","detail":"单次请求最多 200000 个点位"}"""));
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-413.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(413, result.HttpStatus);
        Assert.Equal("too_many_items", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BulkUpsertAsync_409ExhaustsRetries_ReturnsFinalFailure()
    {
        // 客户端从不带 If-Match，IGW 正常不会返回 409；这里仅验证"万一遇到 409 仍会按状态码重试"的防御行为
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.Conflict, """{"error":"revision_conflict","revision":"rev-99"}"""));
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-409.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(409, result.HttpStatus);
        Assert.Equal("revision_conflict", result.Error);
        Assert.Equal(4, handler.Requests.Count); // 首次 + 3 次重试，全部 409，耗尽后返回失败（不抛异常）
    }

    [Fact]
    public async Task BulkUpsertAsync_503RealBody_RetriesRegardlessOfMissingRetryableField()
    {
        // IGW 真实的 503 persistence_failed 响应体没有 retryable 字段——503 本身状态码语义就是可重试
        var attempt = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                return JsonResponse(HttpStatusCode.ServiceUnavailable, """{"error":"persistence_failed","detail":"sqlite busy"}""");
            }
            return JsonResponse(HttpStatusCode.OK, """{"applied":true,"revision":"rev-2","created":1,"updated":0,"deleted":0,"deviceRestarted":false,"timing":"x"}""");
        });
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-retry503.local:48020", SampleRequest(), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(2, handler.Requests.Count); // 第一次 503 重试后第二次成功
    }

    [Fact]
    public async Task BulkUpsertAsync_500WithoutRetryableFlag_DoesNotRetry()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.InternalServerError, """{"error":"persistence_failed","detail":"corrupt doc"}"""));
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-500norty.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(500, result.HttpStatus);
        Assert.Single(handler.Requests); // 500 且没有 retryable:true → 不重试
    }

    [Fact]
    public async Task BulkUpsertAsync_500RetryableTrue_RetriesThenSucceeds()
    {
        var attempt = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                return JsonResponse(HttpStatusCode.InternalServerError, """{"error":"persistence_failed","detail":"disk io error","retryable":true}""");
            }
            return JsonResponse(HttpStatusCode.OK, """{"applied":true,"revision":"rev-3","created":1,"updated":0,"deleted":0,"deviceRestarted":false,"timing":"x"}""");
        });
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-500retry.local:48020", SampleRequest(), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task BulkUpsertAsync_500RuntimeApplyFailed_NeverRetries_RevisionStillSurfaced()
    {
        // runtime_apply_failed 语义：已写库（revision 已推进），只是运行态重建失败——即使误带 retryable:true 也不重试，
        // 且调用方能从 Revision 看出这次写入其实生效了，不能当作"完全没写"处理
        var handler = new StubHttpMessageHandler((req, _) =>
            JsonResponse(HttpStatusCode.InternalServerError,
                """{"error":"runtime_apply_failed","detail":"设备重建失败","revision":"rev-5","retryable":true}"""));
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-runtimefail.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(500, result.HttpStatus);
        Assert.Equal("runtime_apply_failed", result.Error);
        Assert.Equal("rev-5", result.Revision);
        Assert.Single(handler.Requests); // 即使 body 带 retryable:true，runtime_apply_failed 也永不重试
    }

    [Fact]
    public async Task BulkUpsertAsync_ConnectionLevelFailure_RetriesThenThrows()
    {
        // 连接级失败（请求尚未送达 IGW）→ 可安全重试
        var handler = new StubHttpMessageHandler((req, _) => throw new HttpRequestException("connection reset"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.BulkUpsertAsync("http://igw-transport.local:48020", SampleRequest(), CancellationToken.None));

        Assert.Equal(4, handler.Requests.Count); // 首次 + 3 次重试后仍失败才抛出
    }

    [Fact]
    public async Task BulkUpsertAsync_TimeoutAfterRequestSent_DoesNotRetry()
    {
        // 模拟 HttpClient 自身超时：请求已发出（记录到 Requests），响应未在超时内送达。
        // 与调用方主动取消不同（这里传入的 ct 从未被取消），必须视为"不确定是否已生效"，绝不重试。
        var handler = new StubHttpMessageHandler((req, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException()));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.BulkUpsertAsync("http://igw-sendtimeout.local:48020", SampleRequest(), CancellationToken.None));

        Assert.Single(handler.Requests); // 只发了一次，不重试
    }

    [Fact]
    public async Task BulkUpsertAsync_NonJsonHtmlBody_ReturnsRawDetailWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
            });
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-html.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(502, result.HttpStatus);
        Assert.Contains("502 Bad Gateway", result.Detail);
        Assert.Single(handler.Requests); // 502 不在任何重试规则内
    }

    [Fact]
    public async Task BulkUpsertAsync_EmptyBody_ReturnsBareResponseWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler((req, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var result = await client.BulkUpsertAsync("http://igw-empty.local:48020", SampleRequest(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(500, result.HttpStatus);
        Assert.Null(result.Detail);
        Assert.Null(result.Error);
        Assert.Single(handler.Requests); // 空体没有 retryable:true → 500 不重试
    }

    // ---------------------------------------------------------------- RemoveLegacyDevicesAsync

    [Fact]
    public async Task RemoveLegacyDevicesAsync_DeletesOnlyMatchingDevices_AcrossEndpointFormats()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        // 通用 通道：一个裸 IP endpoint、一个 IP:端口 endpoint（均匹配），一个无 dcsLogicalDevice 设备（不匹配）
        graph.AddChannel("ch-generic", "通用",
            ("dev-bare", "S1500_" + ip, "S1500_" + ip, ip),
            ("dev-portish", "S1500_" + ip + "-b", "S1500_" + ip, ip + ":102"),
            ("dev-nological", "OtherDevice", null, ip));
        // 通用_S7Plus 通道：opc.tcp:// 形式 endpoint（匹配）、另一 IP 的同名逻辑设备（不匹配）
        graph.AddChannel("ch-plus", "通用_S7Plus",
            ("dev-opcua", "S7Plus_" + ip, "S7Plus_" + ip, "opc.tcp://" + ip + ":4840"),
            ("dev-otherip", "S7Plus_10.0.0.9", "S7Plus_10.0.0.9", "10.0.0.9:102"));
        // 非旧网关通道：即使设备名/逻辑名看起来匹配也不应被查询到（代码只应枚举 通用/通用_S7Plus 两个通道）
        graph.AddChannel("ch-other", "OtherChannel",
            ("dev-should-not-touch", "S1500_" + ip, "S1500_" + ip, ip));

        var handler = graph.BuildHandler();
        var client = CreateClient(handler);

        var removed = await client.RemoveLegacyDevicesAsync("http://igw-cleanup.local:48020", ip, CancellationToken.None);

        Assert.Equal(3, removed.Count);
        Assert.Contains(removed, d => d.StartsWith("S1500_" + ip + "（", StringComparison.Ordinal));
        Assert.Contains(removed, d => d.StartsWith("S1500_" + ip + "-b（", StringComparison.Ordinal));
        Assert.Contains(removed, d => d.StartsWith("S7Plus_" + ip + "（", StringComparison.Ordinal));
        Assert.Equal(new HashSet<string> { "dev-bare", "dev-portish", "dev-opcua" }, graph.DeletedDeviceIds);

        // OtherChannel 的设备清单从未被查询（代码只枚举 通用/通用_S7Plus 两个通道）
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("tree/channel/ch-other/children"));

        // 每次 deleteEntities 都带 If-Match
        var deleteRequests = handler.Requests.Where(r => r.Url.EndsWith("project/mutations", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, deleteRequests.Count);
        Assert.All(deleteRequests, r => Assert.False(string.IsNullOrWhiteSpace(r.IfMatch)));
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_GraphOnly_RemovesOnlyS7PlusLogicalDevices()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        graph.AddChannel("ch-generic", "通用",
            ("dev-scalar", "S1500_" + ip, "S1500_" + ip, ip));
        graph.AddChannel("ch-plus", "通用_S7Plus",
            ("dev-graph", "S7Plus_" + ip, "S7Plus_" + ip, ip + ":102"),
            ("dev-scalar-in-plus", "S1500_" + ip + "-p", "S1500_" + ip, ip));
        var client = CreateClient(graph.BuildHandler());

        var removed = await client.RemoveLegacyDevicesAsync("http://igw-cleanup-graph.local:48020", ip,
            IgwLegacyDeviceScope.GraphOnly, CancellationToken.None);

        Assert.Equal("S7Plus_" + ip, Assert.Single(removed).LogicalName);
        Assert.Equal(new HashSet<string> { "dev-graph" }, graph.DeletedDeviceIds);
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_NoMatches_MakesNoMutationCalls()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        graph.AddChannel("ch-generic", "通用", ("dev-a", "SomeDevice", null, ip));
        graph.AddChannel("ch-plus", "通用_S7Plus", ("dev-b", "S7Plus_10.0.0.9", "S7Plus_10.0.0.9", "10.0.0.9"));

        var handler = graph.BuildHandler();
        var client = CreateClient(handler);

        var removed = await client.RemoveLegacyDevicesAsync("http://igw-cleanup2.local:48020", ip, CancellationToken.None);

        Assert.Empty(removed);
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("project/mutations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_DeleteFailure_ThrowsListingFailures()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        graph.AddChannel("ch-generic", "通用",
            ("dev-a", "S1500_" + ip, "S1500_" + ip, ip),
            ("dev-b", "S1500_" + ip + "-b", "S1500_" + ip, ip + ":102"));
        graph.FailDeviceIds.Add("dev-b"); // 模拟其中一个设备删除失败

        var handler = graph.BuildHandler();
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RemoveLegacyDevicesAsync("http://igw-cleanup-fail.local:48020", ip, CancellationToken.None));

        Assert.Contains("S1500_" + ip + "-b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_DeleteRejectedButDeviceAlreadyGone_TreatedAsSuccess()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        graph.AddChannel("ch-generic", "通用", ("dev-a", "S1500_" + ip, "S1500_" + ip, ip));
        // 并发的另一注册任务抢先删除：本次 deleteEntities 返回 400 mutation_rejected，复查时设备已不在
        graph.VanishOnDeleteIds.Add("dev-a");

        var handler = graph.BuildHandler();
        var client = CreateClient(handler);

        var removed = await client.RemoveLegacyDevicesAsync("http://igw-cleanup-race.local:48020", ip, CancellationToken.None);

        Assert.Single(removed);
        Assert.Contains("S1500_" + ip, removed[0], StringComparison.Ordinal);
        // 失败后重读了通道设备清单（首次遍历 1 次 + 复查 1 次）
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("tree/channel/ch-generic/children", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_DeleteRejectedAndDeviceStillPresent_Throws()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        graph.AddChannel("ch-generic", "通用", ("dev-a", "S1500_" + ip, "S1500_" + ip, ip));
        graph.RejectDeviceIds.Add("dev-a");

        var handler = graph.BuildHandler();
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RemoveLegacyDevicesAsync("http://igw-cleanup-race2.local:48020", ip, CancellationToken.None));

        Assert.Contains("mutation_rejected", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("tree/channel/ch-generic/children", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_NullOrEmptyIotUrl_ThrowsArgumentException()
    {
        var handler = new StubHttpMessageHandler((req, _) => throw new InvalidOperationException("不应发起任何请求"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveLegacyDevicesAsync(string.Empty, "10.0.0.5", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RemoveLegacyDevicesAsync_PerCallTimeout_DoesNotBudgetWholeWalk()
    {
        const string ip = "10.0.0.5";
        var graph = new FakeProjectGraph(ip);
        graph.AddChannel("ch-generic", "通用", ("dev-a", "S1500_" + ip, "S1500_" + ip, ip));
        // 让"通用"通道的子节点查询模拟长时间不返回：应被单次 30ms 超时终止，而不是拖累整趟遍历的总预算
        graph.SlowChannelChildrenIds.Add("ch-generic");

        var handler = graph.BuildHandler();
        var client = new IgwBulkClient(NullLogger<IgwBulkClient>.Instance, handler, FastRetryDelays,
            treeRequestTimeout: TimeSpan.FromMilliseconds(30), mutationTimeout: TimeSpan.FromSeconds(60));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.RemoveLegacyDevicesAsync("http://igw-cleanup-timeout.local:48020", ip, CancellationToken.None));
        stopwatch.Stop();

        // 用极短的单次超时验证"per-call"而非"整趟共享一个大预算"：若是共享 30s 预算，这里不可能在几百毫秒内失败
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"应在单次调用超时（30ms）附近失败，实际耗时 {stopwatch.ElapsedMilliseconds}ms");
    }

    // ---------------------------------------------------------------- helpers

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>记录到的一次 HTTP 请求（供断言 If-Match / URL / body 用）。</summary>
    private sealed class RecordedRequest
    {
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public string Url { get; init; } = string.Empty;
        public string Body { get; init; }
        public string IfMatch { get; init; }
    }

    /// <summary>自定义 HttpMessageHandler 桩：记录每次请求，按注入的路由函数（同步或异步）返回响应。</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _router;
        public List<RecordedRequest> Requests { get; } = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> router)
            : this((req, body, _) => Task.FromResult(router(req, body)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> router)
        {
            _router = router;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
            request.Headers.TryGetValues("If-Match", out var ifMatchValues);
            Requests.Add(new RecordedRequest
            {
                Method = request.Method,
                Url = request.RequestUri!.ToString(),
                Body = body,
                IfMatch = ifMatchValues?.FirstOrDefault(),
            });
            var response = await _router(request, body, cancellationToken).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
    }

    /// <summary>
    /// 最小可用的假 IGW 工程树：root → 通道 → 设备（+ 设备实体 settings/endpoint），
    /// 支撑 IgwProjectApi 的 summary/tree/children/entity/mutations 四个端点。
    /// </summary>
    private sealed class FakeProjectGraph
    {
        private readonly string _ip;
        private readonly List<(string Id, string Name)> _channels = new();
        private readonly Dictionary<string, List<(string Id, string Name)>> _devicesByChannel = new();
        private readonly Dictionary<string, (string Logical, string Endpoint)> _deviceEntities = new();
        public HashSet<string> DeletedDeviceIds { get; } = new();

        /// <summary>命中这些 deviceId 的 deleteEntities 请求会返回 500，模拟删除失败。</summary>
        public HashSet<string> FailDeviceIds { get; } = new();

        /// <summary>命中这些 deviceId 的 deleteEntities 返回 400 mutation_rejected，且设备从通道中消失（模拟并发方已删除）。</summary>
        public HashSet<string> VanishOnDeleteIds { get; } = new();

        /// <summary>命中这些 deviceId 的 deleteEntities 返回 400 mutation_rejected，设备仍在。</summary>
        public HashSet<string> RejectDeviceIds { get; } = new();

        /// <summary>命中这些 channelId 的 tree/channel/.../children 查询会先等待，直到调用方超时把它取消。</summary>
        public HashSet<string> SlowChannelChildrenIds { get; } = new();

        public FakeProjectGraph(string ip) => _ip = ip;

        /// <summary>添加一个通道及其设备：每项 (deviceId, deviceName, dcsLogicalDevice(可为 null), endpoint)。</summary>
        public void AddChannel(string channelId, string channelName, params (string Id, string Name, string Logical, string Endpoint)[] devices)
        {
            _channels.Add((channelId, channelName));
            _devicesByChannel[channelId] = devices.Select(d => (d.Id, d.Name)).ToList();
            foreach (var d in devices)
            {
                _deviceEntities[d.Id] = (d.Logical, d.Endpoint);
            }
        }

        public StubHttpMessageHandler BuildHandler() => new(RouteAsync);

        private async Task<HttpResponseMessage> RouteAsync(HttpRequestMessage req, string body, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;

            if (path.EndsWith("project/summary", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                var response = JsonResponse(HttpStatusCode.OK, """{"root":{"id":"root-1","kind":"project","name":"root"}}""");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rev-1\"");
                return response;
            }

            if (path.Contains("project/tree/project/root-1/children", StringComparison.Ordinal))
            {
                var items = _channels.Select(c => $$"""{"id":"{{c.Id}}","kind":"channel","name":"{{c.Name}}"}""");
                return JsonResponse(HttpStatusCode.OK, "[" + string.Join(",", items) + "]");
            }

            var channelChildrenPrefix = "project/tree/channel/";
            if (path.Contains(channelChildrenPrefix, StringComparison.Ordinal) && path.EndsWith("/children", StringComparison.Ordinal))
            {
                var channelId = path.Split('/')[^2];
                if (SlowChannelChildrenIds.Contains(channelId))
                {
                    // 故意挂起：会被调用方的单次超时（linked CancellationTokenSource）提前取消，验证 per-call 超时
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
                var devices = _devicesByChannel.TryGetValue(channelId, out var list)
                    ? list
                    : new List<(string Id, string Name)>();
                var items = devices.Select(d => $$"""{"id":"{{d.Id}}","kind":"device","name":"{{d.Name}}"}""");
                return JsonResponse(HttpStatusCode.OK, "[" + string.Join(",", items) + "]");
            }

            var entityDevicePrefix = "project/entity/device/";
            if (path.Contains(entityDevicePrefix, StringComparison.Ordinal))
            {
                var deviceId = path.Substring(path.IndexOf(entityDevicePrefix, StringComparison.Ordinal) + entityDevicePrefix.Length);
                if (!_deviceEntities.TryGetValue(deviceId, out var entity))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                var settingsJson = entity.Logical == null
                    ? "{}"
                    : $$"""{"dcsLogicalDevice":"{{entity.Logical}}"}""";
                return JsonResponse(HttpStatusCode.OK,
                    $$"""{"id":"{{deviceId}}","endpoint":"{{entity.Endpoint}}","settings":{{settingsJson}}}""");
            }

            if (path.EndsWith("project/mutations", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
            {
                var ids = ExtractEntityIds(body).ToList();
                if (ids.Any(id => VanishOnDeleteIds.Contains(id) || RejectDeviceIds.Contains(id)))
                {
                    foreach (var list in _devicesByChannel.Values)
                    {
                        list.RemoveAll(d => VanishOnDeleteIds.Contains(d.Id) && ids.Contains(d.Id));
                    }
                    return JsonResponse(HttpStatusCode.BadRequest, """{"error":"mutation_rejected","detail":"实体不存在"}""");
                }
                if (ids.Any(id => FailDeviceIds.Contains(id)))
                {
                    return JsonResponse(HttpStatusCode.InternalServerError, """{"error":"persistence_failed","detail":"模拟删除失败"}""");
                }
                if (body != null && body.Contains("\"deleteEntities\"", StringComparison.Ordinal))
                {
                    foreach (var id in ids)
                    {
                        DeletedDeviceIds.Add(id);
                    }
                }
                return JsonResponse(HttpStatusCode.OK, "{}");
            }

            throw new InvalidOperationException($"FakeProjectGraph 未预期的请求 {req.Method} {req.RequestUri}");
        }

        /// <summary>从 mutations 请求体提取 entityIds 数组。</summary>
        private static IEnumerable<string> ExtractEntityIds(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                yield break;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("entityIds", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var s = el.GetString();
                    if (s != null) yield return s;
                }
            }
        }
    }
}
