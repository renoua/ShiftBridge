using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Localization;

namespace ShiftBridge.Presentation;

public sealed partial class MainViewModel
{
    // ---------- Sauvegarde des réglages (export / import) ----------

    /// <summary>Résultat de la dernière sauvegarde ou restauration (carte « Sauvegarde » des Réglages).</summary>
    [ObservableProperty] private string _backupStatus = "";

    /// <summary>Des réglages importés attendent un redémarrage : plus rien n'est écrit d'ici là (voir <see cref="ConfigStore.FreezeDefaultFile"/>).</summary>
    [ObservableProperty] private bool _restartPending;

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        try
        {
            string json = ConfigStore.Export(_config, _host.AppVersion);
            bool saved = await _host.SaveTextFileAsync($"shiftbridge-{DateTime.Now:yyyy-MM-dd}.json", json);
            BackupStatus = saved ? Strings.Backup_Exported : "";
        }
        catch (Exception ex) { BackupStatus = Loc.Fmt(Strings.Backup_Failed, ex.Message); }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        string? text;
        try { text = await _host.OpenTextFileAsync(); }
        catch (Exception ex) { BackupStatus = Loc.Fmt(Strings.Backup_Failed, ex.Message); return; }
        if (text is null) return;

        AppConfig imported;
        try { imported = ConfigStore.Import(text, _config); }
        catch (FormatException) { BackupStatus = Strings.Backup_Invalid; return; }

        if (!await _host.ConfirmAsync(Strings.Backup_ConfirmImport, Strings.Backup_ImportConfirmButton)) return;
        try { ConfigStore.Save(imported); }
        catch (Exception ex) { BackupStatus = Loc.Fmt(Strings.Backup_Failed, ex.Message); return; }

        // La configuration en mémoire ne doit plus écraser le fichier importé (vitesse mémorisée, réglage touché avant de relancer).
        ConfigStore.FreezeDefaultFile();
        RestartPending = true;
        AppendLog("Réglages importés depuis un fichier.");
        if (!_host.Restart()) BackupStatus = Strings.Backup_RestartNeeded;
    }
}
