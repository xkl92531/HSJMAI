#nullable enable
using HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>绑定 / 解绑 / 接受建议之后，对涉及的项目各排队一次 GRAPH 步位重新注册。</summary>
public class GraphStepRegistrationTriggerTests
{
    [Fact]
    public void Each_changed_project_is_requested_once()
    {
        var calls = new List<long>();
        var trigger = new GraphStepRegistrationTrigger(id => { calls.Add(id); return true; });

        var results = trigger.Request([2, 1, 2, 0, -5]);

        Assert.Equal([1L, 2L], calls);
        Assert.All(results, r => Assert.True(r.Enqueued));
    }

    [Fact]
    public void A_running_registration_is_reported_as_queued_for_rerun()
    {
        var trigger = new GraphStepRegistrationTrigger(_ => false);

        var result = Assert.Single(trigger.Request([7]));

        Assert.False(result.Enqueued);
        Assert.Null(result.Error);
        Assert.Contains("补跑", GraphStepRegistrationTrigger.Describe([result]));
    }

    [Fact]
    public void A_failing_project_does_not_stop_the_others()
    {
        var calls = new List<long>();
        var trigger = new GraphStepRegistrationTrigger(id =>
        {
            calls.Add(id);
            if (id == 1) throw new InvalidOperationException("Hangfire 不可用");
            return true;
        });

        var results = trigger.Request([1, 2]);

        Assert.Equal([1L, 2L], calls);
        Assert.Equal("Hangfire 不可用", results.Single(r => r.ProjectId == 1).Error);
        Assert.True(results.Single(r => r.ProjectId == 2).Enqueued);
    }

    [Fact]
    public void Nothing_changed_means_nothing_is_requested()
    {
        var calls = new List<long>();
        var trigger = new GraphStepRegistrationTrigger(id => { calls.Add(id); return true; });

        Assert.Empty(trigger.Request([]));
        Assert.Empty(calls);
        Assert.Equal(string.Empty, GraphStepRegistrationTrigger.Describe([]));
    }

    [Fact]
    public void Bind_plan_lists_only_projects_whose_bindings_or_blocks_change()
    {
        var alreadyBound = new PLC_StationBlockBindingEntity
        {
            ProjectId = 1, BlockName = "FB110_Graph", StationCode = "OP010", Reason = StationBlockBindingRules.ManualReason,
        };
        var oldElsewhere = new PLC_StationBlockBindingEntity
        {
            ProjectId = 2, BlockName = "FB210_Graph", StationCode = "OP020", Reason = StationBlockBindingRules.ManualReason,
        };

        var plan = StationBlockBindingRules.PlanManualBind(
            "OP010",
            [new GraphBlockRef(1, 1, "FB110_Graph", "OP010"), new GraphBlockRef(2, 2, "FB210_Graph", "OP020")],
            [alreadyBound, oldElsewhere],
            1, "tester", DateTime.Now);

        Assert.Equal([2L], plan.ChangedProjectIds);
    }
}
