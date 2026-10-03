using System.Diagnostics;
using System.Security.Cryptography;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Identity;

/// <summary>
/// Cached executable metadata. Version info is cheap and read on first sight of a path;
/// signature verification and hashing are expensive and only run on demand (selection,
/// inspector, list entries), then reused until the file changes on disk.
/// </summary>
public sealed class ExecutableIdentityProvider : IExecutableIdentityProvider
{
    private const int MaxEntries = 4096;
    private const long MaxHashBytes = 768L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static ExecutableIdentityProvider Shared { get; } = new();

    public Task<ExecutableFingerprint?> DescribeAsync(
        string path,
        bool includeSignature,
        bool includeHash,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Describe(path, includeSignature, includeHash, cancellationToken), cancellationToken);

    public ExecutableFingerprint? Describe(
        string path,
        bool includeSignature,
        bool includeHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var size = file.Length;
        var lastWrite = file.LastWriteTimeUtc;
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(file.FullName, out entry!)
                || entry.SizeBytes != size
                || entry.LastWriteTimeUtc != lastWrite)
            {
                entry = new Entry(size, lastWrite, ReadVersionInfo(file.FullName, size, lastWrite));
                _entries[file.FullName] = entry;
                TrimIfNeeded();
            }
        }

        var fingerprint = entry.Fingerprint;
        if (includeSignature && fingerprint.SignerStatus is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = AuthenticodeInspector.Inspect(file.FullName);
            fingerprint = fingerprint with { SignerStatus = signature.Status, SignerSubject = signature.Subject };
        }

        if (includeHash && fingerprint.Sha256 is null && size <= MaxHashBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fingerprint = fingerprint with { Sha256 = TryHash(file.FullName, cancellationToken) };
        }

        if (!ReferenceEquals(fingerprint, entry.Fingerprint))
        {
            lock (_sync)
            {
                _entries[file.FullName] = entry with { Fingerprint = fingerprint };
            }
        }

        return fingerprint;
    }

    /// <summary>Cheap lookup for scans: returns metadata already known, reading version info once per path.</summary>
    internal ExecutableFingerprint? DescribeCached(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(path, out var entry))
            {
                return entry.Fingerprint;
            }
        }

        return Describe(path, includeSignature: false, includeHash: false);
    }

    private static ExecutableFingerprint ReadVersionInfo(string path, long size, DateTime lastWriteUtc)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new ExecutableFingerprint
            {
                FileDescription = Blank(version.FileDescription),
                ProductName = Blank(version.ProductName),
                CompanyName = Blank(version.CompanyName),
                OriginalFileName = Blank(version.OriginalFilename),
                FileVersion = Blank(version.FileVersion) ?? Blank(version.ProductVersion),
                FileSizeBytes = size,
                LastWriteTimeUtc = new DateTimeOffset(lastWriteUtc, TimeSpan.Zero)
            };
        }
        catch
        {
            return new ExecutableFingerprint
            {
                FileSizeBytes = size,
                LastWriteTimeUtc = new DateTimeOffset(lastWriteUtc, TimeSpan.Zero)
            };
        }
    }

    private static string? TryHash(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void TrimIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries.Keys.Take(_entries.Count - MaxEntries).ToArray())
        {
            _entries.Remove(key);
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Entry(long SizeBytes, DateTime LastWriteTimeUtc, ExecutableFingerprint Fingerprint);
}
