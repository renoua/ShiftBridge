using ShiftBridge.Core.Updates;
using ShiftBridge.Presentation;

namespace ShiftBridge.MacOS;

internal sealed class MacReleaseChecker(Version currentVersion) : IAppUpdateChecker
{
    private const string Repository = "donaldmunro/ShiftBridge";
    private const string ReleasesApi = $"https://api.github.com/repos/{Repository}/releases";
    private const string AssetName = "ShiftBridge-macos-arm64.dmg";

    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    private static readonly HttpClient Http = CreateClient();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        string? executablePath = Environment.ProcessPath;
        if (executablePath is null || !File.Exists(executablePath)) return null;
        var executableTimestamp = File.GetLastWriteTimeUtc(executablePath);

        for (int page = 1; ; page++)
        {
            using var response = await Http.GetAsync($"{ReleasesApi}?per_page=100&page={page}", ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            var update = ReleaseParser.ParseReleases(json, currentVersion, AssetName, executableTimestamp);
            if (update is not null) return update;
            if (!response.Headers.TryGetValues("Link", out var links)
                || !links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal))) return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ShiftBridge-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
