using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

public class IgwTypeMapperTests
{
    [Theory]
    [InlineData("Bool", "bool")]
    [InlineData(" bool ", "bool")]
    [InlineData("Byte", "byte")]
    [InlineData("USInt", "byte")]
    [InlineData("SInt", "sInt")]
    [InlineData("Int", "int16")]
    [InlineData("UInt", "uInt16")]
    [InlineData("Word", "uInt16")]
    [InlineData("DInt", "int32")]
    [InlineData("UDInt", "uInt32")]
    [InlineData("DWord", "uInt32")]
    [InlineData("Real", "float")]
    [InlineData("LReal", "double")]
    [InlineData("Char", "s7Char")]
    public void Maps_scalar_types(string plcType, string expected)
    {
        Assert.Equal(expected, IgwTypeMapper.MapDataType(plcType, out var len));
        Assert.Null(len);
    }

    [Theory]
    [InlineData("String", "string", 254)]
    [InlineData("String[20]", "string", 20)]
    [InlineData("string[ 8 ]", "string", 8)]
    [InlineData("WString", "wString", 254)]
    [InlineData("WString[40]", "wString", 40)]
    public void Maps_string_types_with_length(string plcType, string expected, int expectedLength)
    {
        Assert.Equal(expected, IgwTypeMapper.MapDataType(plcType, out var len));
        Assert.Equal(expectedLength, len);
    }

    [Theory]
    [InlineData("\"UDT_Robot\"")]
    [InlineData("Struct")]
    [InlineData("Array[0..3] of Bool")]
    [InlineData("Array[*] of Int")]
    // Time/Date/DTL/LInt/ULInt/LWord 已于 2026-09-21 放开，移到 IgwTypeMapperTimeTypesTests
    [InlineData("")]
    [InlineData(null)]
    public void Unmappable_types_return_null(string plcType)
    {
        Assert.Null(IgwTypeMapper.MapDataType(plcType, out var len));
        Assert.Null(len);
    }

    [Theory]
    [InlineData("UInt16", "uInt16")]
    [InlineData("UInt32", "uInt32")]
    [InlineData("WString", "wString")]
    [InlineData("Int16", "int16")]
    [InlineData("SInt", "sInt")]
    [InlineData("S7Char", "s7Char")]
    public void Type_strings_match_igw_camelcase_enum_names(string igwEnumName, string mapperOutput) =>
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(igwEnumName), mapperOutput);

    [Theory]
    [InlineData("Bool", 0, 1, "DB10.DBX0.1")]
    [InlineData("Byte", 1, null, "DB10.DBB1")]
    [InlineData("USInt", 1, null, "DB10.DBB1")]
    [InlineData("Char", 3, null, "DB10.DBB3")]
    [InlineData("Int", 2, null, "DB10.DBW2")]
    [InlineData("UInt", 2, null, "DB10.DBW2")]
    [InlineData("Word", 2, null, "DB10.DBW2")]
    [InlineData("DInt", 4, null, "DB10.DBD4")]
    [InlineData("UDInt", 4, null, "DB10.DBD4")]
    [InlineData("DWord", 4, null, "DB10.DBD4")]
    [InlineData("Real", 4, null, "DB10.DBD4")]
    public void Classic_addresses(string plcType, int offsetByte, int? offsetBit, string expected) =>
        Assert.Equal(expected, IgwTypeMapper.ClassicAddress(10, plcType, offsetByte, offsetBit));

    [Theory]
    [InlineData("LReal")]
    [InlineData("String")]
    [InlineData("SInt")]
    [InlineData("\"UDT_X\"")]
    public void Classic_address_null_for_non_absolute_types(string plcType) =>
        Assert.Null(IgwTypeMapper.ClassicAddress(10, plcType, 0m, 0m));

    [Theory]
    [InlineData("%I4057.4", "I4057.4")]
    [InlineData("%QW2022", "QW2022")]
    [InlineData("%M0.0", "M0.0")]
    [InlineData("%ID100", "ID100")]
    [InlineData("%QB5", "QB5")]
    [InlineData("E1.2", "E1.2")]
    [InlineData("%I1.8", null)]
    [InlineData("%DB10.DBX0.0", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Io_addresses(string address, string expected) =>
        Assert.Equal(expected, IgwTypeMapper.IoAddress(address));

    [Theory]
    [InlineData("Bool", "bool")]
    [InlineData("Byte", "byte")]
    [InlineData("Word", "uInt16")]
    [InlineData("Int", "int16")]
    [InlineData("DWord", "uInt32")]
    [InlineData("DInt", "int32")]
    [InlineData("Real", "float")]
    [InlineData("Bool\"\"", "bool")]
    [InlineData("PD_TEL105_IN\"\"", null)]
    [InlineData("LReal", null)]
    [InlineData("String", null)]
    [InlineData(null, null)]
    public void Io_data_types(string plcType, string expected) =>
        Assert.Equal(expected, IgwTypeMapper.IoDataType(plcType));
}
