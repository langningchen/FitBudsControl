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
    string? Error)
{
    public static UpdateCheckResult Failed(string currentVersion, string error)
        => new(false, false, currentVersion, null, null, error);
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
            return new UpdateCheckResult(
                true,
                latest > current,
                currentText,
                latestText,
                release.HtmlUrl,
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
    }
}
