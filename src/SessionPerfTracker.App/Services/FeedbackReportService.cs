using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.Services;

public sealed class FeedbackReportService
{
    private const string DefaultNtfyTopic = "spt-grollex-feedback-8f3d7c2a51b44e6ca9d0";
    private static readonly Uri DefaultNtfyUri = new($"https://ntfy.sh/{DefaultNtfyTopic}");
    private readonly HttpClient _httpClient;
    private readonly Uri _feedbackUri;

    public FeedbackReportService(HttpClient? httpClient = null, Uri? feedbackUri = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _feedbackUri = feedbackUri ?? DefaultNtfyUri;
    }

    public async Task<string?> SaveAsync(
        string kind,
        string text,
        string? screenshotPath,
        FeedbackReportContext context,
        bool includeLatestSession,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var directory = GetFeedbackDirectory();
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.Now;
        var fileName = $"{timestamp:yyyyMMdd_HHmmss}_{kind}.md";
        var path = Path.Combine(directory, fileName);
        var content = BuildReport(
            kind,
            text.Trim(),
            timestamp,
            includePrivateSystemInfo: true,
            screenshotPath: screenshotPath,
            includeLatestSession: includeLatestSession,
            context: context);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        PruneScreenshots(directory);
        return path;
    }

    public async Task<FeedbackDeliveryResult> TrySendAsync(
        string title,
        string tags,
        string kind,
        string text,
        string? screenshotPath,
        FeedbackReportContext context,
        bool includeLatestSession,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = BuildReport(
                kind,
                text,
                DateTimeOffset.Now,
                includePrivateSystemInfo: false,
                screenshotPath: screenshotPath is null ? null : Path.GetFileName(screenshotPath),
                includeLatestSession: includeLatestSession,
                context: context);
            using var request = new HttpRequestMessage(HttpMethod.Post, _feedbackUri)
            {
                Content = new StringContent(report, Encoding.UTF8, "text/plain")
            };
            request.Headers.TryAddWithoutValidation("Title", $"Session Perf Tracker: {title}");
            request.Headers.TryAddWithoutValidation("Tags", tags);
            request.Headers.TryAddWithoutValidation("Priority", kind == "bug-report" ? "4" : "3");
            request.Headers.TryAddWithoutValidation("Markdown", "yes");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new FeedbackDeliveryResult(false, $"Remote service returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (string.IsNullOrWhiteSpace(screenshotPath))
            {
                return new FeedbackDeliveryResult(true, null);
            }

            var attachment = await TrySendAttachmentAsync(screenshotPath, kind, cancellationToken);
            return attachment.Sent
                ? new FeedbackDeliveryResult(true, null)
                : attachment;
        }
        catch (Exception error)
        {
            return new FeedbackDeliveryResult(false, error.Message);
        }
    }

