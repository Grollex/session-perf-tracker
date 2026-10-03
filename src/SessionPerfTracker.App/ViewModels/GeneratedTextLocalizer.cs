using System.Globalization;
using System.Text.RegularExpressions;

namespace SessionPerfTracker.App.ViewModels;

/// <summary>
/// Translates the English sentences that the recorder stores with a session (exit reasons, stability
/// reasons, notes, recommendation evidence) at display time. Stored data stays English so old sessions,
/// filters and exports keep working; unknown text passes through unchanged.
/// </summary>
public static partial class GeneratedTextLocalizer
{
    private const string Count = @"(?<a>\d[\d\s,.  ]*)";
    private const string ExitCode = @"(?<a>-?\d+(?: \(0x[0-9A-Fa-f]{8}\))?|unknown)";

    private static readonly (Regex Pattern, string Key)[] Rules =
    [
        (Rule(@"The target process closed outside SessionPerfTracker normally\."), "Ui_GenClosedNormally"),
        (Rule(@"The target exited while it was not responding\."), "Ui_GenExitedHung"),
        (Rule(@"The target process disappeared while monitoring was active, and no exit code was available\."), "Ui_GenDisappeared"),
        (Rule(@"The target process terminated with crash status (?<a>0x[0-9A-Fa-f]{8}) \((?<b>[^)]+)\)\."), "Ui_GenCrashStatus"),
        (Rule($@"The target process exited with non-zero exit code {ExitCode}\."), "Ui_GenNonZeroExit"),
        (Rule(@"Exit code: unavailable\."), "Ui_GenExitCodeUnavailable"),
        (Rule($@"Exit code: {ExitCode}\."), "Ui_GenExitCode"),
        (Rule(@"This classification is best-effort\."), "Ui_GenBestEffort"),
        (Rule($@"Root process exited at \+(?<b>\d{{2}}:\d{{2}}:\d{{2}}) \(exit code {ExitCode.Replace("?<a>", "?<c>")}\); recording continued for {Count} child process\(es\)\."), "Ui_GenRootExitedContinued"),
        (Rule(@"Metric collection kept failing: (?<a>.+?)(?=$| Root process exited)"), "Ui_GenCollectionFailing"),
        (Rule(@"Monitoring stopped after an internal error: (?<a>.+?)(?=$| Root process exited)"), "Ui_GenInternalError"),
        (Rule(@"No stability events were recorded\."), "Ui_StabilityReasonHealthy"),
        (Rule(@"Best-effort not responding event was detected\."), "Ui_GenHungDetected"),
        (Rule(@"Performance or startup stability events were detected\."), "Ui_GenPerfEvents"),
        (Rule(@"Best-effort crash-like exit signal was recorded\."), "Ui_StabilityReasonCrashLike"),
        (Rule(@"Target exited unexpectedly, but no crash-like signal was recorded\."), "Ui_StabilityReasonUnexpectedExit"),
        (Rule(@"Target closed outside SessionPerfTracker\."), "Ui_GenTargetClosedOutside"),
        (Rule(@"Target exited without a known normal signal\."), "Ui_GenNoNormalSignal"),
        (Rule(@"Target completed normally\."), "Ui_GenCompletedNormally"),
        (Rule(@"Exit classification is not available\."), "Ui_GenExitUnknown"),
        (Rule(@"A crash-like exit was recorded\."), "Ui_GenCrashLikeRecorded"),
        (Rule(@"The target exited without a normal stop signal\."), "Ui_GenNoStopSignal"),
        (Rule($@"Recorded {Count} samples with no stability events\."), "Ui_GenSamplesNoEvents"),
        (Rule($@"Only {Count} sample\(s\) were recorded\."), "Ui_GenOnlySamples"),
        (Rule(@"This session tracked only the root process\."), "Ui_GenRootOnly"),
        (Rule($@"{Count} not-responding event\(s\) were recorded\."), "Ui_GenHangEvents"),
        (Rule(@"Startup readiness was delayed for (?<a>\d+(?:[.,]\d+)?)s\."), "Ui_GenStartupDelayed"),
        // Event titles.
        (Rule(@"^External close / graceful exit$"), "Ui_GenEvtExternalClose"),
        (Rule(@"^Crash-like exit$"), "Ui_GenEvtCrashLike"),
        (Rule(@"^Unexpected exit$"), "Ui_GenEvtUnexpected"),
        (Rule(@"^Long startup$"), "Ui_GenEvtLongStartup"),
        (Rule(@"^Hang suspected$"), "Ui_GenEvtHang"),
        (Rule(@"^Not responding$"), "Ui_GenEvtNotResponding"),
        (Rule(@"^Recording interrupted$"), "Ui_GenEvtInterrupted"),
        (Rule(@"^(?<a>.+?) threshold breach$"), "Ui_GenEvtBreach"),
        (Rule(@"^(?<a>.+?) grouped event$"), "Ui_GenEvtGrouped"),
        (Rule(@"^(?<a>.+?) spike$"), "Ui_GenEvtSpike"),
        // Global Watch profile reasons such as "CPU over 85%".
        (Rule(@"^(?<a>.+?) critical over (?<b>.+)$"), "Ui_GenCriticalOver"),
        (Rule(@"^(?<a>.+?) over (?<b>.+)$"), "Ui_GenOver"),
        (Rule(@"^(?<a>.+?) near (?<b>.+)$"), "Ui_GenNear")
    ];

