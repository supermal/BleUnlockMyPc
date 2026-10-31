using System.Text.RegularExpressions;
using AutoUnlock.Shared;
using AutoUnlockManager.Core.Config;
using AutoUnlockManager.Core.Localization;
using AutoUnlockManager.Core.Rules;
using Xunit;

namespace AutoUnlockManager.Tests.Rules;

[Collection(TestCulture.LanguageCollection)]
public sealed class RulesContentTests : IDisposable
{
    private static readonly Regex Placeholder = new(@"\{[A-Za-z_]\w*\}");

    public void Dispose()
    {
        ManagerLanguage.Set(AppLanguage.Chinese);
        TestCulture.Reset();
    }

    private static RuleValues Custom => RuleValues.Defaults with { LockDbm = -75, UnlockDbm = -50, PromptCount = 4 };

    private static IEnumerable<string> AllText(IReadOnlyList<RuleSection> sections) =>
        sections.SelectMany(s => new[] { s.Title, s.Lead ?? "" }
            .Concat(s.Params.SelectMany(p => new[] { p.Name, p.Value, p.Effect, p.DefaultText }))
            .Concat(s.Items.SelectMany(i => new[] { i.Key ?? "", i.Text }))
            .Concat(s.Rows.SelectMany(r => new[] { r.Key, r.Text, r.Result }))
            .Concat(s.StepGroups.SelectMany(g => g.Steps.Prepend(g.Title))));

    [Theory]
    [InlineData(AppLanguage.Chinese)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Spanish)]
    public void Every_language_builds_nine_sections_without_leftover_placeholders(AppLanguage language)
    {
        ManagerLanguage.Set(language);

        var sections = RulesContent.Build(Custom);

        Assert.Equal(9, sections.Count);
        Assert.Equal(Enumerable.Range(0, 9), sections.Select(s => s.Number));
        var leftovers = AllText(sections).Where(t => Placeholder.IsMatch(t)).ToList();
        Assert.True(leftovers.Count == 0, string.Join(Environment.NewLine, leftovers));
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
    }

    [Fact]
    public void Text_uses_the_values_in_effect()
    {
        ManagerLanguage.Set(AppLanguage.Chinese);

        var sections = RulesContent.Build(Custom);

        Assert.Contains(sections[3].Items, i => i.Text.Contains("4 个低于 -75"));
        Assert.Contains("-75", sections[4].Rows[0].Text);   // 撤回提示用锁定阈值
    }

    [Fact]
    public void Params_mark_values_that_differ_from_the_default()
    {
        ManagerLanguage.Set(AppLanguage.Chinese);

        var parameters = RulesContent.Build(Custom)[0].Params;

        var lockParam = parameters[0];
        Assert.True(lockParam.Changed);
        Assert.Equal("-75 dBm", lockParam.Value);
        Assert.Equal("默认 -80 dBm", lockParam.DefaultText);
        Assert.False(parameters[3].Changed);                 // 锁屏确认个数未改
        Assert.Equal(10, parameters.Count);
    }

    [Fact]
    public void Examples_follow_the_thresholds()
    {
        var ex = RuleExamples.From(Custom);

        Assert.Equal(-77, ex.Trigger);
        Assert.Equal(-45, ex.Back);
        Assert.All(ex.Withdraw, w => Assert.InRange(w, -74, -50));
    }

    [Fact]
    public void Withdraw_examples_stay_between_close_thresholds()
    {
        var ex = RuleExamples.From(RuleValues.Defaults with { LockDbm = -60, UnlockDbm = -57 });

        Assert.All(ex.Withdraw, w => Assert.InRange(w, -59, -57));
    }

    [Fact]
    public void Default_examples_match_the_documented_walkthrough()
    {
        var ex = RuleExamples.From(RuleValues.Defaults);

        Assert.Equal(-82, ex.Trigger);
        Assert.Equal(-40, ex.Back);
        Assert.Equal([-70, -72, -68, -65], ex.Withdraw);
    }

    [Fact]
    public void FromConfig_maps_every_value()
    {
        var config = new AutoUnlockConfig();
        config.Proximity.LockThresholdDbm = -70;
        config.Proximity.UnlockThresholdDbm = -40;
        config.Proximity.LockPromptSampleCount = 6;
        config.Proximity.LockConfirmSampleCount = 12;
        config.Proximity.UnlockSampleCount = 2;
        config.Proximity.SampleIntervalMs = 250;
        config.Proximity.StartupGraceSeconds = 30;
        config.Proximity.UnlockPendingTimeoutSeconds = 25;
        config.Notification.RejectSnoozeSeconds = 90;
        config.Notification.SnoozeMinutes = 10;

        Assert.Equal(new RuleValues(-70, -40, 6, 12, 2, 250, 30, 25, 90, 10), RuleValues.FromConfig(config));
    }

    [Fact]
    public void Defaults_equal_a_fresh_configuration()
        => Assert.Equal(RuleValues.FromConfig(new AutoUnlockConfig()), RuleValues.Defaults);

    [Fact]
    public void Load_without_configuration_falls_back_to_defaults()
    {
        var values = RuleValues.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "appsettings.json"), out var fromConfig);

        Assert.False(fromConfig);
        Assert.Equal(RuleValues.Defaults, values);
    }
}
