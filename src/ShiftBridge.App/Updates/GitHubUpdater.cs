using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using ShiftBridge.Core.Updates;
using ShiftBridge.Presentation;

namespace ShiftBridge.App.Updates;

/// <summary>
/// Mise à jour depuis GitHub Releases : l'exe est téléchargé à côté de l'exe en cours, vérifié (taille + SHA-256 publié par GitHub),
/// puis l'ancien est renommé en .old (Windows autorise de renommer un exe en cours d'exécution) et le nouveau prend sa place.
/// </summary>
internal sealed class GitHubUpdater : IAppUpdater
{
    private const string LatestUrl = "https://api.github.com/repos/renoua/ShiftBridge/releases/latest";
    /// <summary>Liste des releases récentes : seule façon de voir les préversions, que /releases/latest ignore.</summary>
    private const string ListUrl = "https://api.github.com/repos/renoua/ShiftBridge/releases?per_page=30";
    /// <summary>Remplace l'adresse de l'API (tests locaux : un latest.json servi en HTTP sur localhost).</summary>
    private const string FeedOverrideVariable = "SHIFTBRIDGE_UPDATE_FEED";

    private static readonly HttpClient Http = CreateClient();

    private readonly Version _current;

    public GitHubUpdater(string appVersion)
    {
        _current = Version.TryParse(appVersion, out var v) ? v : new Version(0, 0, 0);
    }

    public static string ExePath => Environment.ProcessPath ?? "";
    public static string OldPath => ExePath + ".old";
    private static string DownloadPath => ExePath + ".download";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ShiftBridge-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public async Task<UpdateInfo?> CheckAsync(bool includePrereleases, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(FeedOverrideVariable) is { Length: > 0 } feed)
            return ReleaseParser.Parse(await GetAsync(feed, ct), _current, includePrereleases: includePrereleases);
        return includePrereleases
            ? ReleaseParser.ParseReleases(await GetAsync(ListUrl, ct), _current, ReleaseParser.DefaultAssetName, includePrereleases: true)
            : ReleaseParser.Parse(await GetAsync(LatestUrl, ct), _current);
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public bool CanSelfUpdate(UpdateInfo update)
    {
        if (update.Sha256 is null || ExePath.Length == 0) return false;
        try
        {
            // Le dossier doit être inscriptible : on essaie de créer (et supprimer) un fichier de test.
            string probe = Path.Combine(Path.GetDirectoryName(ExePath)!, $".shiftbridge-write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct)
    {
        if (update.Sha256 is null) throw new InvalidOperationException("Aucune empreinte SHA-256 fournie.");
        string target = DownloadPath;
        try
        {
            using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    hash.AppendData(buffer, 0, n);
                    read += n;
                    if (total > 0) progress.Report(Math.Min(1.0, (double)read / total));
                }
            }

            if (update.Size > 0 && read != update.Size)
                throw new InvalidDataException($"Taille inattendue ({read} octets au lieu de {update.Size}).");
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, update.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("L'empreinte SHA-256 du fichier téléchargé ne correspond pas.");
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { /* fichier déjà absent ou verrouillé */ }
            throw;
        }
    }

    public void ApplyAndRestart(string downloadedPath)
    {
        string exe = ExePath;
        string old = OldPath;
        try { if (File.Exists(old)) File.Delete(old); } catch { /* reste d'une mise à jour précédente, encore verrouillé */ }
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Move(downloadedPath, exe);
            Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
        }
        catch
        {
            // Retour en arrière : l'ancienne version reprend sa place.
            try { if (File.Exists(exe)) File.Delete(exe); } catch { }
            try { File.Move(old, exe, overwrite: true); } catch { }
            throw;
        }
        // Fermeture normale : OnClosing libère proprement les liaisons Bluetooth avant la sortie.
        Application.Current.Dispatcher.BeginInvoke(() => Application.Current.MainWindow?.Close());
    }

    /// <summary>Supprime l'exe de la version précédente (au démarrage) ; sans effet s'il est absent ou encore verrouillé.</summary>
    public static void CleanupOld()
    {
        try { if (ExePath.Length > 0 && File.Exists(OldPath)) File.Delete(OldPath); } catch { /* sera retenté au prochain démarrage */ }
        try { if (ExePath.Length > 0 && File.Exists(DownloadPath)) File.Delete(DownloadPath); } catch { }
    }
}