    public string GetFeedbackDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SessionPerfTracker",
            "feedback");

    public FileInfo? FindLatestReport()
    {
        var directory = GetFeedbackDirectory();
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return new DirectoryInfo(directory)
            .EnumerateFiles("*.md", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    public void OpenFeedbackDirectory()
    {
        var directory = GetFeedbackDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private async Task<FeedbackDeliveryResult> TrySendAttachmentAsync(
        string screenshotPath,
        string kind,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(screenshotPath))
            {
                return new FeedbackDeliveryResult(false, "Screenshot attachment file was not found.");
            }

            await using var stream = File.OpenRead(screenshotPath);
            using var request = new HttpRequestMessage(HttpMethod.Put, _feedbackUri)
            {
                Content = new StreamContent(stream)
            };
            request.Headers.TryAddWithoutValidation("Title", $"Session Perf Tracker: {kind} screenshot");
            request.Headers.TryAddWithoutValidation("Tags", "camera");
            request.Headers.TryAddWithoutValidation("Priority", "3");
            request.Headers.TryAddWithoutValidation("Filename", Path.GetFileName(screenshotPath));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? new FeedbackDeliveryResult(true, null)
                : new FeedbackDeliveryResult(false, $"Screenshot upload returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception error)
        {
            return new FeedbackDeliveryResult(false, error.Message);
        }
    }

    private string BuildReport(
        string kind,
        string text,
        DateTimeOffset timestamp,
        bool includePrivateSystemInfo,
        string? screenshotPath,
        bool includeLatestSession,
        FeedbackReportContext context)
    {
        var lines = new List<string>
        {
            $"# Session Perf Tracker {kind}",
            "",
            $"Created: {timestamp:yyyy-MM-dd HH:mm:ss zzz}",
            $"Version: {context.CurrentVersionText}",
            $"OS: {Environment.OSVersion}",
            $"Storage: {context.StoragePath}",
            $"Selected tab: {context.SelectedTabIndex}",
            $"Recording: {context.IsRecording}",
            $"Live target: {context.LiveSessionTargetText}",
            $"Live status: {context.LiveSessionStateText}",
            $"Capture: {context.LiveConfigCollectorsText}",
            $"Session profile: {context.ActiveThresholdSourceText}",
            $"Storage status: {context.StorageStatusText}",
            $"Export status: {context.ExportStatusText}",
            $"Update status: {context.UpdateStatusText}",
            "",
            "## User text",
            "",
            text,
            "",
            "## Recent live warning",
            "",
            string.IsNullOrWhiteSpace(context.LiveWarningText) ? "None" : context.LiveWarningText
        };

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            lines.Add("");
            lines.Add("## Screenshot");
            lines.Add("");
            lines.Add(screenshotPath);
        }

        if (includeLatestSession && context.LatestSession is { } latestSession)
        {
            lines.Add("");
            lines.Add("## Latest session summary");
            lines.Add("");
            AddLatestSessionSummary(lines, latestSession);
        }

        if (includePrivateSystemInfo)
        {
            lines.Insert(6, $"User: {Environment.UserName}");
            lines.Insert(6, $"Machine: {Environment.MachineName}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void PruneScreenshots(string directory)
    {
        const int maxScreenshotFiles = 40;
        var oldestAllowed = DateTimeOffset.Now.AddDays(-30);

        try
        {
            var screenshots = Directory
                .EnumerateFiles(directory, "*_screenshot.png", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            foreach (var file in screenshots
                         .Where(file => file.LastWriteTimeUtc < oldestAllowed.UtcDateTime)
                         .Concat(screenshots.Skip(maxScreenshotFiles))
                         .DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // Feedback cleanup must never block saving or sending a report.
                }
            }
        }
        catch
        {
        }
    }

    private static void AddLatestSessionSummary(List<string> lines, SessionRecord session)
    {
        lines.Add($"Target: {session.Target.DisplayName}");
        lines.Add($"Started: {session.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        lines.Add($"Duration: {(int)session.Summary.Duration.TotalMinutes}m {session.Summary.Duration.Seconds:00}s");
        lines.Add($"Status: {session.Status}");
        lines.Add($"Events: {session.Summary.EventCount}");
        lines.Add($"Spikes: {session.Summary.SpikeCount}");
        lines.Add($"Breaches: {session.Summary.ThresholdBreachCount}");
        lines.Add($"Stability: {session.Summary.StabilityStatus}");
        if (!string.IsNullOrWhiteSpace(session.Summary.StabilityReason))
        {
            lines.Add($"Stability reason: {session.Summary.StabilityReason}");
        }

        foreach (var metric in session.Summary.Metrics.Take(6))
        {
            lines.Add($"{metric.Label}: avg {metric.Avg:N1} {metric.Unit}, max {metric.Max:N1} {metric.Unit}, breaches {metric.ThresholdBreaches}");
        }
    }
}

public sealed record FeedbackReportContext(
    string CurrentVersionText,
    string StoragePath,
    int SelectedTabIndex,
    bool IsRecording,
    string LiveSessionTargetText,
    string LiveSessionStateText,
    string LiveConfigCollectorsText,
    string ActiveThresholdSourceText,
    string StorageStatusText,
    string ExportStatusText,
    string UpdateStatusText,
    string LiveWarningText,
    SessionRecord? LatestSession);

public sealed record FeedbackDeliveryResult(bool Sent, string? Error);
