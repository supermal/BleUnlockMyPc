using System.Globalization;
using BleUnlockManager.Core.Resources;
using Xunit;

namespace BleUnlockManager.Tests.Localization;

[Collection(TestCulture.LanguageCollection)]
public sealed class CultureSwitchTests
{
    [Fact]
    public void Culture_switch_changes_probe()
    {
        try
        {
            CoreStrings.Culture = new CultureInfo("es");
            Assert.Equal("Español", CoreStrings.LanguageProbe);

            CoreStrings.Culture = new CultureInfo("en");
            Assert.Equal("English", CoreStrings.LanguageProbe);
        }
        finally
        {
            TestCulture.Reset();
        }
    }

    [Fact]
    public void Tests_default_to_chinese()
        => Assert.Equal("中文", CoreStrings.LanguageProbe);
}
