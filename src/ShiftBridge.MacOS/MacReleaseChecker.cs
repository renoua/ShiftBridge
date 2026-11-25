using ShiftBridge.Core.Updates;
using ShiftBridge.Presentation;

namespace ShiftBridge.MacOS;

internal sealed class MacReleaseChecker(Version currentVersion) : IAppUpdateChecker
{
    private const string Repository = "renoua/ShiftBridge";
    private const string ReleasesApi = $"https://api.github.com/repos/{Repository}/releases";
    private const string AssetName = "ShiftBridge-macos-arm64.dmg";

    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    private static readonly HttpClient Http = CreateClient();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        // Les 100 releases les plus récentes suffisent : le DMG peut manquer sur une release (Windows/Android seuls),
        // d'où la liste plutôt que /releases/latest.
        using var response = await Http.GetAsync($"{ReleasesApi}?per_page=100", ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return ReleaseParser.ParseReleases(json, currentVersion, AssetName);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ShiftBridge-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
