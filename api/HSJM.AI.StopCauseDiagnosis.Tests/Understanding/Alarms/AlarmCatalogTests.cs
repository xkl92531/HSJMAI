#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Alarms;

public class AlarmCatalogTests
{
    static UnderstandingAlarm A(string symbol, long unit, string output = "Q1", params string[] feedback) =>
        new(new CallSiteAlarm(symbol, "Timeout", output, feedback, "10.0", ["FB/5/1"]), unit, "FB121_Cylinder", 17, "Valve[4]", "FB3100_ValveControl");

    [Fact]
    public void DeduplicatesBySymbolKeepingTheLowestUnitAndSorts()
    {
        var rows = AlarmCatalogBuilder.Build([A("M.Cyl[2].Alarm1", 30), A("m.cyl[2].alarm1", 10), A("M.Cyl[1].Alarm1", 20, "Q2", "I1")]);
        Assert.Equal(new[] { "M.Cyl[1].Alarm1", "m.cyl[2].alarm1" }, rows.Select(r => r.AlarmSymbol).ToArray());
        var first = rows[0];
        Assert.Equal("Q2", first.OutputSignal);
        Assert.Equal(new[] { "I1" }, first.FeedbackSignals);
        Assert.Equal("Valve[4]", first.SourceInstance);
        Assert.Equal("FB3100_ValveControl", first.Callee);
    }

    [Fact]
    public void EmptyOutputIsStoredAsNull()
    {
        var conflict = new UnderstandingAlarm(new CallSiteAlarm("M.X.Alarm3", "PositionConflict", "", ["I1", "I2"], "2", ["FB/7/1"]),
            5, "FB121_Cylinder", 17, "Valve[4]", "FB3100_ValveControl");
        Assert.Null(AlarmCatalogBuilder.Build([conflict])[0].OutputSignal);
    }

    [Fact]
    public void PreconditionsAreBypassSignalsUpperCasedDistinctAndSorted()
    {
        var alarm = new UnderstandingAlarm(
            new CallSiteAlarm("M.X.Alarm1", "Timeout", "Q1", ["I1"], "10.0", ["FB/5/1"]) { Bypass = ["db.b", "DB.A", "db.b"] },
            5, "FB121_Cylinder", 17, "Valve[4]", "FB3100_ValveControl");
        Assert.Equal(new[] { "DB.A", "DB.B" }, AlarmCatalogBuilder.Build([alarm])[0].Preconditions);
    }

    [Fact]
    public void PreconditionsDefaultToEmpty()
    {
        Assert.Empty(AlarmCatalogBuilder.Build([A("M.Y.Alarm1", 1)])[0].Preconditions);
    }

    [Fact]
    public void FeedbackGroupsAreCopiedFromTheAlarm()
    {
        var alarm = new UnderstandingAlarm(
            new CallSiteAlarm("M.X.Alarm1", "Timeout", "Q1", ["S1", "S2", "S3"], "10.0", ["FB/5/1"])
            { FeedbackGroups = [["S1", "S2"], ["S3"]] },
            5, "FB121_Cylinder", 17, "Valve[4]", "FB3100_ValveControl");
        var row = AlarmCatalogBuilder.Build([alarm])[0];
        Assert.Equal(new[] { new[] { "S1", "S2" }, new[] { "S3" } }, row.FeedbackGroups.Select(g => g.ToArray()).ToArray());
        Assert.Empty(AlarmCatalogBuilder.Build([A("M.Y.Alarm1", 1)])[0].FeedbackGroups);
    }

    [Fact]
    public void SourceNetworkIsTheDatabaseNetworkNumberNotTheArchiveOrder()
    {
        // 归档根元素序号 15（空网络不计）≠ 库 networkid 16（每个 CompileUnit 计 1）：清单存库口径
        var alarm = new UnderstandingAlarm(new CallSiteAlarm("M.X.Alarm1", "Timeout", "Q1", ["I1"], "10.0", ["FB/5/1"]),
            5, "FB121_Cylinder", 15, "Valve[4]", "FB3100_ValveControl") { DbNetwork = 16 };
        var row = AlarmCatalogBuilder.Build([alarm])[0];
        Assert.Equal(16, row.SourceNetwork);
        Assert.Null(row.LogicUnitId);
    }

    static AlarmCatalogRow LocRow(int? network, string callee = "FB3100_ValveControl", string block = "FB121_Cylinder") =>
        new("M.X.Alarm1", "Timeout", "Q1", ["I1"], "10.0", block, network, "Valve[4]", callee, []);

    [Fact]
    public void LocatorFillsTheLogicUnitThatCallsTheCalleeAtTheSameNetwork()
    {
        var located = AlarmCatalogLocator.Locate([LocRow(16)], [
            new(9015, "fb121_cylinder", 15, ["FB3100_ValveControl"]),
            new(9016, "FB121_Cylinder", 16, ["fb3100_valvecontrol"]),
        ]);
        Assert.Equal(9016, Assert.Single(located).LogicUnitId);
    }

