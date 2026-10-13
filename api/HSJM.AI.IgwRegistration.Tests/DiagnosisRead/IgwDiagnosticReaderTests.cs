using System.Net;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using Xunit;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 2：IGW 诊断读取器。每个用例用独立 host，避免"新接口不支持"静态缓存互相影响。</summary>
public class IgwDiagnosticReaderTests
{
    private static int _port = 48300;

    private static string NewHost() => "http://127.0.0.1:" + Interlocked.Increment(ref _port);

    private sealed class FakeIgw
    {
        public int QualityCalls;
        public int ListCalls;
        public TimeSpan? LastQualityTimeout;
        public TimeSpan? LastListTimeout;
        public readonly List<List<string>> ListSegments = new();
        public Func<List<string>, Task<IgwTransportResult<List<DcsTagQualityItem>>>> Quality =
            batch => Envelope(batch.Select(GoodRow).ToList());
        public Func<List<string>, Task<IgwTransportResult<List<DcsTagReadItem>>>> List =
            segment => Envelope(ListRows(segment));

        public IIotService Create() => Fake.Create<IIotService>((m, a) =>
        {
            var aliases = ((List<string>)a[1]).ToList();
            var timeout = a.Length > 2 ? (TimeSpan?)a[2] : null;
            switch (m.Name)
            {
                case nameof(IIotService.ReadTagsWithQualityAsync):
                    QualityCalls++;
                    LastQualityTimeout = timeout;
                    return Quality(aliases);
                case nameof(IIotService.ReadTagsAsListRawAsync):
                    ListCalls++;
                    LastListTimeout = timeout;
                    ListSegments.Add(aliases);
                    return List(aliases);
                default:
                    throw new NotSupportedException("IIotService." + m.Name);
            }
        });
    }

    private static Task<IgwTransportResult<List<T>>> Http<T>(HttpStatusCode code, string body = "") =>
        Task.FromResult(new IgwTransportResult<List<T>> { HttpStatusCode = code, IsHttpSuccess = false, RawBody = body });

    private static Task<IgwTransportResult<List<T>>> Envelope<T>(List<T> rows, bool success = true, string error = null) =>
        Task.FromResult(new IgwTransportResult<List<T>>
        {
            HttpStatusCode = HttpStatusCode.OK,
            IsHttpSuccess = true,
            Envelope = new HttpResponseResultModel<List<T>> { IsSuccess = success, BackResult = rows, ErrorMessage = error },
        });

    private static Task<IgwTransportResult<List<DcsTagReadItem>>> ListFail(string message) =>
        Envelope(new List<DcsTagReadItem>(), success: false, error: message);

    private static List<DcsTagReadItem> ListRows(IEnumerable<string> aliases) =>
        aliases.Select(a => new DcsTagReadItem { Key = a, Value = true }).ToList();

    private static DcsTagQualityItem GoodRow(string alias) =>
        new() { Key = alias, IsGood = true, Value = true, Status = "Good" };

    private static DcsTagQualityItem BadRow(string alias, string reason) =>
        new() { Key = alias, IsGood = false, StatusCode = 0x80000000u, Status = "Bad", Reason = reason };

    private static IgwDiagnosticReader Reader(FakeIgw igw, List<TimeSpan> delays = null, TimeSpan? timeout = null, bool quality = true) =>
        new(igw.Create(), (d, _) => { delays?.Add(d); return Task.CompletedTask; }, timeout, quality);

    private static List<string> Aliases(int count, string prefix = "通用.S7_10.0.0.1.IO.T.P") =>
        Enumerable.Range(0, count).Select(i => prefix + i).ToList();

