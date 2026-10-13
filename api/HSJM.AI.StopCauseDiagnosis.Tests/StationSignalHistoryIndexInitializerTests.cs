using System.Text.RegularExpressions;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class StationSignalHistoryIndexInitializerTests
{
    [Theory]
    [InlineData(null, StationSignalHistoryIndexInitializer.IndexAction.Create)]
    [InlineData(true, StationSignalHistoryIndexInitializer.IndexAction.Skip)]
    // 无效索引可能是另一实例正在构建，不能自动删
    [InlineData(false, StationSignalHistoryIndexInitializer.IndexAction.WarnInvalid)]
    public void Decide_maps_index_state_to_action(bool? isValid, StationSignalHistoryIndexInitializer.IndexAction expected)
    {
        Assert.Equal(expected, StationSignalHistoryIndexInitializer.Decide(isValid));
    }

    [Fact]
    public void Every_index_is_built_concurrently_and_idempotently_on_the_history_table()
    {
        Assert.NotEmpty(StationSignalHistoryIndexInitializer.Indexes);
        foreach (var (name, ddl) in StationSignalHistoryIndexInitializer.Indexes)
        {
            Assert.StartsWith("CREATE INDEX CONCURRENTLY IF NOT EXISTS " + name + " ON ai_plc_station_signal_history ", ddl);
        }
    }

    [Fact]
    public void Offline_script_contains_the_same_statements()
    {
        var script = File.ReadAllText(FindRepoFile("tools", "mig_20260923_station_signal_history_index.sql"));
        var normalizedScript = Normalize(script);
        foreach (var (_, ddl) in StationSignalHistoryIndexInitializer.Indexes)
        {
            Assert.Contains(Normalize(ddl) + ";", normalizedScript);
        }
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }
}
