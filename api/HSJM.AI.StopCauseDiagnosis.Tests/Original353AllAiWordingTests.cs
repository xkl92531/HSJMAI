#nullable enable
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353AllAiWordingTests
{
    [Fact]
    public async Task MissingModelCannotSilentlyReturnTemplateWording()
    {
        var config = new ConfigurationBuilder().Build();
        var service = new Original353RuleTextService(config, null!, null!, new UnboundPurposeResolver(),
            NullLogger<Original353RuleTextService>.Instance);
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        var generated = new Original353ProjectResult(1, "test", "hash",
            [new Original353UnitResult(2, [rule], [], [])], 1, 0, []);

        var error = await Assert.ThrowsAsync<AiModelPurposeException>(() =>
            service.BuildAsync(new PipelineSnapshot(1, [], []), generated, CancellationToken.None));
        Assert.Equal("PurposeUnbound", error.Code);
    }

    private sealed class UnboundPurposeResolver : IAiModelPurposeResolver
    {
        public Task<ResolvedAiModel> ResolveAsync(string purpose, CancellationToken token = default)
            => throw new AiModelPurposeException("PurposeUnbound", "模型用途尚未绑定");
    }

    [Theory]
    [InlineData("Deterministic", false)]
    [InlineData("DeterministicFallback", false)]
    [InlineData("AIValidated", true)]
    [InlineData("AICache", true)]
    public void OnlyValidatedAiOrItsCacheCanSatisfyPublication(string origin, bool accepted)
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        var text = new Original353RuleWording("A", "A、B互斥，同一时刻最多一个为true。", origin, "完整接线白话文");
        var generated = new Original353ProjectResult(1, "test", "hash",
            [new Original353UnitResult(2, [rule], [], [])], 1, 0, []);
        var wording = new Dictionary<(long, string), Original353RuleWording> { [(2, rule.Key)] = text };

        Assert.Equal(accepted, Original353AiWordingPolicy.IsAccepted(rule, text));
        if (accepted) Original353AiWordingPolicy.RequireComplete(generated, wording);
        else Assert.Throws<PipelineException>(() => Original353AiWordingPolicy.RequireComplete(generated, wording));
    }

    [Fact]
    public void AllRuleKindsAndEveryLogicUnitNeedTheirOwnDescription()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        var generated = new Original353ProjectResult(1, "test", "hash",
            [new(2, [rule], [], []), new(3, [rule], [], [])], 2, 0, []);
        var wording = new Dictionary<(long, string), Original353RuleWording> {
            [(2, rule.Key)] = new("A", "A、B互斥，最多一个为true。", "AIValidated", "PLD")
        };
        var error = Assert.Throws<PipelineException>(() => Original353AiWordingPolicy.RequireComplete(generated, wording));
        Assert.Equal("AiDescriptionsIncomplete:1/2", error.Code);
    }

    [Fact]
    public void DeduplicationPreservesTheTimedContractUsedForPublication()
    {
        var direct = new PipelineRule("IMPLIES", [new("Q", "Source"), new("I", "Target")], "raw");
        var timed = direct with { RequiresSettlingEvidence = true, Applicability = "AfterSettling" };
        var unit = new Original353UnitResult(2, [direct], [], []) {
            ConditionalFeedbackRules = [new(timed, [], "AfterSettling", true, [])]
        };
        Assert.Same(timed, Assert.Single(Original353AiWordingPolicy.Rules(unit)));
        Assert.False(Original353AiWordingPolicy.IsAccepted(timed,
            new("Q", "Q为true时I立即到位。", "AIValidated", "PLD")));
    }

    [Fact]
    public void MissingPlainLanguageCannotBeMarkedAsCompletedAiWork()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        Assert.False(Original353AiWordingPolicy.IsAccepted(rule,
            new("A", "A、B互斥，最多一个为true。", "AIValidated")));
    }

    [Fact]
    public void SingleObjectResponseIsAllowedOnlyForOneExpectedRule()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        const string response = """{"key":"one","name":"A","description":"A、B互斥，最多一个为true。"}""";
        Assert.Single(Original353RuleTextAi.TryParse(response, new Dictionary<string, PipelineRule> { ["one"] = rule }));
        Assert.Empty(Original353RuleTextAi.TryParse(response, new Dictionary<string, PipelineRule> { ["one"] = rule, ["two"] = rule }));
        Assert.Empty(Original353RuleTextAi.TryParse(response.Replace("最多", "至少"),
            new Dictionary<string, PipelineRule> { ["one"] = rule }));
    }

    // 模型会抄错 64 位哈希键（实测把 …8D006E 重复一段）。单条批次的唯一返回项只能对应唯一的规则，按它归属，内容照常校验
    [Fact]
    public void MiscopiedKeyIsAttributedOnlyInSingleRuleBatch()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        const string response = """[{"key":"u:ABC8D006E8D006E","name":"A","description":"A、B互斥，最多一个为true。"}]""";
        var single = Original353RuleTextAi.TryParse(response, new Dictionary<string, PipelineRule> { ["u:ABC8D006E"] = rule });
        Assert.Equal("u:ABC8D006E", Assert.Single(single).Key);
        Assert.Empty(Original353RuleTextAi.TryParse(response,
            new Dictionary<string, PipelineRule> { ["u:ABC8D006E"] = rule, ["u:DEF"] = rule }));
        Assert.Empty(Original353RuleTextAi.TryParse(response.Replace("最多", "至少"),
            new Dictionary<string, PipelineRule> { ["u:ABC8D006E"] = rule }));
    }

    [Theory]
    [InlineData("SAME_STATE", "A与B始终保持相同状态。", false)]
    [InlineData("SAME_STATE", "A与B在程序执行条件成立时应保持相同状态。", true)]
    [InlineData("EXACTLY_ONE", "A与B最多一个为true；程序执行后的同一快照中应一真一假。", false)]
    [InlineData("EXACTLY_ONE", "A与B在程序执行后的同一快照中应一真一假。", true)]
    public void AiDescriptionMustPreserveProgramExecutionScope(string op, string description, bool accepted)
    {
        var rule = new PipelineRule(op, [new("A", "Member"), new("B", "Member")], "raw");
        Assert.Equal(accepted, Original353RuleText.IsValidAiText(rule, "A", description));
    }

    // 正式项目变量表里点名本身就是地址（泰星正式 PLC2 1596 个点中 721 个形如 %Q12001.1）。
    // 说明必须逐字出现信号名，又不许出现地址 —— 信号名即地址时照抄信号名不算违规，编造的其它地址仍拒。
    [Theory]
    [InlineData("%Q12001.1与%Q12001.2互斥，同一时刻最多一个为true。", true)]
    [InlineData("%Q12001.1与%Q12001.2互斥，同一时刻最多一个为true，参见%Q9.0。", false)]
    [InlineData("%Q12001.1与%Q12001.2互斥，同一时刻最多一个为true，参见%Q12001.10。", false)]
    public void AddressNamedSignalsMayAppearVerbatimButNoOtherAddress(string description, bool accepted)
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("%Q12001.1", "Member"), new("%Q12001.2", "Member")], "raw");
        Assert.Equal(accepted, Original353RuleText.IsValidAiText(rule, "%Q12001.1互斥", description));
    }

    [Fact]
    public void NamedSignalsStillRejectAnyAddressInTheDescription()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw");
        Assert.False(Original353RuleText.IsValidAiText(rule, "A", "A（%Q1.0）与B互斥，最多一个为true。"));
    }

    [Theory]
    [InlineData(4617381855010885632L, "HSJM_XMLV2_PLC1_ARCHIVE", 222)]
    [InlineData(4617382048292802560L, "HSJM_XMLV2_PLC2_ARCHIVE", 173)]
    public async Task RealArchivesHaveFullPlainLanguageForEveryPublishedRule(long projectId, string archiveVariable, int expected)
    {
        var connection = Environment.GetEnvironmentVariable("HSJM_XMLV2_TEST_CONNECTION");
        var archive = Environment.GetEnvironmentVariable(archiveVariable);
        var reportPath = Environment.GetEnvironmentVariable("HSJM_XMLV2_AI_WORKLOAD_REPORT");
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(archive) || string.IsNullOrWhiteSpace(reportPath)) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["IndependentPlcPipeline:Enabled"] = "true",
            ["IndependentPlcPipeline:ExpectedDatabase"] = "hsjm_ai_new_test",
            ["IndependentPlcPipeline:ConnectionString"] = connection
        }).Build();
        var tags = (await new PostgresPipelineStore(config).LoadSourceAsync(projectId, CancellationToken.None)).Tags;
        var source = Original353ArchiveSource.Read(archive, projectId, tags);
        var generated = OriginalConstraintWorkflow.Generate(source);
        var tagInfo = tags.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (group.First().Address, (string?)null), StringComparer.OrdinalIgnoreCase);
        var rows = generated.Units.Where(unit => Original353AiWordingPolicy.Rules(unit).Count > 0).Select(unit => {
            var code = source.Units.Single(item => item.Id == unit.LogicUnitId);
            var plain = SiemensFlgNetConstraintExtractor.BuildParsedLogicDescription(code.RawContent,
                tagInfo, code.NetworkOrder, blockName: code.BlockName);
            Assert.False(string.IsNullOrWhiteSpace(plain));
            Assert.DoesNotContain("XML 解析失败", plain);
            Assert.True(plain.Length <= 120000);
            return new { unit.LogicUnitId, code.BlockName, code.NetworkOrder,
                Rules = Original353AiWordingPolicy.Rules(unit).Count, PlainChars = plain.Length };
        }).ToArray();
        Assert.Equal(expected, rows.Sum(row => row.Rules));
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, $"workload-{projectId}.json"),
            System.Text.Json.JsonSerializer.Serialize(new { projectId, units = rows.Length,
                rules = rows.Sum(row => row.Rules), batches = rows.Sum(row => (row.Rules + 3) / 4),
                maxPlainChars = rows.Max(row => row.PlainChars), rows },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- 整分支审查 I-2：只挂 AI 契约规则的程序段不让发布失败；AI 规则单独成批、只试一次 ----

    static PipelineRule AiRule(string a, string b) =>
        new("AT_MOST_ONE", [new(a, "Member"), new(b, "Member")], "raw") { Proof = HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.UnderstandingProofs.AiFbContract };

    static Original353RuleTextService ServiceWithModel() =>
        new(new ConfigurationBuilder().Build(), null!, null!,
            new BoundPurposeResolver(new ResolvedAiModel(new HSJM.AI.IDbService.LLM_ModelEntity { Id = 1, ModelCode = "m" }, 1)),
            NullLogger<Original353RuleTextService>.Instance);

    [Fact]
    public async Task AiContractOnlyUnitWithoutSourceIsSkippedInsteadOfFailingThePublish()
    {
        var generated = new Original353ProjectResult(1, "test", "hash", [new Original353UnitResult(2, [AiRule("A", "B")], [], [])], 1, 0, []);
        var wording = await ServiceWithModel().BuildAsync(new PipelineSnapshot(1, [], []), generated, CancellationToken.None);
        Assert.Empty(wording);
        Original353AiWordingPolicy.RequireComplete(generated, wording);   // AI 契约规则缺说明不阻断
    }

    [Theory]
    [InlineData(HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.UnderstandingProofs.CodeNetwork)]
    [InlineData(HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.UnderstandingProofs.CodeCallSite)]
    [InlineData(HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding.UnderstandingProofs.ReviewCallSiteDefaults)]
    public async Task NewFlowOnlyUnitWithoutSourceIsSkippedInsteadOfFailingThePublish(string proof)
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("A", "Member"), new("B", "Member")], "raw") { Proof = proof };
        var generated = new Original353ProjectResult(1, "test", "hash", [new Original353UnitResult(2, [rule], [], [])], 1, 0, []);
        var wording = await ServiceWithModel().BuildAsync(new PipelineSnapshot(1, [], []), generated, CancellationToken.None);
        Assert.Empty(wording);
        Assert.Null(Record.Exception(() => Original353AiWordingPolicy.RequireComplete(generated, wording)));
    }

    [Fact]
    public async Task UnitWithANonAiRuleStillRequiresItsSource()
    {
        var plain = new PipelineRule("AT_MOST_ONE", [new("C", "Member"), new("D", "Member")], "raw");
        var generated = new Original353ProjectResult(1, "test", "hash", [new Original353UnitResult(2, [AiRule("A", "B"), plain], [], [])], 2, 0, []);
        var error = await Assert.ThrowsAsync<PipelineException>(() =>
            ServiceWithModel().BuildAsync(new PipelineSnapshot(1, [], []), generated, CancellationToken.None));
        Assert.Equal("AiDescriptionSourceMissing:2", error.Code);
    }

    [Fact]
    public void NewFlowOnlyRequiresEverySelectedRuleToBeANewFlowRule()
    {
        Assert.True(Original353AiWordingPlan.NewFlowOnly([AiRule("A", "B")]));
        Assert.False(Original353AiWordingPlan.NewFlowOnly([AiRule("A", "B"), new PipelineRule("AT_MOST_ONE", [new("C", "Member"), new("D", "Member")], "raw")]));
        Assert.False(Original353AiWordingPlan.NewFlowOnly([]));
    }

    [Fact]
    public void AiContractRulesAreBatchedSeparatelyAndTriedOnce()
    {
        var items = new List<(long Unit, PipelineRule Rule)>();
        for (var i = 0; i < 5; i++) items.Add((7, AiRule("A" + i, "B" + i)));
        for (var i = 0; i < 3; i++) items.Add((7, new PipelineRule("AT_MOST_ONE", [new("C" + i, "Member"), new("D" + i, "Member")], "raw")));
        items.Add((8, AiRule("X", "Y")));
        var batches = Original353AiWordingPlan.Batches(items, x => x.Unit, x => x.Rule);
        Assert.Equal(4, batches.Count);
        // 第 7 段：非 AI 规则一批（整批 2 次 + 逐条重试），AI 规则 4+1 两批（各 1 次、不逐条重试）
        Assert.Equal((3, 2, true), (batches[0].Items.Length, batches[0].Attempts, batches[0].RetryEach));
        Assert.All(batches[0].Items, x => Assert.Null(x.Rule.Proof));
        Assert.Equal((4, 1, false), (batches[1].Items.Length, batches[1].Attempts, batches[1].RetryEach));
        Assert.Equal((1, 1, false), (batches[2].Items.Length, batches[2].Attempts, batches[2].RetryEach));
        Assert.All(batches[1].Items.Concat(batches[2].Items), x => Assert.Equal(7, x.Unit));
        Assert.Equal(8, Assert.Single(batches[3].Items).Unit);
    }

    private sealed class BoundPurposeResolver(ResolvedAiModel binding) : IAiModelPurposeResolver
    {
        public Task<ResolvedAiModel> ResolveAsync(string purpose, CancellationToken token = default) => Task.FromResult(binding);
    }
}
