using System.Text.Json;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.Mcp;
using ModelContextProtocol;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public sealed class McpProgressLoggingTests
{
    [Fact]
    public async Task Adapter_LogsOnlyProgressNotificationsThatWereActuallySent()
    {
        var sink = new RecordingSink();
        var scope = await new McpInvocationLogger(sink)
            .StartAsync("diagnose_line_stoppage", new { stationCode = "OP010" });
        var sent = new List<ProgressNotificationValue>();
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(sent.Add),
            TimeSpan.Zero,
            (value, _) => scope.ReportProgressAsync(value));

        await adapter.ReportAsync(new DiagnosisProgressEvent
        {
            Progress = 20,
            Message = "已发送",
        });
        await adapter.ReportAsync(new DiagnosisProgressEvent
        {
            Progress = 10,
            Message = "倒退事件被抑制",
        });
        await adapter.ReportAsync(new DiagnosisProgressEvent
        {
            Progress = 20,
            Message = "重复事件被抑制",
        });
        await adapter.ReportAsync(new DiagnosisProgressEvent
        {
            Progress = 100,
            Message = "完成",
            Force = true,
        });

        Assert.Equal([20f, 100f], sent.Select(item => item.Progress));
        var progressEvents = sink.Lines
            .Select(line => JsonDocument.Parse(line))
            .Where(item => item.RootElement.GetProperty("event").GetString() == "progress")
            .ToList();
        Assert.Equal(2, progressEvents.Count);
        Assert.Equal([1, 2], progressEvents.Select(item => item.RootElement.GetProperty("sequence").GetInt32()));
        Assert.Equal(
            [20f, 100f],
            progressEvents.Select(item => item.RootElement.GetProperty("data").GetProperty("progress").GetSingle()));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class RecordingSink : IMcpInvocationLogSink
    {
        public List<string> Lines { get; } = [];

        public Task WriteAsync(string line)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }
    }
}
