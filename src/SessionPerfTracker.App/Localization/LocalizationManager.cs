using System.Globalization;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace SessionPerfTracker.App.Localization;

public static class LocalizationManager
{
    public const string Russian = "ru-RU";
    public const string English = "en-US";

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return Russian;
        }

        languageCode = languageCode.Trim();
        if (languageCode.Equals("en", StringComparison.OrdinalIgnoreCase)
            || languageCode.Equals(English, StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        return Russian;
    }

    public static void ApplyLanguage(string? languageCode)
    {
        var normalized = NormalizeLanguageCode(languageCode);
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var dictionaries = WpfApplication.Current.Resources.MergedDictionaries;
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            var source = dictionaries[index].Source?.OriginalString ?? string.Empty;
            if (source.Contains("/Localization/Strings.", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/Localization/Strings.{normalized}.xaml", UriKind.Relative)
        });
    }
}
