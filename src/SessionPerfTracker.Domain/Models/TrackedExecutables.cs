namespace SessionPerfTracker.Domain.Models;

/// <summary>
/// A list entry (ban rule, suspicious item) that follows one executable across moves and updates.
/// </summary>
public interface ITrackedExecutable
{
    string NormalizedPath { get; }
    string ExeName { get; }
    string? ProductName { get; }
    string? CompanyName { get; }
    string? SignerStatus { get; }
    ExecutableFingerprint? Fingerprint { get; }
    List<string> AlternatePaths { get; }
    List<TrackedPathChange> PathHistory { get; }
    string? PathStatus { get; }
    DateTimeOffset? PathCheckedAt { get; }
    DateTimeOffset? LastSeenRunningAt { get; }
}

/// <summary>Identity facts captured when an executable is added to a list.</summary>
public sealed record ExecutableFingerprint
{
    public string? FileDescription { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? OriginalFileName { get; init; }
    public string? FileVersion { get; init; }
    public string? SignerStatus { get; init; }
    public string? SignerSubject { get; init; }
    public long? FileSizeBytes { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public string? Sha256 { get; init; }

    public bool HasMetadata =>
        !string.IsNullOrWhiteSpace(ProductName)
        || !string.IsNullOrWhiteSpace(CompanyName)
        || !string.IsNullOrWhiteSpace(OriginalFileName)
        || !string.IsNullOrWhiteSpace(SignerSubject);

    /// <summary>Keeps known values when a newer probe could not read them (e.g. file locked).</summary>
    public ExecutableFingerprint MergeMissingFrom(ExecutableFingerprint? older) => older is null
        ? this
        : this with
        {
            FileDescription = FileDescription ?? older.FileDescription,
            ProductName = ProductName ?? older.ProductName,
            CompanyName = CompanyName ?? older.CompanyName,
            OriginalFileName = OriginalFileName ?? older.OriginalFileName,
            FileVersion = FileVersion ?? older.FileVersion,
            SignerStatus = SignerStatus ?? older.SignerStatus,
            SignerSubject = SignerSubject ?? older.SignerSubject,
            FileSizeBytes = FileSizeBytes ?? older.FileSizeBytes,
            LastWriteTimeUtc = LastWriteTimeUtc ?? older.LastWriteTimeUtc,
            Sha256 = Sha256 ?? older.Sha256
        };
}

public sealed record TrackedPathChange
{
    public required string PreviousPath { get; init; }
    public required string NewPath { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
    public required string Reason { get; init; }
    public int Confidence { get; init; }
}

public static class TrackedPathStatus
{
    public const string Present = "present";
    public const string Missing = "missing";
    public const string Relocated = "relocated";
    public const string Unknown = "unknown";
}

public static class TrackedPathChangeReason
{
    public const string RunningProcessMatch = "running-process";
    public const string FileSearch = "file-search";
    public const string Manual = "manual";
    public const string SameBinaryCopy = "same-binary-copy";
}
