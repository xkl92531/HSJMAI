using System.Collections.Generic;
using HsjmTia.Core.Naming;
using Xunit;

namespace HsjmTia.Core.Tests.Naming
{
    public class MultilingualTextPickerTests
    {
        private static KeyValuePair<string, string> Item(string c, string t)
            => new KeyValuePair<string, string>(c, t);

        [Fact]
        public void Pick_PrefersSimplifiedChinese_EvenWhenItIsNotFirst()
        {
            var items = new[] { Item("de-DE", "Zylinder"), Item("zh-CN", "气缸"), Item("en-GB", "Cylinder") };
            Assert.Equal("气缸", MultilingualTextPicker.Pick(items));
        }

        [Fact]
        public void Pick_FallsBackToEnglish_WhenChineseMissing()
        {
            var items = new[] { Item("de-DE", "Zylinder"), Item("en-GB", "Cylinder") };
            Assert.Equal("Cylinder", MultilingualTextPicker.Pick(items));
        }

        [Fact]
        public void Pick_SkipsEmptyChinese_BecauseBlankEntriesAreCommonInTiaExports()
        {
            var items = new[] { Item("zh-CN", "   "), Item("en-GB", "Cylinder") };
            Assert.Equal("Cylinder", MultilingualTextPicker.Pick(items));
        }

        [Fact]
        public void Pick_FallsBackToFirstNonEmpty_WhenNoKnownCultureMatches()
        {
            var items = new[] { Item("fr-FR", "Vérin") };
            Assert.Equal("Vérin", MultilingualTextPicker.Pick(items));
        }

        [Fact]
        public void Pick_ReturnsEmpty_WhenNoItems()
            => Assert.Equal(string.Empty, MultilingualTextPicker.Pick(new KeyValuePair<string, string>[0]));

        [Fact]
        public void Pick_ReturnsEmpty_WhenInputIsNull()
            => Assert.Equal(string.Empty, MultilingualTextPicker.Pick(null));
    }
}
