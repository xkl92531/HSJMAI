#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>计划 3 整计划审查修正：C-1 网络号口径、C-2 管道未启用、M-4 绝对地址不按 DB 路径读。</summary>
public class AlarmCatalogReviewFixTests
{
    // ── C-1：归档读取器的库口径编号 ──

    const string BlockWithEmptyNetwork = """
        <Document><SW.Blocks.FB><AttributeList><Name>FB121_Cylinder</Name><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList>
        <SW.Blocks.CompileUnit ID="3"><AttributeList><NetworkSource><FlgNet><Parts /></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>
        <SW.Blocks.CompileUnit ID="4"><AttributeList><NetworkSource /></AttributeList></SW.Blocks.CompileUnit>
        <SW.Blocks.CompileUnit ID="5"><AttributeList /></SW.Blocks.CompileUnit>
        <SW.Blocks.CompileUnit ID="6"><AttributeList><NetworkSource><FlgNet><Parts><Call UId="21"><CallInfo Name="FB3100_ValveControl" BlockType="FB" /></Call></Parts></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>
        </ObjectList></SW.Blocks.FB></Document>
        """;

    [Fact]
    public void ArchiveUnitsCarryTheDatabaseNetworkNumberEvenAfterEmptyNetworks()
    {
        var block = PipelineExportReader.Read(BlockWithEmptyNetwork, 42, "FB121_Cylinder.xml");
        Assert.Equal(2, block.Units.Count);
        // 归档根元素序号：空网络 / 无 NetworkSource 的 CompileUnit 不计 → 1、2
        Assert.Equal(new int?[] { 1, 2 }, block.Units.Select(u => u.NetworkOrder).ToArray());
        // 库口径（上传解析器每个 CompileUnit 计 1）→ 1、4
        Assert.Equal(new int?[] { 1, 4 }, block.Units.Select(u => u.CompileUnitOrder).ToArray());
        Assert.Equal(new int?[] { 1, 4 }, block.Units.Select(u => u.DbNetworkId).ToArray());
    }

    [Fact]
    public void CompileUnitOrderDoesNotChangeUnitIdsOrTheSnapshotFingerprint()
    {
        var block = PipelineExportReader.Read(BlockWithEmptyNetwork, 42, "FB121_Cylinder.xml");
        var stripped = block.Units.Select(u => u with { CompileUnitOrder = null }).ToArray();
        Assert.Equal(block.Units.Select(u => u.Id), stripped.Select(u => u.Id));
        Assert.Equal(new PipelineSnapshot(42, block.Units, []).Fingerprint(), new PipelineSnapshot(42, stripped, []).Fingerprint());
    }

    [Fact]
    public void DatabaseSourcedUnitsUseTheirNetworkOrderAsTheDatabaseNumber()
        => Assert.Equal(16, new PipelineUnit(1, "FB", 2, "LAD", "", "", [], NetworkOrder: 16).DbNetworkId);

    [Fact]
    public void CallSiteAfterAnEmptyNetworkIsLocatedAtTheDatabaseNetwork()
    {
        // 端到端：归档单元 → 报警（DbNetwork）→ 清单行 → 按库逻辑单元回查
        var block = PipelineExportReader.Read(BlockWithEmptyNetwork, 42, "FB121_Cylinder.xml");
        var callUnit = block.Units.Single(u => u.Callees.Count > 0);
        var alarm = new UnderstandingAlarm(new CallSiteAlarm("M.Cyl[1].Alarm1", "Timeout", "Q1", ["I1"], "10.0", []),
            callUnit.Id, callUnit.BlockName, callUnit.NetworkOrder, "Valve[1]", "FB3100_ValveControl") { DbNetwork = callUnit.DbNetworkId };
        var rows = AlarmCatalogBuilder.Build([alarm]);
        // 库：上传解析器给 4 个 CompileUnit 依次编 1..4，调用记录挂在第 4 个
        var located = AlarmCatalogLocator.Locate(rows, [
            new(7001, "FB121_Cylinder", 1, []),
            new(7002, "FB121_Cylinder", 2, []),
            new(7003, "FB121_Cylinder", 3, []),
            new(7004, "FB121_Cylinder", 4, ["FB3100_ValveControl"]),
        ]);
        var row = Assert.Single(located);
        Assert.Equal(4, row.SourceNetwork);
        Assert.Equal(7004, row.LogicUnitId);
        // 若仍用归档序号 2，会指到库里的空网络（7002）——回查要求被调块一致，也不会挂错
        Assert.Null(AlarmCatalogLocator.Locate([row with { SourceNetwork = callUnit.NetworkOrder, LogicUnitId = null }], [
            new(7002, "FB121_Cylinder", 2, []), new(7004, "FB121_Cylinder", 4, ["FB3100_ValveControl"])])[0].LogicUnitId);
    }

