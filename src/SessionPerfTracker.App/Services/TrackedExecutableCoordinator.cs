using System.Collections.Concurrent;
using System.IO;
using SessionPerfTracker.App.ViewModels;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;

namespace SessionPerfTracker.App.Services;

public enum TrackedListKind
{
    Ban,
    Suspicious
}

public sealed record TrackedPathUpdate(
    TrackedListKind List,
    string ExeName,
    string PreviousPath,
    string NewPath,
    string Reason,
    int Confidence,
    bool IsAlternatePath);

public sealed record TrackedPathVerification(
    int Checked,
    int Present,
    int Missing,
    IReadOnlyList<TrackedPathUpdate> Updates);

/// <summary>
/// Keeps ban rules and suspicious-watch entries attached to the right executable when it is moved,
/// updated into a new version folder, reinstalled elsewhere or copied. Uses running processes
/// (cheap, every scan) and a bounded file-system search (rare, background) as evidence.
/// </summary>
public sealed class TrackedExecutableCoordinator
{
    private static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RejectedCandidateRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutomaticSearchInterval = TimeSpan.FromHours(6);
    private readonly IThresholdSettingsStore _store;
    private readonly IExecutableIdentityProvider _identity;
    private readonly IExecutableLocator? _locator;
    private readonly Func<string, bool> _fileExists;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rejectedCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSearchByItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _verifyLock = new(1, 1);

