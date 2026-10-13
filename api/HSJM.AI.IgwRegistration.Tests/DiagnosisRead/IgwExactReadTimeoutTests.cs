using System.Diagnostics;
using System.Net;
using HSJM.AI.DbService.Service.IOT;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>
/// 2026-09-23：精确读超时必须真正结束 HTTP 请求，且超时后不原样重发。
/// 旧实现只在外面包 WaitAsync(3s)，请求本体仍跑到 HttpClient 默认 100 s，随后又重发一次——
/// IGW 同一台 PLC 串行执行请求，没人等的请求会堵住后面所有读取。
/// </summary>
public class IgwExactReadTimeoutTests
{
    private const string OkBody =
        "{\"httpStatusCode\":200,\"isSuccess\":true,\"backResult\":[{\"key\":\"a\",\"value\":true}]," +
        "\"errorMessage\":null,\"exceptionMessage\":null}";

    private static IotService Iot(StubHandler handler) =>
        new(new RequestToHttpHelper(new StubHttpClientFactory(handler)));

    [Fact]
    public async Task ReadTagsAsList_request_timeout_ends_the_http_call_and_reports_408()
    {
        // 真实网络 handler 会响应 HttpClient 超时发出的取消；StubHandler 不传取消令牌，这里用会响应取消的慢 handler
        var handler = new SlowHandler(TimeSpan.FromSeconds(20));

        var sw = Stopwatch.StartNew();
        var result = await new IotService(new RequestToHttpHelper(new StubHttpClientFactory(handler))).ReadTagsAsList("http://127.0.0.1:48020", new List<string> { "a" },
            requestTimeout: TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.RequestTimeout, result.HttpStatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {sw.Elapsed}，HttpClient 超时没有生效");
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return StubHandler.Json(HttpStatusCode.OK, OkBody);
        }
    }

    [Fact]
    public async Task ReadTagsAsList_without_timeout_keeps_existing_behaviour()
    {
        var handler = new StubHandler(_ => Task.FromResult(StubHandler.Json(HttpStatusCode.OK, OkBody)));

        var result = await Iot(handler).ReadTagsAsList("http://127.0.0.1:48020", new List<string> { "a" });

        Assert.True(result.IsSuccess);
        Assert.True(Assert.Single(result.BackResult).IsGood);
    }