    // ── C-2：管道未启用视为无清单；读库失败照常抛出 ──

    static PostgresPipelineStore Store(Dictionary<string, string?> settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    [Fact]
    public void DisabledPipelineReportsUnavailableWithoutThrowing()
    {
        var store = Store(new() { ["WebConfig:DBList:0:ConnectionString"] = "Host=h;Database=hsjm_ai_new;Username=u" });
        Assert.False(store.TryGetConnectionString(out var cs, out var reason));
        Assert.Equal("", cs);
        Assert.Equal("IndependentPipelineDisabled", reason);
    }

    [Fact]
    public void NonTestDatabaseReportsUnavailableWithoutThrowing()
    {
        var store = Store(new()
        {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new",
            ["WebConfig:DBList:0:ConnectionString"] = "Host=h;Database=hsjm_ai_new;Username=u",
        });
        Assert.False(store.TryGetConnectionString(out _, out var reason));
        Assert.Equal("TestDatabaseMismatch", reason);
    }

    [Fact]
    public void EnabledPipelineReturnsTheConnectionString()
    {
        const string connection = "Host=h;Database=hsjm_ai_new_test;Username=u";
        var store = Store(new()
        {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["WebConfig:DBList:0:ConnectionString"] = connection,
        });
        Assert.True(store.TryGetConnectionString(out var cs, out var reason));
        Assert.Equal(connection, cs);
        Assert.Null(reason);
    }

    [Fact]
    public async Task UnavailablePipelineMeansNoCatalogAndNoRead()
    {
        string? reported = null;
        var loaded = false;
        var (rows, version) = await ValveAlarmCatalogService.LoadCatalogAsync(
            () => (false, "", "IndependentPipelineDisabled"),
            (_, _) => { loaded = true; return Task.FromResult<(IReadOnlyList<AlarmCatalogRow>, string?)>(([], "v")); },
            r => reported = r, CancellationToken.None);
        Assert.Empty(rows);
        Assert.Null(version);
        Assert.False(loaded);
        Assert.Equal("IndependentPipelineDisabled", reported);
    }

    [Fact]
    public async Task DatabaseFailureWhileReadingTheCatalogStillThrows()
    {
        // 读不到 ≠ 没有：连接可用时读库失败不回退旧提取，由调用方记为整体报警读取失败
        var reported = false;
        await Assert.ThrowsAsync<NpgsqlException>(() => ValveAlarmCatalogService.LoadCatalogAsync(
            () => (true, "Host=h;Database=x_test", null),
            (_, _) => throw new NpgsqlException("connection refused"),
            _ => reported = true, CancellationToken.None));
        Assert.False(reported);
    }

    [Fact]
    public async Task AvailablePipelinePassesTheConnectionStringToTheLoader()
    {
        var row = new AlarmCatalogRow("DB.A", "Timeout", null, [], null, "FB", 1, "V", "FB_V", []);
        string? used = null;
        var (rows, version) = await ValveAlarmCatalogService.LoadCatalogAsync(
            () => (true, "cs", null),
            (cs, _) => { used = cs; return Task.FromResult<(IReadOnlyList<AlarmCatalogRow>, string?)>(([row], "v1")); },
            _ => throw new InvalidOperationException(), CancellationToken.None);
        Assert.Equal("cs", used);
        Assert.Equal("v1", version);
        Assert.Same(row, Assert.Single(rows));
    }

    // ── M-4：点表里没有的绝对地址不当 DB 路径读 ──

    [Theory]
    [InlineData("I1106.5")]
    [InlineData("%Q3000.1")]
    [InlineData("m10.0")]
    [InlineData("%IW64")]
    public void AbsoluteAddressesAreNotReadAsDbPaths(string name)
    {
        Assert.Null(ValveAlarmEvaluator.ReadKey(new ValveAlarmSignalRef(name, null, null)));
        var def = new ValveAlarmDefinition(1, name, "Timeout", "", "V", "FB", 0, null, null,
            new ValveAlarmSignalRef(name, null, null), [new ValveAlarmSignalRef(name, null, null)]);
        Assert.Empty(ValveAlarmEvaluator.BuildRequests([def]));
    }

    [Fact]
    public void DbPathsAndIoPointsAreStillRead()
    {
        Assert.Equal("DB_X.Valve[1].Q", ValveAlarmEvaluator.ReadKey(new ValveAlarmSignalRef("DB_X.Valve[1].Q", null, null)));
        Assert.NotNull(ValveAlarmEvaluator.ReadKey(new ValveAlarmSignalRef("I1106.5", "IO", "%I1106.5")));
    }
}
