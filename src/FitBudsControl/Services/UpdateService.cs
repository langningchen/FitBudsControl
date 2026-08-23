using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FitBudsControl.Services;

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? InstallerDownloadUrl,
    string? InstallerFileName,
    string? Error)
{
    public static UpdateCheckResult Failed(string currentVersion, string error)
        => new(false, false, currentVersion, null, null, null, null, error);
}

public static class UpdateService
{
    private const string Repository = "langningchen/FitBudsControl";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CurrentVersion => GetCurrentVersion().ToString(3);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersion();
        var currentText = current.ToString(3);

        try
        {
            using var response = await HttpClient.GetAsync(LatestReleaseUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(currentText, $"GitHub 返回 {(int)response.StatusCode}");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(content, JsonOptions, cancellationToken).ConfigureAwait(false);
            var latest = ParseVersion(release?.TagName);
            if (latest is null || release is null || release.Draft || release.Prerelease)
            {
                return UpdateCheckResult.Failed(currentText, "没有找到可用的稳定版本");
            }

            var latestText = latest.ToString(3);
            var installer = release.Assets.FirstOrDefault(asset =>
                asset.Name is not null &&
                asset.Name.StartsWith("FitBudsControl-Setup-", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            return new UpdateCheckResult(
                true,
                latest > current,
                currentText,
                latestText,
                release.HtmlUrl,
                installer?.BrowserDownloadUrl,
                installer?.Name,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return UpdateCheckResult.Failed(currentText, "无法连接 GitHub");
        }
    }

    public static Version GetCurrentVersion()
    {
        var version = typeof(UpdateService).Assembly.GetName().Version;
        return version is null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    public static async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.Succeeded || !update.IsUpdateAvailable ||
            string.IsNullOrWhiteSpace(update.InstallerDownloadUrl) ||
            string.IsNullOrWhiteSpace(update.InstallerFileName))
        {
            throw new InvalidOperationException("此版本暂时没有可下载的安装程序");
        }

        if (!Uri.TryCreate(update.InstallerDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装程序下载地址无效");
        }

        var fileName = Path.GetFileName(update.InstallerFileName);
        if (!fileName.StartsWith("FitBudsControl-Setup-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装程序文件名无效");
        }

        var updateDirectory = Path.Combine(Path.GetTempPath(), "FitBudsControl", "Updates");
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, fileName);

        try
        {
            using var response = await HttpClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var length = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                received += count;
                if (length is > 0)
                {
                    progress?.Report(received * 100.0 / length.Value);
                }
            }

            if (received == 0)
            {
                throw new InvalidDataException("下载的安装程序为空");
            }

            progress?.Report(100);
            return destination;
        }
        catch
        {
            try
            {
                File.Delete(destination);
            }
            catch
            {
            }
            throw;
        }
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var value = tag.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        return Version.TryParse(value, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FitBudsControl", CurrentVersionForUserAgent()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string CurrentVersionForUserAgent()
    {
        var version = typeof(UpdateService).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        public bool Draft { get; set; }
        public bool Prerelease { get; set; }

        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
