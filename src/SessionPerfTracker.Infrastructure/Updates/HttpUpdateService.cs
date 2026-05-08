using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Updates;

public sealed class HttpUpdateService : IUpdateService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public HttpUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        CurrentVersion = ResolveCurrentVersion();
    }

    public string CurrentVersion { get; }

    public async Task<UpdateCheckResult> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                Status = "Update manifest URL is not configured."
            };
        }

        try
        {
            var (json, source) = await ReadManifestAsync(manifestUrl.Trim(), cancellationToken);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, SerializerOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.InstallerUrl))
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = CurrentVersion,
                    Status = "Update manifest is missing version or installerUrl."
                };
            }

            manifest = manifest with
            {
                InstallerUrl = ResolveInstallerUrl(source, manifest.InstallerUrl.Trim()),
                Sha256 = string.IsNullOrWhiteSpace(manifest.Sha256) ? null : manifest.Sha256.Trim()
            };

            if (!TryParseVersion(CurrentVersion, out var currentVersion)
                || !TryParseVersion(manifest.Version, out var latestVersion))
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = CurrentVersion,
                    LatestVersion = manifest.Version,
                    Manifest = manifest,
                    Status = $"Latest version {manifest.Version} found, but version comparison was not possible."
                };
            }

            var hasUpdate = latestVersion > currentVersion;
            return new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = manifest.Version,
                IsUpdateAvailable = hasUpdate,
                Manifest = manifest,
                Status = hasUpdate
                    ? $"Update available: {CurrentVersion} -> {manifest.Version}."
                    : $"Session Perf Tracker is up to date ({CurrentVersion})."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                Status = $"Update check failed: {error.Message}"
            };
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateManifest manifest,
        string updateDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.InstallerUrl))
        {
            throw new InvalidOperationException("Update manifest does not include an installer URL.");
        }

        Directory.CreateDirectory(updateDirectory);
        var fileName = GetInstallerFileName(manifest);
        var destinationPath = Path.Combine(updateDirectory, fileName);

        if (Uri.TryCreate(manifest.InstallerUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            await using var stream = await _httpClient.GetStreamAsync(uri, cancellationToken);
            await using var output = File.Create(destinationPath);
            await stream.CopyToAsync(output, cancellationToken);
        }
        else
        {
            var sourcePath = Uri.TryCreate(manifest.InstallerUrl, UriKind.Absolute, out var fileUri) && fileUri.IsFile
                ? fileUri.LocalPath
                : manifest.InstallerUrl;
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Installer package was not found.", sourcePath);
            }

            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            await VerifySha256Async(destinationPath, manifest.Sha256, cancellationToken);
        }

        return destinationPath;
    }

    public Task LaunchInstallerAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Downloaded installer was not found.", installerPath);
        }

        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Verb = "runas"
        });

        return Task.CompletedTask;
    }

    private static async Task<(string Json, string Source)> ReadManifestAsync(
        string manifestUrl,
        CancellationToken cancellationToken)
    {
        if (File.Exists(manifestUrl))
        {
            var path = Path.GetFullPath(manifestUrl);
            return (await File.ReadAllTextAsync(path, cancellationToken), path);
        }

        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Update manifest URL must be a URL or a local file path.");
        }

        if (uri.IsFile)
        {
            return (await File.ReadAllTextAsync(uri.LocalPath, cancellationToken), uri.LocalPath);
        }

        using var client = new HttpClient();
        return (await client.GetStringAsync(uri, cancellationToken), manifestUrl);
    }

    private static string ResolveInstallerUrl(string manifestSource, string installerUrl)
    {
        if (Uri.TryCreate(installerUrl, UriKind.Absolute, out _))
        {
            return installerUrl;
        }

        if (Uri.TryCreate(manifestSource, UriKind.Absolute, out var sourceUri)
            && (sourceUri.Scheme == Uri.UriSchemeHttp || sourceUri.Scheme == Uri.UriSchemeHttps))
        {
            return new Uri(sourceUri, installerUrl).ToString();
        }

        var sourceDirectory = Directory.Exists(manifestSource)
            ? manifestSource
            : Path.GetDirectoryName(manifestSource);
        return Path.GetFullPath(Path.Combine(sourceDirectory ?? Environment.CurrentDirectory, installerUrl));
    }

    private static string GetInstallerFileName(UpdateManifest manifest)
    {
        if (Uri.TryCreate(manifest.InstallerUrl, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        var localName = Path.GetFileName(manifest.InstallerUrl);
        return string.IsNullOrWhiteSpace(localName)
            ? $"SessionPerfTracker-{manifest.Version}-setup.exe"
            : localName;
    }

    private static async Task VerifySha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, expectedSha256.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Downloaded installer failed SHA256 verification.");
        }
    }

    private static string ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(HttpUpdateService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0].Trim();
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        var core = text.Split('+')[0].Split('-')[0].Trim();
        if (Version.TryParse(core, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }
}
