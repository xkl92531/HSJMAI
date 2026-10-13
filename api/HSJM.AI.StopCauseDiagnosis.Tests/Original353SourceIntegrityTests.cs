using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>审查 P4：缺程序体（MissingSource）的单元。非 FC → 旧规则与报警清单行原样保留并列出；FC 不计。</summary>
public class Original353SourceIntegrityTests
{
    private const long Project = 77;

    private static long BlockId(string kind, string name) => PipelineExportReader.StableId($"{Project}:{kind}:{name}");
    private static long UnitId(string kind, string name, int order, string root = "FlgNet") =>
        PipelineExportReader.StableId($"{BlockId(kind, name)}:{order}:{root}");

    private static PipelineUnit Body(string kind, string name, int order) =>
        new(BlockId(kind, name), name, UnitId(kind, name, order), "LAD", "<FlgNet/>", "", [], order, "", kind, "", "FlgNet")
            { CompileUnitOrder = order };

    private static PipelineUnit Missing(string kind, string name) =>
        new(BlockId(kind, name), name, PipelineExportReader.StableId($"{BlockId(kind, name)}:missing-body"), "LAD", "", "", [], 0, "",
            kind, "", "MissingBody");

    private static PipelineSnapshot Snapshot(params PipelineUnit[] units) => new(Project, units, []);

    private static Original353ProjectResult Generate(PipelineSnapshot source) =>
        OriginalConstraintWorkflow.Generate(source);

    private static PLC_IOConstraintRuleEntity Rule(long id, string source, long unitId, string blockName = null) => new()
    {
        Id = id, ProjectId = Project, Source = source,
        ValidStates = blockName is null
            ? JsonSerializer.Serialize(new { Generator = "g", LogicUnitId = unitId.ToString() })
            : JsonSerializer.Serialize(new { Generator = "g", LogicUnitId = unitId.ToString(), BlockName = blockName })
    };

    [Fact]
    public void Inspect_splits_fc_from_other_blocks_and_ignores_units_with_body()
    {
        var source = Snapshot(Body("FB", "FB1", 1), Missing("FB", "FB9"), Missing("OB", "OB100"), Missing("FC", "FC3"));
        var report = Original353SourceIntegrity.Inspect(source, Generate(source));
        Assert.Equal(new[] { "FB9", "OB100" }, report.NonFc.Select(u => u.BlockName).Order().ToArray());
        Assert.Equal(new[] { "FC3" }, report.Fc.Select(u => u.BlockName).ToArray());
        Assert.True(report.HasNonFc);
    }

    [Fact]
    public void Describe_lists_first_blocks_with_kind_and_the_total()
    {
        var units = Enumerable.Range(1, 12).Select(i => Missing("FB", $"FB{i:00}")).ToArray();
        var source = Snapshot(units);
        var text = Original353SourceIntegrity.Inspect(source, Generate(source)).DescribeNonFc(3);
        Assert.Contains("FB01(FB)", text);
        Assert.Contains("FB03(FB)", text);
        Assert.DoesNotContain("FB04", text);
        Assert.Contains("12", text);
    }

    [Fact]
    public void Rules_of_a_missing_non_fc_block_are_preserved_by_block_name_or_legacy_unit_identity()
    {
        var source = Snapshot(Body("FB", "FB1", 1), Missing("FB", "FB9"), Missing("FC", "FC3"));
        var report = Original353SourceIntegrity.Inspect(source, Generate(source));
        var old = new[]
        {
            Rule(1, "XMLV2", 111, "FB9"),                              // 新写法：按块名
            Rule(2, "XMLV2_TIMED", UnitId("FB", "FB9", 5)),            // 旧行没有块名：按归档单元身份推回块
            Rule(3, "XMLV2_REVIEW", UnitId("FB", "FB9", 7, "StructuredText")),
            Rule(4, "XMLV2", UnitId("FB", "FB1", 1)),                  // 有程序体的块：照常替换
            Rule(5, "XMLV2", 222, "FB1"),
            Rule(6, "XMLV2", UnitId("FC", "FC3", 2)),                  // FC 不管
            Rule(7, "XMLV2", 333, "FC3"),
            Rule(8, null, UnitId("FB", "FB9", 2)),                     // 老方法规则不保留（不混入老方法）
            Rule(9, "AI", 444, "FB9"),
            Rule(10, "XMLV2", UnitId("FB", "FB2", 1)),                 // 别的块（已删除的网络 / 块）不保留
        };
        var kept = Original353SourceIntegrity.PreservedRuleIds(source, report, old);
        Assert.Equal(new long[] { 1, 2, 3 }, kept.Order().ToArray());
    }

