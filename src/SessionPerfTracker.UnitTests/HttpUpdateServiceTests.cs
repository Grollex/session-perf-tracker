using System.Security.Cryptography;
using System.Net;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Updates;

namespace SessionPerfTracker.UnitTests;

public sealed class HttpUpdateServiceTests
{
    [Fact]
    public async Task Remote_manifest_check_uses_injected_http_client()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "version": "99.0.0",
              "installerUrl": "SessionPerfTracker-99.0.0-win-x64-setup.exe",
              "sha256": "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
            }
            """);
        var httpClient = new HttpClient(handler);
        var service = new HttpUpdateService(httpClient);

        var result = await service.CheckAsync("https://updates.example.test/session-perf-tracker/version.json");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("https://updates.example.test/session-perf-tracker/version.json", handler.RequestedUri?.ToString());
        Assert.Equal(
            "https://updates.example.test/session-perf-tracker/SessionPerfTracker-99.0.0-win-x64-setup.exe",
            result.Manifest?.InstallerUrl);
    }

    [Fact]
    public async Task Remote_installer_download_requires_sha256()
    {
        var service = new HttpUpdateService();
        var manifest = new UpdateManifest
        {
            Version = "9.9.9",
            InstallerUrl = "https://example.com/SessionPerfTracker-9.9.9-setup.exe"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadInstallerAsync(manifest, CreateTemporaryDirectory()));

        Assert.Contains("SHA256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remote_installer_download_rejects_invalid_sha256()
    {
        var service = new HttpUpdateService();
        var manifest = new UpdateManifest
        {
            Version = "9.9.9",
            InstallerUrl = "https://example.com/SessionPerfTracker-9.9.9-setup.exe",
            Sha256 = "not-a-sha"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadInstallerAsync(manifest, CreateTemporaryDirectory()));

        Assert.Contains("invalid SHA256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Local_installer_download_accepts_matching_sha256()
    {
        var sourceDirectory = CreateTemporaryDirectory();
        var updateDirectory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(sourceDirectory, "SessionPerfTracker-9.9.9-setup.exe");
        await File.WriteAllTextAsync(sourcePath, "fake installer payload");
        var sha256 = await ComputeSha256Async(sourcePath);

        var service = new HttpUpdateService();
        var manifest = new UpdateManifest
        {
            Version = "9.9.9",
            InstallerUrl = sourcePath,
            Sha256 = sha256
        };

        var downloadedPath = await service.DownloadInstallerAsync(manifest, updateDirectory);

        Assert.True(File.Exists(downloadedPath));
        Assert.Equal(sha256, await ComputeSha256Async(downloadedPath));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    [Fact]
    public async Task Manifest_with_utf8_bom_served_as_octet_stream_is_parsed()
    {
        // Locally packaged manifests are written by Windows PowerShell with a UTF-8 BOM, and GitHub
        // serves release assets as application/octet-stream without a charset.
        var json = """
            {
                "version":  "99.0.0",
                "installerUrl":  "https://example.com/SessionPerfTracker-99.0.0-win-x64-setup.exe",
                "sha256":  "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF"
            }
            """;
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(json)).ToArray();
        var service = new HttpUpdateService(new HttpClient(new StubHttpMessageHandler(bytes)));

        var result = await service.CheckAsync("https://updates.example.test/version.json");

        Assert.True(result.IsUpdateAvailable, result.Status);
        Assert.Equal("99.0.0", result.Manifest?.Version);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string? _content;
        private readonly byte[]? _bytes;

        public StubHttpMessageHandler(string content)
        {
            _content = content;
        }

        public StubHttpMessageHandler(byte[] bytes)
        {
            _bytes = bytes;
        }

        public Uri? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            HttpContent content;
            if (_bytes is null)
            {
                content = new StringContent(_content!);
            }
            else
            {
                content = new ByteArrayContent(_bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
