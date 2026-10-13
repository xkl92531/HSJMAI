using HsjmTia.Core.Naming;
using Xunit;

namespace HsjmTia.Core.Tests.Naming
{
    public class FileNameSanitizerTests
    {
        [Theory]
        [InlineData("FB320_OP30Manual", "FB320_OP30Manual")]
        [InlineData("FC12/Sub", "FC12_Sub")]
        [InlineData("A:B", "A_B")]
        [InlineData("a*b?c\"d<e>f|g\\h", "a_b_c_d_e_f_g_h")]
        public void Sanitize_ReplacesIllegalCharsWithUnderscore(string input, string expected)
            => Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

        [Fact]
        public void Sanitize_TrimsTrailingDotsAndSpaces_BecauseWindowsRejectsThem()
            => Assert.Equal("Block", FileNameSanitizer.Sanitize("Block. "));

        [Fact]
        public void Sanitize_ReturnsPlaceholder_WhenNameIsEmptyAfterCleaning()
            => Assert.Equal("_", FileNameSanitizer.Sanitize("..."));

        [Fact]
        public void Sanitize_DoesNotDeleteCharacters_SoLengthIsPreserved()
            => Assert.Equal("A_B_C".Length, FileNameSanitizer.Sanitize("A/B\\C").Length);
    }
}
