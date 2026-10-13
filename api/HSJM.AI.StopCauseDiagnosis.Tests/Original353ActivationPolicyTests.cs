using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353ActivationPolicyTests
{
    [Fact]
    public void OutputToInputFeedbackNeedsRuntimeGuardEvenWhenGeneratedAsPlainImplies()
    {
        var rule = new PipelineRule("IMPLIES", [new("ValveOutput", "Source"), new("Feedback", "Target")], "feedback");
        var tags = new PipelineTag[] { new("ValveOutput", "%Q10.0", "Bool"), new("Feedback", "%I11.0", "Bool") };

        var result = Original353ActivationPolicy.Classify(rule, tags);

        Assert.Equal("RequiresRuntimeGuard", result.Status);
    }

    [Fact]
    public void GuardedRuleCannotBecomeAnInstantRuleWhenItsMetadataIsPresent()
    {
        var rule = new PipelineRule("IMPLIES", [new("ValveOutput", "Source"), new("Feedback", "Target")], "feedback")
        { Applicability = "AfterSettling", RequiresSettlingEvidence = true };

        var result = Original353ActivationPolicy.Classify(rule, []);

        Assert.Equal("RequiresRuntimeGuard", result.Status);
    }

    [Fact]
    public void MutexWithKnownBooleanSignalsCanBeActiveImmediately()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("Extend", "Member"), new("Retract", "Member")], "mutex");
        var tags = new PipelineTag[] { new("Extend", "%Q10.0", "Bool"), new("Retract", "%Q10.1", "Bool") };

        var result = Original353ActivationPolicy.Classify(rule, tags);

        Assert.Equal("Active", result.Status);
    }

    [Fact]
    public void UnrepairedThreeWaySafetyMutexCannotBecomeAnActiveAlarm()
    {
        var rule = new PipelineRule("AT_MOST_ONE", [new("Request", "Member"),
            new("Reset", "Member"), new("Feedback", "Member")], "legacy safety");
        var tags = new PipelineTag[] { new("Request", "%Q10.0", "Bool"),
            new("Reset", "%I11.0", "Bool"), new("Feedback", "%I11.1", "Bool") };

        Assert.Equal("Unknown", Original353ActivationPolicy.Classify(rule, tags).Status);
    }
}
