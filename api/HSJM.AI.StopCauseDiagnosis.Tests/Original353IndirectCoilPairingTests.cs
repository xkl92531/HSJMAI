#nullable enable
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 间接接线：阀门 FB 的 QFwd/QBkd 未绑定，电磁阀线圈另起一个网络，只由送进 FwdAuto/BkdAuto 引脚的同一个信号驱动
/// （实测 FB220_OP80Manual / FB320_OP90Manual 共 7 处）。线圈即该方向命令 → 线圈得电时 IWP/IHP 的 %I 反馈必须到位。
/// </summary>
public class Original353IndirectCoilPairingTests
{
    private const string FwdCmd = "DB245_OP80Cylinder.Control.OP080_CYL04_FwdAuto";
    private const string BkdCmd = "DB245_OP80Cylinder.Control.OP080_CYL04_BkdAuto";

    private static Dictionary<string, PLC_IOTagEntity> Tags() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["YV_W"] = new() { TagName = "YV_W", Address = "%Q2019.1", DataType = "Bool" },
        ["YV_H"] = new() { TagName = "YV_H", Address = "%Q2019.0", DataType = "Bool" },
        ["YV_X"] = new() { TagName = "YV_X", Address = "%Q2019.2", DataType = "Bool" },
        ["CYL_W"] = new() { TagName = "CYL_W", Address = "%I2018.1", DataType = "Bool" },
        ["CYL_H"] = new() { TagName = "CYL_H", Address = "%I2018.0", DataType = "Bool" },
    };

    private static PldAnchorBlock Valve(string? qFwd, string? qBkd, params string[] fwdAutoContacts)
    {
        var block = new PldAnchorBlock { Type = PldAnchorType.FbCall, Anchor = "Valve[4]", TypeText = "FB 调用: FB3002_Valve" };
        if (qFwd != null) block.OutPins["QFwd"] = qFwd;
        if (qBkd != null) block.OutPins["QBkd"] = qBkd;
        block.InPinContacts["IWP"] = [new("NO", "CYL_W"), new("NO", "DB210_OP080_User.Cmd.ByPass_CYL[7]")];
        block.InPinContacts["IHP"] = [new("NO", "CYL_H"), new("NO", "DB210_OP080_User.Cmd.ByPass_CYL[8]")];
        block.InPinContacts["FwdAuto"] = fwdAutoContacts.Select(name => new ContactRef("NO", name)).ToList();
        block.InPinContacts["BkdAuto"] = [new("NO", BkdCmd)];
        return block;
    }

    private static PldAnchorBlock Coil(string target, params (string Polarity, string Name)[] contacts)
    {
        var block = new PldAnchorBlock { Type = PldAnchorType.Coil, Anchor = target, TypeText = "LAD/FBD 网络 (FlgNet)" };
        block.DirectContacts.AddRange(contacts.Select(c => new ContactRef(c.Polarity, c.Name)));
        return block;
    }

    private static List<(string Op, string[] Sources, string[] Targets, string[] Members)> Rules(List<PldAnchorBlock> anchors)
    {
        using var json = JsonDocument.Parse(PldStructuralConstraintAugmenter.AugmentFromBlocks(anchors, Tags()));
        return json.RootElement.EnumerateArray().Select(rule =>
        {
            var signals = rule.GetProperty("signals").EnumerateArray()
                .Select(s => (Name: s.GetProperty("signalName").GetString()!, Role: s.GetProperty("signalRole").GetString()!)).ToArray();
            return (rule.GetProperty("operator").GetString()!,
                signals.Where(s => s.Role == "Source").Select(s => s.Name).ToArray(),
                signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray(),
                signals.Where(s => s.Role == "Member").Select(s => s.Name).ToArray());
        }).ToList();
    }

    private static bool HasPairing(List<(string Op, string[] Sources, string[] Targets, string[] Members)> rules, string source, string target)
        => rules.Any(r => r.Op == "IMPLIES" && r.Sources.SequenceEqual([source]) && r.Targets.SequenceEqual([target]));

    [Fact]
    public void CoilDrivenOnlyByTheFwdAutoSourcePairsWithTheForwardFeedback()
    {
        var rules = Rules([Coil("YV_W", ("NO", FwdCmd)), Valve(qFwd: null, qBkd: "YV_H", FwdCmd)]);

        Assert.True(HasPairing(rules, "YV_W", "CYL_W"));
        Assert.True(HasPairing(rules, "YV_H", "CYL_H")); // 既有 QBkd→IHP 不受影响
    }

    [Fact]
    public void IndirectCoilIsNotMadeMutuallyExclusiveWithTheReverseOutput()
    {
        // 线圈绕开了 FB 内部联锁，程序证明不了它与 QBkd 不同时得电
        var rules = Rules([Coil("YV_W", ("NO", FwdCmd)), Valve(qFwd: null, qBkd: "YV_H", FwdCmd)]);

        Assert.DoesNotContain(rules, r => r.Op == "AT_MOST_ONE" && r.Members.Contains("YV_W"));
    }

    [Fact]
    public void BackwardDirectionIsHandledSymmetrically()
    {
        var rules = Rules([Coil("YV_H", ("NO", BkdCmd)), Valve(qFwd: "YV_W", qBkd: null, FwdCmd)]);

        Assert.True(HasPairing(rules, "YV_H", "CYL_H"));
    }

    [Fact]
    public void UnboundPinTextIsTreatedAsUnbound()
    {
        var rules = Rules([Coil("YV_W", ("NO", FwdCmd)), Valve(qFwd: "(未绑定)", qBkd: "YV_H", FwdCmd)]);

        Assert.True(HasPairing(rules, "YV_W", "CYL_W"));
    }

    [Fact]
    public void TwoCoilsOnTheSameCommandAreAmbiguousAndProduceNothing()
    {
        var rules = Rules([Coil("YV_W", ("NO", FwdCmd)), Coil("YV_X", ("NO", FwdCmd)),
            Valve(qFwd: null, qBkd: "YV_H", FwdCmd)]);

        Assert.DoesNotContain(rules, r => r.Op == "IMPLIES" && (r.Sources.Contains("YV_W") || r.Sources.Contains("YV_X")));
    }

    [Fact]
    public void FwdAutoWithMoreThanOneContactProducesNothing()
    {
        var rules = Rules([Coil("YV_W", ("NO", FwdCmd)), Valve(qFwd: null, qBkd: "YV_H", FwdCmd, "Other.Cmd")]);

        Assert.False(HasPairing(rules, "YV_W", "CYL_W"));
    }

    [Fact]
    public void CoilWithExtraConditionsOrInvertedContactProducesNothing()
    {
        var extra = Rules([Coil("YV_W", ("NO", FwdCmd), ("NO", "Other.Permit")), Valve(null, "YV_H", FwdCmd)]);
        var inverted = Rules([Coil("YV_W", ("NC", FwdCmd)), Valve(null, "YV_H", FwdCmd)]);

        Assert.False(HasPairing(extra, "YV_W", "CYL_W"));
        Assert.False(HasPairing(inverted, "YV_W", "CYL_W"));
    }

    [Fact]
    public void SetOrResetCoilIsNotACommandCoil()
    {
        var coil = Coil("YV_W", ("NO", FwdCmd));
        coil.TypeText = "LAD/FBD 网络 (FlgNet) — 置位线圈 SCoil";

        Assert.False(HasPairing(Rules([coil, Valve(null, "YV_H", FwdCmd)]), "YV_W", "CYL_W"));
    }

    [Fact]
    public void BoundQFwdKeepsTheExistingRuleAndIgnoresOtherCoilsOnTheCommand()
    {
        var rules = Rules([Coil("YV_X", ("NO", FwdCmd)), Valve(qFwd: "YV_W", qBkd: "YV_H", FwdCmd)]);

        Assert.True(HasPairing(rules, "YV_W", "CYL_W"));
        Assert.DoesNotContain(rules, r => r.Op == "IMPLIES" && r.Sources.Contains("YV_X"));
    }

    [Fact]
    public void ReverseOutputDrivingBothCoilsProducesNoForwardPairing()
    {
        // 实测第二种写法：QBkd 同时驱动 YV_W 与 YV_H，QFwd 什么都不驱动 —— 前进方向没有可证明的输出
        var rules = Rules([Coil("YV_W", ("NO", "Valve[4].QBkd")), Coil("YV_H", ("NO", "Valve[4].QBkd")),
            Valve(qFwd: null, qBkd: null, FwdCmd)]);

        Assert.False(HasPairing(rules, "YV_W", "CYL_W"));
        Assert.DoesNotContain(rules, r => r.Op == "AT_MOST_ONE" && r.Members.Contains("YV_W") && r.Members.Contains("YV_H"));
    }
}
