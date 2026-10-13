using System.Text.Json;
using HSJM.AI.Mcp;
using ModelContextProtocol;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public sealed class McpInvocationLoggerTests
{
    [Fact]
    public async Task Events_ShareCallIdAndRecordInputProgressAndOutput()
    {
        var sink = new RecordingSink();
        var logger = new McpInvocationLogger(sink);

        var scope = await logger.StartAsync("diagnose_line_stoppage", new
        {
            stationCode = "OP010",
            password = "input-secret",
        });
        await scope.ReportProgressAsync(new ProgressNotificationValue
        {
            Progress = 40,
            Total = 100,
            Message = "诊断进行中",
        });
        await scope.CompleteAsync(new
        {
            ok = true,
            reasoning = "output-secret",
        });

        Assert.Equal(3, sink.Lines.Count);
        var events = sink.Lines.Select(Parse).ToList();
        Assert.Equal(["input", "progress", "output"], events.Select(ReadEvent));
        var callId = events[0].RootElement.GetProperty("callId").GetString();
        Assert.True(Guid.TryParseExact(callId, "N", out _));
        Assert.All(events, item => Assert.Equal(callId, item.RootElement.GetProperty("callId").GetString()));
        Assert.Contains("OP010", sink.Lines[0]);
        Assert.Equal(1, events[1].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(40, events[1].RootElement.GetProperty("data").GetProperty("progress").GetSingle());
        Assert.True(events[2].RootElement.GetProperty("durationMs").GetInt64() >= 0);

        var allLogs = string.Join('\n', sink.Lines);
        Assert.DoesNotContain("input-secret", allLogs);
        Assert.DoesNotContain("output-secret", allLogs);
        Assert.Contains("REDACTED", allLogs);
    }

    [Fact]
    public async Task Payload_OverLimitIsTruncatedAndReportsOriginalLength()
    {
        var sink = new RecordingSink();
        var logger = new McpInvocationLogger(sink, maxPayloadCharacters: 64);
        var longValue = new string('x', 512);

        await logger.StartAsync("diagnose_energy_flow", new { userDescription = longValue });

        using var entry = Parse(Assert.Single(sink.Lines));
        Assert.True(entry.RootElement.GetProperty("payloadTruncated").GetBoolean());
        Assert.True(entry.RootElement.GetProperty("payloadOriginalCharacters").GetInt32() > 64);
        Assert.DoesNotContain(longValue, sink.Lines[0]);
    }

    [Fact]
    public async Task EmbeddedCredentials_AreRedactedFromStringValues()
    {
        var sink = new RecordingSink();
        var logger = new McpInvocationLogger(sink);

        await logger.StartAsync("diagnose_energy_flow", new
        {
            userDescription = "Authorization: Bearer bearer-secret; password=top-secret",
        });

        var line = Assert.Single(sink.Lines);
        Assert.DoesNotContain("bearer-secret", line);
        Assert.DoesNotContain("top-secret", line);
        Assert.Contains("REDACTED", line);
    }

    [Fact]
    public async Task EmbeddedConnectionStrings_AreRedactedAsAWhole()
    {
        var sink = new RecordingSink();
        var logger = new McpInvocationLogger(sink);

        await logger.StartAsync("diagnose_energy_flow", new
        {
            userDescription = "Host=192.0.2.10;Port=5432;Database=production;Username=admin;Password=db-secret postgresql://user:uri-secret@db.example/production",
        });

        var line = Assert.Single(sink.Lines);
        Assert.DoesNotContain("192.0.2.10", line);
        Assert.DoesNotContain("production", line);
        Assert.DoesNotContain("admin", line);
        Assert.DoesNotContain("db-secret", line);
        Assert.DoesNotContain("uri-secret", line);
        Assert.Contains("CONNECTION_STRING_REDACTED", line);
    }

    [Fact]
    public async Task NestedSensitiveNameVariants_AreRedactedRecursively()
    {
        var sink = new RecordingSink();
        var logger = new McpInvocationLogger(sink);

        await logger.StartAsync("diagnose_energy_flow", new
        {
            nested = new
            {
                databasePasswordHash = "nested-password-secret",
                apiKeyValue = "nested-api-key-secret",
                modelPromptText = "nested-prompt-secret",
                diagnosisProcessEvents = "nested-process-secret",
                rawResultDetails = "nested-raw-secret",
            },
            includeProcess = true,
            includeRawResult = true,
        });

        var line = Assert.Single(sink.Lines);
        Assert.DoesNotContain("nested-password-secret", line);
        Assert.DoesNotContain("nested-api-key-secret", line);
        Assert.DoesNotContain("nested-prompt-secret", line);
        Assert.DoesNotContain("nested-process-secret", line);
        Assert.DoesNotContain("nested-raw-secret", line);
        Assert.Contains("\"includeProcess\":true", line);
        Assert.Contains("\"includeRawResult\":true", line);
    }

    [Fact]
    public async Task ProgressMessage_EmbeddedJsonRedactsReasoningAndRawResponse()
    {
        var sink = new RecordingSink();
        var scope = await new McpInvocationLogger(sink)
            .StartAsync("diagnose_line_stoppage", new { stationCode = "OP020" });
        var message = JsonSerializer.Serialize(new
        {
            type = "llm_done",
            llmReport = new
            {
                rootCauseStation = "OP020",
                rootCauseDescription = "页面可见的诊断结论",
                reasoning = "nested-reasoning-secret",
                rawResponse = "nested-raw-response-secret",
                suggestedActions = new[] { "检查机器人通信" },
            },
        });

        await scope.ReportProgressAsync(new ProgressNotificationValue
        {
            Progress = 98,
            Total = 100,
            Message = message,
        });

        var allLogs = string.Join('\n', sink.Lines);
        Assert.Contains("页面可见的诊断结论", allLogs);
        Assert.Contains("检查机器人通信", allLogs);
        Assert.DoesNotContain("nested-reasoning-secret", allLogs);
        Assert.DoesNotContain("nested-raw-response-secret", allLogs);
        Assert.Contains("REDACTED", allLogs);
    }

    [Fact]
    public async Task TerminalEvent_IsWrittenOnlyOnce()
    {
        var sink = new RecordingSink();
        var scope = await new McpInvocationLogger(sink)
            .StartAsync("diagnose_energy_flow", new { productionLineId = 1 });

        await scope.CompleteAsync(new { ok = true });
        await scope.FailAsync(new InvalidOperationException("late failure"));
        await scope.CancelAsync();

        Assert.Equal(2, sink.Lines.Count);
        Assert.Equal("output", ReadEvent(Parse(sink.Lines[1])));
    }

    [Fact]
    public async Task ErrorAndCancellation_RecordSafeTerminalEvents()
    {
        var sink = new RecordingSink();
        var logger = new McpInvocationLogger(sink);
        var failed = await logger.StartAsync("diagnose_energy_flow", new { productionLineId = 1 });
        var cancelled = await logger.StartAsync("diagnose_line_stoppage", new { stationCode = "OP020" });

        await failed.FailAsync(new InvalidOperationException("password=exception-secret"));
        await cancelled.CancelAsync();

        var terminalEvents = sink.Lines.Select(Parse).Where(item => ReadEvent(item) != "input").ToList();
        Assert.Equal(["error", "cancelled"], terminalEvents.Select(ReadEvent));
        Assert.Equal(
            "InvalidOperationException",
            terminalEvents[0].RootElement.GetProperty("data").GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("exception-secret", sink.Lines[1]);
        Assert.DoesNotContain("stack", sink.Lines[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SinkFailure_DoesNotEscapeIntoMcpCall()
    {
        var logger = new McpInvocationLogger(new ThrowingSink());

        var scope = await logger.StartAsync("diagnose_energy_flow", new { productionLineId = 1 });
        await scope.ReportProgressAsync(new ProgressNotificationValue
        {
            Progress = 10,
            Total = 100,
            Message = "开始",
        });
        await scope.CompleteAsync(new { ok = true });
    }

    private static JsonDocument Parse(string line) => JsonDocument.Parse(line);

    private static string? ReadEvent(JsonDocument item) =>
        item.RootElement.GetProperty("event").GetString();

    private sealed class RecordingSink : IMcpInvocationLogSink
    {
        public List<string> Lines { get; } = [];

        public Task WriteAsync(string line)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IMcpInvocationLogSink
    {
        public Task WriteAsync(string line) => throw new IOException("log storage unavailable");
    }
}
