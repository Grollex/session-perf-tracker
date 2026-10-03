using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Services;

public sealed record ExecutableCandidate(
    string NormalizedPath,
    string ExeName,
    ExecutableFingerprint? Fingerprint);

/// <summary>
/// Decides whether an executable seen at another path is the same program as a list entry.
/// Name-only matches are never enough: relocation needs the same binary (SHA-256) or the
/// same file name plus publisher metadata, so an unrelated tool with a common name is ignored.
/// </summary>
public static class TrackedExecutableMatcher
{
    public const int SameBinaryScore = 100;
    public const int MaxMetadataScore = 95;
    public const int RelocationThreshold = 75;

    public static int Score(ITrackedExecutable item, ExecutableCandidate candidate)
    {
        var known = item.Fingerprint;
        var other = candidate.Fingerprint;

        if (!string.IsNullOrWhiteSpace(known?.Sha256)
            && !string.IsNullOrWhiteSpace(other?.Sha256)
            && string.Equals(known.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return SameBinaryScore;
        }

        var score = 0;
        if (SameText(NormalizeExeName(item.ExeName), NormalizeExeName(candidate.ExeName)))
        {
            score += 35;
        }

        if (SameText(known?.OriginalFileName, other?.OriginalFileName))
        {
            score += 25;
        }

        if (SameText(known?.ProductName ?? item.ProductName, other?.ProductName))
        {
            score += 15;
        }

        if (SameText(known?.CompanyName ?? item.CompanyName, other?.CompanyName))
        {
            score += 12;
        }

        if (SameText(known?.SignerSubject, other?.SignerSubject))
        {
            score += 8;
        }

        // Metadata alone tops out at 95: only an identical hash proves "the same binary".
        // A file name alone (no metadata on either side) never reaches the relocation threshold.
        return Math.Min(score, MaxMetadataScore);
    }

    public static bool MatchesPath(ITrackedExecutable item, string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        return string.Equals(item.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
            || item.AlternatePaths.Any(path => string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> AllPaths(ITrackedExecutable item) =>
        new[] { item.NormalizedPath }
            .Concat(item.AlternatePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    public static string NormalizeExeName(string? exeName)
    {
        var name = Path.GetFileName((exeName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name.ToLowerInvariant()
            : $"{name}.exe".ToLowerInvariant();
    }

    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
