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
    // ---------- Connexion aux applis ----------

    partial void OnEnableBleChanged(bool value) { if (!_loading) { _config.EnableBle = value; _ = ApplyTransportsAsync(); } }
    partial void OnEnableNetworkChanged(bool value) { if (!_loading) { _config.EnableNetwork = value; _ = ApplyTransportsAsync(); } }
    partial void OnEnableDirConChanged(bool value) { if (!_loading) { _config.EnableDirCon = value; _ = ApplyTransportsAsync(); } }

    // Lancé sans attente à chaque case cochée : deux appels ne doivent pas se croiser (deux serveurs du même type, état incohérent).
    private readonly SemaphoreSlim _transportsGate = new(1, 1);

    private async Task ApplyTransportsAsync()
    {
        await _transportsGate.WaitAsync();
        try { await ApplyTransportsCoreAsync(); }
        catch (Exception ex) { ReportError(Loc.Fmt(Strings.Log_TransportsFailed, ex.Message)); }
        finally { _transportsGate.Release(); }
    }

    private async Task ApplyTransportsCoreAsync()
    {
        if (EnableNetwork && _tcp is null)
        {
            _tcp = new ObcTcpServer(mdns: _mdns?.CreateAdvertiser());
            await _bridge.AddTransportAsync(_tcp);
            if (!_bridge.Transports.Contains(_tcp)) _tcp = null; // échec de démarrage
        }
        else if (!EnableNetwork && _tcp is not null)
        {
            await _bridge.RemoveTransportAsync(_tcp);
            _tcp = null;
        }

        if (EnableDirCon && _dircon is null)
        {
            _dircon = new DirConServer(_mdns?.CreateAdvertiser());
            await _bridge.AddTransportAsync(_dircon);
            if (!_bridge.Transports.Contains(_dircon)) _dircon = null;
        }
        else if (!EnableDirCon && _dircon is not null)
        {
            await _bridge.RemoveTransportAsync(_dircon);
            _dircon = null;
        }

        if (EnableBle && _ble is null)
        {
            if (!await _bridge.Platform.IsPeripheralSupportedAsync())
            {
                // Pas de « return » : les réglages réseau/DirCon ci-dessus doivent quand même être sauvegardés.
                _bleUnavailable = true;
                BleStatus = Strings.Ble_Unavailable;
            }
            else
            {
                _ble = _bridge.Platform.CreateObcPeripheral();
                await _bridge.AddTransportAsync(_ble);
                if (!_bridge.Transports.Contains(_ble)) _ble = null;
            }
        }
        else if (!EnableBle && _ble is not null)
        {
            await _bridge.RemoveTransportAsync(_ble);
            _ble = null;
        }

        RefreshTransportStatus();
        SaveConfig();
    }

    /// <summary>
    /// « Choisissez “DESKTOP-AB12” » quand l'OBC Bluetooth est actif : Windows et Android annoncent le nom Bluetooth de l'appareil,
    /// pas « ShiftBridge », et l'utilisateur ne le retrouverait pas dans la liste de son appli.
    /// </summary>
    private string? BleNameHint() =>
        _ble?.AdvertisedName is { Length: > 0 } name ? Loc.Fmt(Strings.App_ChooseBleName, name) : null;

    /// <summary>L'adaptateur Bluetooth ne peut pas être périphérique : le statut « non disponible » ne doit pas être écrasé.</summary>
    private bool _bleUnavailable;

    private void RefreshTransportStatus()
    {
        string Describe(IObcTransport? t, string off, string waiting)
        {
            if (t is null) return off;
            if (!t.HasClient) return waiting;
            return t.CurrentAppInfo is { } i ? Loc.Fmt(Strings.Status_ConnectedTo, $"{i.AppId} {i.AppVersion}".Trim()).Trim() : Strings.App_Connected;
        }

        if (!EnableBle || _ble is not null || !_bleUnavailable)
        {
            _bleUnavailable = false;
            BleStatus = Describe(_ble, Strings.Status_Off,
                BleNameHint() is { } hint ? $"{Strings.Status_WaitingApp} {hint}" : Strings.Status_WaitingApp);
        }
        NetworkStatus = _tcp is null
            ? Strings.Status_Off
            : Describe(_tcp, "", Loc.Fmt(Strings.Status_WaitingPort, _tcp.Port));
        DirConStatus = _dircon is null
            ? Strings.Status_Off
            : Describe(_dircon, "", Loc.Fmt(Strings.Status_WaitingPort, _dircon.Port));
        LocalAddressText = LocalNetwork.Describe(LocalNetwork.GetIPv4Addresses(),
            (Strings.Net_ObcNetwork, _tcp?.Port ?? 0), ("DirCon", _dircon?.Port ?? 0));
        OnPropertyChanged(nameof(OnboardingAppText)); // le nom Bluetooth annoncé peut avoir changé
        RefreshSummaries();
    }

    /// <summary>Adresses et ports à saisir dans l'appli d'entraînement si elle ne trouve pas ShiftBridge toute seule.</summary>
    [ObservableProperty] private string _localAddressText = "";

    [RelayCommand]
    private Task TestShiftUpAsync() => _bridge.SendTestActionAsync(ObcButtonId.ShiftUp);

    [RelayCommand]
    private Task TestShiftDownAsync() => _bridge.SendTestActionAsync(ObcButtonId.ShiftDown);
}
