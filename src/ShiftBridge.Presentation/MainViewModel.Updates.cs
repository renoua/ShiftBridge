using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Gears;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;
using ShiftBridge.Platform.Input;
using ShiftBridge.Platform.Obc;
using ShiftBridge.Platform.Sensors;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Presentation;

public sealed partial class MainViewModel
{
    // ---------- Mises à jour ----------

    private static readonly TimeSpan UpdateFirstDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    private readonly CancellationTokenSource _updateCts = new();
    private ShiftBridge.Core.Updates.UpdateInfo? _pendingUpdate;
    private bool _updateDismissed;
    private int _updateCheckInProgress;

    [ObservableProperty] private bool _checkForUpdates;
    /// <summary>Recevoir aussi les versions de test (préversions GitHub).</summary>
    [ObservableProperty] private bool _includePrereleases;
    [ObservableProperty] private bool _updateAvailable;
    /// <summary>Une version plus récente existe (même si le bandeau de l'Accueil a été masqué par « Plus tard ») : boutons des Réglages.</summary>
    [ObservableProperty] private bool _hasPendingUpdate;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesNowCommand))]
    private bool _isUpdating;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesNowCommand))]
    private bool _isCheckingUpdates;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string _updateProgressText = "";
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>L'hôte sait se mettre à jour (Windows) : affiche la carte « Mises à jour » des Réglages.</summary>
    public bool HasUpdater => _host.Updater is not null;
    public bool HasUpdateChecker => _host.UpdateChecker is not null;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        _config.CheckForUpdates = value;
        SaveConfig();
    }

    partial void OnIncludePrereleasesChanged(bool value)
    {
        if (_loading) return;
        _config.IncludePrereleases = value;
        SaveConfig();
    }

    private void StartUpdateLoop()
    {
        if (!HasUpdateChecker) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var ct = _updateCts.Token;
                await Task.Delay(UpdateFirstDelay, ct);
                while (!ct.IsCancellationRequested)
                {
                    if (CheckForUpdates && !IsUpdating) await CheckUpdatesAsync(manual: false);
                    await Task.Delay(UpdateInterval, ct);
                }
            }
            catch (OperationCanceledException) { /* arrêt de l'application */ }
        });
    }

    /// <summary>Interroge GitHub. En automatique, un échec reste silencieux (journal seulement) ; en manuel, l'état s'affiche dans les Réglages.</summary>
    private async Task CheckUpdatesAsync(bool manual)
    {
        var checker = _host.UpdateChecker;
        if (checker is null || Interlocked.Exchange(ref _updateCheckInProgress, 1) != 0) return;
        try
        {
            // Vérification manuelle : la ligne d'état dit ce qui se passe (elle restait vide pendant la requête).
            _ui.Post(() => { IsCheckingUpdates = true; if (manual) UpdateStatus = Strings.Update_Checking; });
            var update = await checker.CheckAsync(IncludePrereleases, _updateCts.Token);
            _ui.Post(() =>
            {
                _pendingUpdate = update;
                HasPendingUpdate = update is not null;
                if (update is null)
                {
                    UpdateAvailable = false;
                    if (manual) UpdateStatus = Loc.Fmt(Strings.Update_UpToDate, _host.AppVersion);
                    return;
                }
                UpdateText = Loc.Fmt(update.IsPrerelease ? Strings.Update_AvailablePrerelease : Strings.Update_Available, update.Version.ToString());
                UpdateStatus = UpdateText;
                if (!_updateDismissed || manual) { _updateDismissed = false; UpdateAvailable = true; }
            });
        }
        catch (OperationCanceledException) when (_updateCts.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ReportError($"Mise à jour : vérification impossible ({ex.Message})"); // thread de fond : passe par l'interface
            if (manual) _ui.Post(() => UpdateStatus = Strings.Update_CheckFailed);
        }
        finally
        {
            _ui.Post(() => IsCheckingUpdates = false);
            Interlocked.Exchange(ref _updateCheckInProgress, 0);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckUpdatesNow))]
    private Task CheckUpdatesNow() => IsUpdating || IsCheckingUpdates ? Task.CompletedTask : CheckUpdatesAsync(manual: true);
    private bool CanCheckUpdatesNow() => !IsUpdating && !IsCheckingUpdates;

    [RelayCommand]
    private void DismissUpdate()
    {
        _updateDismissed = true;
        UpdateAvailable = false;
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        if (_pendingUpdate is { PageUrl.Length: > 0 } u) _host.OpenUrl(u.PageUrl);
    }

    /// <summary>« Installer » est grisé pendant le téléchargement (il restait cliquable, contrairement à « Plus tard »).</summary>
    private bool CanApplyUpdate() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private async Task ApplyUpdate()
    {
        var updater = _host.Updater;
        var update = _pendingUpdate;
        if (updater is null || update is null || IsUpdating) return;

        // Sans empreinte vérifiable ou dossier non inscriptible : on ne remplace rien nous-mêmes, la page de la release s'ouvre.
        if (!updater.CanSelfUpdate(update))
        {
            _host.OpenUrl(update.PageUrl);
            return;
        }
        if (ProxyActive && !await _host.ConfirmAsync(Strings.Update_ConfirmProxy, Strings.Update_Install)) return;

        IsUpdating = true;
        UpdateProgressText = Loc.Fmt(Strings.Update_Downloading, 0);
        try
        {
            var progress = new Progress<double>(p => UpdateProgressText = Loc.Fmt(Strings.Update_Downloading, (int)Math.Round(p * 100)));
            string file = await updater.DownloadAsync(update, progress, _updateCts.Token);
            UpdateProgressText = "";
            updater.ApplyAndRestart(file); // ferme l'application ; si cela échoue, l'exception arrive ici
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Mise à jour : échec ({ex.Message})");
            UpdateProgressText = Loc.Fmt(Strings.Update_Failed, ex.Message);
        }
        finally { IsUpdating = false; }
    }

    /// <summary>Démarrage réduit demandé par la configuration (lu par MainWindow avant l'affichage).</summary>
    public bool ShouldStartMinimized => _config.StartMinimized;

    /// <summary>Texte de l'info-bulle de l'icône : la vitesse virtuelle courante.</summary>
    public string TrayTooltip => Loc.Fmt(Strings.Tray_Tooltip, _bridge.Gear.Gear, _bridge.Gear.GearCount, _bridge.GearRatio);
}