    [Fact]
    public void LocatorLeavesTheRowUnlocatedWhenTheUnitAtThatNetworkDoesNotCallTheCallee()
    {
        // 编号对不上时（例如旧口径的网络号指到了前一个说明段），该段没有对被调块的调用：不定位
        var located = AlarmCatalogLocator.Locate([LocRow(15)], [
            new(9015, "FB121_Cylinder", 15, []),
            new(9016, "FB121_Cylinder", 16, ["FB3100_ValveControl"]),
        ]);
        Assert.Null(Assert.Single(located).LogicUnitId);
    }

    [Fact]
    public void LocatorLeavesTheRowUnlocatedWhenSeveralUnitsMatch()
    {
        var located = AlarmCatalogLocator.Locate([LocRow(16)], [
            new(9016, "FB121_Cylinder", 16, ["FB3100_ValveControl"]),
            new(9116, "fb121_CYLINDER", 16, ["FB3100_ValveControl"]),
        ]);
        Assert.Null(Assert.Single(located).LogicUnitId);
    }

    [Fact]
    public void LocatorNeedsANetworkNumber()
    {
        var located = AlarmCatalogLocator.Locate([LocRow(null)], [new(9016, "FB121_Cylinder", 16, ["FB3100_ValveControl"])]);
        Assert.Null(Assert.Single(located).LogicUnitId);
    }

    [Fact]
    public void DdlCreatesTheCatalogTableWithAProjectScopedKey()
    {
        Assert.Contains("logic_unit_id bigint NULL", AlarmCatalogStore.Ddl);
        Assert.Contains("ADD COLUMN IF NOT EXISTS logic_unit_id bigint NULL", AlarmCatalogStore.Ddl);
        Assert.Contains("plc_independent.alarm_catalog", AlarmCatalogStore.Ddl);
        Assert.Contains("PRIMARY KEY (project_id, alarm_symbol)", AlarmCatalogStore.Ddl);
        Assert.Contains("feedback_signals jsonb", AlarmCatalogStore.Ddl);
        Assert.Contains("preconditions jsonb", AlarmCatalogStore.Ddl);
        Assert.Contains("ADD COLUMN IF NOT EXISTS preconditions jsonb", AlarmCatalogStore.Ddl);
        Assert.Contains("feedback_groups jsonb NOT NULL DEFAULT '[]'", AlarmCatalogStore.Ddl);
        Assert.Contains("ADD COLUMN IF NOT EXISTS feedback_groups jsonb NOT NULL DEFAULT '[]'", AlarmCatalogStore.Ddl);
    }

    [Fact]
    public void ContractAlarmWinsOverALatchedBitWithTheSameSymbol()
    {
        var latched = new UnderstandingAlarm(new CallSiteAlarm("DB_A.Err1", "Latched", "", [], null, ["置位：FB_A 网络 1：X"]), 1, "FB_A", 1, "", "");
        var timeout = new UnderstandingAlarm(new CallSiteAlarm("db_a.err1", "Timeout", "Q1", ["I1"], "10.0", ["FB/5/1"]), 9, "FB121_Cylinder", 17, "Valve[4]", "FB3100_ValveControl");
        var row = Assert.Single(AlarmCatalogBuilder.Build([latched, timeout]));
        Assert.Equal("Timeout", row.Kind);
    }

    [Fact]
    public void LatchedRowsWithoutCalleeAreLocatedByBlockAndNetworkAlone()
    {
        var row = new AlarmCatalogRow("DB_A.Err1", "Latched", null, [], null, "FB_Alarm", 7, "", "", []);
        Assert.Equal(9007, Assert.Single(AlarmCatalogLocator.Locate([row], [new(9007, "fb_alarm", 7, []), new(9008, "FB_Alarm", 8, [])])).LogicUnitId);
        // 同块同网络号不唯一 → 不定位
        Assert.Null(Assert.Single(AlarmCatalogLocator.Locate([row], [new(9007, "FB_Alarm", 7, []), new(9107, "FB_ALARM", 7, [])])).LogicUnitId);
    }

    [Fact]
    public void FallbackTextIsCarriedToTheRowAndStored()
    {
        var alarm = new UnderstandingAlarm(new CallSiteAlarm("CylDB.1.Cylinder[3].Alarm3", "Latched", "", [], null, ["调用：…"]) { Text = " 缩回到位超时 " },
            60, "FC_Caller", 7, "Valve[3]", "FB_Valve");
        Assert.Equal("缩回到位超时", AlarmCatalogBuilder.Build([alarm])[0].FallbackText);
        Assert.Null(AlarmCatalogBuilder.Build([A("M.Y.Alarm1", 1)])[0].FallbackText);
        Assert.Contains("ADD COLUMN IF NOT EXISTS fallback_text text NULL", AlarmCatalogStore.Ddl);
    }
}
