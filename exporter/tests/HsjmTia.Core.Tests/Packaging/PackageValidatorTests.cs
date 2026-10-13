using System.Linq;
using HsjmTia.Core.Packaging;
using Xunit;

namespace HsjmTia.Core.Tests.Packaging
{
    public class PackageValidatorTests
    {
        private static readonly string[] Good =
        {
            "manifest.json",
            "blocks/block_path.txt",
            "blocks/LAD/FB1.xml",
            "plcTags/tags.csv",
            "udt/udt_index.json",
        };

        [Fact]
        public void Validate_AcceptsAWellFormedPackage()
            => Assert.Empty(PackageValidator.Validate(Good));

        [Fact]
        public void Validate_RejectsAPackageWithoutManifest()
            => Assert.Contains(PackageValidator.Validate(Good.Where(e => e != "manifest.json")),
                p => p.Contains("manifest.json"));

        [Fact]
        public void Validate_RejectsAPackageWithoutAnyBlockXml()
            => Assert.Contains(PackageValidator.Validate(new[] { "manifest.json", "plcTags/tags.csv" }),
                p => p.Contains("blocks"));

        [Fact]
        public void Validate_RejectsMoreThanOneTagCsv_BecauseTheBackendMergesThemAll()
            => Assert.Contains(
                PackageValidator.Validate(Good.Concat(new[] { "plcTags/tags_old.csv" })),
                p => p.Contains("plcTags"));

        [Fact]
        public void Validate_RejectsANestedPlcLevel_BecauseOnePackageMustHoldExactlyOnePlc()
        {
            // Good is a valid, self-contained root-level package. Adding a second PLC's
            // block nested under an extra path segment must be caught on its own —
            // even though the root-level package is otherwise complete — because the
            // backend's block-path mapping keys on block name alone and would silently
            // let this nested duplicate inherit the first PLC's folder path.
            var withNestedPlc = Good.Concat(new[] { "PLC_B/blocks/LAD/FB1.xml" });

            var problems = PackageValidator.Validate(withNestedPlc);

            Assert.Contains(problems, p => p.Contains("PLC_B/blocks/LAD/FB1.xml"));
        }

        [Fact]
        public void Validate_AcceptsLaterPhaseRoots_AlarmsHardwareAndTechObjects()
            => Assert.Empty(
                PackageValidator.Validate(Good.Concat(new[]
                {
                    "alarms/alarm_index.json",
                    "hardware/hardware.json",
                    "tech_objects/tech_objects.json",
                })));
    }
}