    [Theory]
    [InlineData(30000, 30001)] // 30000 是 RequestToHttpHelper 的"未设置"哨兵值，必须避开
    [InlineData(2999, 2999)]
    [InlineData(0, 1)]
    public void Request_timeout_avoids_the_unset_sentinel(int ms, int expected) =>
        Assert.Equal(expected, IotService.ToRequestTimeoutMs(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public async Task Retry_core_does_not_resend_after_timeout_and_passes_timeout_down()
    {
        var calls = new List<TimeSpan?>();
        var iot = IgwReadChainRoutingTests.Fake.Create<IIotService>((m, a) =>
        {
            calls.Add((TimeSpan?)a[3]);
            return Task.FromResult(new HttpResponseResultModel<List<PLC_ReadTagResult>>
            {
                IsSuccess = false,
                HttpStatusCode = HttpStatusCode.RequestTimeout,
            });
        });

        var result = await PLC_VariableAccessService.ReadIgwTagsWithRetryCoreAsync(
            iot, "http://127.0.0.1:48020", new List<string> { "a" }, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestTimeout, result!.HttpStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(calls));
    }

    [Fact]
    public async Task Retry_core_retries_once_on_fast_failure()
    {
        var calls = 0;
        var iot = IgwReadChainRoutingTests.Fake.Create<IIotService>((m, a) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseResultModel<List<PLC_ReadTagResult>> { IsSuccess = false, ErrorMessage = "连接被拒绝" }
                : new HttpResponseResultModel<List<PLC_ReadTagResult>>
                {
                    IsSuccess = true,
                    BackResult = new List<PLC_ReadTagResult> { new() { TagName = "a", IsGood = true, Value = true } },
                });
        });

        var result = await PLC_VariableAccessService.ReadIgwTagsWithRetryCoreAsync(
            iot, "http://127.0.0.1:48020", new List<string> { "a" }, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.True(result!.IsSuccess);
        Assert.Equal(2, calls);
    }

    // ---------- 通讯故障补读（GRAPH 会话失效时 EXEC_BITS 读不到值） ----------

    private static HttpResponseResultModel<List<PLC_ReadTagResult>> BatchFailed(string message, params string[] aliases) => new()
    {
        IsSuccess = true, // ReadTagsAsList 把批内失败展开成逐行坏值，整体仍算成功
        BackResult = aliases.Select(a => new PLC_ReadTagResult { TagName = a, IsGood = false, ErrorMessage = message }).ToList(),
    };

    private static HttpResponseResultModel<List<PLC_ReadTagResult>> AllGood(params string[] aliases) => new()
    {
        IsSuccess = true,
        BackResult = aliases.Select(a => new PLC_ReadTagResult { TagName = a, IsGood = true, Value = true }).ToList(),
    };

    private const string Bits = "通用.S7Plus_1.OP010.DB1.RT_DATA.EXEC_BITS";

    [Theory]
    [InlineData("点位质量异常:" + Bits + " BadCommFailure(0x80050000)")]
    [InlineData("点位质量异常:" + Bits + " BadNotConnected(0x808A0000)")]
    [InlineData("点位质量异常:" + Bits + " BadTimeout(0x800A0000)")]
    public async Task Comm_failure_rows_are_read_again_once_for_diagnosis(string message)
    {
        var calls = 0;
        var iot = IgwReadChainRoutingTests.Fake.Create<IIotService>((m, a) =>
            Task.FromResult(++calls == 1 ? BatchFailed(message, Bits) : AllGood(Bits)));

        var result = await PLC_VariableAccessService.ReadIgwTagsWithRetryCoreAsync(
            iot, "http://127.0.0.1:48020", new List<string> { Bits }, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(Assert.Single(result!.BackResult).IsGood);
    }

    [Fact]
    public async Task Missing_points_are_not_read_again()
    {
        var calls = 0;
        var iot = IgwReadChainRoutingTests.Fake.Create<IIotService>((m, a) =>
        {
            calls++;
            return Task.FromResult(BatchFailed("点位不存在:" + Bits, Bits));
        });

        await PLC_VariableAccessService.ReadIgwTagsWithRetryCoreAsync(
            iot, "http://127.0.0.1:48020", new List<string> { Bits }, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Monitor_polling_does_not_read_again_on_comm_failure()
    {
        var calls = 0;
        var iot = IgwReadChainRoutingTests.Fake.Create<IIotService>((m, a) =>
        {
            calls++;
            return Task.FromResult(BatchFailed("点位质量异常:" + Bits + " BadCommFailure(0x80050000)", Bits));
        });

        // 监控页 100 ms 后本来就会再读，不在这一轮里补读拖慢它
        await PLC_VariableAccessService.ReadIgwTagsWithRetryCoreAsync(
            iot, "http://127.0.0.1:48020", new List<string> { Bits }, TimeSpan.FromSeconds(3), CancellationToken.None,
            keepPolling: true);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Comm_failure_is_read_again_only_once()
    {
        var calls = 0;
        var iot = IgwReadChainRoutingTests.Fake.Create<IIotService>((m, a) =>
        {
            calls++;
            return Task.FromResult(BatchFailed("点位质量异常:" + Bits + " BadCommFailure(0x80050000)", Bits));
        });

        var result = await PLC_VariableAccessService.ReadIgwTagsWithRetryCoreAsync(
            iot, "http://127.0.0.1:48020", new List<string> { Bits }, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.False(Assert.Single(result!.BackResult).IsGood);
    }
}
