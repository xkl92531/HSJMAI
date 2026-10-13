using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;
using Xunit.Abstractions;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 真实 TIA 导出（PLC文件/xml.rar 两个工程）经解析器得到的接口行 / IO 标签夹具，
/// 用全量模式 + 默认配置跑规划器，校验生成的请求满足 IGW 批量契约，并把统计打印到测试输出。
/// 夹具由 tools/igw_bulk_e2e/make_fixtures.py 生成。
/// </summary>
public class RealDataPlanTests
{
    private const string Ip = "192.168.6.10";

    private static readonly Regex ClassicAddressRegex = new(@"^DB\d+\.(DBX|DBB|DBW|DBD)\d+(\.[0-7])?$", RegexOptions.CultureInvariant);
    private static readonly Regex IoAddressRegex = new(@"^(I|Q|M|E|A)(X|B|W|D)?\d+(\.[0-7])?$", RegexOptions.CultureInvariant);

    private readonly ITestOutputHelper _output;

    public RealDataPlanTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("plc1")]
    [InlineData("plc2")]
    public void FixtureLoads(string fixture)
    {
        var data = RealDataFixture.Load(fixture);

        Assert.NotEmpty(data.DbRows);
        Assert.NotEmpty(data.IoTags);
        Assert.All(data.DbRows, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.BlockName));
            Assert.True(r.BlockNumber > 0);
            Assert.False(string.IsNullOrEmpty(r.VariablePath));
            Assert.Equal(r.OffsetByte == null, r.OffsetBit == null);
        });
        // 至少有一部分 Standard 块带偏移（→ S7），也有优化块不带偏移（→ S7Plus）
        Assert.Contains(data.DbRows, r => r.OffsetByte != null);
        Assert.Contains(data.DbRows, r => r.OffsetByte == null);
    }

    [Theory]
    [InlineData("plc1")]
    [InlineData("plc2")]
    public void FullPlan_SatisfiesIgwContract(string fixture)
    {
        var data = RealDataFixture.Load(fixture);
        var config = new IgwRegistrationConfig();
        var input = new PlannerInput("RealData_" + fixture, Ip, 102, data.DbRows, data.IoTags,
            Array.Empty<string>(), Array.Empty<string>(), IoRefreshed: false, Full: true, Config: config);

        var plan = IgwRegistrationPlanner.Plan(input);

        var stats = PrintStatistics(fixture, data, plan);

        // ---------- 名称 / 深度 / 地址 / 上限 ----------
        foreach (var device in new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus })
        {
            var deviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var planned in plan.Requests.Where(r => r.Device == device))
            {
                var request = planned.Request;
                Assert.True(request.Items.Count <= config.MaxItemsPerRequest,
                    $"{device} 请求点数 {request.Items.Count} 超过 {config.MaxItemsPerRequest}");
                Assert.True(request.ReplaceGroups.Count <= IgwRegistrationPlanner.MaxReplaceGroupsPerRequest);
                foreach (var group in request.ReplaceGroups)
                {
                    Assert.InRange(group.Count, 1, IgwTagNaming.MaxGroupDepth);
                    Assert.All(group, AssertIgwName);
                }

                var requestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in request.Items)
                {
                    Assert.InRange(item.GroupPath.Count, 1, IgwTagNaming.MaxGroupDepth);
                    Assert.All(item.GroupPath, AssertIgwName);
                    AssertIgwName(item.Name);

                    var key = string.Join("", item.GroupPath) + "" + item.Name;
                    Assert.True(requestKeys.Add(key), $"同一请求内 (GroupPath,Name) 重复：{key}");
                    Assert.True(deviceKeys.Add(key), $"同一设备跨请求 (GroupPath,Name) 重复：{key}");

                    Assert.False(string.IsNullOrEmpty(item.Address));
                    Assert.True(item.Address.Length <= IgwRegistrationPlanner.MaxAddressLength);
                    Assert.False(string.IsNullOrEmpty(item.DataType));
                    Assert.Equal("readWrite", item.Access);
                    Assert.True(item.ScanRateMs > 0);

                    if (device == IgwDeviceKind.S7)
                    {
                        Assert.True(ClassicAddressRegex.IsMatch(item.Address) || IoAddressRegex.IsMatch(item.Address),
                            $"S7 设备地址非经典/IO 格式：{item.Address}");
                    }
                    else
                    {
                        Assert.Contains('.', item.Address);
                        Assert.DoesNotContain(item.Address, char.IsWhiteSpace);
                        Assert.False(ClassicAddressRegex.IsMatch(item.Address), $"S7Plus 设备出现经典地址：{item.Address}");
                    }
                }
            }
        }

        // ---------- 规模 / 跳过率 ----------
        Assert.True(stats.Registrable > 0, "可注册点数为 0");
        Assert.True(stats.SkipRate < 0.5, $"跳过率 {stats.SkipRate:P2} ≥ 50%");
    }

    private static void AssertIgwName(string name)
    {
        Assert.False(string.IsNullOrEmpty(name), "名称为空");
        Assert.True(name.Length <= IgwTagNaming.MaxNameLength, $"名称超长：{name}");
        Assert.DoesNotContain('.', name);
        Assert.DoesNotContain(name, char.IsControl);
        Assert.Equal(name.Trim(), name);
    }

    private sealed record PlanStatistics(int Registrable, int Skipped, double SkipRate);

    private PlanStatistics PrintStatistics(string fixture, RealDataFixture data, PlanResult plan)
    {
        var sb = new StringBuilder();
        var blocksWithTags = plan.Blocks.Where(b => b.TagCount > 0).ToList();
        var s7Blocks = blocksWithTags.Where(b => b.Device == IgwDeviceKind.S7).ToList();
        var s7PlusBlocks = blocksWithTags.Where(b => b.Device == IgwDeviceKind.S7Plus).ToList();
        var emptyBlocks = plan.Blocks.Count - blocksWithTags.Count;
        var dbRegistrable = plan.Blocks.Sum(b => b.TagCount);
        var dbSkipped = plan.Blocks.Sum(b => b.Skipped);
        var ioSkipped = data.IoTags.Count - plan.IoTagCount;
        var registrable = dbRegistrable + plan.IoTagCount;
        var skipped = dbSkipped + ioSkipped;
        var total = data.DbRows.Count + data.IoTags.Count;
        var skipRate = total == 0 ? 0 : (double)skipped / total;

        sb.AppendLine($"===== {fixture}（{data.Project}） =====");
        sb.AppendLine($"输入：DB 块 {plan.Blocks.Count}，接口叶子行 {data.DbRows.Count}（有偏移 {data.DbRows.Count(r => r.OffsetByte != null)}），IO 标签 {data.IoTags.Count}");
        sb.AppendLine($"DB 块归属：S7 {s7Blocks.Count}（{s7Blocks.Sum(b => b.TagCount)} 点） / S7Plus {s7PlusBlocks.Count}（{s7PlusBlocks.Sum(b => b.TagCount)} 点） / 无可注册点 {emptyBlocks}");
        sb.AppendLine($"可注册：DB {dbRegistrable} + IO {plan.IoTagCount} = {registrable}");
        sb.AppendLine($"跳过：DB {dbSkipped}（{Rate(dbSkipped, data.DbRows.Count)}） + IO {ioSkipped}（{Rate(ioSkipped, data.IoTags.Count)}） = {skipped}，总跳过率 {skipRate:P2}");
        sb.AppendLine("跳过原因：");
        sb.AppendLine("| 原因 | 数量 |");
        sb.AppendLine("|---|---|");
        foreach (var (reason, count) in plan.SkipReasons.OrderByDescending(x => x.Value))
        {
            sb.AppendLine($"| {reason} | {count} |");
        }
        sb.AppendLine($"改名：IO 同组重名 {plan.IoRenames.Count}，DB 同组重名 {plan.DbRenames.Count}，块根组 {plan.BlockRootRenames.Count}");
        foreach (var (block, root) in plan.BlockRootRenames)
        {
            sb.AppendLine($"  块根组改名：{block} → {root}");
        }

        sb.AppendLine("请求：");
        sb.AppendLine("| # | 设备 | 点数 | replaceGroups | 块数 | JSON 字节 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var index = 0;
        foreach (var planned in plan.Requests)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(planned.Request, IgwBulkJson.Options).Length;
            sb.AppendLine($"| {++index} | {planned.Device} | {planned.Request.Items.Count} | {planned.Request.ReplaceGroups.Count} | {planned.BlockNames.Count} | {bytes} |");
        }

        // 未映射类型 Top 10（帮助判断跳过是否合理）
        var unmapped = data.DbRows
            .Where(r => IgwTypeMapper.MapDataType(r.DataType, out _) == null)
            .GroupBy(r => r.DataType ?? "(null)")
            .OrderByDescending(g => g.Count())
            .Take(10);
        sb.AppendLine("未映射 DB 类型 Top 10：");
        foreach (var g in unmapped)
        {
            sb.AppendLine($"  {g.Key}: {g.Count()}");
        }

        _output.WriteLine(sb.ToString());
        return new PlanStatistics(registrable, skipped, skipRate);
    }

    private static string Rate(int part, int whole) => whole == 0 ? "0%" : ((double)part / whole).ToString("P2");
}
