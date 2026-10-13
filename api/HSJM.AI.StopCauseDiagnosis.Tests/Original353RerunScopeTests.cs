using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>审查 P2/P3：单 LU / 单块按范围重跑——库 LU 映射到归档程序段、删除范围、AI 完整性。</summary>
public class Original353RerunScopeTests
{
    private const long Project = 88;

    private static long BlockId(string kind, string name) => PipelineExportReader.StableId($"{Project}:{kind}:{name}");
    private static long UnitId(string kind, string name, int order, string root = "FlgNet") =>
        PipelineExportReader.StableId($"{BlockId(kind, name)}:{order}:{root}");

    /// <summary>order = 归档根元素序号；compileUnit = 库 networkid 口径（块内第几个 CompileUnit，空网络也计）。</summary>
    private static PipelineUnit Body(string kind, string name, int order, int compileUnit, params string[] callees) =>
        new(BlockId(kind, name), name, UnitId(kind, name, order), "LAD", "<FlgNet/>", "", callees, order, "", kind, "", "FlgNet")
            { CompileUnitOrder = compileUnit };

    private static PipelineUnit Missing(string kind, string name) =>
        new(BlockId(kind, name), name, PipelineExportReader.StableId($"{BlockId(kind, name)}:missing-body"), "LAD", "", "", [], 0, "",
            kind, "", "MissingBody");

    // FB1：网络 1、3（第 2 个 CompileUnit 是空网络，归档不出单元）；FB2：网络 1；FB9 缺程序体；FC5 缺程序体
    private static readonly PipelineSnapshot Source = new(Project, [
        Body("FB", "FB1", 1, 1, "FB_Valve"), Body("FB", "FB1", 2, 3), Body("FB", "FB2", 1, 1),
        Missing("FB", "FB9"), Missing("FC", "FC5")
    ], []);

    private static DbLogicUnitRef Lu(long id, string block, int? network) => new(id, block, network);

    private static readonly DbLogicUnitRef[] Fb1Lus = [Lu(501, "FB1", 1), Lu(502, "FB1", 2), Lu(503, "FB1", 3)];