    [Fact]
    public async Task Quality_rows_map_every_reason_and_bad_rows_carry_no_value()
    {
        var reasons = new[] { null, "SymbolNotFound", "NotRegistered", "CommFailure", "Timeout", "WaitingForData", "AccessDenied", "DeviceError", "SomethingNew" };
        var expected = new[]
        {
            PLC_SignalReadStatus.Good, PLC_SignalReadStatus.SymbolNotFound, PLC_SignalReadStatus.NotRegistered,
            PLC_SignalReadStatus.CommFailure, PLC_SignalReadStatus.Timeout, PLC_SignalReadStatus.WaitingForData,
            PLC_SignalReadStatus.AccessDenied, PLC_SignalReadStatus.DeviceError, PLC_SignalReadStatus.DeviceError,
        };
        var aliases = Aliases(reasons.Length);
        var igw = new FakeIgw
        {
            Quality = batch => Envelope(batch.Select((a, i) => reasons[i] == null ? GoodRow(a) : BadRow(a, reasons[i])).ToList()),
        };

        var result = await Reader(igw).ReadAsync(NewHost(), aliases, CancellationToken.None);

        for (var i = 0; i < aliases.Count; i++)
        {
            Assert.Equal(expected[i], result[aliases[i]].Status);
        }
        Assert.Equal(true, result[aliases[0]].Value);
        Assert.Null(result[aliases[1]].Value);
        Assert.Contains("0x80000000", result[aliases[1]].Detail);
        Assert.Equal(1, igw.QualityCalls);
        Assert.Equal(0, igw.ListCalls);
    }

    [Fact]
    public async Task Quality_404_falls_back_to_list_and_is_cached_per_host()
    {
        var host = NewHost();
        var igw = new FakeIgw { Quality = _ => Http<DcsTagQualityItem>(HttpStatusCode.NotFound) };
        var reader = Reader(igw);

        var first = await reader.ReadAsync(host, Aliases(2), CancellationToken.None);
        var second = await reader.ReadAsync(host, Aliases(2), CancellationToken.None);
        await reader.ReadAsync(NewHost(), Aliases(1), CancellationToken.None);

        Assert.All(first.Values.Concat(second.Values), r => Assert.Equal(PLC_SignalReadStatus.Good, r.Status));
        Assert.Equal(2, igw.QualityCalls); // host 1 探测 1 次（第二次命中缓存）+ 新 host 1 次
        Assert.Equal(3, igw.ListCalls);
    }

    [Fact]
    public async Task Legacy_quality_message_locates_bad_point_and_retries_the_rest()
    {
        var aliases = Aliases(3);
        var bad = aliases[1];
        var igw = new FakeIgw
        {
            List = segment => segment.Contains(bad)
                ? ListFail("点位质量异常:" + bad + " BadNodeIdUnknown(0x80340000)")
                : Envelope(ListRows(segment)),
        };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(PLC_SignalReadStatus.Good, result[aliases[0]].Status);
        Assert.Equal(PLC_SignalReadStatus.SymbolNotFound, result[bad].Status);
        Assert.Null(result[bad].Value);
        Assert.Equal(PLC_SignalReadStatus.Good, result[aliases[2]].Status);
        Assert.Equal(2, igw.ListCalls);
        Assert.Equal(new[] { aliases[0], aliases[2] }, igw.ListSegments[1]);
    }

    [Fact]
    public async Task Legacy_missing_message_marks_not_registered()
    {
        var aliases = Aliases(2);
        var igw = new FakeIgw
        {
            List = segment => segment.Contains(aliases[0]) ? ListFail("点位不存在:" + aliases[0]) : Envelope(ListRows(segment)),
        };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(PLC_SignalReadStatus.NotRegistered, result[aliases[0]].Status);
        Assert.Equal(PLC_SignalReadStatus.Good, result[aliases[1]].Status);
    }

    [Fact]
    public async Task Legacy_unlocatable_failure_bisects_down_to_the_single_bad_point()
    {
        var aliases = Aliases(4);
        var bad = aliases[3];
        var igw = new FakeIgw { List = segment => segment.Contains(bad) ? ListFail("内部错误") : Envelope(ListRows(segment)) };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(PLC_SignalReadStatus.DeviceError, result[bad].Status);
        Assert.Equal("内部错误", result[bad].Detail);
        Assert.All(aliases.Take(3), a => Assert.Equal(PLC_SignalReadStatus.Good, result[a].Status));
        Assert.Equal(5, igw.ListCalls); // [0..3] → [0,1] ✓ → [2,3] ✗ → [2] ✓ → [3] ✗
    }

