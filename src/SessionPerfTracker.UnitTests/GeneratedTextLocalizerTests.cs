using SessionPerfTracker.App.ViewModels;

namespace SessionPerfTracker.UnitTests;

public sealed class GeneratedTextLocalizerTests
{
    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        ["Ui_GenClosedNormally"] = "Закрыт штатно.",
        ["Ui_GenExitCode"] = "Код выхода: {0}.",
        ["Ui_GenBestEffort"] = "Приблизительно.",
        ["Ui_GenCrashStatus"] = "Сбой {0} ({1}).",
        ["Ui_GenRootExitedContinued"] = "Корень вышел на +{1} (код {2}); дочерних: {0}.",
        ["Ui_GenSamplesNoEvents"] = "Образцов: {0}, событий нет.",
        ["Ui_GenSignedCatalog"] = "Подписан (каталог Windows)",
        ["Ui_Unavailable"] = "Недоступно",
        ["Ui_GlobalFallbackSource"] = "Глобальный"
    };

    [Fact]
    public void Stored_values_are_translated_for_display_only()
    {
        Assert.Equal("Подписан (каталог Windows)", GeneratedTextLocalizer.LocalizeValue("Signed (Windows catalog)", Translate));
        Assert.Equal("Недоступно", GeneratedTextLocalizer.LocalizeValue("Unavailable", Translate));
        Assert.Equal("Недоступно", GeneratedTextLocalizer.LocalizeValue(null, Translate));
        Assert.Equal("Глобальный (Игры)", GeneratedTextLocalizer.LocalizeValue("Global fallback (Игры)", Translate));
        Assert.Equal(@"C:\Games\game.exe", GeneratedTextLocalizer.LocalizeValue(@"C:\Games\game.exe", Translate));
    }

    private static string Translate(string key) => Russian.TryGetValue(key, out var value) ? value : key;

    [Fact]
    public void Exit_details_are_translated_sentence_by_sentence()
    {
        var text = GeneratedTextLocalizer.Localize(
            "The target process closed outside SessionPerfTracker normally. Exit code: 0. This classification is best-effort.",
            Translate);

        Assert.Equal("Закрыт штатно. Код выхода: 0. Приблизительно.", text);
    }

    [Fact]
    public void Crash_status_and_hex_exit_code_keep_their_values()
    {
        var text = GeneratedTextLocalizer.Localize(
            "The target process terminated with crash status 0xC0000005 (access violation). Exit code: -1073741819 (0xC0000005). This classification is best-effort.",
            Translate);

        Assert.Equal("Сбой 0xC0000005 (access violation). Код выхода: -1073741819 (0xC0000005). Приблизительно.", text);
    }

    [Fact]
    public void Root_exit_note_maps_all_three_values()
    {
        var text = GeneratedTextLocalizer.Localize(
            "Root process exited at +00:00:07 (exit code 0); recording continued for 2 child process(es).",
            Translate);

        Assert.Equal("Корень вышел на +00:00:07 (код 0); дочерних: 2.", text);
    }

    [Fact]
    public void Formatted_counts_and_unknown_text_pass_through()
    {
        Assert.Equal("Образцов: 1,234, событий нет.", GeneratedTextLocalizer.Localize("Recorded 1,234 samples with no stability events.", Translate));
        Assert.Equal("Something custom.", GeneratedTextLocalizer.Localize("Something custom.", Translate));
        // A missing resource leaves the original English sentence.
        Assert.Equal("Exit code: unavailable.", GeneratedTextLocalizer.Localize("Exit code: unavailable.", Translate));
    }
}
