using ShiftBridge.Core.Updates;
using Xunit;

namespace ShiftBridge.Core.Tests;

public class ReleaseParserTests
{
    private const string Hash = "dacc46f8c5c56c322a314f7bcb548c1cbb9b466634e005850a78975dc7905921";

    private static string Json(string tag = "v0.16.0", bool draft = false, bool prerelease = false,
        string asset = "ShiftBridge.exe", string? digest = "sha256:" + Hash,
        string url = "https://github.com/x/y/releases/download/v0.16.0/ShiftBridge.exe", string? uploadedAt = null)
    {
        string d = digest is null ? "" : $"\"digest\": \"{digest}\",";
        string date = uploadedAt is null ? "" : $"\"created_at\": \"{uploadedAt}\",";
        return $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/x/y/releases/tag/{{tag}}", "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [ { "name": "other.apk", "size": 1, "browser_download_url": "https://example.com/other.apk" },
                      { "name": "{{asset}}", "size": 76656163, {{d}} {{date}} "browser_download_url": "{{url}}" } ] }
        """;
    }

    private static readonly Version Current = new(0, 15, 6);

    [Fact]
    public void Newer_release_is_returned_with_asset_details()
    {
        var u = ReleaseParser.Parse(Json(), Current);
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 16, 0), u!.Version);
        Assert.Equal(Hash, u.Sha256);
        Assert.Equal(76656163, u.Size);
        Assert.EndsWith("/ShiftBridge.exe", u.DownloadUrl);
        Assert.Contains("/releases/tag/v0.16.0", u.PageUrl);
    }

    [Fact] public void Same_version_is_not_an_update() => Assert.Null(ReleaseParser.Parse(Json("v0.15.6"), Current));
    [Fact] public void Older_version_is_not_an_update() => Assert.Null(ReleaseParser.Parse(Json("v0.15.5"), Current));
    [Fact] public void Tag_without_v_prefix_is_accepted() => Assert.NotNull(ReleaseParser.Parse(Json("0.16.0"), Current));
    [Fact] public void Two_part_tag_equals_three_part_version() => Assert.Null(ReleaseParser.Parse(Json("v0.15"), new Version(0, 15, 0)));
    [Fact] public void Draft_is_ignored() => Assert.Null(ReleaseParser.Parse(Json(draft: true), Current));
    [Fact] public void Prerelease_is_ignored() => Assert.Null(ReleaseParser.Parse(Json(prerelease: true), Current));
    [Fact] public void Missing_asset_is_ignored() => Assert.Null(ReleaseParser.Parse(Json(asset: "Other.exe"), Current));

    [Fact]
    public void Missing_digest_still_returns_update_without_hash()
    {
        var u = ReleaseParser.Parse(Json(digest: null), Current);
        Assert.NotNull(u);
        Assert.Null(u!.Sha256);
    }

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("md5:" + Hash)]
    [InlineData("sha256:zzzz46f8c5c56c322a314f7bcb548c1cbb9b466634e005850a78975dc7905921")]
    public void Malformed_digest_is_dropped(string digest) =>
        Assert.Null(ReleaseParser.Parse(Json(digest: digest), Current)!.Sha256);

    [Fact] public void Digest_is_lowercased() => Assert.Equal(Hash, ReleaseParser.ParseDigest("SHA256:" + Hash.ToUpperInvariant()));

    [Fact] public void Plain_http_download_is_refused() =>
        Assert.Null(ReleaseParser.Parse(Json(url: "http://evil.example/ShiftBridge.exe"), Current));

    [Fact] public void Loopback_http_download_is_allowed_for_tests() =>
        Assert.NotNull(ReleaseParser.Parse(Json(url: "http://localhost:8000/ShiftBridge.exe"), Current));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": 5}")]
    [InlineData("{\"tag_name\": \"latest\", \"assets\": []}")]
    public void Invalid_json_returns_null_without_throwing(string json) => Assert.Null(ReleaseParser.Parse(json, Current));

    private const string MacAsset = "ShiftBridge-macos-arm64.dmg";
    private static readonly DateTimeOffset ExecutableTimestamp = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("v0.16.0", "2026-11-25T12:00:01Z", true)]
    [InlineData("v0.16.0", "2026-11-25T12:00:00Z", false)]
    [InlineData("v0.16.0", "2026-11-25T11:59:59Z", false)]
    [InlineData("v0.16.0", "invalid", false)]
    [InlineData("v0.16.0", null, false)]
    [InlineData("v0.15.6", "2026-11-25T12:00:01Z", false)]
    public void Mac_update_requires_a_newer_version_and_upload(string tag, string? uploadedAt, bool expected)
    {
        string release = Json(tag, asset: MacAsset, uploadedAt: uploadedAt);
        var update = ReleaseParser.ParseReleases($"[{release}]", Current, MacAsset, ExecutableTimestamp);
        Assert.Equal(expected, update is not null);
    }

    [Fact]
    public void Mac_update_skips_newer_releases_without_a_stable_matching_dmg()
    {
        const string uploadedAt = "2026-11-25T12:00:01Z";
        string noMacAsset = Json("v0.20.0", uploadedAt: uploadedAt);
        string prerelease = Json("v0.19.0", prerelease: true, asset: MacAsset, uploadedAt: uploadedAt);
        string draft = Json("v0.18.0", draft: true, asset: MacAsset, uploadedAt: uploadedAt);
        string eligible = Json("v0.17.0", asset: MacAsset, uploadedAt: uploadedAt);
        string older = Json("v0.16.0", asset: MacAsset, uploadedAt: uploadedAt);

        var update = ReleaseParser.ParseReleases($"[{noMacAsset},{draft},{older},{prerelease},{eligible}]",
            Current, MacAsset, ExecutableTimestamp);

        Assert.Equal(new Version(0, 17, 0), update?.Version);
    }
}
