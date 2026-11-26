using System.Text.Json;

namespace ShiftBridge.Core.Updates;

/// <summary>Une version plus récente publiée sur GitHub Releases.</summary>
/// <param name="Sha256">Empreinte SHA-256 en hexadécimal minuscule ; null si la release ne la fournit pas (mise à jour en un clic impossible).</param>
/// <param name="IsPrerelease">Version de test (« pre-release » GitHub), proposée seulement à qui l'a demandé.</param>
public sealed record UpdateInfo(Version Version, string PageUrl, string DownloadUrl, long Size, string? Sha256, bool IsPrerelease = false);

/// <summary>Lit les versions publiées par l'API GitHub Releases.</summary>
public static class ReleaseParser
{
    public const string DefaultAssetName = "ShiftBridge.exe";

    /// <summary>
    /// La mise à jour proposée, ou null si la release n'est pas plus récente que <paramref name="current"/>, si c'est un brouillon,
    /// une préversion (sauf <paramref name="includePrereleases"/>), si le fichier attendu est absent ou si le JSON est illisible.
    /// Ne lève jamais d'exception.
    /// </summary>
    public static UpdateInfo? Parse(string json, Version current, string assetName = DefaultAssetName, bool includePrereleases = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseRelease(doc.RootElement, current, assetName, includePrereleases);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Cherche, sur une page de l'API (liste de releases), la version la plus récente qui publie <paramref name="assetName"/> :
    /// stable seulement, ou stable et préversions avec <paramref name="includePrereleases"/>.
    /// <paramref name="executableTimestamp"/> (facultatif) écarte en plus les fichiers envoyés avant cette date.
    /// </summary>
    public static UpdateInfo? ParseReleases(string json, Version current, string assetName, DateTimeOffset? executableTimestamp = null,
        bool includePrereleases = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            UpdateInfo? newest = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var update = ParseRelease(release, current, assetName, includePrereleases, executableTimestamp);
                if (update is not null && (newest is null || Normalize(update.Version) > Normalize(newest.Version)))
                    newest = update;
            }
            return newest;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    private static UpdateInfo? ParseRelease(JsonElement release, Version current, string assetName, bool includePrereleases,
        DateTimeOffset? executableTimestamp = null)
    {
        if (release.ValueKind != JsonValueKind.Object || Bool(release, "draft")) return null;
        bool prerelease = Bool(release, "prerelease");
        if (prerelease && !includePrereleases) return null;
        string? tag = Str(release, "tag_name");
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        if (Normalize(version) <= Normalize(current)) return null;

        string pageUrl = Str(release, "html_url") ?? "";
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(Str(asset, "name"), assetName, StringComparison.OrdinalIgnoreCase)) continue;
            if (executableTimestamp is { } timestamp && (!asset.TryGetProperty("created_at", out var date)
                || date.ValueKind != JsonValueKind.String || !date.TryGetDateTimeOffset(out var uploadedAt) || uploadedAt <= timestamp)) continue;

            string? url = Str(asset, "browser_download_url");
            if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !IsLoopbackHttp(url)) return null;
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
            return new UpdateInfo(version, pageUrl, url, size, ParseDigest(Str(asset, "digest")), prerelease);
        }
        return null;
    }

    /// <summary>« sha256:ABC… » → « abc… » ; null si le format n'est pas celui d'un SHA-256 (64 caractères hexadécimaux).</summary>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>« 0.16 » et « 0.16.0 » désignent la même version (les composants absents valent −1 pour <see cref="Version"/>).</summary>
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>HTTP clair toléré uniquement vers la machine locale (flux de test).</summary>
    private static bool IsLoopbackHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
