using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Settings;

namespace SessionPerfTracker.UnitTests;

public sealed class SettingsTrustDefaultsTests
{
    [Fact]
    public void New_settings_keep_trust_sensitive_startup_actions_opt_in()
    {
        var settings = ThresholdProfileDefaults.CreateSettings();

        Assert.True(settings.Updates.AutomaticallyCheckForUpdates);
        Assert.False(settings.Updates.AutomaticallyInstallUpdatesOnStartup);
        Assert.False(settings.Behavior.StartWithWindows);
        Assert.True(settings.Behavior.StartMinimizedToTray);
        Assert.False(settings.Feedback.SendRemoteFeedback);
    }

    [Fact]
    public void New_settings_default_to_english_language()
    {
        var settings = ThresholdProfileDefaults.CreateSettings();

        Assert.Equal("en-US", settings.Language.LanguageCode);
    }

    [Fact]
    public void Unknown_language_codes_normalize_to_english()
    {
        var settings = JsonThresholdSettingsStore.Normalize(
            ThresholdProfileDefaults.CreateSettings() with
            {
                Language = new AppLanguageSettings { LanguageCode = "fr-FR" }
            });

        Assert.Equal("en-US", settings.Language.LanguageCode);
    }

    [Theory]
    [InlineData(null, "en-US")]
    [InlineData("", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("ru", "ru-RU")]
    [InlineData("ru-RU", "ru-RU")]
    public void App_language_manager_keeps_supported_languages_and_defaults_to_english(
        string? input,
        string expected)
    {
        Assert.Equal(expected, LocalizationManager.NormalizeLanguageCode(input));
    }

    [Fact]
    public async Task Explicit_user_opt_ins_are_preserved_by_settings_store()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-trust-defaults-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonThresholdSettingsStore(Path.Combine(directory, "settings.json"));
        var settings = ThresholdProfileDefaults.CreateSettings() with
        {
            Updates = new AppUpdateSettings
            {
                AutomaticallyCheckForUpdates = true,
                AutomaticallyInstallUpdatesOnStartup = true,
                ManifestUrl = AppUpdateSettings.DefaultManifestUrl
            },
            Behavior = new AppBehaviorSettings
            {
                MinimizeToTrayOnClose = true,
                StartWithWindows = true,
                StartMinimizedToTray = true
            }
        };

        await store.SaveAsync(settings);

        Assert.True(store.Current.Updates.AutomaticallyInstallUpdatesOnStartup);
        Assert.True(store.Current.Behavior.StartWithWindows);
    }

    [Fact]
    public async Task Explicit_update_auto_check_opt_out_is_preserved_without_last_check()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-trust-defaults-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonThresholdSettingsStore(Path.Combine(directory, "settings.json"));
        var settings = ThresholdProfileDefaults.CreateSettings() with
        {
            Updates = new AppUpdateSettings
            {
                AutomaticallyCheckForUpdates = false,
                AutomaticallyInstallUpdatesOnStartup = false,
                LastCheckedAt = null,
                ManifestUrl = AppUpdateSettings.DefaultManifestUrl
            }
        };

        await store.SaveAsync(settings);

        Assert.False(store.Current.Updates.AutomaticallyCheckForUpdates);
        Assert.Null(store.Current.Updates.LastCheckedAt);
    }

    [Fact]
    public async Task Atomic_settings_updates_preserve_changes_from_latest_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-trust-defaults-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonThresholdSettingsStore(Path.Combine(directory, "settings.json"));
        await store.LoadAsync();

        await store.UpdateAsync(settings => settings with
        {
            WatchJournal = settings.WatchJournal with { MaxEntries = 123 }
        });
        await store.UpdateAsync(settings => settings with
        {
            Updates = settings.Updates with { AutomaticallyCheckForUpdates = false }
        });

        Assert.Equal(123, store.Current.WatchJournal.MaxEntries);
        Assert.False(store.Current.Updates.AutomaticallyCheckForUpdates);
    }
}
