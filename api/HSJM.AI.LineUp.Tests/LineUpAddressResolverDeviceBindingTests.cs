using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Resolution;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// 设备 → PLC 项目解析的关联优先级回归：
/// 项目详情页「绑定 PLC 设备」的 ID（ai_plc_project_new.plcdeviceid）优先，
/// 设备 ProjectName 名字匹配降级为兜底——名字漂移（项目叫 PLC1六面检测、设备只写 六面检测）
/// 不再让预览/提交链路 Blocking（PlcProjectMissing）。
/// </summary>
public sealed class LineUpAddressResolverDeviceBindingTests
{
    private const long LineId = 10;
    private const long DeviceId = 90;
    private const long ProjectId = 77;
    private const long GraphBlockId = 501;
    private const long CallerBlockId = 502;
    private const long CallerLogicUnitId = 701;
    private const long InstanceDbBlockId = 601;
    private const string GraphBlockName = "FB150_OP1030_电芯上线扫码枪自动流程";
    private const string InstanceDbName = "DB150_OP1030";

    private static LineUpGraphPreview BuildPreview() => new()
    {
        BlockName = GraphBlockName,
        BlockNumber = 150,
        Sequences = [new LineUpGraphPreviewSequence { SourceKey = "seq1" }],
    };

    private static LineUpPlcResolutionData BuildResolutionData() => new(
        [new LineUpProjectLookup(ProjectId, "PLC1六面检测")],
        [
            new LineUpBlockLookup(GraphBlockId, ProjectId, GraphBlockName, "FB", 150, "GRAPH"),
            new LineUpBlockLookup(InstanceDbBlockId, ProjectId, InstanceDbName, "InstanceDB", 150, "DB"),
            new LineUpBlockLookup(CallerBlockId, ProjectId, "FB100_Main", "FB", 100, "LAD"),
        ],
        [
            new LineUpCallSiteLookup(
                1, ProjectId, CallerBlockId, CallerLogicUnitId,
                GraphBlockName, "FB", InstanceDbName, LineUpCallBindingParseStatus.Parsed),
        ]);

