using System.IO;
using System.Windows;
using SessionPerfTracker.App.Services;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using Application = System.Windows.Application;

namespace SessionPerfTracker.App.ViewModels;

/// <summary>
/// Ban list and suspicious watchlist maintenance: path verification, relocation after moves or
/// updates, manual re-linking and adding executables that are not running.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool _isVerifyingTrackedPaths;
    private string _trackedPathsStatusText = string.Empty;

    public bool IsVerifyingTrackedPaths
    {
        get => _isVerifyingTrackedPaths;
        private set
        {
            if (SetProperty(ref _isVerifyingTrackedPaths, value))
            {
                OnPropertyChanged(nameof(CanVerifyTrackedPaths));
            }
        }
    }

    public bool CanVerifyTrackedPaths => !IsVerifyingTrackedPaths;

    public string UiTitleAddSuspicious => GetText("Ui_AddSuspiciousExeTitle");
    public string UiTitleAddBan => GetText("Ui_AddBanExeTitle");
    public string UiTitleLocateExecutable => GetText("Ui_LocateExeTitle");

    public bool CanRelocateSelectedProcessBan => SelectedProcessBan is not null;
    public bool CanRelocateSelectedSuspiciousItem => SelectedSuspiciousWatchItem is not null;

    public string TrackedPathsStatusText
    {
        get => _trackedPathsStatusText;
        private set => SetProperty(ref _trackedPathsStatusText, value);
    }

    public async Task VerifyTrackedPathsAsync(CancellationToken cancellationToken = default)
    {
        if (IsVerifyingTrackedPaths)
        {
            return;
        }

        IsVerifyingTrackedPaths = true;
        TrackedPathsStatusText = GetText("Ui_TrackedPathsVerifying");
        try
        {
            await _trackedExecutableCoordinator.EnsureFingerprintsAsync(cancellationToken);
            var result = await _trackedExecutableCoordinator.VerifyPathsAsync(forceSearch: true, cancellationToken);
            ApplyTrackedPathResult(result, manual: true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            TrackedPathsStatusText = FormatText("Ui_TrackedPathsVerifyFailed", error.Message);
        }
        finally
        {
            IsVerifyingTrackedPaths = false;
        }
    }

    public async Task RelocateSelectedProcessBanAsync(string newPath, CancellationToken cancellationToken = default)
    {
        if (SelectedProcessBan is null)
        {
            ProcessBanStatusText = GetText("Ui_SelectBanFirst");
            return;
        }

        if (!IsAllowedListPath(newPath, out var reason))
        {
            ProcessBanStatusText = reason;
            return;
        }

        var update = await _trackedExecutableCoordinator.RelocateManuallyAsync(
            TrackedListKind.Ban,
            SelectedProcessBan.Rule.Id,
            newPath,
            cancellationToken);
        RefreshProcessBanCollections();
        ProcessBanStatusText = update is null
            ? GetText("Ui_TrackedRelocateFailed")
            : FormatText("Ui_TrackedRelocated", update.ExeName, update.NewPath);
    }

    public async Task RelocateSelectedSuspiciousItemAsync(string newPath, CancellationToken cancellationToken = default)
    {
        if (SelectedSuspiciousWatchItem is null)
        {
            SuspiciousWatchStatusText = GetText("Ui_SelectSuspiciousFirst");
            return;
        }

        if (!IsAllowedListPath(newPath, out var reason))
        {
            SuspiciousWatchStatusText = reason;
            return;
        }

        var update = await _trackedExecutableCoordinator.RelocateManuallyAsync(
            TrackedListKind.Suspicious,
            SelectedSuspiciousWatchItem.Item.NormalizedPath,
            newPath,
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        SuspiciousWatchStatusText = update is null
            ? GetText("Ui_TrackedRelocateFailed")
            : FormatText("Ui_TrackedRelocated", update.ExeName, update.NewPath);
    }

    /// <summary>Adds an executable picked from disk to the suspicious watchlist (no need for it to run).</summary>
    public async Task AddSuspiciousExecutableAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedListPath(path, out var reason))
        {
            SuspiciousWatchStatusText = reason;
            return;
        }

        var normalizedPath = TrackedExecutableMatcher.NormalizePath(path);
        var fingerprint = await _trackedExecutableCoordinator.DescribeForNewEntryAsync(normalizedPath, cancellationToken);
        var exeName = TrackedExecutableMatcher.NormalizeExeName(Path.GetFileName(normalizedPath));
        await _thresholdSettingsStore.UpdateAsync(
            settings =>
            {
                var watchlist = settings.SuspiciousWatchlist;
                var items = watchlist.Items
                    .Where(item => !string.Equals(item.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                items.Insert(0, new SuspiciousWatchItem
                {
                    NormalizedPath = normalizedPath,
                    ExeName = exeName,
                    ProductName = fingerprint?.ProductName,
                    CompanyName = fingerprint?.CompanyName,
                    SignerStatus = fingerprint?.SignerStatus,
                    MarkedAt = DateTimeOffset.UtcNow,
                    Note = GetText("Ui_AddedFromFileNote"),
                    Fingerprint = fingerprint,
                    PathStatus = TrackedPathStatus.Present,
                    PathCheckedAt = DateTimeOffset.UtcNow
                });
                return settings with { SuspiciousWatchlist = watchlist with { Items = items } };
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        SelectedSuspiciousWatchItem = SuspiciousWatchItems.FirstOrDefault(item =>
            string.Equals(item.Item.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        SuspiciousWatchStatusText = FormatText("Ui_SuspiciousAddedFromFile", exeName);
    }

    /// <summary>Creates a ban for an executable picked from disk; it is enforced on the next scan.</summary>
    public async Task AddBanForExecutableAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedListPath(path, out var reason))
        {
            ProcessBanStatusText = reason;
            return;
        }

        var normalizedPath = TrackedExecutableMatcher.NormalizePath(path);
        var fingerprint = await _trackedExecutableCoordinator.DescribeForNewEntryAsync(normalizedPath, cancellationToken);
        var exeName = TrackedExecutableMatcher.NormalizeExeName(Path.GetFileName(normalizedPath));
        var duration = SelectedProcessBanDurationOption ?? ProcessBanDurationOptions.First();
        var target = new ProcessActionAuditTarget(
            ExeName: exeName,
            TargetText: exeName,
            NormalizedPath: normalizedPath,
            DisplayPath: path,
            ProductName: fingerprint?.ProductName ?? GetText("Ui_Unavailable"),
            CompanyName: fingerprint?.CompanyName ?? GetText("Ui_Unavailable"),
            SignerStatus: fingerprint?.SignerStatus ?? GetText("Ui_StatusUnknown"),
            ProcessId: null,
            IsGroup: false);

        if (!RequestDestructiveProcessActionConfirmation(
                GetText("Ui_ConfirmProcessBanTitle"),
                GetText("Ui_ActionCreateBan"),
                target.TargetText,
                target.DisplayPath,
                duration.Label,
                GetText("Ui_BanScopeFullPath"),
                GetText("Ui_BanImpactNoImmediateKill")))
        {
            await AddProcessActionAuditEventAsync(target, "ban_cancelled", duration.Label, 0, GetText("Ui_AuditUserCancelledConfirmation"), cancellationToken);
            ProcessBanStatusText = FormatText("Ui_ActionCancelledFor", GetText("Ui_ActionCreateBan"), exeName);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rule = new ProcessBanRule
        {
            Id = $"ban_{Guid.NewGuid():N}",
            NormalizedPath = normalizedPath,
            ExeName = exeName,
            ProductName = fingerprint?.ProductName,
            CompanyName = fingerprint?.CompanyName,
            SignerStatus = fingerprint?.SignerStatus,
            CreatedAt = now,
            ExpiresAt = duration.Duration is null ? null : now + duration.Duration.Value,
            DurationLabel = duration.Label,
            Reason = FormatText("Ui_ManualBanFrom", GetText("Ui_AddedFromFileNote")),
            Fingerprint = fingerprint,
            PathStatus = TrackedPathStatus.Present,
            PathCheckedAt = now
        };

        await _thresholdSettingsStore.UpdateAsync(
            settings =>
            {
                var bans = settings.ProcessBans;
                var active = bans.Active
                    .Where(item => !string.Equals(item.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                active.Insert(0, rule);
                var history = bans.History.ToList();
                history.Insert(0, _processBanCoordinator.CreateEvent(rule, "ban_created", null, 0, GetText("Ui_AddedFromFileNote")));
                return settings with
                {
                    ProcessBans = bans with
                    {
                        Active = active,
                        History = history.OrderByDescending(entry => entry.Timestamp).Take(bans.MaxHistory).ToList()
                    }
                };
            },
            cancellationToken);
        RefreshProcessBanCollections();
        ProcessBanStatusText = FormatText("Ui_ProcessBannedForDuration", rule.ExeName, rule.DurationLabel);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _signerTextByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Signature checks are too expensive for every scanned process, so they run lazily for the
    /// row the user actually looks at; the result is cached per file for later scans.
    /// </summary>
    private string ResolveSelectedSignerText()
    {
        var selected = SelectedGlobalProcess;
        if (selected is null)
        {
            return GetText("Ui_Unavailable");
        }

        if (!TryGetSelectedGlobalProcessUsablePath(out var path))
        {
            return ShowValue(selected.SignerStatus);
        }

        if (_signerTextByPath.TryGetValue(path, out var cached))
        {
            return cached;
        }

        _signerTextByPath[path] = GetText("Ui_SignerChecking");
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var fingerprint = await _identityProvider.DescribeAsync(path, includeSignature: true, includeHash: false).ConfigureAwait(false);
                text = fingerprint?.SignerStatus is null
                    ? GetText("Ui_StatusUnknown")
                    : string.IsNullOrWhiteSpace(fingerprint.SignerSubject)
                        ? ShowValue(fingerprint.SignerStatus)
                        : $"{ShowValue(fingerprint.SignerStatus)} — {fingerprint.SignerSubject}";
            }
            catch
            {
                text = GetText("Ui_StatusUnknown");
            }

            _signerTextByPath[path] = text;
            if (_signerTextByPath.Count > 2048)
            {
                _signerTextByPath.Clear();
            }

            await Application.Current.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(SelectedGlobalProcessSignerText)));
        });

        return _signerTextByPath[path];
    }

    /// <summary>Captures version/signature/hash for newly added entries without blocking the UI.</summary>
    private void QueueFingerprintCapture()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _trackedExecutableCoordinator.EnsureFingerprintsAsync(CancellationToken.None).ConfigureAwait(false) > 0)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        RefreshProcessBanCollections();
                        RefreshSuspiciousWatchCollections();
                    });
                }
            }
            catch
            {
            }
        });
    }

    private bool IsAllowedListPath(string path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            reason = GetText("Ui_TrackedFileMissing");
            return false;
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = GetText("Ui_TrackedOnlyExe");
            return false;
        }

        var safety = ProcessActionSafety.Assess(null, Path.GetFileName(path), path);
        if (!safety.IsAllowed)
        {
            reason = FormatText("Ui_ProtectedTargetReason", safety.Reason);
            return false;
        }

        return true;
    }

    private void ApplyTrackedPathUpdates(IReadOnlyList<TrackedPathUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        RefreshProcessBanCollections();
        RefreshSuspiciousWatchCollections();
        var latest = updates[^1];
        TrackedPathsStatusText = latest.IsAlternatePath
            ? FormatText("Ui_TrackedCopyDetected", latest.ExeName, latest.NewPath)
            : FormatText("Ui_TrackedRelocated", latest.ExeName, latest.NewPath);
        if (latest.List == TrackedListKind.Ban)
        {
            ProcessBanStatusText = TrackedPathsStatusText;
        }
        else
        {
            SuspiciousWatchStatusText = TrackedPathsStatusText;
        }
    }

    private void ApplyTrackedPathResult(TrackedPathVerification result, bool manual)
    {
        RefreshProcessBanCollections();
        RefreshSuspiciousWatchCollections();
        if (result.Updates.Count > 0)
        {
            ApplyTrackedPathUpdates(result.Updates);
        }

        if (manual || result.Missing > 0)
        {
            TrackedPathsStatusText = FormatText(
                "Ui_TrackedPathsVerified",
                result.Checked,
                result.Present,
                Math.Max(0, result.Missing),
                result.Updates.Count);
        }
    }
}
