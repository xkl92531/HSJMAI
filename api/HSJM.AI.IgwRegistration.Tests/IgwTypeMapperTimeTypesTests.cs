using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 时间/长整型放开（2026-09-21）。原先 Time/Date/DTL/LInt/ULInt/LWord 一律 unmapped_type 跳过，
/// 但 IGW 全部支持（IgwDataType 枚举含 Dtl/Date/Tod/Ltod/Time/Ltime/Dt/Ldt/S5Time/LWord/Int64/UInt64）。
/// 现场 PLC1 导出实测：被跳过的这几类共 6,399 个成员，其中 Time 6,359 个——正是 G7_StepPlus_V6
/// 步骤结构里的 T / U / T_MAX / T_WARN，判断「哪一步卡住、超没超时」要看的数据。
/// </summary>
public class IgwTypeMapperTimeTypesTests
{
    [Theory]
    [InlineData("Time", "time")]
    [InlineData("LTime", "ltime")]
    [InlineData("Date", "date")]
    [InlineData("Time_Of_Day", "tod")]
    [InlineData("TOD", "tod")]
    [InlineData("LTime_Of_Day", "ltod")]
    [InlineData("LTOD", "ltod")]
    [InlineData("DTL", "dtl")]
    [InlineData("Date_And_Time", "dt")]
    [InlineData("DT", "dt")]
    [InlineData("LDT", "ldt")]
    [InlineData("S5Time", "s5Time")]
    [InlineData("LInt", "int64")]
    [InlineData("ULInt", "uInt64")]
    [InlineData("LWord", "lWord")]
    public void Time_and_long_types_map_to_igw_data_types(string plcType, string expected)
    {
        Assert.Equal(expected, IgwTypeMapper.MapDataType(plcType, out var len));
        Assert.Null(len);
    }

    [Theory]
    [InlineData("Time", "DB5.DBD8")]              // 4 字节 → DBD
    [InlineData("Time_Of_Day", "DB5.DBD8")]       // 4 字节 → DBD
    [InlineData("Date", "DB5.DBW8")]              // 2 字节 → DBW
    [InlineData("S5Time", "DB5.DBW8")]            // 2 字节 → DBW
    public void Short_time_types_keep_classic_addressing(string plcType, string expected)
    {
        // 有经典地址 → 含这些成员的块不会因此从 S7 翻到 S7Plus
        Assert.Equal(expected, IgwTypeMapper.ClassicAddress(5, plcType, 8m, null));
    }

    [Theory]
    [InlineData("LTime")]
    [InlineData("DTL")]
    [InlineData("Date_And_Time")]
    [InlineData("LDT")]
    [InlineData("LInt")]
    [InlineData("LWord")]
    public void Long_types_have_no_classic_form(string plcType)
    {
        // 8/12 字节，DBD 放不下 → 只能走 S7Plus 符号地址（整块判定会把这些块归 S7Plus）
        Assert.Null(IgwTypeMapper.ClassicAddress(5, plcType, 8m, null));
    }
}
