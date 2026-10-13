using System.Collections.Generic;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Upload;
using Xunit;

namespace HsjmTia.Core.Tests.Upload
{
    public class FullUpdateSafetyTests
    {
        private static ExportGap Gap(string kind, string target, string detail = "detail")
            => ExportGap.Create(kind, target, detail);

        [Fact]
        public void FindUnsafeGaps_ReturnsEmpty_WhenThereAreNoGapsAtAll()
            => Assert.Empty(FullUpdateSafety.FindUnsafeGaps(new List<ExportGap>()));

        [Fact]
        public void FindUnsafeGaps_ReturnsEmpty_WhenGapsAreNull()
            => Assert.Empty(FullUpdateSafety.FindUnsafeGaps(null));

        // 只有 BlockMissing 代表"这个块本次没有进 zip"——其余全部 Kind 都是安全的，
        // 逐个枚举，确保每一种都单独验证过（不是靠"混合列表里剩下的都没被选中"侧面推断）。
        [Theory]
        [InlineData(GapKinds.PlcIpMissing)]
        [InlineData(GapKinds.CapabilityMissing)]
        [InlineData(GapKinds.NameSanitized)]
        [InlineData(GapKinds.AnalyzeFailed)]
        [InlineData(GapKinds.KnowHowProtected)]
        // ExportFailed 现在是安全的：它被 6 个不同的生产点共用，其中 4 个（源码导出失败——
        // XML 已经导出、entries 已经 Add 过；Tag 的 Retentive 属性无法识别；UDT 导出失败/重名）
        // 都不代表"块缺席 zip"。真正代表块缺席的两个点已经改用 GapKinds.BlockMissing。
        [InlineData(GapKinds.ExportFailed)]
        public void FindUnsafeGaps_TreatsEverySafeKindAsSafe(string safeKind)
        {
            var gaps = new List<ExportGap> { Gap(safeKind, "FB1") };

            Assert.Empty(FullUpdateSafety.FindUnsafeGaps(gaps));
        }

        [Fact]
        public void FindUnsafeGaps_IgnoresNonBlockGapKinds()
        {
            // PlcIpMissing / CapabilityMissing 等缺口不代表"块被排除出了 zip"——
            // 全项目更新在这些情况下仍然安全，不应该被拒绝。
            var gaps = new List<ExportGap>
            {
                Gap(GapKinds.PlcIpMissing, "PLC_1"),
                Gap(GapKinds.CapabilityMissing, "LocalAnalyze"),
                Gap(GapKinds.NameSanitized, "FB1"),
                Gap(GapKinds.AnalyzeFailed, "FB1.xml"),
                Gap(GapKinds.ExportFailed, "FB1"),
                Gap(GapKinds.KnowHowProtected, "FB1"),
            };

            Assert.Empty(FullUpdateSafety.FindUnsafeGaps(gaps));
        }

        [Fact]
        public void FindUnsafeGaps_FlagsBlockMissingGaps()
        {
            var gaps = new List<ExportGap> { Gap(GapKinds.BlockMissing, "FB100_Vendor") };

            var unsafeGaps = FullUpdateSafety.FindUnsafeGaps(gaps);

            Assert.Single(unsafeGaps);
            Assert.Equal("FB100_Vendor", unsafeGaps[0].Target);
            Assert.Equal(GapKinds.BlockMissing, unsafeGaps[0].Kind);
        }

        [Fact]
        public void FindUnsafeGaps_ReturnsOnlyTheBlockMissingGaps_AmongAMixOfSafeKinds()
        {
            var gaps = new List<ExportGap>
            {
                Gap(GapKinds.PlcIpMissing, "PLC_1"),
                Gap(GapKinds.BlockMissing, "FB100_Vendor"),
                Gap(GapKinds.NameSanitized, "FC1"),
                Gap(GapKinds.ExportFailed, "FB200"),
                Gap(GapKinds.BlockMissing, "FB300"),
                Gap(GapKinds.CapabilityMissing, "LocalAnalyze"),
            };

            var unsafeGaps = FullUpdateSafety.FindUnsafeGaps(gaps);

            Assert.Equal(2, unsafeGaps.Count);
            Assert.Contains(unsafeGaps, g => g.Target == "FB100_Vendor");
            Assert.Contains(unsafeGaps, g => g.Target == "FB300");
        }

        [Fact]
        public void IsUnsafe_MirrorsFindUnsafeGaps()
        {
            Assert.False(FullUpdateSafety.IsUnsafe(new List<ExportGap> { Gap(GapKinds.PlcIpMissing, "PLC_1") }));
            Assert.False(FullUpdateSafety.IsUnsafe(new List<ExportGap> { Gap(GapKinds.ExportFailed, "FB1") }));
            Assert.True(FullUpdateSafety.IsUnsafe(new List<ExportGap> { Gap(GapKinds.BlockMissing, "FB1") }));
        }
    }
}
