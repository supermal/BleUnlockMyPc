using System.ComponentModel;
using AutoUnlock.Shared;
using AutoUnlockManager.Core.Localization;
using AutoUnlockManager.Core.Resources;
using Xunit;

namespace AutoUnlockManager.Tests.Localization;

[Collection(TestCulture.LanguageCollection)]
public sealed class LocalizationSourceTests : IDisposable
{
    public LocalizationSourceTests() => ManagerLanguage.Set(AppLanguage.Chinese);

    public void Dispose()
    {
        ManagerLanguage.Set(AppLanguage.Chinese);
        TestCulture.Reset();
    }

    [Fact]
    public void Indexer_follows_language()
    {
        var source = new LocalizationSource(CoreStrings.ResourceManager);

        ManagerLanguage.Set(AppLanguage.Spanish);

        Assert.Equal("Español", source["LanguageProbe"]);
    }

    [Fact]
    public void Language_change_raises_indexer_change()
    {
        var source = new LocalizationSource(CoreStrings.ResourceManager);
        var names = new List<string?>();
        ((INotifyPropertyChanged)source).PropertyChanged += (_, e) => names.Add(e.PropertyName);

        ManagerLanguage.Set(AppLanguage.English);

        Assert.Equal(["Item[]"], names);
    }

    [Fact]
    public void Missing_key_is_visible()
        => Assert.Equal("!Nope!", new LocalizationSource(CoreStrings.ResourceManager)["Nope"]);
}