    [Fact]
    public async Task Legacy_bisection_is_capped_at_64_requests_per_batch()
    {
        var aliases = Aliases(200);
        var igw = new FakeIgw { List = _ => ListFail("内部错误") };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(IgwDiagnosticReader.MaxRequestsPerBatch, igw.ListCalls);
        Assert.Equal(200, result.Count);
        Assert.All(result.Values, r => Assert.NotEqual(PLC_SignalReadStatus.Good, r.Status));
        Assert.Contains(result.Values, r => r.Detail == "定位坏点次数超限");
    }

    [Fact]
    public async Task Quality_503_waits_one_second_and_retries_once()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var igw = new FakeIgw
        {
            Quality = batch => ++calls == 1 ? Http<DcsTagQualityItem>(HttpStatusCode.ServiceUnavailable) : Envelope(batch.Select(GoodRow).ToList()),
        };
        var aliases = Aliases(2);

        var result = await Reader(igw, delays).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.Equal(2, igw.QualityCalls);
        Assert.All(aliases, a => Assert.Equal(PLC_SignalReadStatus.Good, result[a].Status));
    }

    [Fact]
    public async Task Quality_503_twice_marks_the_batch_comm_failure()
    {
        var igw = new FakeIgw { Quality = _ => Http<DcsTagQualityItem>(HttpStatusCode.ServiceUnavailable) };
        var aliases = Aliases(2);

        var result = await Reader(igw, new List<TimeSpan>()).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(2, igw.QualityCalls);
        Assert.All(aliases, a =>
        {
            Assert.Equal(PLC_SignalReadStatus.CommFailure, result[a].Status);
            Assert.Contains("热重载", result[a].Detail);
        });
    }

    [Fact]
    public async Task Network_failure_only_affects_its_own_batch()
    {
        var aliases = Aliases(1500);
        var igw = new FakeIgw
        {
            Quality = batch => batch.Count == 1000
                ? Task.FromException<IgwTransportResult<List<DcsTagQualityItem>>>(new HttpRequestException("down"))
                : Envelope(batch.Select(GoodRow).ToList()),
        };

        var result = await Reader(igw).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.All(aliases.Take(1000), a =>
        {
            Assert.Equal(PLC_SignalReadStatus.CommFailure, result[a].Status);
            Assert.Contains("down", result[a].Detail);
        });
        Assert.All(aliases.Skip(1000), a => Assert.Equal(PLC_SignalReadStatus.Good, result[a].Status));
    }

    [Fact]
    public async Task Hanging_request_times_out_as_timeout()
    {
        var igw = new FakeIgw { Quality = _ => new TaskCompletionSource<IgwTransportResult<List<DcsTagQualityItem>>>().Task };
        var aliases = Aliases(1);

        var result = await Reader(igw, timeout: TimeSpan.FromMilliseconds(50)).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(PLC_SignalReadStatus.Timeout, result[aliases[0]].Status);
    }

    // ---- Fix round 1 ----

    [Fact]
    public async Task Legacy_located_point_whose_status_parses_as_good_becomes_device_error()
    {
        // 报文本身是一次失败（"点位质量异常:"），但解析出的状态码/状态名恰好是 Good——矛盾数据：
        // 既然报了错就不该真的是 Good。必须归为 DeviceError，不能出现 Value=null 却 Status=Good。
        var aliases = Aliases(2);
        var bad = aliases[0];
        var igw = new FakeIgw
        {
            List = segment => segment.Contains(bad)
                ? ListFail("点位质量异常:" + bad + " Good(0x00000000)")
                : Envelope(ListRows(segment)),
        };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(PLC_SignalReadStatus.DeviceError, result[bad].Status);
        Assert.Null(result[bad].Value);
        Assert.Equal(PLC_SignalReadStatus.Good, result[aliases[1]].Status);
    }

    [Fact]
    public void Parser_can_report_a_good_status_from_message_text_and_caller_must_not_treat_it_as_a_pass()
    {
        // 解析器本身是纯函数、按报文原样解析；不变式的强制收敛发生在 IgwAliasReading.Bad（见上一测试）。
        Assert.True(IgwLegacyReadErrorParser.TryLocate(
            "点位质量异常:通用.S7_10.0.0.1.IO.T.B Good(0x00000000)",
            new List<string> { "通用.S7_10.0.0.1.IO.T.B" },
            out var alias, out var status));
        Assert.Equal("通用.S7_10.0.0.1.IO.T.B", alias);
        Assert.Equal(PLC_SignalReadStatus.Good, status);
    }

    [Theory]
    [InlineData("BadNotConnected(0x808A0000)", PLC_SignalReadStatus.CommFailure)]
    [InlineData("BadTimeout(0x800A0000)", PLC_SignalReadStatus.Timeout)]
    public async Task Legacy_device_or_timeout_level_failure_stops_locating_the_rest_of_the_segment(
        string statusText, PLC_SignalReadStatus expected)
    {
        var aliases = Aliases(5);
        var bad = aliases[0];
        var igw = new FakeIgw
        {
            List = segment => segment.Contains(bad)
                ? ListFail("点位质量异常:" + bad + " " + statusText)
                : Envelope(ListRows(segment)),
        };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(expected, result[bad].Status);
        Assert.All(aliases.Skip(1), a => Assert.Equal(expected, result[a].Status));
        Assert.InRange(igw.ListCalls, 1, 2);
    }

    [Fact]
    public async Task Legacy_device_level_failure_keeps_already_resolved_points_and_stops_locating_the_rest()
    {
        var aliases = Aliases(10);
        var symbolNotFound = aliases[0];
        var commFailure = aliases[1];
        var igw = new FakeIgw
        {
            List = segment =>
            {
                if (segment.Contains(symbolNotFound))
                {
                    return ListFail("点位质量异常:" + symbolNotFound + " BadNodeIdUnknown(0x80340000)");
                }
                if (segment.Contains(commFailure))
                {
                    return ListFail("点位质量异常:" + commFailure + " BadNotConnected(0x808A0000)");
                }
                return Envelope(ListRows(segment));
            },
        };

        var result = await Reader(igw, quality: false).ReadAsync(NewHost(), aliases, CancellationToken.None);

        // 已定位的第一个坏点（非设备级）保留原状态，不受后续设备级短路影响
        Assert.Equal(PLC_SignalReadStatus.SymbolNotFound, result[symbolNotFound].Status);
        // 第二个坏点是设备级故障，其余未定位点全部同状态，不再逐点定位
        Assert.Equal(PLC_SignalReadStatus.CommFailure, result[commFailure].Status);
        Assert.All(aliases.Skip(2), a => Assert.Equal(PLC_SignalReadStatus.CommFailure, result[a].Status));
        Assert.Equal(2, igw.ListCalls);
    }

    [Fact]
    public async Task Reader_forwards_its_request_timeout_to_the_quality_endpoint()
    {
        var timeout = TimeSpan.FromMilliseconds(1234);
        var igw = new FakeIgw();

        await Reader(igw, timeout: timeout).ReadAsync(NewHost(), Aliases(1), CancellationToken.None);

        Assert.Equal(timeout, igw.LastQualityTimeout);
    }

    [Fact]
    public async Task Reader_forwards_its_request_timeout_to_the_legacy_list_endpoint()
    {
        var timeout = TimeSpan.FromMilliseconds(1234);
        var igw = new FakeIgw();

        await Reader(igw, timeout: timeout, quality: false).ReadAsync(NewHost(), Aliases(1), CancellationToken.None);

        Assert.Equal(timeout, igw.LastListTimeout);
    }

    [Fact]
    public async Task Malformed_2xx_body_maps_to_device_error_not_comm_failure()
    {
        // 2xx 响应体本身解析失败（Newtonsoft JsonReaderException/JsonSerializationException）说明网关确实
        // 响应了，只是内容有问题——是设备/接口侧数据问题，不是"连不上"，不该归为 CommFailure。
        var igw = new FakeIgw
        {
            Quality = _ => Task.FromException<IgwTransportResult<List<DcsTagQualityItem>>>(
                new Newtonsoft.Json.JsonReaderException("Unexpected character encountered while parsing value")),
        };
        var aliases = Aliases(2);

        var result = await Reader(igw).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.All(aliases, a =>
        {
            Assert.Equal(PLC_SignalReadStatus.DeviceError, result[a].Status);
            Assert.Contains("返回内容无法解析", result[a].Detail);
        });
    }

    [Fact]
    public async Task Quality_missing_row_for_an_alias_is_marked_missing()
    {
        var aliases = Aliases(3);
        var missing = aliases[1];
        var igw = new FakeIgw
        {
            // 网关整批返回成功，但漏掉了其中一个点（如刚被删除/未注册）。
            Quality = batch => Envelope(batch.Where(a => a != missing).Select(GoodRow).ToList()),
        };

        var result = await Reader(igw).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.Equal(PLC_SignalReadStatus.Good, result[aliases[0]].Status);
        Assert.Equal(PLC_SignalReadStatus.Missing, result[missing].Status);
        Assert.Null(result[missing].Value);
        Assert.Equal(PLC_SignalReadStatus.Good, result[aliases[2]].Status);
    }

    [Fact]
    public async Task Quality_rows_out_of_order_are_resolved_by_key_not_index()
    {
        var aliases = Aliases(3);
        var igw = new FakeIgw
        {
            // 行顺序与请求顺序不一致（如网关内部按设备重排），必须按 key 兜底匹配，而不是按索引位置对齐。
            Quality = batch => Envelope(new List<DcsTagQualityItem> { GoodRow(batch[2]), GoodRow(batch[0]), GoodRow(batch[1]) }),
        };

        var result = await Reader(igw).ReadAsync(NewHost(), aliases, CancellationToken.None);

        Assert.All(aliases, a => Assert.Equal(PLC_SignalReadStatus.Good, result[a].Status));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_folded_into_a_status()
    {
        var igw = new FakeIgw { Quality = _ => new TaskCompletionSource<IgwTransportResult<List<DcsTagQualityItem>>>().Task };
        using var cts = new CancellationTokenSource();
        var readTask = Reader(igw, timeout: TimeSpan.FromSeconds(30)).ReadAsync(NewHost(), Aliases(1), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task Quality_405_falls_back_to_list_same_as_404()
    {
        var igw = new FakeIgw { Quality = _ => Http<DcsTagQualityItem>(HttpStatusCode.MethodNotAllowed) };

        var result = await Reader(igw).ReadAsync(NewHost(), Aliases(2), CancellationToken.None);

        Assert.All(result.Values, r => Assert.Equal(PLC_SignalReadStatus.Good, r.Status));
        Assert.Equal(1, igw.QualityCalls);
        Assert.Equal(1, igw.ListCalls);
    }

    [Theory]
    [InlineData("点位质量异常:通用.S7_10.0.0.1.IO.变量 表.A BadNotConnected(0x808A0000)", "通用.S7_10.0.0.1.IO.变量 表.A", PLC_SignalReadStatus.CommFailure)]
    [InlineData("点位质量异常:通用.S7_10.0.0.1.IO.T.B Bad", "通用.S7_10.0.0.1.IO.T.B", PLC_SignalReadStatus.DeviceError)]
    [InlineData("点位质量异常:通用.S7_10.0.0.1.IO.T.B BadWaitingForInitialData", "通用.S7_10.0.0.1.IO.T.B", PLC_SignalReadStatus.WaitingForData)]
    [InlineData("无该点位读取权限:通用.S7_10.0.0.1.IO.T.B", "通用.S7_10.0.0.1.IO.T.B", PLC_SignalReadStatus.AccessDenied)]
    [InlineData("DCS 点位别名不唯一:通用.S7_10.0.0.1.IO.T.B", "通用.S7_10.0.0.1.IO.T.B", PLC_SignalReadStatus.DeviceError)]
    public void Parser_locates_alias_and_status(string message, string expectedAlias, PLC_SignalReadStatus expectedStatus)
    {
        var segment = new List<string> { "通用.S7_10.0.0.1.IO.变量 表.A", "通用.S7_10.0.0.1.IO.T.B" };

        Assert.True(IgwLegacyReadErrorParser.TryLocate(message, segment, out var alias, out var status));
        Assert.Equal(expectedAlias, alias);
        Assert.Equal(expectedStatus, status);
    }

    [Theory]
    [InlineData("点位不存在:别的别名")]
    [InlineData("网关正在热重载，请稍后重试")]
    [InlineData("")]
    public void Parser_rejects_messages_not_about_the_segment(string message) =>
        Assert.False(IgwLegacyReadErrorParser.TryLocate(message, new List<string> { "a" }, out _, out _));
}