    // Identity/profile values keep English sentinels because other code compares them.
    private static readonly Dictionary<string, string> ValueKeys = new(StringComparer.Ordinal)
    {
        ["Unavailable"] = "Ui_Unavailable",
        ["n/a"] = "Ui_Unavailable",
        ["Unknown"] = "Ui_StatusUnknown",
        ["Signed"] = "Ui_GenSigned",
        ["Signed (Windows catalog)"] = "Ui_GenSignedCatalog",
        ["Signed (untrusted certificate)"] = "Ui_GenSignedUntrusted",
        ["Signed (expired certificate)"] = "Ui_GenSignedExpired",
        ["Signature invalid"] = "Ui_GenSignatureInvalid",
        ["Unsigned"] = "Ui_GenUnsigned",
        ["Application group"] = "Ui_GenApplicationGroup",
        ["Within profile"] = "Ui_GenWithinProfile",
        ["profile limit"] = "Ui_GenProfileLimit",
        ["fallback limit"] = "Ui_GenFallbackLimit"
    };

    /// <summary>Translates a single stored value such as a signer status, "Unavailable" or "Global fallback (Games)".</summary>
    public static string LocalizeValue(string? value, Func<string, string> getText)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return getText("Ui_Unavailable");
        }

        if (ValueKeys.TryGetValue(value, out var key))
        {
            var translated = getText(key);
            return translated == key ? value : translated;
        }

        const string fallbackPrefix = "Global fallback (";
        if (value.StartsWith(fallbackPrefix, StringComparison.Ordinal) && value.EndsWith(')'))
        {
            var fallback = getText("Ui_GlobalFallbackSource");
            return fallback == "Ui_GlobalFallbackSource"
                ? value
                : $"{fallback} ({value[fallbackPrefix.Length..^1]})";
        }

        return value;
    }

    public static string Localize(string? text, Func<string, string> getText)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var (pattern, key) in Rules)
        {
            result = pattern.Replace(result, match =>
            {
                var format = getText(key);
                if (format == key)
                {
                    return match.Value;
                }

                var second = match.Groups["b"].Value;
                return string.Format(
                    CultureInfo.CurrentCulture,
                    format,
                    match.Groups["a"].Value.Trim(),
                    second.Length == 0 ? second : LocalizeValue(second, getText),
                    match.Groups["c"].Value);
            });
        }

        return result;
    }

    // Interpreted on purpose: inputs are a few short sentences, compiling 27 patterns would cost more.
    private static Regex Rule(string pattern) => new(pattern, RegexOptions.CultureInvariant);
}
