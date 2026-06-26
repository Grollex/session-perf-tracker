using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SessionPerfTracker.UnitTests;

public sealed partial class LocalizationResourceTests
{
    private static readonly string[] VisibleTextAttributeNames =
    [
        "Content",
        "Header",
        "Text",
        "Title",
        "ToolTip"
    ];

    [Fact]
    public void English_and_russian_resource_keys_match()
    {
        var root = FindRepositoryRoot();
        var localizationDirectory = Path.Combine(root, "src", "SessionPerfTracker.App", "Localization");

        var englishKeys = LoadResourceKeys(Path.Combine(localizationDirectory, "Strings.en-US.xaml"));
        var russianKeys = LoadResourceKeys(Path.Combine(localizationDirectory, "Strings.ru-RU.xaml"));

        var missingInRussian = englishKeys.Except(russianKeys, StringComparer.Ordinal).Order().ToArray();
        var missingInEnglish = russianKeys.Except(englishKeys, StringComparer.Ordinal).Order().ToArray();

        Assert.True(
            missingInRussian.Length == 0 && missingInEnglish.Length == 0,
            "Localization resource keys must match."
            + Environment.NewLine
            + $"Missing in ru-RU: {string.Join(", ", missingInRussian)}"
            + Environment.NewLine
            + $"Missing in en-US: {string.Join(", ", missingInEnglish)}");
    }

    [Fact]
    public void Visible_xaml_strings_are_bound_to_resources()
    {
        var root = FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "SessionPerfTracker.App");
        var xamlFiles = new[]
            {
                Path.Combine(appDirectory, "MainWindow.xaml"),
                Path.Combine(appDirectory, "ProcessInspectorWindow.xaml")
            }
            .Concat(Directory.EnumerateFiles(Path.Combine(appDirectory, "Views"), "*.xaml", SearchOption.AllDirectories))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var violations = xamlFiles
            .SelectMany(FindVisibleStringLiterals)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Visible XAML strings must use localization resources."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void App_csharp_sources_do_not_contain_hardcoded_cyrillic_string_literals()
    {
        var root = FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "SessionPerfTracker.App");
        var sourceFiles = Directory
            .EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var violations = sourceFiles
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { Line = line, LineNumber = index + 1 })
                .Where(item => CyrillicStringLiteralRegex().IsMatch(item.Line))
                .Select(item => $"{Path.GetRelativePath(root, path)}:{item.LineNumber} {item.Line.Trim()}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "C# UI code must not hardcode Russian text; use localization resources."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var marker = Path.Combine(
                directory.FullName,
                "src",
                "SessionPerfTracker.App",
                "Localization",
                "Strings.en-US.xaml");

            if (File.Exists(marker))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Session Perf Tracker repository root.");
    }

    private static ISet<string> LoadResourceKeys(string path)
    {
        var keyNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var keys = XDocument.Load(path)
            .Descendants()
            .Select(element => element.Attribute(keyNamespace + "Key")?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToArray();

        var duplicateKeys = keys
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicateKeys.Length == 0,
            $"{Path.GetFileName(path)} contains duplicate localization keys: {string.Join(", ", duplicateKeys)}");

        return new SortedSet<string>(keys, StringComparer.Ordinal);
    }

    private static IEnumerable<string> FindVisibleStringLiterals(string path)
    {
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (!VisibleTextAttributeNames.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                {
                    continue;
                }

                var value = attribute.Value.Trim();
                if (value.Length == 0
                    || value.StartsWith('{')
                    || !LetterRegex().IsMatch(value))
                {
                    continue;
                }

                var lineInfo = (IXmlLineInfo)attribute;
                var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
                var relativePath = Path.GetRelativePath(FindRepositoryRoot(), path);
                yield return $"{relativePath}:{line} {attribute.Name.LocalName}=\"{value}\"";
            }
        }
    }

    [GeneratedRegex(@"[A-Za-zА-Яа-я]")]
    private static partial Regex LetterRegex();

    [GeneratedRegex("\"[^\"]*[А-Яа-я][^\"]*\"")]
    private static partial Regex CyrillicStringLiteralRegex();
}
