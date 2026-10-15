#nullable enable
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class ValveAlarmStationResolverTests
{
    private static PLC_StationLogicUnitBindingEntity Lu(long luId, string code, decimal? conf = 1m) =>
        new() { LogicUnitId = luId, StationCode = code, Confidence = conf };
    private static PLC_StationBlockBindingEntity Blk(long? blockId, string name, string code, decimal? conf = 1m) =>
        new() { BlockId = blockId, BlockName = name, StationCode = code, Confidence = conf };

    private static string? Resolve(
        IReadOnlyList<PLC_StationLogicUnitBindingEntity> lu,
        IReadOnlyList<PLC_StationBlockBindingEntity> blk,
        string? blockStation = null,
        HashSet<string>? allowed = null) =>
        ValveAlarmStationResolver.Resolve(10, 20, "FB220_OP20Manual", lu, blk, blockStation, allowed);

    [Fact]
    public void LogicUnitBindingWins()
    {
        Assert.Equal("OP020", Resolve([Lu(10, "OP020")], [Blk(20, "FB220_OP20Manual", "OP030")], "OP040"));
    }

    [Fact]
    public void FallsBackToBlockStationCodeThenSuggestedBlockBinding()
    {
        // 2026-10-14：没有 reason 的旧块绑定算自动建议，排在块工位字段之后（手动绑定 → 块工位 → 自动建议）
        Assert.Equal("OP040", Resolve([], [Blk(null, "fb220_op20manual", "OP030")], "OP040"));
        Assert.Equal("OP030", Resolve([], [Blk(null, "fb220_op20manual", "OP030")]));
        Assert.Equal("OP040", Resolve([], [], "OP040"));
    }

    [Fact]
    public void ManualBlockBindingWinsOverLogicUnitAndBlockStation()
    {
        var manual = Blk(20, "FB220_OP20Manual", "OP021");
        manual.Reason = "手动绑定";

        Assert.Equal("OP021", Resolve([Lu(10, "OP020")], [manual], "OP040"));
    }

    [Fact]
    public void RejectedBlockBindingIsNeverUsed()
    {
        var rejected = Blk(20, "FB220_OP20Manual", "OP030");
        rejected.Reason = "已拒绝";

        Assert.Null(Resolve([], [rejected]));
    }

    [Fact]
    public void HighestConfidenceWinsAndTieOnDifferentStationsFallsThrough()
    {
        Assert.Equal("OP021", Resolve([Lu(10, "OP020", 0.5m), Lu(10, "OP021", 0.9m)], []));
        Assert.Equal("OP030", Resolve([Lu(10, "OP020", 0.9m), Lu(10, "OP021", 0.9m)], [Blk(20, "x", "OP030")]));
    }

    [Fact]
    public void StationsOutsideThisPlcAreIgnored()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OP030" };

        Assert.Equal("OP030", Resolve([Lu(10, "OP020")], [Blk(20, "x", "OP030")], null, allowed));
        Assert.Null(Resolve([Lu(10, "OP020")], [], "OP099", allowed));
    }

    [Fact]
    public void BindingsOfOtherLogicUnitsAreNotUsed()
    {
        Assert.Null(Resolve([Lu(11, "OP020")], []));
    }
}