    public TrackedExecutableCoordinator(
        IThresholdSettingsStore store,
        IExecutableIdentityProvider identity,
        IExecutableLocator? locator,
        Func<string, bool>? fileExists = null)
    {
        _store = store;
        _identity = identity;
        _locator = locator;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>Called after each Global Watch scan; cheap when nothing changed.</summary>
    public async Task<IReadOnlyList<TrackedPathUpdate>> ReconcileWithRunningProcessesAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _store.Current;
        if (settings.ProcessBans.Active.Count == 0 && settings.SuspiciousWatchlist.Items.Count == 0)
        {
            return [];
        }

        var rowsByPath = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath))
            .GroupBy(row => row.NormalizedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rowsByExe = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath))
            .GroupBy(row => TrackedExecutableMatcher.NormalizeExeName(row.ExeName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var plans = new List<ItemPlan>();
        foreach (var rule in settings.ProcessBans.Active)
        {
            if (await PlanFromRunningAsync(TrackedListKind.Ban, rule.Id, rule, rowsByPath, rowsByExe, capturedAt, cancellationToken) is { } plan)
            {
                plans.Add(plan);
            }
        }

        foreach (var item in settings.SuspiciousWatchlist.Items)
        {
            if (await PlanFromRunningAsync(TrackedListKind.Suspicious, item.NormalizedPath, item, rowsByPath, rowsByExe, capturedAt, cancellationToken) is { } plan)
            {
                plans.Add(plan);
            }
        }

        return await ApplyPlansAsync(plans, capturedAt, cancellationToken);
    }

    /// <summary>Checks that every saved path still exists and searches for missing executables.</summary>
    public async Task<TrackedPathVerification> VerifyPathsAsync(bool forceSearch, CancellationToken cancellationToken)
    {
        await _verifyLock.WaitAsync(cancellationToken);
        try
        {
            var settings = _store.Current;
            var now = DateTimeOffset.UtcNow;
            var plans = new List<ItemPlan>();
            var present = 0;
            var missing = 0;
            var items = settings.ProcessBans.Active
                .Select(rule => (List: TrackedListKind.Ban, Key: rule.Id, Item: (ITrackedExecutable)rule))
                .Concat(settings.SuspiciousWatchlist.Items
                    .Select(item => (List: TrackedListKind.Suspicious, Key: item.NormalizedPath, Item: (ITrackedExecutable)item)))
                .ToArray();

            foreach (var (list, key, item) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_fileExists(item.NormalizedPath))
                {
                    present++;
                    var status = item.PathStatus == TrackedPathStatus.Relocated ? TrackedPathStatus.Relocated : TrackedPathStatus.Present;
                    if (item.PathStatus != status || forceSearch)
                    {
                        plans.Add(new ItemPlan(list, key) { PathStatus = status, PathCheckedAt = now });
                    }

                    continue;
                }

                missing++;
                var searchKey = $"{list}|{key}";
                var searchDue = forceSearch
                    || !_lastSearchByItem.TryGetValue(searchKey, out var lastSearch)
                    || now - lastSearch >= AutomaticSearchInterval;
                ItemPlan? relocation = null;
                if (searchDue && _locator is not null)
                {
                    _lastSearchByItem[searchKey] = now;
                    relocation = await PlanFromFileSearchAsync(list, key, item, cancellationToken);
                }

                plans.Add(relocation ?? new ItemPlan(list, key)
                {
                    PathStatus = TrackedPathStatus.Missing,
                    PathCheckedAt = now
                });
            }

            var updates = await ApplyPlansAsync(plans, now, cancellationToken);
            return new TrackedPathVerification(items.Length, present, missing - updates.Count(update => !update.IsAlternatePath), updates);
        }
        finally
        {
            _verifyLock.Release();
        }
    }

    /// <summary>Fills in version/signature/hash facts for entries that do not have them yet.</summary>
    public async Task<int> EnsureFingerprintsAsync(CancellationToken cancellationToken)
    {
        var settings = _store.Current;
        var plans = new List<ItemPlan>();
        var items = settings.ProcessBans.Active
            .Select(rule => (List: TrackedListKind.Ban, Key: rule.Id, Item: (ITrackedExecutable)rule))
            .Concat(settings.SuspiciousWatchlist.Items
                .Select(item => (List: TrackedListKind.Suspicious, Key: item.NormalizedPath, Item: (ITrackedExecutable)item)));

        foreach (var (list, key, item) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Fingerprint?.Sha256 is not null && item.Fingerprint.SignerStatus is not null)
            {
                continue;
            }

            if (!_fileExists(item.NormalizedPath))
            {
                continue;
            }

            var fingerprint = await _identity.DescribeAsync(item.NormalizedPath, includeSignature: true, includeHash: true, cancellationToken);
            if (fingerprint is not null)
            {
                plans.Add(new ItemPlan(list, key) { Fingerprint = fingerprint.MergeMissingFrom(item.Fingerprint) });
            }
        }

        await ApplyPlansAsync(plans, DateTimeOffset.UtcNow, cancellationToken);
        return plans.Count;
    }

    /// <summary>User picked the new location explicitly.</summary>
    public async Task<TrackedPathUpdate?> RelocateManuallyAsync(
        TrackedListKind list,
        string key,
        string newPath,
        CancellationToken cancellationToken)
    {
        var normalized = TrackedExecutableMatcher.NormalizePath(newPath);
        if (string.IsNullOrWhiteSpace(normalized) || !_fileExists(normalized))
        {
            return null;
        }

        var fingerprint = await _identity.DescribeAsync(normalized, includeSignature: true, includeHash: true, cancellationToken);
        var plan = new ItemPlan(list, key)
        {
            NewPrimaryPath = normalized,
            Fingerprint = fingerprint,
            Reason = TrackedPathChangeReason.Manual,
            Confidence = 100,
            PathStatus = TrackedPathStatus.Relocated,
            PathCheckedAt = DateTimeOffset.UtcNow
        };
        var updates = await ApplyPlansAsync([plan], DateTimeOffset.UtcNow, cancellationToken);
        return updates.FirstOrDefault();
    }

    public Task<ExecutableFingerprint?> DescribeForNewEntryAsync(string path, CancellationToken cancellationToken) =>
        _identity.DescribeAsync(path, includeSignature: true, includeHash: true, cancellationToken);

    private async Task<ItemPlan?> PlanFromRunningAsync(
        TrackedListKind list,
        string key,
        ITrackedExecutable item,
        IReadOnlyDictionary<string, GlobalProcessRowViewModel> rowsByPath,
        IReadOnlyDictionary<string, GlobalProcessRowViewModel[]> rowsByExe,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var runningAtKnownPath = TrackedExecutableMatcher.AllPaths(item)
            .Select(path => rowsByPath.GetValueOrDefault(path))
            .FirstOrDefault(row => row is not null);
        if (runningAtKnownPath is not null)
        {
            var lastSeenStale = item.LastSeenRunningAt is null || capturedAt - item.LastSeenRunningAt.Value >= LastSeenPersistInterval;
            var statusStale = item.PathStatus is not (TrackedPathStatus.Present or TrackedPathStatus.Relocated);
            return lastSeenStale || statusStale
                ? new ItemPlan(list, key)
                {
                    LastSeenRunningAt = capturedAt,
                    PathStatus = statusStale ? TrackedPathStatus.Present : null
                }
                : null;
        }

        if (!rowsByExe.TryGetValue(TrackedExecutableMatcher.NormalizeExeName(item.ExeName), out var candidates))
        {
            return null;
        }

        var knownPaths = TrackedExecutableMatcher.AllPaths(item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates.Select(row => row.NormalizedFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (knownPaths.Contains(path) || IsRecentlyRejected(list, key, path, capturedAt))
            {
                continue;
            }

            var evaluation = await EvaluateCandidateAsync(item, path, cancellationToken);
            if (evaluation is null)
            {
                Reject(list, key, path, capturedAt);
                continue;
            }

            var (score, fingerprint) = evaluation.Value;
            var primaryExists = _fileExists(item.NormalizedPath);
            if (score >= TrackedExecutableMatcher.SameBinaryScore && primaryExists)
            {
                return new ItemPlan(list, key)
                {
                    AddAlternatePath = path,
                    Reason = TrackedPathChangeReason.SameBinaryCopy,
                    Confidence = score,
                    LastSeenRunningAt = capturedAt
                };
            }

            if (score >= TrackedExecutableMatcher.RelocationThreshold && !primaryExists)
            {
                return new ItemPlan(list, key)
                {
                    NewPrimaryPath = path,
                    Fingerprint = fingerprint,
                    Reason = TrackedPathChangeReason.RunningProcessMatch,
                    Confidence = score,
                    PathStatus = TrackedPathStatus.Relocated,
                    PathCheckedAt = capturedAt,
                    LastSeenRunningAt = capturedAt
                };
            }

            Reject(list, key, path, capturedAt);
        }

        return null;
    }

    private async Task<ItemPlan?> PlanFromFileSearchAsync(
        TrackedListKind list,
        string key,
        ITrackedExecutable item,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> candidates;
        try
        {
            candidates = await _locator!.FindCandidatePathsAsync(
                item.NormalizedPath,
                item.ExeName,
                item.Fingerprint?.ProductName ?? item.ProductName,
                maxResults: 12,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var best = (Score: 0, Path: string.Empty, Fingerprint: (ExecutableFingerprint?)null);
        foreach (var candidate in candidates)
        {
            var normalized = TrackedExecutableMatcher.NormalizePath(candidate);
            var evaluation = await EvaluateCandidateAsync(item, normalized, cancellationToken);
            if (evaluation is { } result && result.Score > best.Score)
            {
                best = (result.Score, normalized, result.Fingerprint);
            }
        }

        return best.Score >= TrackedExecutableMatcher.RelocationThreshold
            ? new ItemPlan(list, key)
            {
                NewPrimaryPath = best.Path,
                Fingerprint = best.Fingerprint,
                Reason = TrackedPathChangeReason.FileSearch,
                Confidence = best.Score,
                PathStatus = TrackedPathStatus.Relocated,
                PathCheckedAt = DateTimeOffset.UtcNow
            }
            : null;
    }

    private async Task<(int Score, ExecutableFingerprint? Fingerprint)?> EvaluateCandidateAsync(
        ITrackedExecutable item,
        string path,
        CancellationToken cancellationToken)
    {
        // Never move a list entry onto a protected system binary or onto this app itself.
        if (!ProcessActionSafetyPolicy.Default.Assess(null, Path.GetFileName(path), path).IsAllowed)
        {
            return null;
        }

        var known = item.Fingerprint;
        var basic = await _identity.DescribeAsync(path, includeSignature: false, includeHash: false, cancellationToken);
        if (basic is null)
        {
            return null;
        }

        // Hash only when it can prove "same binary": identical size and a known reference hash.
        var hashUseful = known?.Sha256 is not null
            && known.FileSizeBytes is not null
            && known.FileSizeBytes == basic.FileSizeBytes;
        var signatureUseful = known?.SignerSubject is not null;
        var fingerprint = hashUseful || signatureUseful
            ? await _identity.DescribeAsync(path, signatureUseful, hashUseful, cancellationToken) ?? basic
            : basic;

        var exeName = Path.GetFileName(path);
        var score = TrackedExecutableMatcher.Score(item, new ExecutableCandidate(path, exeName, fingerprint));
        return (score, fingerprint);
    }

    private async Task<IReadOnlyList<TrackedPathUpdate>> ApplyPlansAsync(
        IReadOnlyList<ItemPlan> plans,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (plans.Count == 0)
        {
            return [];
        }

        var updates = new List<TrackedPathUpdate>();
        await _store.UpdateAsync(settings =>
        {
            updates.Clear();
            var bans = settings.ProcessBans;
            var banHistory = bans.History.ToList();
            var active = bans.Active
                .Select(rule =>
                {
                    var plan = plans.FirstOrDefault(item => item.List == TrackedListKind.Ban && item.Key == rule.Id);
                    if (plan is null)
                    {
                        return rule;
                    }

                    var updated = ApplyToBan(rule, plan, now, out var update);
                    if (update is not null)
                    {
                        updates.Add(update);
                        banHistory.Insert(0, new ProcessBanEvent
                        {
                            Id = $"ban_event_{Guid.NewGuid():N}",
                            Timestamp = now,
                            NormalizedPath = updated.NormalizedPath,
                            ExeName = updated.ExeName,
                            ProductName = updated.ProductName,
                            CompanyName = updated.CompanyName,
                            SignerStatus = updated.SignerStatus,
                            Action = update.IsAlternatePath ? "ban_path_added" : "ban_path_updated",
                            DurationLabel = updated.DurationLabel,
                            Details = $"{update.PreviousPath} -> {update.NewPath} ({update.Reason}, confidence {update.Confidence}%)."
                        });
                    }

                    return updated;
                })
                .ToList();

            var watchlist = settings.SuspiciousWatchlist;
            var items = watchlist.Items
                .Select(item =>
                {
                    var plan = plans.FirstOrDefault(entry => entry.List == TrackedListKind.Suspicious
                        && string.Equals(entry.Key, item.NormalizedPath, StringComparison.OrdinalIgnoreCase));
                    if (plan is null)
                    {
                        return item;
                    }

                    var updated = ApplyToSuspicious(item, plan, now, out var update);
                    if (update is not null)
                    {
                        updates.Add(update);
                    }

                    return updated;
                })
                .ToList();

            return settings with
            {
                ProcessBans = bans with
                {
                    Active = active,
                    History = banHistory.OrderByDescending(entry => entry.Timestamp).Take(bans.MaxHistory).ToList()
                },
                SuspiciousWatchlist = watchlist with { Items = items }
            };
        }, cancellationToken);

        return updates;
    }

    private static ProcessBanRule ApplyToBan(ProcessBanRule rule, ItemPlan plan, DateTimeOffset now, out TrackedPathUpdate? update)
    {
        update = null;
        var result = rule with
        {
            LastSeenRunningAt = plan.LastSeenRunningAt ?? rule.LastSeenRunningAt,
            PathStatus = plan.PathStatus ?? rule.PathStatus,
            PathCheckedAt = plan.PathCheckedAt ?? rule.PathCheckedAt,
            Fingerprint = plan.Fingerprint?.MergeMissingFrom(plan.NewPrimaryPath is null ? rule.Fingerprint : null) ?? rule.Fingerprint,
            // Fresh signature facts replace values captured by older builds (which reported every file as unsigned).
            SignerStatus = plan.Fingerprint?.SignerStatus ?? rule.SignerStatus,
            ProductName = rule.ProductName ?? plan.Fingerprint?.ProductName,
            CompanyName = rule.CompanyName ?? plan.Fingerprint?.CompanyName
        };

        if (plan.NewPrimaryPath is { } newPath && !string.Equals(newPath, rule.NormalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            var change = CreateChange(rule.NormalizedPath, newPath, plan, now);
            update = new TrackedPathUpdate(TrackedListKind.Ban, rule.ExeName, rule.NormalizedPath, newPath, change.Reason, change.Confidence, false);
            result = result with
            {
                NormalizedPath = newPath,
                ExeName = TrackedExecutableMatcher.NormalizeExeName(Path.GetFileName(newPath)),
                AlternatePaths = result.AlternatePaths.Where(path => !string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase)).ToList(),
                PathHistory = [change, .. result.PathHistory],
                ProductName = plan.Fingerprint?.ProductName ?? rule.ProductName,
                CompanyName = plan.Fingerprint?.CompanyName ?? rule.CompanyName,
                SignerStatus = plan.Fingerprint?.SignerStatus ?? rule.SignerStatus
            };
        }

        if (plan.AddAlternatePath is { } alternate && !TrackedExecutableMatcher.MatchesPath(result, alternate))
        {
            var change = CreateChange(rule.NormalizedPath, alternate, plan, now);
            update = new TrackedPathUpdate(TrackedListKind.Ban, rule.ExeName, rule.NormalizedPath, alternate, change.Reason, change.Confidence, true);
            result = result with
            {
                AlternatePaths = [.. result.AlternatePaths, alternate],
                PathHistory = [change, .. result.PathHistory]
            };
        }

        return result;
    }

    private static SuspiciousWatchItem ApplyToSuspicious(SuspiciousWatchItem item, ItemPlan plan, DateTimeOffset now, out TrackedPathUpdate? update)
    {
        update = null;
        var result = item with
        {
            LastSeenRunningAt = plan.LastSeenRunningAt ?? item.LastSeenRunningAt,
            PathStatus = plan.PathStatus ?? item.PathStatus,
            PathCheckedAt = plan.PathCheckedAt ?? item.PathCheckedAt,
            Fingerprint = plan.Fingerprint?.MergeMissingFrom(plan.NewPrimaryPath is null ? item.Fingerprint : null) ?? item.Fingerprint,
            SignerStatus = plan.Fingerprint?.SignerStatus ?? item.SignerStatus,
            ProductName = item.ProductName ?? plan.Fingerprint?.ProductName,
            CompanyName = item.CompanyName ?? plan.Fingerprint?.CompanyName
        };

        if (plan.NewPrimaryPath is { } newPath && !string.Equals(newPath, item.NormalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            var change = CreateChange(item.NormalizedPath, newPath, plan, now);
            update = new TrackedPathUpdate(TrackedListKind.Suspicious, item.ExeName, item.NormalizedPath, newPath, change.Reason, change.Confidence, false);
            result = result with
            {
                NormalizedPath = newPath,
                ExeName = TrackedExecutableMatcher.NormalizeExeName(Path.GetFileName(newPath)),
                AlternatePaths = result.AlternatePaths.Where(path => !string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase)).ToList(),
                PathHistory = [change, .. result.PathHistory],
                ProductName = plan.Fingerprint?.ProductName ?? item.ProductName,
                CompanyName = plan.Fingerprint?.CompanyName ?? item.CompanyName,
                SignerStatus = plan.Fingerprint?.SignerStatus ?? item.SignerStatus
            };
        }

        if (plan.AddAlternatePath is { } alternate && !TrackedExecutableMatcher.MatchesPath(result, alternate))
        {
            var change = CreateChange(item.NormalizedPath, alternate, plan, now);
            update = new TrackedPathUpdate(TrackedListKind.Suspicious, item.ExeName, item.NormalizedPath, alternate, change.Reason, change.Confidence, true);
            result = result with
            {
                AlternatePaths = [.. result.AlternatePaths, alternate],
                PathHistory = [change, .. result.PathHistory]
            };
        }

        return result;
    }

    private static TrackedPathChange CreateChange(string previousPath, string newPath, ItemPlan plan, DateTimeOffset now) => new()
    {
        PreviousPath = previousPath,
        NewPath = newPath,
        ChangedAt = now,
        Reason = plan.Reason ?? TrackedPathChangeReason.RunningProcessMatch,
        Confidence = plan.Confidence
    };

    private bool IsRecentlyRejected(TrackedListKind list, string key, string path, DateTimeOffset now) =>
        _rejectedCandidates.TryGetValue($"{list}|{key}|{path}", out var rejectedAt)
        && now - rejectedAt < RejectedCandidateRetry;

    private void Reject(TrackedListKind list, string key, string path, DateTimeOffset now) =>
        _rejectedCandidates[$"{list}|{key}|{path}"] = now;

    private sealed record ItemPlan(TrackedListKind List, string Key)
    {
        public string? NewPrimaryPath { get; init; }
        public string? AddAlternatePath { get; init; }
        public ExecutableFingerprint? Fingerprint { get; init; }
        public string? Reason { get; init; }
        public int Confidence { get; init; }
        public string? PathStatus { get; init; }
        public DateTimeOffset? PathCheckedAt { get; init; }
        public DateTimeOffset? LastSeenRunningAt { get; init; }
    }
}
