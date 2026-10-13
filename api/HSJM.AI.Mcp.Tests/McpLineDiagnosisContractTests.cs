using System.Text.Json;
using HSJM.AI.AppLayer.StopCauseDiagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using HSJM.AI.Mcp;
using ModelContextProtocol;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public class McpLineDiagnosisContractTests
{
    [Fact]
    public void AgentRequestDto_保留旧接口字段与默认值()
    {
        var dtoType = typeof(LineDiagnosisRequestDto).Assembly.GetType(
            "HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS.AgentLineDiagnosisRequestDto");

        Assert.NotNull(dtoType);
        var request = Activator.CreateInstance(dtoType!);
        Assert.Equal("FinalNodesOnly", dtoType!.GetProperty("OutputMode")!.GetValue(request));
        Assert.Equal(30, dtoType.GetProperty("MaxFinalNodes")!.GetValue(request));
        Assert.Equal(true, dtoType.GetProperty("UseLlmAnalysis")!.GetValue(request));
        Assert.NotNull(dtoType.GetProperty("LlmMode"));
        Assert.NotNull(dtoType.GetProperty("UseAgentDiagnosis"));
        Assert.NotNull(dtoType.GetProperty("EnablePerStationLlm"));
        Assert.NotNull(dtoType.GetProperty("IncludeProcess"));
        Assert.NotNull(dtoType.GetProperty("IncludeRawResult"));
    }

    [Fact]
    public void AgentResponseDto_保留总结节点过程和原始结果()
    {
        var responseType = typeof(LineDiagnosisRequestDto).Assembly.GetType(
            "HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS.AgentLineDiagnosisResponseDto");

        Assert.NotNull(responseType);
        Assert.NotNull(responseType!.GetProperty("Summary"));
        Assert.NotNull(responseType.GetProperty("FinalNodes"));
        Assert.NotNull(responseType.GetProperty("StationStatuses"));
        Assert.NotNull(responseType.GetProperty("Events"));
        Assert.NotNull(responseType.GetProperty("RawResult"));
        Assert.NotNull(responseType.GetProperty("Warnings"));
    }

    [Fact]
    public async Task Mcp工具_默认参数与产线诊断页面一致()
    {
        LineDiagnosisRequestDto capturedRequest = null;
        var tool = new McpDiagnosisTools((request, _, _, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(SuccessResult());
        });

        await tool.DiagnoseLineStoppageAsync();

        Assert.NotNull(capturedRequest);
        Assert.Empty(capturedRequest!.ProjectIds);
        Assert.Empty(capturedRequest.StationCodes);
        Assert.Equal(3, capturedRequest.MaxParallelStations);
        Assert.Equal(500, capturedRequest.MaxAnchorsPerStation);
        Assert.True(capturedRequest.UseLlmAnalysis);
        Assert.True(capturedRequest.ForceLlmSummaryWhenNoAbnormal);
        Assert.False(capturedRequest.UseAgentDiagnosis);
        Assert.False(capturedRequest.EnablePerStationLlm);
        Assert.True(capturedRequest.GraphOnlyMode);
        Assert.False(capturedRequest.EnableDeviceFocusCheck);
        Assert.Empty(capturedRequest.DeviceFocusAnchorTypes);
        Assert.Equal(8, capturedRequest.ActiveGraphGraceSeconds);
        Assert.True(capturedRequest.IncludeConstraintAnalysis);
        Assert.Equal(4, capturedRequest.MaxFbPenetrationDepth);
        Assert.Equal("zh-CN", capturedRequest.OutputLanguage);
    }

    [Fact]
    public async Task Mcp工具_原样返回页面业务结果并合并单双值筛选参数()
    {
        LineDiagnosisRequestDto capturedRequest = null;
        string capturedTaskId = null;
        CancellationToken capturedCancellation = default;
        var expected = SuccessResult(totalStations: 3);
        using var cancellation = new CancellationTokenSource();
        var tool = new McpDiagnosisTools((request, taskId, _, cancellationToken) =>
        {
            capturedRequest = request;
            capturedTaskId = taskId;
            capturedCancellation = cancellationToken;
            return Task.FromResult(expected);
        });

        var actual = await tool.DiagnoseLineStoppageAsync(
            projectId: 101,
            projectIds: [102, 103, 101],
            stationCode: "OP010",
            stationCodes: ["OP020", "OP030", "OP010"],
            maxParallelStations: 4,
            maxAnchorsPerStation: 321,
            useLlmAnalysis: false,
            outputLanguage: "en-US",
            forceLlmSummaryWhenNoAbnormal: false,
            useAgentDiagnosis: true,
            agentMaxIterations: 7,
            enablePerStationLlm: true,
            timeoutSeconds: 45,
            taskId: "line-task",
            graphOnlyMode: false,
            enableDeviceFocusCheck: true,
            deviceFocusAnchorTypes: ["DevicePermitState", "DeviceMotionFeedback"],
            activeGraphGraceSeconds: 3,
            includeConstraintAnalysis: false,
            maxFbPenetrationDepth: 6,
            cancellationToken: cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Same(expected.BackResult, actual.BackResult);
        Assert.Equal([101L, 102L, 103L], capturedRequest!.ProjectIds);
        Assert.Equal(["OP010", "OP020", "OP030"], capturedRequest.StationCodes);
        Assert.Equal(4, capturedRequest.MaxParallelStations);
        Assert.Equal(321, capturedRequest.MaxAnchorsPerStation);
        Assert.False(capturedRequest.UseLlmAnalysis);
        Assert.Equal("en-US", capturedRequest.OutputLanguage);
        Assert.False(capturedRequest.ForceLlmSummaryWhenNoAbnormal);
        Assert.True(capturedRequest.UseAgentDiagnosis);
        Assert.Equal(7, capturedRequest.AgentMaxIterations);
        Assert.True(capturedRequest.EnablePerStationLlm);
        Assert.Equal(45, capturedRequest.TimeoutSeconds);
        Assert.Equal("line-task", capturedRequest.TaskId);
        Assert.False(capturedRequest.GraphOnlyMode);
        Assert.True(capturedRequest.EnableDeviceFocusCheck);
        Assert.Equal(["DevicePermitState", "DeviceMotionFeedback"], capturedRequest.DeviceFocusAnchorTypes);
        Assert.Equal(3, capturedRequest.ActiveGraphGraceSeconds);
        Assert.False(capturedRequest.IncludeConstraintAnalysis);
        Assert.Equal(6, capturedRequest.MaxFbPenetrationDepth);
        Assert.Equal("line-task", capturedTaskId);
        Assert.Equal(cancellation.Token, capturedCancellation);
    }

    [Fact]
    public async Task Mcp工具_快速完成的每个工站过程都进入Progress且最终发送完整结果事件()
    {
        const int stationCount = 10;
        var progressValues = new List<ProgressNotificationValue>();
        var expected = SuccessResult(stationCount);
        var tool = new McpDiagnosisTools(async (_, taskId, onEvent, _) =>
        {
            await onEvent(new LineDiagnosisProgressEventDto
            {
                Type = "start",
                TaskId = taskId,
                StationCodes = Enumerable.Range(1, stationCount).Select(index => $"OP{index:000}").ToList(),
                TotalCount = stationCount,
                Message = "Line diagnosis started.",
            });
            for (var index = 1; index <= stationCount; index++)
            {
                await onEvent(new LineDiagnosisProgressEventDto
                {
                    Type = "station_done",
                    TaskId = taskId,
                    StationCode = $"OP{index:000}",
                    CompletedCount = index,
                    TotalCount = stationCount,
                    Station = new StationDiagnosisSummaryDto
                    {
                        StationCode = $"OP{index:000}",
                        StationOrder = index,
                        OverallStatus = "AllTrue",
                        DiagnosisStrategy = "GraphDirectEval",
                    },
                    Message = "Station diagnosis completed.",
                });
            }
            return expected;
        });

        var actual = await tool.DiagnoseLineStoppageAsync(
            taskId: "line-task",
            progress: new InlineProgress<ProgressNotificationValue>(progressValues.Add));

        Assert.Same(expected, actual);
        Assert.Equal(stationCount + 2, progressValues.Count);
        Assert.All(progressValues, value => Assert.Equal<float?>(100, value.Total));
        Assert.Equal(
            Enumerable.Range(1, stationCount).Select(index => $"OP{index:000}"),
            progressValues
                .Select(ParseMessage)
                .Where(item => item.RootElement.GetProperty("type").GetString() == "station_done")
                .Select(item => item.RootElement.GetProperty("stationCode").GetString()));

        using var complete = ParseMessage(progressValues[^1]);
        Assert.Equal(100, progressValues[^1].Progress);
        Assert.Equal("complete", complete.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            stationCount,
            complete.RootElement.GetProperty("payload").GetProperty("totalStations").GetInt32());
    }

    [Fact]
    public async Task Mcp工具_Llm完成事件包含页面可见的完整AI诊断结论()
    {
        var report = new LineDiagnosisLlmReport
        {
            Success = true,
            RootCauseStation = "OP020",
            RootCauseDescription = "机器人同步结束信号 FirstWork 为 False，Graph 步骤卡在 Transition。",
            Confidence = 0.95,
            ImpactChain = "根因:OP020 → 受影响:[OP030,OP040]",
            SecondaryCauses =
            [
                new SecondaryCauseItem
                {
                    StationCode = "OP010",
                    Description = "上游流程尚未结束。",
                },
            ],
            SuggestedActions = ["检查机器人通信状态。", "检查 FirstWork 变量。"],
            Reasoning = "根据工站状态与 GRAPH 转移条件定位根因。",
        };
        var evt = LineDiagnosisBusinessProgress.CreateLlmDoneEvent(
            "line-task",
            report,
            ["OP010", "OP020", "OP030", "OP040"]);
        var progressValues = new List<ProgressNotificationValue>();
        var stream = new McpLineDiagnosisProgressStream(
            new InlineProgress<ProgressNotificationValue>(progressValues.Add));

        await stream.ReportEventAsync(evt);

        using var message = ParseMessage(Assert.Single(progressValues));
        var llmReport = message.RootElement.GetProperty("llmReport");
        Assert.Equal("OP020", llmReport.GetProperty("rootCauseStation").GetString());
        Assert.Contains("FirstWork", llmReport.GetProperty("rootCauseDescription").GetString());
        Assert.Equal(0.95, llmReport.GetProperty("confidence").GetDouble());
        Assert.Equal("OP010", llmReport.GetProperty("secondaryCauses")[0].GetProperty("stationCode").GetString());
        Assert.Equal(2, llmReport.GetProperty("suggestedActions").GetArrayLength());
        Assert.Contains("GRAPH", llmReport.GetProperty("reasoning").GetString());
    }

    [Fact]
    public void Mcp工具_返回类型与页面接口相同()
    {
        var method = typeof(McpDiagnosisTools).GetMethod(nameof(McpDiagnosisTools.DiagnoseLineStoppageAsync))!;
        var envelopeType = method.ReturnType.GenericTypeArguments.Single();

        Assert.Equal(typeof(HttpResponseResultModel<LineDiagnosisResultDto>), envelopeType);
    }

    [Fact]
    public void Mcp诊断业务入口_接受详细事件回调与外部取消令牌()
    {
        var method = typeof(StopCauseDiagnosisApp).GetMethod("DiagnoseLineStoppageForMcpAsync");

        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(), parameter =>
            parameter.ParameterType == typeof(Func<LineDiagnosisProgressEventDto, Task>));
        var cancellationParameter = method.GetParameters()
            .Single(parameter => parameter.Name == "cancellationToken");
        Assert.Equal(typeof(CancellationToken), cancellationParameter.ParameterType);
        Assert.True(cancellationParameter.HasDefaultValue);
    }

    [Fact]
    public async Task Mcp工具_原样传播业务执行期间的外部取消()
    {
        CancellationToken capturedCancellation = default;
        using var cancellation = new CancellationTokenSource();
        var tool = new McpDiagnosisTools((_, _, _, cancellationToken) =>
        {
            capturedCancellation = cancellationToken;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseResultModel<LineDiagnosisResultDto>>(
                cancellationToken);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.DiagnoseLineStoppageAsync(cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, capturedCancellation);
    }

    private static HttpResponseResultModel<LineDiagnosisResultDto> SuccessResult(int totalStations = 0) =>
        new()
        {
            IsSuccess = true,
            BackResult = new LineDiagnosisResultDto
            {
                TotalStations = totalStations,
                DiagnosedStations = totalStations,
                StationSummaries = Enumerable.Range(1, totalStations)
                    .Select(index => new StationDiagnosisSummaryDto
                    {
                        StationCode = $"OP{index:000}",
                        StationOrder = index,
                        OverallStatus = "AllTrue",
                        DiagnosisStrategy = "GraphDirectEval",
                    })
                    .ToList(),
            },
        };

    private static JsonDocument ParseMessage(ProgressNotificationValue value) =>
        JsonDocument.Parse(Assert.IsType<string>(value.Message));

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
