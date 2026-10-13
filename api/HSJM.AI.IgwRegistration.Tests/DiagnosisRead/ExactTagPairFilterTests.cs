using System.Diagnostics;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using SqlSugar;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>
/// 2026-09-23：精确读候选查询不再把每个点拼成一长串 OR。
/// 实测 SqlSugar 翻译 OR 链的耗时约按点数三次方增长（不连库，只生成 SQL）：
/// 200 点 0.9 s、400 点 10.6 s、800 点 103 s——停机诊断一个工位读几百个点，GraphActiveCheck 因此要 30~40 s。
/// 改为 表名 IN (…) AND 点名 IN (…) 取超集，再在内存按 (表名, 点名) 精确配对。
/// </summary>
public class ExactTagPairFilterTests
{
    private static PLC_ExactReadRequest Req(string block, string path) => new($"{block}.{path}", block, path);

    private static PLC_IOTagEntity Tag(string table, string name) => new() { TableName = table, TagName = name };

    [Fact]
    public void Only_requested_pairs_match_even_when_the_in_lists_cross()
    {
        var filter = PLC_VariableAccessService.BuildExactTagPairFilter(new[] { Req("DB1", "A"), Req("DB2", "B") });

        Assert.Equal(new[] { "DB1", "DB2" }, filter.Blocks);
        Assert.Equal(new[] { "A", "B" }, filter.Paths);
        Assert.True(filter.Matches(Tag("DB1", "A")));
        Assert.True(filter.Matches(Tag("DB2", "B")));
        // 两个 IN 列表的交叉组合会被查出来，但不是请求过的配对
        Assert.False(filter.Matches(Tag("DB1", "B")));
        Assert.False(filter.Matches(Tag("DB2", "A")));
    }

    [Fact]
    public void Matching_is_case_sensitive_like_the_database()
    {
        var filter = PLC_VariableAccessService.BuildExactTagPairFilter(new[] { Req("DB1", "Flag") });

        Assert.False(filter.Matches(Tag("DB1", "flag")));
        Assert.False(filter.Matches(Tag("db1", "Flag")));
    }

    [Fact]
    public void Duplicate_requests_collapse()
    {
        var filter = PLC_VariableAccessService.BuildExactTagPairFilter(new[] { Req("DB1", "A"), Req("DB1", "A") });

        Assert.Single(filter.Blocks);
        Assert.Single(filter.Paths);
    }

    [Fact]
    public void Sql_for_800_points_is_generated_quickly()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            DbType = DbType.PostgreSQL,
            ConnectionString = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",
            IsAutoCloseConnection = true,
        });
        var filter = PLC_VariableAccessService.BuildExactTagPairFilter(
            Enumerable.Range(0, 800).Select(i => Req($"DB{i % 40}_Graph", $"Step{i}.RT.X{i}")).ToArray());

        var sw = Stopwatch.StartNew();
        var sql = db.Queryable<PLC_IOTagEntity>()
            .Where(tag => tag.IsDeleted == false && tag.ProjectId == 1)
            .Where(tag => filter.Blocks.Contains(tag.TableName) && filter.Paths.Contains(tag.TagName))
            .ToSqlString();
        sw.Stop();

        Assert.Contains(" IN ", sql);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"生成 SQL 耗时 {sw.Elapsed}（旧 OR 链 800 点实测 103 s）");
    }
}