    [Fact]
    public void A_prepared_resolver_gives_the_same_preserved_rules()
    {
        // 审查复核 M-4：发布器在事务外先建好（并预热）解析器，事务里复用，结果必须与临时建的一致
        var source = Snapshot(Body("FB", "FB1", 1), Missing("FB", "FB9"));
        var report = Original353SourceIntegrity.Inspect(source, Generate(source));
        var old = new[] { Rule(1, "XMLV2", UnitId("FB", "FB9", 5)), Rule(2, "XMLV2", 222, "FB9"), Rule(3, "XMLV2", UnitId("FB", "FB1", 1)) };
        var resolver = Original353SourceIntegrity.ResolverFor(source, report);
        Assert.Equal(Original353SourceIntegrity.PreservedRuleIds(source, report, old).Order(),
            Original353SourceIntegrity.PreservedRuleIds(report, old, resolver).Order());
        Assert.Equal(new long[] { 1, 2 }, Original353SourceIntegrity.PreservedRuleIds(report, old, resolver).Order().ToArray());
    }

    [Fact]
    public void Nothing_is_preserved_when_every_executable_block_has_a_body()
    {
        var source = Snapshot(Body("FB", "FB1", 1), Missing("FC", "FC3"));
        var report = Original353SourceIntegrity.Inspect(source, Generate(source));
        Assert.False(report.HasNonFc);
        Assert.Empty(Original353SourceIntegrity.PreservedRuleIds(source, report, [Rule(1, "XMLV2", 1, "FC3")]));
    }

    [Fact]
    public void Rule_origin_reads_unit_id_and_block_name_and_tolerates_bad_json()
    {
        var a = Original353SourceIntegrity.ReadOrigin(Rule(1, "XMLV2", 42, "FB1"));
        Assert.Equal(42, a.UnitId);
        Assert.Equal("FB1", a.BlockName);
        var b = Original353SourceIntegrity.ReadOrigin(new PLC_IOConstraintRuleEntity { Id = 2, Source = "XMLV2", ValidStates = "{bad" });
        Assert.Null(b.UnitId);
        Assert.Null(b.BlockName);
    }

    [Fact]
    public void Mapper_records_the_block_name_in_valid_states()
    {
        var rule = new PipelineRule("IMPLIES", [new("Q1", "Source"), new("I1", "Target")], "d");
        var tags = new[] { new PipelineTag("Q1", "%Q0.0", "Bool"), new PipelineTag("I1", "%I0.0", "Bool") };
        var row = Original353LegacyRuleMapper.Map(Project, 5, rule, tags, persistSourceLuId: false, blockName: "FB1");
        using var doc = JsonDocument.Parse(row.Rule.ValidStates);
        Assert.Equal("FB1", doc.RootElement.GetProperty("BlockName").GetString());
        Assert.Equal("5", doc.RootElement.GetProperty("LogicUnitId").GetString());
    }

    private static AlarmCatalogRow Alarm(string symbol, string block, int? network) =>
        new(symbol, "Timeout", null, [], null, block, network, "", "", []);

    [Fact]
    public void Catalog_merge_keeps_rows_outside_the_scope_and_takes_fresh_rows_inside_it()
    {
        var old = new[] { Alarm("A1", "FB9", 1), Alarm("A2", "FB1", 1), Alarm("A3", "FB1", 2) };
        var fresh = new[] { Alarm("A2", "FB1", 1), Alarm("A4", "FB1", 3), Alarm("A1", "FB1", 4) };
        // 范围 = FB1 全部行：FB9 的 A1 原样保留；新 A1 与保留行同名 → 不覆盖保留行
        var merged = AlarmCatalogMerge.Merge(old, fresh, r => r.SourceBlock == "FB1");
        Assert.Equal(new[] { "A1|FB9", "A2|FB1", "A4|FB1" },
            merged.Select(r => r.AlarmSymbol + "|" + r.SourceBlock).Order().ToArray());
    }
}
