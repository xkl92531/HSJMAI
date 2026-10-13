using System.Net;
using HSJM.AI.DbService.Service.IOT;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 1：IGW 逐点质量读的 HTTP 层与状态映射。</summary>
public class IotServiceQualityReadTests
{
    // IGW 1.6.78 实际线上格式：camelCase
    private const string QualityBody =
        "{\"httpStatusCode\":200,\"isSuccess\":true,\"backResult\":[" +
        "{\"key\":\"a\",\"value\":true,\"isGood\":true,\"statusCode\":0,\"status\":\"Good\",\"timestampUtc\":\"2026-09-19T12:00:00.123Z\",\"reason\":null}," +
        "{\"key\":\"b\",\"value\":null,\"isGood\":false,\"statusCode\":2150891520,\"status\":\"BadNodeIdUnknown\",\"timestampUtc\":null,\"reason\":\"NotRegistered\"}]," +
        "\"errorMessage\":null,\"exceptionMessage\":null}";

    private static IotService Iot(StubHandler handler) =>
        new(new RequestToHttpHelper(new StubHttpClientFactory(handler)));

    [Fact]
    public async Task ReadTagsWithQuality_posts_aliases_and_parses_camelCase_rows()
    {
        var handler = new StubHandler(_ => Task.FromResult(StubHandler.Json(HttpStatusCode.OK, QualityBody)));

        var result = await Iot(handler).ReadTagsWithQualityAsync("127.0.0.1:48020", new List<string> { "a", "b" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:48020/Tag/ReadTagsWithQuality", request.Uri);
        Assert.Equal("[\"a\",\"b\"]", request.Body);
        Assert.True(result.IsHttpSuccess);
        Assert.Equal(HttpStatusCode.OK, result.HttpStatusCode);
        Assert.True(result.Envelope.IsSuccess);
        var rows = result.Envelope.BackResult;
        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0].Key);
        Assert.True(rows[0].IsGood);
        Assert.Equal(true, rows[0].Value);
        Assert.Equal(new DateTime(2026, 9, 19, 12, 0, 0, 123, DateTimeKind.Utc), rows[0].TimestampUtc);
        Assert.Null(rows[0].Reason);
        Assert.False(rows[1].IsGood);
        Assert.Null(rows[1].Value);
        Assert.Equal(0x80340000u, rows[1].StatusCode);
        Assert.Equal("BadNodeIdUnknown", rows[1].Status);
        Assert.Null(rows[1].TimestampUtc);
        Assert.Equal("NotRegistered", rows[1].Reason);
    }

    [Fact]
    public async Task ReadTagsWithQuality_404_returns_transport_status_without_envelope()
    {
        var handler = new StubHandler(_ => Task.FromResult(StubHandler.Json(HttpStatusCode.NotFound, "not found")));

        var result = await Iot(handler).ReadTagsWithQualityAsync("http://127.0.0.1:48020/", new List<string> { "a" });

        Assert.False(result.IsHttpSuccess);
        Assert.Equal(HttpStatusCode.NotFound, result.HttpStatusCode);
        Assert.Null(result.Envelope);
        Assert.Equal("not found", result.RawBody);
    }

    [Fact]
    public async Task ReadTagsWithQuality_does_not_swallow_network_exceptions()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Iot(handler).ReadTagsWithQualityAsync("http://127.0.0.1:48020", new List<string> { "a" }));
    }

    [Fact]
    public async Task ReadTagsAsListRaw_keeps_business_failure_message()
    {
        const string body = "{\"httpStatusCode\":200,\"isSuccess\":false,\"backResult\":[]," +
                            "\"errorMessage\":\"点位质量异常:a BadNodeIdUnknown(0x80340000)\",\"exceptionMessage\":null}";
        var handler = new StubHandler(_ => Task.FromResult(StubHandler.Json(HttpStatusCode.OK, body)));

        var result = await Iot(handler).ReadTagsAsListRawAsync("http://127.0.0.1:48020", new List<string> { "a" });

        Assert.Equal("http://127.0.0.1:48020/Tag/ReadTagsAsList", Assert.Single(handler.Requests).Uri);
        Assert.True(result.IsHttpSuccess);
        Assert.False(result.Envelope.IsSuccess);
        Assert.Equal("点位质量异常:a BadNodeIdUnknown(0x80340000)", result.Envelope.ErrorMessage);
    }

    [Theory]
    [InlineData(true, null, PLC_SignalReadStatus.Good)]
    [InlineData(false, "SymbolNotFound", PLC_SignalReadStatus.SymbolNotFound)]
    [InlineData(false, "notregistered", PLC_SignalReadStatus.NotRegistered)]
    [InlineData(false, "CommFailure", PLC_SignalReadStatus.CommFailure)]
    [InlineData(false, "Timeout", PLC_SignalReadStatus.Timeout)]
    [InlineData(false, "WaitingForData", PLC_SignalReadStatus.WaitingForData)]
    [InlineData(false, "AccessDenied", PLC_SignalReadStatus.AccessDenied)]
    [InlineData(false, "DeviceError", PLC_SignalReadStatus.DeviceError)]
    [InlineData(false, "SomethingNew", PLC_SignalReadStatus.DeviceError)]
    [InlineData(false, null, PLC_SignalReadStatus.DeviceError)]
    public void Igw_reason_maps_to_status(bool isGood, string reason, PLC_SignalReadStatus expected) =>
        Assert.Equal(expected, PLC_SignalReadStatusMapper.FromIgwReason(isGood, reason));

    [Theory]
    [InlineData(0x80340000u, PLC_SignalReadStatus.SymbolNotFound)]
    [InlineData(0x800A0000u, PLC_SignalReadStatus.Timeout)]
    [InlineData(0x80320000u, PLC_SignalReadStatus.WaitingForData)]
    [InlineData(0x80050000u, PLC_SignalReadStatus.CommFailure)]
    [InlineData(0x808A0000u, PLC_SignalReadStatus.CommFailure)]
    [InlineData(0x801F0000u, PLC_SignalReadStatus.AccessDenied)]
    [InlineData(0x80000000u, PLC_SignalReadStatus.DeviceError)]
    [InlineData(0x40000000u, PLC_SignalReadStatus.DeviceError)]
    [InlineData(0u, PLC_SignalReadStatus.Good)]
    public void Status_code_maps_to_status(uint code, PLC_SignalReadStatus expected) =>
        Assert.Equal(expected, PLC_SignalReadStatusMapper.FromStatusCode(code));

    [Theory]
    [InlineData("BadNodeIdUnknown", PLC_SignalReadStatus.SymbolNotFound)]
    [InlineData("BadTimeout", PLC_SignalReadStatus.Timeout)]
    [InlineData("BadWaitingForInitialData", PLC_SignalReadStatus.WaitingForData)]
    [InlineData("BadNotConnected", PLC_SignalReadStatus.CommFailure)]
    [InlineData("BadCommFailure", PLC_SignalReadStatus.CommFailure)]
    [InlineData("BadUserAccessDenied", PLC_SignalReadStatus.AccessDenied)]
    [InlineData("Bad", PLC_SignalReadStatus.DeviceError)]
    [InlineData(null, PLC_SignalReadStatus.DeviceError)]
    public void Status_name_maps_to_status(string name, PLC_SignalReadStatus expected) =>
        Assert.Equal(expected, PLC_SignalReadStatusMapper.FromStatusName(name));
}