    [Fact]
    public void Logic_unit_maps_to_archive_unit_by_block_and_db_network_number()
    {
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(Fb1Lus[2], Fb1Lus));
        Assert.Equal(new[] { UnitId("FB", "FB1", 2) }, scope.ArchiveUnitIds.ToArray());
        Assert.Equal(new long[] { 503 }, scope.DbLuIds.ToArray());
        Assert.Equal(new[] { 3 }, scope.DbNetworks!.ToArray());
    }

    [Fact]
    public void Block_name_match_ignores_case()
    {
        var lu = Lu(503, "fb1", 3);
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(lu, [lu]));
        Assert.Single(scope.ArchiveUnitIds);
    }

    [Fact]
    public void Logic_unit_without_archive_counterpart_fails_instead_of_guessing()
    {
        // 库里网络 2 在归档里是空网络（没有单元）：映射不上 → 失败，不删任何东西
        var error = Assert.Throws<PipelineException>(() =>
            Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(Fb1Lus[1], Fb1Lus)));
        Assert.StartsWith("RerunLogicUnitNotInArchive", error.Code);
        var noNetwork = Lu(504, "FB1", null);
        Assert.StartsWith("RerunLogicUnitNotInArchive",
            Assert.Throws<PipelineException>(() => Original353RerunScope.Resolve(Source,
                Original353RerunRequest.ForLogicUnit(noNetwork, [noNetwork]))).Code);
    }

    [Fact]
    public void Logic_units_sharing_one_archive_network_are_rerun_together()
    {
        var a = Lu(601, "FB2", 1); var b = Lu(602, "FB2", 1);
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(a, [a, b]));
        Assert.Equal(new long[] { 601, 602 }, scope.DbLuIds.Order().ToArray());
        Assert.NotEmpty(scope.Notes);
    }

    [Fact]
    public void Block_scope_covers_all_archive_units_and_db_units_of_the_block()
    {
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForBlock("FB1", Fb1Lus));
        Assert.Equal(new[] { UnitId("FB", "FB1", 1), UnitId("FB", "FB1", 2) }.Order(), scope.ArchiveUnitIds.Order());
        Assert.Equal(new long[] { 501, 502, 503 }, scope.DbLuIds.Order().ToArray());
        Assert.Null(scope.DbNetworks);
    }

    [Theory]
    [InlineData("FB9")]
    [InlineData("FC5")]
    public void Block_without_program_body_fails(string block)
    {
        var error = Assert.Throws<PipelineException>(() =>
            Original353RerunScope.Resolve(Source, Original353RerunRequest.ForBlock(block, [Lu(700, block, 1)])));
        Assert.StartsWith("RerunScopeMissingSource", error.Code);
        // 单 LU 落在缺程序体的块里同样失败
        Assert.StartsWith("RerunScopeMissingSource", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(Lu(700, block, 1), [Lu(700, block, 1)]))).Code);
    }

    [Fact]
    public void Block_missing_from_archive_fails()
    {
        Assert.StartsWith("RerunBlockNotInArchive", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.Resolve(Source, Original353RerunRequest.ForBlock("FB404", []))).Code);
    }

    private static PLC_IOConstraintRuleEntity Rule(long id, string source, long? unitId = null, string block = null, long? sourceLuId = null) => new()
    {
        Id = id, ProjectId = Project, Source = source, SourceLuId = sourceLuId,
        ValidStates = unitId is null ? null : block is null
            ? JsonSerializer.Serialize(new { LogicUnitId = unitId.Value.ToString() })
            : JsonSerializer.Serialize(new { LogicUnitId = unitId.Value.ToString(), BlockName = block })
    };

    private static readonly PLC_IOConstraintRuleEntity[] OldRules =
    [
        Rule(1, "XMLV2", UnitId("FB", "FB1", 2), "FB1"),          // FB1 网络 3（LU 503）
        Rule(2, "XMLV2_TIMED", UnitId("FB", "FB1", 2)),           // 同上，旧行没有块名
        Rule(3, "XMLV2", UnitId("FB", "FB1", 1), "FB1"),          // FB1 网络 1（范围外）
        Rule(4, "XMLV2", UnitId("FB", "FB2", 1), "FB2"),          // 别的块
        Rule(5, "Manual", UnitId("FB", "FB1", 2), "FB1"),         // 手工规则永远不动
        Rule(6, null, sourceLuId: 503),                           // 老方法规则：同 LU → 一并删（不混入老方法）
        Rule(7, "AI", sourceLuId: 501),                           // 老方法规则：别的 LU
        Rule(8, "XMLV2", 123456789, "FB1"),                       // FB1 已不存在的程序段（块范围才删）
        Rule(9, "XMLV2_REVIEW", UnitId("FB", "FB1", 9)),          // 旧行无块名，按归档身份推回 FB1（块范围才删）
        Rule(10, "XMLV2", 987654321),                             // 推不回任何块：不删
        Rule(11, "XMLV2"),                                        // 没有 ValidStates：不删
    ];

    [Fact]
    public void Logic_unit_rerun_replaces_only_rules_of_that_network()
    {
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(Fb1Lus[2], Fb1Lus));
        var ids = Original353RerunScope.RulesToReplace(OldRules, scope, Original353SourceIntegrity.BlockResolver(Source,
            [(BlockId("FB", "FB1"), "FB1")]));
        Assert.Equal(new long[] { 1, 2, 6 }, ids.Order().ToArray());
    }

    [Fact]
    public void Block_rerun_replaces_rules_of_the_block_only()
    {
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForBlock("FB1", Fb1Lus));
        var ids = Original353RerunScope.RulesToReplace(OldRules, scope, Original353SourceIntegrity.BlockResolver(Source,
            [(BlockId("FB", "FB1"), "FB1")]));
        Assert.Equal(new long[] { 1, 2, 3, 6, 7, 8, 9 }, ids.Order().ToArray());
    }

    [Fact]
    public void Restrict_keeps_only_units_in_scope()
    {
        var generated = OriginalConstraintWorkflow.Generate(Source);
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForBlock("FB1", Fb1Lus));
        var restricted = Original353RerunScope.Restrict(generated, scope);
        Assert.Equal(2, restricted.Units.Count);
        Assert.All(restricted.Units, u => Assert.Contains(u.LogicUnitId, scope.ArchiveUnitIds));
        Assert.Equal(restricted.Units.Sum(u => u.Rules.Count), restricted.RuleCount);
    }

    [Fact]
    public void Catalog_scope_matches_block_and_network()
    {
        var lu = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(Fb1Lus[2], Fb1Lus));
        var block = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForBlock("FB1", Fb1Lus));
        AlarmCatalogRow Row(string b, int? n) => new("A", "Timeout", null, [], null, b, n, "", "", []);
        Assert.True(lu.CatalogInScope(Row("fb1", 3)));
        Assert.False(lu.CatalogInScope(Row("FB1", 1)));
        Assert.False(lu.CatalogInScope(Row("FB2", 3)));
        Assert.True(block.CatalogInScope(Row("FB1", 1)));
        Assert.True(block.CatalogInScope(Row("FB1", null)));
        Assert.False(block.CatalogInScope(Row("FB2", 1)));
    }

    private static UnderstandingReport Report(Dictionary<string, string> fb, Dictionary<long, string> networks) =>
        new(fb, new Dictionary<string, string>(), [], new Dictionary<string, int>(), 0, 0) { NetworkStatus = networks };

    [Fact]
    public void Ai_completeness_is_required_inside_the_scope_only()
    {
        var scope = Original353RerunScope.Resolve(Source, Original353RerunRequest.ForLogicUnit(Fb1Lus[0], Fb1Lus));
        var inScope = UnitId("FB", "FB1", 1);
        var outside = UnitId("FB", "FB2", 1);

        Assert.StartsWith("RerunUnderstandingFailed", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(Source, scope, null)).Code);
        Assert.StartsWith("RerunNetworkAiIncomplete", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(Source, scope, Report([], new() { [inScope] = NetworkAiStatus.Failed }))).Code);
        Assert.StartsWith("RerunNetworkAiIncomplete", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(Source, scope, Report([], new() { [inScope] = NetworkAiStatus.Deadline }))).Code);
        Assert.StartsWith("RerunFbContractUnavailable", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(Source, scope, Report(new() { ["fb_valve"] = FbContractStatus.Failed }, []))).Code);

        // 范围外失败、确定性的 TooLarge / Rejected 不挡
        Original353RerunScope.RequireAiComplete(Source, scope, Report(new() { ["FB_Other"] = FbContractStatus.Failed, ["FB_Valve"] = FbContractStatus.Rejected },
            new() { [outside] = NetworkAiStatus.Failed, [inScope] = NetworkAiStatus.TooLarge }));
    }

    // 审查复核 I-2：契约状态仍 Ok 的超长 FB 分段读取失败、代码证明到期 / 异常——非确定性少产，按范围替换时会删掉旧规则 → 判失败
    private static readonly PipelineSnapshot NestedSource = new(Project, [
        Body("FB", "FB1", 1, 1, "FB_Valve"), Body("FB", "FB_Valve", 1, 1, "FB_Inner"), Body("FB", "FB_Inner", 1, 1), Body("FB", "FB2", 1, 1, "FB_Other")
    ], []);

    private static Original353RerunScopeResolved Fb1Network1() =>
        Original353RerunScope.Resolve(NestedSource, Original353RerunRequest.ForLogicUnit(Lu(501, "FB1", 1), [Lu(501, "FB1", 1)]));

    [Fact]
    public void Segment_failure_of_a_called_fb_fails_the_rerun_even_when_its_status_is_ok()
    {
        var report = Report(new() { ["FB_Valve"] = FbContractStatus.Ok }, []) with { FbSegmentFailures = new Dictionary<string, string> { ["fb_valve"] = "SegmentFailed: 2/3 TimeoutException" } };
        Assert.StartsWith("RerunFbContractIncomplete", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(NestedSource, Fb1Network1(), report)).Code);
    }

    [Fact]
    public void Failures_of_fbs_called_through_nested_calls_count_too()
    {
        // FB1 调 FB_Valve，FB_Valve 调 FB_Inner：下层契约派生到外层，读取失败同样会让范围内少规则
        Assert.StartsWith("RerunFbContractUnavailable", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(NestedSource, Fb1Network1(),
                Report(new() { ["FB_Inner"] = FbContractStatus.Failed }, []))).Code);
        var segments = Report([], []) with { FbSegmentFailures = new Dictionary<string, string> { ["FB_Inner"] = "SegmentFailed: 1/2 ReadingDeadline" } };
        Assert.StartsWith("RerunFbContractIncomplete", Assert.Throws<PipelineException>(() =>
            Original353RerunScope.RequireAiComplete(NestedSource, Fb1Network1(), segments)).Code);
        // 范围外的 FB 不挡
        Original353RerunScope.RequireAiComplete(NestedSource, Fb1Network1(),
            Report(new() { ["FB_Other"] = FbContractStatus.Failed }, []) with
            { FbSegmentFailures = new Dictionary<string, string> { ["FB_Other"] = "SegmentFailed: 1/2 x" } });
    }

    [Theory]
    [InlineData("CodeProofFailed")]
    [InlineData("CodeProofDeadline")]
    [InlineData("CodeProofDisabled")]
    [InlineData("CodeProofFiltersFailed")]
    public void Code_proof_failure_or_deadline_fails_the_rerun(string note)
    {
        var report = new UnderstandingReport(new Dictionary<string, string>(), new Dictionary<string, string>(), [],
            new Dictionary<string, int> { [note] = 1, ["CodeProof:AT_MOST_ONE:Code:Network"] = 3 }, 0, 0);
        var error = Assert.Throws<PipelineException>(() => Original353RerunScope.RequireAiComplete(NestedSource, Fb1Network1(), report));
        Assert.StartsWith("RerunCodeProofIncomplete", error.Code);
        Assert.Contains(note, error.Code);
    }

    [Fact]
    public void Successful_code_proof_notes_do_not_block()
    {
        var report = new UnderstandingReport(new Dictionary<string, string>(), new Dictionary<string, string>(), [],
            new Dictionary<string, int> { ["CodeProof:AT_MOST_ONE:Code:Network"] = 3, ["ConstantBit"] = 2, ["KeptCodeProof"] = 1 }, 0, 0);
        Original353RerunScope.RequireAiComplete(NestedSource, Fb1Network1(), report);
    }
}
