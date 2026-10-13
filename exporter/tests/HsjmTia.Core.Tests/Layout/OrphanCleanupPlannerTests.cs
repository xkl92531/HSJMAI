using HsjmTia.Core.Layout;
using Xunit;

namespace HsjmTia.Core.Tests.Layout
{
    public class OrphanCleanupPlannerTests
    {
        [Fact]
        public void Plan_RemovesDirectoriesForPlcsThatNoLongerExist_OnFullExport()
        {
            var plan = OrphanCleanupPlanner.Plan(
                new[] { "PLC_1_1.1.1.1", "OldPlc_2.2.2.2" },
                new[] { "PLC_1_1.1.1.1" },
                isFullExport: true);
            Assert.Equal(new[] { "OldPlc_2.2.2.2" }, plan);
        }

        [Fact]
        public void Plan_KeepsEverythingElse_OnPartialExport_SoColleaguesExportsSurvive()
        {
            var plan = OrphanCleanupPlanner.Plan(
                new[] { "PLC_1_1.1.1.1", "OtherPlc_2.2.2.2" },
                new[] { "PLC_1_1.1.1.1" },
                isFullExport: false);
            Assert.Empty(plan);
        }

        [Fact]
        public void Plan_IsEmpty_WhenNothingIsStale()
            => Assert.Empty(OrphanCleanupPlanner.Plan(new[] { "A" }, new[] { "A" }, true));

        [Fact]
        public void Plan_IgnoresCase_BecauseWindowsPathsAreCaseInsensitive()
            => Assert.Empty(OrphanCleanupPlanner.Plan(new[] { "plc_1_1.1.1.1" }, new[] { "PLC_1_1.1.1.1" }, true));

        [Fact]
        public void Plan_HandlesEmptyInputs()
            => Assert.Empty(OrphanCleanupPlanner.Plan(new string[0], new string[0], true));
    }
}
