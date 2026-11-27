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
    // ---------- Appareils ----------

    // ---------- Radios (Bluetooth / Wi-Fi) ----------

    /// <summary>Bluetooth coupé : bandeau de l'Accueil avec un bouton d'activation (renseigné par l'hôte).</summary>
    [ObservableProperty] private bool _bluetoothOff;
    partial void OnBluetoothOffChanged(bool value) => OnPropertyChanged(nameof(ShowBluetoothOff));
    partial void OnHasPermissionProblemChanged(bool value) => OnPropertyChanged(nameof(ShowBluetoothOff));

    /// <summary>Bandeau « Bluetooth éteint », masqué tant que le bandeau de permission (prioritaire) est affiché.</summary>
    public bool ShowBluetoothOff => BluetoothOff && !HasPermissionProblem;

    public void SetBluetoothOff(bool off) => _ui.Post(() => BluetoothOff = off);

    private Action? _pendingBluetooth;

    /// <summary>
    /// Vrai si le Bluetooth est allumé. Sinon affiche la demande système et mémorise <paramref name="retry"/> : il est relancé par
    /// <see cref="OnBluetoothTurnedOn"/> dès que l'utilisateur l'a activé (demande, réglages rapides ou réglages système).
    /// </summary>
    private bool EnsureBluetooth(Action? retry)
    {
        if (_host.IsBluetoothOn) return true;
        _pendingBluetooth = retry;
        SetBluetoothOff(true);
        if (_host.PromptForBluetoothOnAutomatically) _host.RequestBluetoothOn();
        return false;
    }

    /// <summary>Appelé par l'hôte quand le Bluetooth vient de s'allumer : relance la recherche des manettes et l'action en attente.</summary>
    public void OnBluetoothTurnedOn() => _ui.Post(() =>
    {
        BluetoothOff = false;
        var retry = _pendingBluetooth;
        _pendingBluetooth = null;
        StartScan();
        retry?.Invoke();
        if (!HeartRateConnected && !IsHeartRateBusy) _ = AutoConnectHeartRateAsync();
    });

    /// <summary>Vrai si l'appareil est sur un réseau local. Sinon explique et ouvre le panneau Wi-Fi.</summary>
    private bool EnsureWifi()
    {
        if (_host.IsWifiOn) return true;
        SetTrainerMessage(Strings.Wifi_Off);
        _host.RequestWifiOn();
        return false;
    }

    [RelayCommand]
    private void EnableBluetooth() => _host.RequestBluetoothOn();

    [RelayCommand]
    private void StartScan()
    {
        if (!EnsureBluetooth(null)) { IsScanning = false; return; }
        SetBluetoothOff(false);
        _bridge.StartScan();
        IsScanning = _bridge.IsScanning;
    }

    // ---------- Cardio (ceinture / montre Bluetooth republiée par le proxy) ----------

    public ObservableCollection<DiscoveredHeartRateSensor> FoundHeartRateSensors { get; } = [];
    private DiscoveredHeartRateSensor? _savedHeartRate;
    [ObservableProperty] private DiscoveredHeartRateSensor? _selectedHeartRateSensor;
    [ObservableProperty] private bool _isScanningHeartRate;
    [ObservableProperty] private bool _heartRateConnected;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectHeartRateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectHeartRateCommand))]
    private bool _isHeartRateBusy;

    /// <summary>Connecter / Déconnecter grisés pendant une connexion ou une déconnexion en cours.</summary>
    private bool CanUseHeartRate() => !IsHeartRateBusy;
    [ObservableProperty] private string _heartRateStatus = "";
    private CancellationTokenSource? _hrLoopCts;
    private bool _hrFailed; // un message d'échec reste affiché jusqu'à la prochaine action de l'utilisateur

    public string HeartRateScanButtonText => IsScanningHeartRate ? Strings.Ble_BtnStop : Strings.Ble_BtnSearch;
    partial void OnIsScanningHeartRateChanged(bool value)
    {
        OnPropertyChanged(nameof(HeartRateScanButtonText));
        RefreshHeartRateSummary();
    }

    [RelayCommand]
    private void ToggleHeartRateScan()
    {
        _hrFailed = false;
        if (IsScanningHeartRate) { _bridge.StopHeartRateScan(); IsScanningHeartRate = false; RefreshHeartRate(); return; }
        if (!EnsureBluetooth(ToggleHeartRateScan)) { _hrFailed = true; HeartRateStatus = Strings.Bt_Off; return; }
        _bridge.StartHeartRateScan();
        IsScanningHeartRate = true;
        HeartRateStatus = Strings.Hr_Searching;
    }

    private void RefreshHeartRateSensors()
    {
        var selected = SelectedHeartRateSensor;
        var seen = _bridge.HeartRateSensors;
        // Mise à jour sur place : le capteur choisi (ou mémorisé) reste dans la liste même avant d'être revu.
        foreach (var s in seen)
        {
            int i = -1;
            for (int j = 0; j < FoundHeartRateSensors.Count; j++) if (FoundHeartRateSensors[j].Address == s.Address) { i = j; break; }
            if (i < 0) FoundHeartRateSensors.Add(s); else if (FoundHeartRateSensors[i] != s) FoundHeartRateSensors[i] = s;
        }
        if (selected is not null)
            SelectedHeartRateSensor = FoundHeartRateSensors.FirstOrDefault(t => t.Address == selected.Address);
        IsScanningHeartRate = _bridge.HeartRateScanning;
        if (IsScanningHeartRate && !HeartRateConnected)
            HeartRateStatus = seen.Count == 0 ? Strings.Hr_Searching : Loc.Plural(seen.Count, Strings.Hr_Found_One, Strings.Hr_Found_Many);
    }

    [RelayCommand(CanExecute = nameof(CanUseHeartRate))]
    private async Task ConnectHeartRateAsync()
    {
        if (IsHeartRateBusy) return;
        if (SelectedHeartRateSensor is not { } sensor) { HeartRateStatus = Strings.Hr_ChooseSensor; return; }
        if (!EnsureBluetooth(() => _ = ConnectHeartRateAsync())) { _hrFailed = true; HeartRateStatus = Strings.Bt_Off; return; }
        IsHeartRateBusy = true;
        IsScanningHeartRate = false;
        _hrFailed = false;
        HeartRateStatus = Strings.Hr_Connecting;
        try { await _bridge.ConnectHeartRateAsync(sensor.Address, sensor.Name); SaveConfig(); }
        catch (Exception ex) { _hrFailed = true; HeartRateStatus = Loc.Fmt(Strings.Hr_ConnectFailed, ex.Message); }
        finally { IsHeartRateBusy = false; }
        RefreshHeartRate();
    }

    [RelayCommand(CanExecute = nameof(CanUseHeartRate))]
    private async Task DisconnectHeartRateAsync()
    {
        if (IsHeartRateBusy) return;
        IsHeartRateBusy = true;
        _hrFailed = false;
        try { await _bridge.DisconnectHeartRateAsync(); SaveConfig(); }
        catch (Exception ex) { _hrFailed = true; HeartRateStatus = Loc.Fmt(Strings.Hr_ConnectFailed, ex.Message); }
        finally { IsHeartRateBusy = false; }
        RefreshHeartRate();
    }

    /// <summary>Reconnexion au dernier capteur au démarrage, AVANT le proxy (qui le republie sans redémarrer). Un capteur éteint ne bloque rien.</summary>
    private async Task AutoConnectHeartRateAsync()
    {
        if (!BleDeviceId.TryParseSavedAddress(_config.HeartRateBleAddress, out var address)) return;
        try { await _bridge.ConnectHeartRateAsync(address, string.IsNullOrWhiteSpace(_config.HeartRateBleName) ? Strings.Hr_Title : _config.HeartRateBleName); }
        catch (Exception ex) { _hrFailed = true; HeartRateStatus = Loc.Fmt(Strings.Hr_ConnectFailed, ex.Message); AppendLog(HeartRateStatus); }
        RefreshHeartRate();
    }

    private void RefreshHeartRate()
    {
        HeartRateConnected = _bridge.HeartRateConnected;
        RefreshHeartRateSummary(); // tuile Cardio de l'Accueil (état, bpm)
        if (!HeartRateConnected)
        {
            _hrLoopCts?.Cancel(); _hrLoopCts = null;
            if (!IsHeartRateBusy && !IsScanningHeartRate && !_hrFailed) HeartRateStatus = Strings.Hr_None;
            return;
        }
        string name = _bridge.HeartRateName;
        HeartRateStatus = _bridge.HeartRateState == LinkState.Reconnecting ? Loc.Fmt(Strings.Hr_Reconnecting, name)
            : _bridge.HeartRateBpm is { } bpm ? Loc.Fmt(Strings.Hr_ConnectedBpm, name, bpm)
            : Loc.Fmt(Strings.Hr_Connected, name);
        if (_hrLoopCts is null)
        {
            var cts = _hrLoopCts = new CancellationTokenSource();
            _ = HeartRateLoopAsync(cts.Token);
        }
    }

    /// <summary>Rafraîchit le BPM affiché une fois par seconde tant qu'un capteur est connecté.</summary>
    private async Task HeartRateLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
                _ui.Post(() => { if (!ct.IsCancellationRequested) RefreshHeartRate(); });
        }
        catch (OperationCanceledException) { }
    }
}
