using System.IO.Compression;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 真实数据夹具加载器（Fixtures/{name}.json.gz，由 tools/igw_bulk_e2e/make_fixtures.py 生成）。
/// 格式：{"project", "blocks":[{"name","number","rows":[[前缀长度, 路径后缀, DataType, OffsetByte?, OffsetBit?, Comment?]]}],
/// "ioTags":[[TableName, TagName, Address, DataType, Comment]]}；VariablePath = 上一行路径[..前缀长度] + 后缀，行尾缺省项为 null/空。
/// </summary>
public sealed class RealDataFixture
{
    public string Project { get; private init; }
    public IReadOnlyList<PlannerDbRow> DbRows { get; private init; }
    public IReadOnlyList<PlannerIoTag> IoTags { get; private init; }

    private static readonly Dictionary<string, RealDataFixture> Cache = new();

    public static RealDataFixture Load(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var fixture))
            {
                fixture = Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json.gz"));
                Cache[name] = fixture;
            }
            return fixture;
        }
    }

    private static RealDataFixture Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gzip);
        var root = doc.RootElement;

        var dbRows = new List<PlannerDbRow>();
        foreach (var block in root.GetProperty("blocks").EnumerateArray())
        {
            var blockName = block.GetProperty("name").GetString();
            var blockNumber = block.GetProperty("number").GetInt32();
            var previous = string.Empty;
            foreach (var row in block.GetProperty("rows").EnumerateArray())
            {
                var prefix = row[0].GetInt32();
                var variablePath = previous.Substring(0, prefix) + row[1].GetString();
                previous = variablePath;
                dbRows.Add(new PlannerDbRow(blockName, blockNumber, variablePath, StringAt(row, 2),
                    DecimalAt(row, 3), DecimalAt(row, 4), StringAt(row, 5) ?? string.Empty));
            }
        }

        var ioTags = new List<PlannerIoTag>();
        foreach (var tag in root.GetProperty("ioTags").EnumerateArray())
        {
            ioTags.Add(new PlannerIoTag(StringAt(tag, 0), StringAt(tag, 1), StringAt(tag, 2), StringAt(tag, 3), StringAt(tag, 4)));
        }

        return new RealDataFixture
        {
            Project = root.GetProperty("project").GetString(),
            DbRows = dbRows,
            IoTags = ioTags,
        };
    }

    private static string StringAt(JsonElement array, int index) =>
        index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.String ? array[index].GetString() : null;

    private static decimal? DecimalAt(JsonElement array, int index) =>
        index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number ? array[index].GetDecimal() : null;
}
