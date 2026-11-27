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
    // ---------- Inspecteur Bluetooth (page Diagnostic) ----------

    public ObservableCollection<InspectedDeviceRowViewModel> InspectorDevices { get; } = [];
    [ObservableProperty] private bool _isInspectorScanning;
    [ObservableProperty] private bool _isInspectorCapturing;
    [ObservableProperty] private bool _inspectorEmpty = true;
    public string InspectorScanText => IsInspectorScanning ? Strings.Inspect_StopScan : Strings.Inspect_Scan;

    partial void OnIsInspectorScanningChanged(bool value) => OnPropertyChanged(nameof(InspectorScanText));

    private void RefreshInspector()
    {
        var insp = _bridge.Inspector;
        // Mise à jour sur place : vider puis remplir 40 lignes à chaque annonce faisait clignoter la liste et perdre le clic sur
        // « Capturer ».
        var rows = insp.Devices().Take(40)
            .Select(d => new InspectedDeviceRowViewModel(d, insp.IsCapturing && insp.CapturingAddress == d.Address)).ToList();
        ListMerge.Apply(InspectorDevices, rows, r => r.Device.Address, (a, b) => a.Device == b.Device && a.IsCapturing == b.IsCapturing);
        InspectorEmpty = InspectorDevices.Count == 0;
        IsInspectorScanning = insp.IsScanning;
        IsInspectorCapturing = insp.IsCapturing;
    }

    [RelayCommand]
    private void ToggleInspectorScan()
    {
        var insp = _bridge.Inspector;
        if (insp.IsScanning) { insp.Stop(); return; }
        if (!EnsureBluetooth(null)) return;
        insp.Start();
    }

    [RelayCommand]
    private async Task CaptureInspectedAsync(InspectedDeviceRowViewModel? row)
    {
        if (row is null) return;
        var insp = _bridge.Inspector;
        if (insp.IsCapturing && insp.CapturingAddress == row.Device.Address) { await insp.StopCaptureAsync(); return; }
        await insp.StartCaptureAsync(row.Device.Address);
    }

    [RelayCommand]
    private async Task StopInspectorCaptureAsync() => await _bridge.Inspector.StopCaptureAsync();

    [RelayCommand]
    private async Task ToggleDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null) return;
        if (row.IsConnected) await _bridge.DisconnectAsync(row.Device.Address);
        else _bridge.Connect(row.Device);
        RefreshDevices();
    }

    [RelayCommand]
    private async Task VibrateDeviceAsync(DeviceRowViewModel? row)
    {
        if (row?.Controller is not { } c) return;
        AppendLog(Loc.Fmt(Strings.Log_VibrationTest, row.Title));
        await c.VibrateAsync();
    }

    [RelayCommand]
    private async Task ForgetDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null) return;
        if (!await _host.ConfirmAsync(Loc.Fmt(Strings.Dev_ConfirmForget, row.Title), Strings.Confirm_Forget)) return;
        await _bridge.DisconnectAsync(row.Device.Address, forget: true);
        SaveConfig();
    }

    [RelayCommand]
    private async Task RenameDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null) return;
        string? name = await _host.PromptTextAsync(Strings.Dev_RenamePrompt, row.CustomName ?? "");
        if (name is null) return; // annulé
        _bridge.SetDeviceName(row.Device.AddressText, name);
        SaveConfig();
    }

    private void RefreshDevices()
    {
        var controllers = _bridge.Controllers.ToDictionary(c => c.Info.Address);
        var rows = _bridge.SeenDevices
            .OrderBy(d => d.Name).ThenBy(d => d.Address)
            .Select(d => new DeviceRowViewModel(d, controllers.GetValueOrDefault(d.Address), _bridge.CustomName(d.AddressText)))
            .ToList();
        ListMerge.Apply(Devices, rows, r => r.Device.Address, (a, b) => a.ShowsSameAs(b));
        // Un nouveau modèle de manette vient d'être connecté : ses touches (et leurs assignations par défaut) apparaissent,
        // et la config (modèle mémorisé) est enregistrée.
        if (AddControllerRows()) { ReloadRows(); SaveConfig(); }
        IsScanning = _bridge.IsScanning;
        RefreshSummaries();
    }

    [ObservableProperty] private string _noteText = "";

    /// <summary>Insère une note horodatée dans le journal (ex. « j'appuie sur Y gauche ») pour relier gestes et trames.</summary>
    [RelayCommand]
    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(NoteText)) return;
        AppendLog($"NOTE >>> {NoteText.Trim()}");
        NoteText = "";
    }

    /// <summary>Confirmation brève sous les boutons du journal (« Journal copié »), effacée au bout de 3 s.</summary>
    [ObservableProperty] private string _logStatus = "";
    private CancellationTokenSource? _logStatusCts;

    [RelayCommand]
    private void CopyLog()
    {
        _host.CopyText(string.Join(Environment.NewLine, _logLines)); // le texte affiché peut avoir ~250 ms de retard
        LogStatus = Strings.Diag_Copied;
        Debounce(ref _logStatusCts, () => LogStatus = "", TimeSpan.FromSeconds(3));
    }

    [RelayCommand]
    private void ClearLog() { _logLines.Clear(); _logStale = false; LogText = ""; }

    /// <summary>Ajoute un message au journal depuis n'importe quel thread (erreur de démarrage de l'hôte, permission refusée…).</summary>
    public void ReportError(string message) => OnUi(() => AppendLog(message));

    /// <summary>Levé pour chaque ligne du journal (l'hôte peut la recopier dans un journal système, p. ex. logcat sous Android).</summary>
    public event Action<string>? LineLogged;

    // Le journal peut recevoir des dizaines de lignes par seconde (journal détaillé) : on les range aussitôt, mais le texte affiché
    // n'est reconstruit qu'au plus 4 fois par seconde, et seulement si la page Diagnostic est ouverte.
    private bool _logFlushPending;
    private bool _logStale;

    private void AppendLog(string message)
    {
        LineLogged?.Invoke(message);
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        WriteLogFile(line);
        _logLines.Enqueue(line);
        while (_logLines.Count > 500) _logLines.Dequeue();
        _logStale = true;
        if (_logFlushPending) return;
        _logFlushPending = true;
        _ = FlushLogLaterAsync();
    }

    /// <summary>Recopie le journal dans %APPDATA%\ShiftBridge\log.txt (tâche de fond, tourne à 5 Mo) pour diagnostiquer après coup.</summary>
    private static void WriteLogFile(string line) => LogFileWriter.Default?.Write(line);

    private async Task FlushLogLaterAsync()
    {
        await Task.Delay(250);
        _ui.Post(() =>
        {
            _logFlushPending = false;
            RefreshLogText();
        });
    }

    private void RefreshLogText()
    {
        if (!_logStale || CurrentPage != Pages.Diagnostic) return; // sinon : reconstruit à l'ouverture de la page
        _logStale = false;
        LogText = string.Join(Environment.NewLine, _logLines);
    }
}
