using System.Text.Json;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using HSJM.AI.Mcp;
using ModelContextProtocol;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public class ConstraintDiagnosisContractTests
{
    [Fact]
    public void EnergyFlowRequest_保留原诊断控制字段()
    {
        var request = new EnergyFlowDiagnosticRequestDto
        {
            ProductionLineId = 7,
            UserDescription = "PLC2 在动作阶段卡住",
            ForceDeepAnalysis = true,
            DiagnosticId = 9001
        };

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"productionLineId\":7", json);
        Assert.Contains("\"userDescription\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"forceDeepAnalysis\":true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"diagnosticId\":9001", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mcp工具_调用EnergyFlow业务入口并原样返回业务结果()
    {
        EnergyFlowDiagnosticRequestDto capturedRequest = null;
        IDiagnosisProgressReporter capturedReporter = null;
        CancellationToken capturedCancellation = default;
        var expected = new HttpResponseResultModel<string>
        {
            IsSuccess = true,
            BackResult = "original business result"
        };
        var progressValues = new List<ProgressNotificationValue>();
        using var cancellation = new CancellationTokenSource();
        var tool = new McpDiagnosisTools(async (request, reporter, cancellationToken) =>
        {
            capturedRequest = request;
            capturedReporter = reporter;
            capturedCancellation = cancellationToken;
            await reporter.ReportAsync(DiagnosisProgressEvent.Running(
                "energy_flow", "signals", 35, "信号已加载", force: true),
                cancellationToken);
            return expected;
        });

        var actual = await tool.DiagnoseEnergyFlowAsync(
            productionLineId: 7,
            userDescription: "原诊断描述",
            forceDeepAnalysis: true,
            diagnosticId: 9001,
            progress: new InlineProgress<ProgressNotificationValue>(progressValues.Add),
            cancellationToken: cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(7, capturedRequest!.ProductionLineId);
        Assert.Equal("原诊断描述", capturedRequest.UserDescription);
        Assert.True(capturedRequest.ForceDeepAnalysis);
        Assert.Equal(9001, capturedRequest.DiagnosticId);
        Assert.IsType<McpDiagnosisProgressAdapter>(capturedReporter);
        Assert.Equal(cancellation.Token, capturedCancellation);
        Assert.Equal(35, Assert.Single(progressValues).Progress);
    }

    [Fact]
    public void EnergyFlow业务入口_接受外部取消令牌()
    {
        var method = typeof(DiagnosticStrategyFactoryApp).GetMethod("DiagnoseEnergyFlowAsync")!;
        var cancellationParameter = method.GetParameters()
            .Single(parameter => parameter.Name == "cancellationToken");

        Assert.Equal(typeof(CancellationToken), cancellationParameter.ParameterType);
        Assert.True(cancellationParameter.HasDefaultValue);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(45, 45)]
    [InlineData(130, 100)]
    public void ProgressEvent_Normalize_将百分比限制到有效范围(float input, float expected)
    {
        var normalized = new DiagnosisProgressEvent
        {
            Progress = input,
            Message = "测试"
        }.Normalize();

        Assert.Equal(expected, normalized.Progress);
        Assert.Equal(100, normalized.Total);
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 100)]
    [InlineData(float.NegativeInfinity, 0)]
    public void ProgressEvent_Normalize_将非有限进度规范为有限边界(float input, float expected)
    {
        var normalized = new DiagnosisProgressEvent
        {
            Progress = input,
            Total = float.PositiveInfinity,
            Message = "测试"
        }.Normalize();

        Assert.Equal(expected, normalized.Progress);
        Assert.Equal(100, normalized.Total);
        Assert.True(float.IsFinite(normalized.Progress));
        Assert.True(float.IsFinite(normalized.Total));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