    /// <summary>名字匹配路径的假仓储：默认抛异常证明“不应被调用”，可注入期望数据。</summary>
    private sealed class FakePlcMetadataRepository(
        LineUpPlcResolutionData? nameData = null, bool deviceBound = true) : ILineUpPlcMetadataRepository
    {
        public int NameLoadCount { get; private set; }
        public int DeviceLoadCount { get; private set; }

        public Task<LineUpPlcResolutionData> LoadResolutionDataAsync(
            string projectName, string graphBlockName, int graphBlockNumber, CancellationToken cancellationToken)
        {
            NameLoadCount++;
            return Task.FromResult(nameData ?? throw new InvalidOperationException(
                "名字匹配路径不应被调用：设备已通过 plcdeviceid 绑定项目"));
        }

        public Task<LineUpPlcResolutionData> LoadResolutionDataByDeviceIdAsync(
            long deviceId, string graphBlockName, int graphBlockNumber, CancellationToken cancellationToken)
        {
            DeviceLoadCount++;
            return Task.FromResult(deviceBound
                ? BuildResolutionData()
                : new LineUpPlcResolutionData([], [], []));
        }

        public Task<LineUpVariableLookupData> LoadVariableDataAsync(
            long plcProjectId, long instanceDbBlockId,
            IReadOnlyCollection<string> canonicalVariablePaths, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LineUpGraphSymbolLookupData> LoadGraphSymbolDataAsync(
            long plcProjectId, IReadOnlyCollection<LineUpGraphSymbolLookupRequest> symbols,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LineUpGraphStepLedgerLookup> LoadGraphStepLedgerAsync(
            long plcProjectId, string graphBlockName, CancellationToken cancellationToken)
            => Task.FromResult(new LineUpGraphStepLedgerLookup(true, 26, 26, "DB160.RT_DATA.EXEC_BITS.EXEC_BITS"));
    }

    private sealed class FakeHierarchyRepository : ILineUpHierarchyRepository
    {
        private readonly string? deviceProjectName;

        public FakeHierarchyRepository(string? deviceProjectName)
        {
            this.deviceProjectName = deviceProjectName;
        }

        public Task<LineUpHierarchyLookupContext> LoadContextAsync(
            long lineId, long deviceId, IReadOnlyCollection<string> sequenceSourceKeys,
            CancellationToken cancellationToken, string? graphBlockName = null)
            => Task.FromResult(new LineUpHierarchyLookupContext(
                new LineUpLineLookup(lineId, "L1", "产线一"),
                new LineUpDeviceLookup(deviceId, lineId, "PLC1", deviceProjectName),
                [], [], [], [], []));

        public Task<long> EnsureProcessAsync(
            LineUpLineLookup line, string code, string name, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> EnsureWorkStationAsync(
            LineUpLineLookup line, LineUpProcessLookup process, string code, string name,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> EnsureSubStationAsync(
            LineUpLineLookup line, LineUpProcessLookup process, LineUpStationLookup station,
            LineUpDeviceLookup device, string code, string name, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> EnsureGraphSequenceAsync(
            string sourceKey, string sourceName, string displayName, long plcProjectId,
            long graphBlockId, long? instanceDbBlockId, string? sourceHash,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> InsertSubStationBindingAsync(
            long lineId, long processId, long stationId, long subStationId, long deviceId,
            long plcProjectId, long sequenceId, long graphBlockId, long? instanceDbBlockId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task DeviceBinding_WinsOverMismatchedProjectName()
    {
        // 设备 ProjectName=六面检测 与项目名 PLC1六面检测 漂移；plcdeviceid 已绑定 → 仍应 Ready
        var plcRepository = new FakePlcMetadataRepository();
        var resolver = new LineUpAddressResolver(
            new FakeHierarchyRepository("六面检测"),
            plcRepository);

        var result = await resolver.ResolveAsync(LineId, DeviceId, BuildPreview(), CancellationToken.None);

        Assert.True(result.CanCommitStructure, string.Join(";", result.Issues.Select(x => x.IssueType)));
        Assert.Equal(ProjectId, result.PlcProjectId);
        Assert.Equal(GraphBlockId, result.GraphBlockId);
        Assert.Equal(InstanceDbBlockId, result.InstanceDbBlockId);
        Assert.Equal(1, plcRepository.DeviceLoadCount);
        Assert.Equal(0, plcRepository.NameLoadCount);
        var projectCandidate = result.Candidates.Single(x => x.CandidateKind == "PlcProject");
        Assert.Contains("DeviceBindingId", projectCandidate.EvidenceCodes);
    }

    [Fact]
    public async Task FallsBackToProjectName_WhenDeviceNotBound()
    {
        // 设备无 plcdeviceid 绑定（ByDeviceId 空）→ 回退名字匹配路径
        var nameData = new LineUpPlcResolutionData(
            [new LineUpProjectLookup(ProjectId, "六面检测")],
            BuildResolutionData().Blocks,
            BuildResolutionData().CallSites);
        var plcRepository = new FakePlcMetadataRepository(nameData, deviceBound: false);
        var resolver = new LineUpAddressResolver(
            new FakeHierarchyRepository("六面检测"),
            plcRepository);

        var result = await resolver.ResolveAsync(LineId, DeviceId, BuildPreview(), CancellationToken.None);

        Assert.True(result.CanCommitStructure, string.Join(";", result.Issues.Select(x => x.IssueType)));
        Assert.Equal(ProjectId, result.PlcProjectId);
        Assert.Equal(1, plcRepository.NameLoadCount);
        var projectCandidate = result.Candidates.Single(x => x.CandidateKind == "PlcProject");
        Assert.Contains("ExactProjectName", projectCandidate.EvidenceCodes);
    }

    [Fact]
    public async Task Blocked_WhenNeitherDeviceBindingNorNameMatches()
    {
        // 两条路都查不到项目 → Blocking PlcProjectMissing（保持旧语义）
        var nameData = new LineUpPlcResolutionData([], [], []);
        var resolver = new LineUpAddressResolver(
            new FakeHierarchyRepository("六面检测"),
            new FakePlcMetadataRepository(nameData, deviceBound: false));

        var result = await resolver.ResolveAsync(LineId, DeviceId, BuildPreview(), CancellationToken.None);

        Assert.False(result.CanCommitStructure);
        Assert.Equal(LineUpStructuralStatus.Blocked, result.StructuralStatus);
        Assert.Contains(result.Issues, x => x.IssueType == "PlcProjectMissing");
    }
}
