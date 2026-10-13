using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353ModeConflictRulesTests
{
    [Theory]
    [InlineData("test-v2-source-plc1.zip", 12)]
    [InlineData("test-v2-source-plc2.zip", 6)]
    public void RealSourceBindsDistinctModeOperandsFromControllerCall(string file, int expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, ".artifacts"))) root = root.Parent;
        if (root == null) return;
        var archive = Path.Combine(root.FullName, ".artifacts", file);
        if (!File.Exists(archive)) return;
        var source = Original353ArchiveSource.Read(archive, 1, []);
        var bindings = Original353ManualModeBindings.Read(archive);
        var original = OriginalConstraintWorkflow.Generate(source);
        var updated = Original353ModeConflictRules.Append(original, source, bindings);
        var rules = updated.Units.SelectMany(u => u.Rules).Where(Original353ModeConflictRules.IsModeRule).ToArray();
        Assert.Equal(expected, rules.Length);
        foreach (var rule in rules)
        {
            Assert.Contains(rule.Signals, s => s.Name.EndsWith(".ManualMod"));
            Assert.Equal("ModeCheck", Original353ActivationPolicy.Classify(rule, []).Status);
            var wording = Original353RuleText.Render(rule, new("ModeCheck", ""));
            Assert.True(Original353RuleText.IsValidAiText(rule, wording.Name, wording.Description));
            Assert.False(Original353RuleText.IsValidAiText(rule, wording.Name, "DB220.General.AutoMod与ManualMod互斥。当" + rule.DisplayLabel + "自动或手动模式同时开启时，请检查。"));
            Assert.Equal("XMLV2_MODE", Original353LegacyRuleMapper.Map(1, 1, rule, [], modeBindings: bindings).Rule.Source);
        }
        Assert.Equal(updated.RuleCount, Original353ModeConflictRules.Append(updated, source, bindings).RuleCount);
    }
}
