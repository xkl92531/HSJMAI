#nullable enable
using System;
using System.IO;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class HoldoutClaimTests
{
    [Fact]
    public void OnlyTheFirstClaimSucceedsAndTheNoteIsWritten()
    {
        var marker = Path.Combine(Path.GetTempPath(), "plan5-holdout-" + Guid.NewGuid().ToString("N"), "sub", "RUN-ONCE.marker");
        Assert.True(HoldoutClaim.TryClaim(marker, "第一次"));
        Assert.False(HoldoutClaim.TryClaim(marker, "第二次"));
        Assert.Equal("第一次", File.ReadAllText(marker));
    }
}
