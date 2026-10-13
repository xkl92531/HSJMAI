using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 2026-09-22 只追加注册：根组内容指纹。指纹一致 → 整根跳过、一个点都不发；
/// 不一致 → 发该根的 items（upsert 更新），但**永不发 replaceGroups**（不删除）。
///
/// ⚠️ 指纹必须覆盖 IGW 那条 UPDATE 会写的每一个字段：
///   UPDATE tags SET group_id,name,canonical_path,address,data_type,access,scan_rate_ms,string_length...
/// 漏掉任何一个，那个字段以后就"改了不生效"——今天的工步块提速就只改了 scan_rate_ms，
/// 若指纹不含它，这次改动会被判成"没变"而永远跳过。
/// </summary>
public sealed class IgwRootFingerprintTests
{
    private static IgwBulkItem Item(
        string name = "A", string address = "DB1.DBX0.0", string dataType = "bool",
        int scanRate = 1000, int? stringLength = null, string description = null,
        params string[] groupPath) =>
        new()
        {
            GroupPath = new List<string>(groupPath.Length > 0 ? groupPath : new[] { "DB1" }),
            Name = name,
            Address = address,
            DataType = dataType,
            ScanRateMs = scanRate,
            StringLength = stringLength,
            Description = description,
        };

    [Fact]
    public void Same_content_gives_the_same_fingerprint()
    {
        Assert.Equal(
            IgwRootFingerprint.Compute(new[] { Item(), Item(name: "B") }),
            IgwRootFingerprint.Compute(new[] { Item(), Item(name: "B") }));
    }

    [Fact]
    public void An_empty_root_has_a_stable_fingerprint()
    {
        Assert.Equal(IgwRootFingerprint.Compute(Array.Empty<IgwBulkItem>()),
                     IgwRootFingerprint.Compute(Array.Empty<IgwBulkItem>()));
        Assert.False(string.IsNullOrEmpty(IgwRootFingerprint.Compute(Array.Empty<IgwBulkItem>())));
    }

    /// <summary>点的先后次序不该影响判定——规划器的排序变了不代表 IGW 上的内容变了。</summary>
    [Fact]
    public void Order_does_not_matter()
    {
        Assert.Equal(
            IgwRootFingerprint.Compute(new[] { Item(name: "A"), Item(name: "B") }),
            IgwRootFingerprint.Compute(new[] { Item(name: "B"), Item(name: "A") }));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("address")]
    [InlineData("dataType")]
    [InlineData("scanRate")]
    [InlineData("stringLength")]
    [InlineData("description")]
    [InlineData("groupPath")]
    public void Every_field_the_gateway_updates_changes_the_fingerprint(string field)
    {
        var baseline = IgwRootFingerprint.Compute(new[] { Item() });
        var changed = field switch
        {
            "name" => Item(name: "Z"),
            "address" => Item(address: "DB1.DBX0.1"),
            "dataType" => Item(dataType: "int16"),
            "scanRate" => Item(scanRate: 100),
            "stringLength" => Item(stringLength: 32),
            "description" => Item(description: "注释"),
            "groupPath" => Item(groupPath: new[] { "DB1", "Sub" }),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEqual(baseline, IgwRootFingerprint.Compute(new[] { changed }));
    }

    /// <summary>少一个点也是变了——否则删点之后剩下的点会被判成"没变"。</summary>
    [Fact]
    public void Removing_a_point_changes_the_fingerprint()
    {
        Assert.NotEqual(
            IgwRootFingerprint.Compute(new[] { Item(name: "A"), Item(name: "B") }),
            IgwRootFingerprint.Compute(new[] { Item(name: "A") }));
    }

    /// <summary>要能进 varchar(64)，且不含需要转义的字符。</summary>
    [Fact]
    public void Fingerprint_is_a_short_hex_string()
    {
        var fp = IgwRootFingerprint.Compute(new[] { Item() });
        Assert.Equal(64, fp.Length);
        Assert.All(fp, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), $"非十六进制字符: {c}"));
    }
}
