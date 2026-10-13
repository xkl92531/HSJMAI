using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class Original353ArchiveCoverageTests
{
    private static PipelineSnapshot Source(params string[] names) => new(8,
        names.Select((name, i) => new PipelineUnit(100 + i, name, 200 + i,
            "LAD", "", "", [])).ToArray(), []);

    [Fact]
    public void PartialArchiveCannotReplaceOtherExecutableBlocksRules()
    {
        var active = new[] {
            new PLC_BlockEntity { ProjectId = 8, BlockName = "FC1", BlockType = "FC" },
            new PLC_BlockEntity { ProjectId = 8, BlockName = "FB2", BlockType = "fb" },
            new PLC_BlockEntity { ProjectId = 8, BlockName = "UDT1", BlockType = "UDT" }
        };
        var error = Assert.Throws<PipelineException>(() =>
            Original353ArchiveCoverage.RequireComplete(Source("FC1"), active));
        Assert.Contains("FB2", error.Code);
        Original353ArchiveCoverage.RequireComplete(Source("FC1", "FB2"), active);
    }

    [Fact]
    public void DeletedAndOtherProjectBlocksDoNotAffectCoverage()
    {
        Original353ArchiveCoverage.RequireComplete(Source("FC1"), [
            new PLC_BlockEntity { ProjectId = 8, BlockName = "FC1", BlockType = "FC" },
            new PLC_BlockEntity { ProjectId = 8, BlockName = "FC2", BlockType = "FC", IsDeleted = true },
            new PLC_BlockEntity { ProjectId = 9, BlockName = "FC3", BlockType = "FC" }
        ]);
    }
}
