#nullable enable
using System.Text.Json;
using HSJM.AI.IDbService;
using Xunit;
using Legacy = HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using Old = HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 位置传感器组互斥：只有 FB 调用上「恰好 3 个」只接 1 个常开 %I 的输入引脚、且同字节时才成立
/// （一根伺服轴的负限位/原点/正限位就是它的全部离散输入）。4 个以上说明这些输入属于多台设备或多种状态：
/// 实测 2026-09-30 —— FB3100_ValveControl 一次调用带两只气缸（4 个）、FB3430_GearSync 主从两根轴（6 个）、
/// FB6040_TOS9301 耐压测试仪状态（7~8 个）、FB12350_Gate_Display_FB 按钮+门栓（4 个），全部误报；
/// FB7010_V90PN（3 个）正确。两份补全器（XMLV2 与老方法）规则一致。
/// </summary>
public class PositionGroupMutexTests
{
    private static Dictionary<string, PLC_IOTagEntity> Tags(params (string Name, string Address)[] tags) =>
        tags.ToDictionary(t => t.Name, t => new PLC_IOTagEntity { TagName = t.Name, Address = t.Address, DataType = "Bool" },
            StringComparer.OrdinalIgnoreCase);

    private static readonly (string Pin, string Signal, string Address)[] V90 =
    [
        ("Stop_cam_minus", "SQ17", "%I1006.0"), ("Servo_HP", "SQ18", "%I1006.1"), ("Stop_cam_plus", "SQ19", "%I1006.2"),
    ];

    private static readonly (string Pin, string Signal, string Address)[] TwoCylinderValve =
    [
        ("I_BkdPos1", "CYL03_1_CP", "%I1002.4"), ("I_FwdPos1", "CYL03_1_OP", "%I1002.5"),
        ("I_BkdPos2", "CYL03_2_CP", "%I1002.6"), ("I_FwdPos2", "CYL03_2_OP", "%I1002.7"),
    ];

    // 三个同字节 + 第四个在别的字节（安全门：请求按钮/复位按钮/门栓反馈 + 另一路输入）
    private static readonly (string Pin, string Signal, string Address)[] ThreeInByteOneElsewhere =
    [
        ("i_RequestEnterPB", "Req_PB", "%I102.1"), ("i_ResetPB", "Reset_PB", "%I102.2"),
        ("i_DoorBolt1", "RFeedback", "%I102.0"), ("i_DoorBolt2", "RFeedback2", "%I105.3"),
    ];

    private static readonly (string Pin, string Signal, string Address)[] ThreeAcrossBytes =
    [
        ("Stop_cam_minus", "SQ17", "%I1006.0"), ("Servo_HP", "SQ18", "%I1007.1"), ("Stop_cam_plus", "SQ19", "%I1006.2"),
    ];

    public static TheoryData<string, bool> Cases() => new()
    {
        { nameof(V90), true },
        { nameof(TwoCylinderValve), false },
        { nameof(ThreeInByteOneElsewhere), false },
        { nameof(ThreeAcrossBytes), false },
    };

    private static (string Pin, string Signal, string Address)[] Pins(string name) => name switch
    {
        nameof(V90) => V90,
        nameof(TwoCylinderValve) => TwoCylinderValve,
        nameof(ThreeInByteOneElsewhere) => ThreeInByteOneElsewhere,
        _ => ThreeAcrossBytes,
    };

    private static int PositionGroups(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Count(r => r.GetProperty("ruleName").GetString()!.EndsWith("_位置组互斥", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Cases))]
    public void XmlV2AugmenterFiresOnlyForExactlyThreeSinglePositionInputs(string fb, bool expected)
    {
        var pins = Pins(fb);
        var block = new Legacy.PldAnchorBlock { Type = Legacy.PldAnchorType.FbCall, Anchor = "Inst", TypeText = "FB 调用: X" };
        foreach (var p in pins) block.InPinContacts[p.Pin] = [new Legacy.ContactRef("NO", p.Signal)];
        var json = Legacy.PldStructuralConstraintAugmenter.AugmentFromBlocks([block], Tags(pins.Select(p => (p.Signal, p.Address)).ToArray()));

        Assert.Equal(expected ? 1 : 0, PositionGroups(json));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LegacyAugmenterFiresOnlyForExactlyThreeSinglePositionInputs(string fb, bool expected)
    {
        var pins = Pins(fb);
        var block = new Old.PldAnchorBlock { Type = Old.PldAnchorType.FbCall, Anchor = "Inst", TypeText = "FB 调用: X" };
        foreach (var p in pins) block.InPinContacts[p.Pin] = [new Old.ContactRef("NO", p.Signal)];
        var json = Old.PldStructuralConstraintAugmenter.AugmentFromBlocks([block], Tags(pins.Select(p => (p.Signal, p.Address)).ToArray()));

        Assert.Equal(expected ? 1 : 0, PositionGroups(json));
    }
}
