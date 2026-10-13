using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public class McpProgressAdapterTests
{
    [Fact]
    public async Task ReportAsync_ForwardsTypedNestedProgressAndSafeMetrics()
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);
        var progresses = new[] { 70f, 73f, 76f, 79f, 82f };

        foreach (var progress in progresses)
        {
            await adapter.ReportAsync(DiagnosisProgressEvent.Running(
                "line-1",
                "constraints",
                progress,
                "约束阶段正在执行",
                new DiagnosisProgressMetrics
                {
                    SignalCount = 12,
                    CompletedCount = 2,
                    TotalCount = 4,
                    RuleCount = 8,
                    IssueCount = 1,
                    ElapsedMs = 123
                },
                force: true));
        }

        Assert.Equal(progresses, values.Select(item => item.Progress));
        Assert.All(values, item => Assert.Equal<float?>(100f, item.Total));
        Assert.Contains("SignalCount=12", values[0].Message);
        Assert.Contains("CompletedCount=2/TotalCount=4", values[0].Message);
        Assert.Contains("RuleCount=8", values[0].Message);
        Assert.Contains("IssueCount=1", values[0].Message);
        Assert.Contains("ElapsedMs=123", values[0].Message);
    }

    [Fact]
    public async Task ReportAsync_RejectsRegressionsAndOnlyAllowsForcedEqualBoundary()
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);

        await adapter.ReportAsync(Running(20, "first"));
        await adapter.ReportAsync(Running(10, "regression", force: true));
        await adapter.ReportAsync(Running(20, "duplicate"));
        await adapter.ReportAsync(Running(20, "forced boundary", force: true));

        Assert.Equal([20f, 20f], values.Select(item => item.Progress));
        Assert.Equal("forced boundary", values[^1].Message);
    }

    [Fact]
    public async Task ReportAsync_ThrottlesOnlyNonForcedUpdates()
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values, TimeSpan.FromHours(1));

        await adapter.ReportAsync(Running(10, "first"));
        await adapter.ReportAsync(Running(20, "too soon"));
        await adapter.ReportAsync(Running(30, "stage boundary", force: true));

        Assert.Equal([10f, 30f], values.Select(item => item.Progress));
    }

    [Fact]
    public async Task ReportAsync_PropagatesActualCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(_ =>
                throw new OperationCanceledException(cancellation.Token)),
            TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ReportAsync(Running(10, "start"), cancellation.Token));
    }

    [Fact]
    public async Task ReportAsync_DoesNotEmitFurtherResultFragmentsAfterCancellation()
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);
        using var cancellation = new CancellationTokenSource();

        await adapter.ReportAsync(
            Running(20, "结果片段：Success=true, RootCauseStation=OP010", force: true),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ReportAsync(
            Running(30, "结果片段：Success=true, RootCauseStation=OP020", force: true),
            cancellation.Token));

        Assert.Equal(
            "结果片段：Success=true, RootCauseStation=OP010",
            Assert.Single(values).Message);
    }

    [Fact]
    public async Task ReportAsync_SwallowsOrdinaryNotificationFailure()
    {
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(_ =>
                throw new InvalidOperationException("transport unavailable")),
            TimeSpan.Zero);

        await adapter.ReportAsync(Running(10, "start"));
    }

    [Fact]
    public async Task ReportAsync_SwallowsNonCancellationOperationCanceledException()
    {
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(_ =>
                throw new OperationCanceledException()),
            TimeSpan.Zero);

        await adapter.ReportAsync(Running(10, "start"));
    }

    [Fact]
    public async Task ReportAsync_CommitsAcceptedProgressBeforeNotificationSideEffectThrows()
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(value =>
            {
                values.Add(value);
                throw new InvalidOperationException("failed after side effect");
            }),
            TimeSpan.Zero);

        await adapter.ReportAsync(Running(60, "accepted"));
        await adapter.ReportAsync(Running(50, "must be rejected"));

        Assert.Equal([60f], values.Select(item => item.Progress));
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 100)]
    [InlineData(float.NegativeInfinity, 0)]
    public async Task ReportAsync_NormalizesNonFiniteProgressBeforeSdkNotification(
        float input,
        float expected)
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);

        await adapter.ReportAsync(Running(input, "non-finite"));

        var notification = Assert.Single(values);
        Assert.True(float.IsFinite(notification.Progress));
        Assert.Equal(expected, notification.Progress);
        Assert.Equal<float?>(100, notification.Total);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public async Task ReportAsync_NonFiniteLowProgressCannotReopenAcceptedRange(float input)
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);

        await adapter.ReportAsync(Running(60, "accepted"));
        await adapter.ReportAsync(Running(input, "invalid"));
        await adapter.ReportAsync(Running(50, "regression"));

        Assert.Equal([60f], values.Select(item => item.Progress));
    }

    [Fact]
    public async Task ReportAsync_PositiveInfinityCompletesRangeAndRejectsLaterRegression()
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);

        await adapter.ReportAsync(Running(60, "accepted"));
        await adapter.ReportAsync(Running(float.PositiveInfinity, "invalid high"));
        await adapter.ReportAsync(Running(50, "regression"));

        Assert.Equal([60f, 100f], values.Select(item => item.Progress));
        Assert.All(values, item => Assert.True(float.IsFinite(item.Progress)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task ReportAsync_NormalizesNonFiniteTotalBeforeSdkNotification(float total)
    {
        var values = new List<ProgressNotificationValue>();
        var adapter = CreateAdapter(values);
        var progress = Running(40, "invalid total") with { Total = total };

        await adapter.ReportAsync(progress);

        var notification = Assert.Single(values);
        Assert.Equal(40, notification.Progress);
        Assert.Equal<float?>(100, notification.Total);
        Assert.True(notification.Total.HasValue && float.IsFinite(notification.Total.Value));
    }

    [Fact]
    public async Task ReportAsync_PropagatesCancellationTriggeredInsideSuccessfulCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(_ => cancellation.Cancel()),
            TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ReportAsync(Running(10, "start"), cancellation.Token));
    }

    [Fact]
    public async Task ReportAsync_PropagatesCancellationTriggeredBeforeOrdinaryCallbackFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new McpDiagnosisProgressAdapter(
            new InlineProgress<ProgressNotificationValue>(_ =>
            {
                cancellation.Cancel();
                throw new InvalidOperationException("transport failed during cancellation");
            }),
            TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ReportAsync(Running(10, "start"), cancellation.Token));
    }

    [Theory]
    [InlineData("password=secret")]
    [InlineData("Authorization: Bearer abc.def")]
    [InlineData("Authorization = Basic dXNlcjpwYXNz")]
    [InlineData("Authorization: AWS4-HMAC Credential=AKIA/20260817/cn/service/aws4_request")]
    [InlineData("Authorization = Token short-secret")]
    [InlineData("Bearer secret")]
    [InlineData("access_token=abcdef")]
    [InlineData("token=abcdef")]
    [InlineData("api-key: super-secret")]
    [InlineData("ConnectionString=Server=db;Database=prod;User Id=sa;Password=x")]
    [InlineData("Host=db;Database=prod;User Id=sa;Password=x")]
    [InlineData("AccountKey=storage-account-secret")]
    [InlineData("SharedAccessKey: service-bus-secret")]
    [InlineData("postgresql://admin:secret@db.internal:5432/prod")]
    [InlineData("redis://:secret@cache.internal")]
    [InlineData("admin/secret@ORCL")]
    [InlineData("System.InvalidOperationException: boom\n at Demo.Service.Run() in C:\\src\\Demo.cs:line 42")]
    [InlineData("Demo.Service.Run(String value)")]
    [InlineData("Service.Run(String value)")]
    [InlineData("at Run() in C:\\src\\Demo.cs:line 42")]
    [InlineData("现场地址 %I1.0=true")]
    [InlineData("现场地址 %IX1.0=true")]
    [InlineData("现场地址 %QX2.1=false")]
    [InlineData("现场地址 %MX3.2=1")]
    [InlineData("PLC 地址 DB1.DBX0.0 = false")]
    [InlineData("{\"signalValue\":true}")]
    [InlineData("<signal address=\"%I1.0\" value=\"true\" />")]
    [InlineData("signalValue: true")]
    [InlineData("- signalValue: true")]
    [InlineData("- %IX1.0\n- true")]
    [InlineData("payload=raw-diagnostic-data")]
    [InlineData("结果片段：token=must-not-leak")]
    [InlineData("结果片段：{\"rawSignal\":true}")]
    public void Formatter_RedactsEntireUnsafeMessageToFixedText(string unsafeMessage)
    {
        var formatted = McpProgressMessageFormatter.Format(Running(25, unsafeMessage));

        Assert.Equal("诊断阶段正在执行", formatted);
    }

    [Theory]
    [InlineData("token bucket initialized")]
    [InlineData("Authorization checks complete")]
    [InlineData("开始原模型诊断阶段")]
    [InlineData("结果片段：Success=true, RootCauseStation=OP020")]
    public void Formatter_PreservesSafeOperationalMessages(string safeMessage)
    {
        var formatted = McpProgressMessageFormatter.Format(DiagnosisProgressEvent.Running(
            "diagnosis",
            "stage",
            25,
            safeMessage,
            new DiagnosisProgressMetrics { IssueCount = 2 }));

        Assert.Equal($"{safeMessage} | IssueCount=2", formatted);
    }

    [Fact]
    public void Formatter_DoesNotAppendUnknownOrRawPayloadData()
    {
        var formatted = McpProgressMessageFormatter.Format(DiagnosisProgressEvent.Running(
            "energy-1",
            "signals_loaded",
            35,
            "信号已加载",
            new DiagnosisProgressMetrics { SignalCount = 9 },
            force: true));

        Assert.Equal("信号已加载 | SignalCount=9", formatted);
        Assert.DoesNotContain("energy-1", formatted);
        Assert.DoesNotContain("signals_loaded", formatted);
    }

    [Fact]
    public async Task ConcurrentAdapters_KeepProgressStateIndependent()
    {
        var first = new List<ProgressNotificationValue>();
        var second = new List<ProgressNotificationValue>();
        var firstAdapter = CreateAdapter(first);
        var secondAdapter = CreateAdapter(second);

        await Task.WhenAll(
            firstAdapter.ReportAsync(Running(80, "first")),
            secondAdapter.ReportAsync(Running(15, "second")));
        await Task.WhenAll(
            firstAdapter.ReportAsync(Running(70, "first regression")),
            secondAdapter.ReportAsync(Running(25, "second advances")));

        Assert.Equal([80f], first.Select(item => item.Progress));
        Assert.Equal([15f, 25f], second.Select(item => item.Progress));
    }

    [Fact]
    public void ToolCatalog_PreservesNamesArgumentsAndBusinessResultContracts()
    {
        var tools = typeof(McpDiagnosisTools)
            .GetMethods()
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttributes(typeof(McpServerToolAttribute), false)
                    .Cast<McpServerToolAttribute>()
                    .SingleOrDefault()
            })
            .Where(item => item.Attribute != null)
            .ToList();

        Assert.Equal(
            ["diagnose_energy_flow", "diagnose_line_stoppage"],
            tools.Select(item => item.Attribute!.Name).OrderBy(name => name));

        var energy = tools.Single(item => item.Attribute!.Name == "diagnose_energy_flow").Method;
        Assert.Contains("productionLineId", energy.GetParameters().Select(item => item.Name));
        Assert.Contains("userDescription", energy.GetParameters().Select(item => item.Name));
        Assert.Contains("forceDeepAnalysis", energy.GetParameters().Select(item => item.Name));
        Assert.Contains("diagnosticId", energy.GetParameters().Select(item => item.Name));
        AssertBusinessEnvelope(energy, typeof(string));

        var line = tools.Single(item => item.Attribute!.Name == "diagnose_line_stoppage").Method;
        var lineParameters = line.GetParameters().Select(item => item.Name ?? string.Empty).ToHashSet();
        Assert.Contains("useLlmAnalysis", lineParameters);
        Assert.Contains("useAgentDiagnosis", lineParameters);
        Assert.Contains("enablePerStationLlm", lineParameters);
        Assert.Contains("projectIds", lineParameters);
        Assert.Contains("stationCodes", lineParameters);
        Assert.Contains("includeConstraintAnalysis", lineParameters);
        Assert.Contains("progress", lineParameters);
        AssertBusinessEnvelope(line, typeof(LineDiagnosisResultDto));
    }

    private static void AssertBusinessEnvelope(System.Reflection.MethodInfo method, Type payloadType)
    {
        var result = method.ReturnType.GenericTypeArguments.Single();
        Assert.Equal("HttpResponseResultModel`1", result.Name);
        Assert.Equal(payloadType, result.GenericTypeArguments.Single());
    }

    private static DiagnosisProgressEvent Running(float progress, string message, bool force = false) =>
        DiagnosisProgressEvent.Running("diagnosis", "stage", progress, message, force: force);

    private static McpDiagnosisProgressAdapter CreateAdapter(
        List<ProgressNotificationValue> values,
        TimeSpan? minimumInterval = null) =>
        new(new InlineProgress<ProgressNotificationValue>(values.Add), minimumInterval ?? TimeSpan.Zero);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
