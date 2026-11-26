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

/// <summary>État visuel d'une pastille (couleur).</summary>
public enum UiState { Off, Waiting, Ok, Warning, Error }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly BridgeService _bridge;
    private readonly Dictionary<ButtonKey, ButtonRowViewModel> _rowByButton = [];
    // Profil modifié dans l'onglet Touches : le profil par défaut ou celui d'une appli (pas forcément le profil actif).
    private Profile _editProfile;
    private string _editKey = "";
    private readonly StringBuilder _log = new();
    private readonly Queue<string> _logLines = new();
    private IObcTransport? _ble;
    private ObcTcpServer? _tcp;
    private DirConServer? _dircon;
    private bool _loading = true;

    private readonly IUiDispatcher _ui;
    private readonly IAppHost _host;
    private readonly IMdnsServiceDiscovery? _mdns;

    /// <param name="localInput">Clavier, télécommandes HID et manettes de jeu. Null = non pris en charge (Android) : l'interface les masque.</param>
    public MainViewModel(IBlePlatform platform, IUiDispatcher ui, IAppHost host, ILocalInputProvider? localInput = null,
        IMdnsServiceDiscovery? mdns = null)
    {
        _mdns = mdns;
        _ui = ui;
        _host = host;
        _config = ConfigStore.Load();
        HasLocalInput = localInput is not null;
        _bridge = new BridgeService(_config, platform, localInput, mdns);
        _editProfile = _bridge.DefaultProfile;

        Actions = [.. ObcButtonCatalog.Assignable.Select(a => new ActionItem(a.Id, a.Category, a.Label))];
        BuildButtonRows();

        _bridge.Log += m => OnUi(() => AppendLog(m));
        _bridge.DevicesChanged += () => OnUi(RefreshDevices);
        _bridge.Inspector.DevicesChanged += () => OnUi(RefreshInspector);
        _bridge.Inspector.CaptureChanged += () => OnUi(RefreshInspector);
        _bridge.TransportsChanged += () => OnUi(RefreshTransportStatus);
        _bridge.ActiveProfileChanged += () => OnUi(() => { RefreshProfiles(); RefreshSummaries(); });
        _bridge.GearChanged += () => OnUi(RefreshGear);
        _bridge.TrainerChanged += () => OnUi(RefreshTrainer);
        _bridge.BleTrainersChanged += () => OnUi(RefreshBleTrainers);
        _bridge.HeartRateChanged += () => OnUi(RefreshHeartRate);
        _bridge.HeartRateSensorsChanged += () => OnUi(RefreshHeartRateSensors);
        _trainerHost = _config.TrainerHost;
        _trainerPortText = _config.TrainerPort.ToString();
        _useBluetooth = _config.TrainerTransport == "ble";
        _slopeOnlyMode = _config.SlopeOnlyMode;
        _nativeZwiftMode = _config.NativeZwiftMode;
        _appHandlesShifting = _config.AppHandlesShifting;
        _reportGearToApp = _config.ReportGearToApp;
        _controllerVibration = _config.ControllerVibration;
        _ltwooPin = _config.LtwooPin ?? "";
        _sramAxsAdvanced = _config.SramAxsAdvanced;
        _autoStartVirtualShifting = _config.AutoStartVirtualShifting;
        _showOverlay = _config.ShowOverlay;
        _overlayLocked = _config.OverlayLocked;
        _overlayScale = Math.Clamp(_config.OverlayScale, 0.5, 3.0);
        _overlayOpacity = Math.Clamp(_config.OverlayOpacity, 0.2, 1.0);
        _overlayShowBar = _config.OverlayShowBar;
        _overlayFlash = _config.OverlayFlash;
        _overlayShowTelemetry = _config.OverlayShowTelemetry;
        _riderWeightText = _config.RiderWeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _bikeWeightText = _config.BikeWeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _gearCountValue = _bridge.Gear.GearCount;
        _gearCountText = Loc.Fmt(Strings.Trainer_GearCountValue, _bridge.Gear.GearCount);
        _chainringText = _bridge.Drivetrain.ChainringTeeth.ToString();
        _cogText = _bridge.Drivetrain.CogTeeth.ToString();
        _drivetrainInfo = Loc.Fmt(Strings.Drive_RealRatio, _bridge.Drivetrain.Ratio, _bridge.Drivetrain);
        // Dernier trainer Bluetooth utilisé : proposé d'emblée, sans nouvelle recherche.
        if (BleDeviceId.TryParseSavedAddress(_config.TrainerBleAddress, out var savedAddress))
        {
            var saved = new DiscoveredBleTrainer(savedAddress, string.IsNullOrWhiteSpace(_config.TrainerBleName) ? Strings.Trainer_Last : _config.TrainerBleName, 0, TrainerProtocols.None);
            _savedBleTrainer = saved;
            FoundBleTrainers.Add(saved);
            _selectedBleTrainer = saved;
        }
        // Dernier capteur cardio : proposé d'emblée (reconnecté automatiquement au démarrage).
        if (BleDeviceId.TryParseSavedAddress(_config.HeartRateBleAddress, out var savedHr))
        {
            var hr = new DiscoveredHeartRateSensor(savedHr, string.IsNullOrWhiteSpace(_config.HeartRateBleName) ? Strings.Hr_Title : _config.HeartRateBleName, 0);
            _savedHeartRate = hr;
            FoundHeartRateSensors.Add(hr);
            _selectedHeartRateSensor = hr;
        }
        _heartRateStatus = Strings.Hr_None;
        _bridge.PhysicalButtonChanged += (b, down) => OnUi(() => { if (_rowByButton.TryGetValue(b, out var r)) r.IsPressed = down; });
        _keyboardInputEnabled = _config.KeyboardInputEnabled;
        _gamepadInputEnabled = _config.GamepadInputEnabled;
        _suppressBoundKeys = _config.SuppressBoundKeys;
        if (HasLocalInput)
        {
            _bridge.LocalInputChanged += () => OnUi(RefreshLocalDevices);
            _bridge.LocalInput.Captured += key => OnUi(() => OnInputCaptured(key));
            RefreshLocalDevices();
        }

        _enableBle = _config.EnableBle;
        _enableNetwork = _config.EnableNetwork;
        _enableDirCon = _config.EnableDirCon;
        _startMinimized = _config.StartMinimized;
        _minimizeToTray = _config.MinimizeToTray;
        _startWithWindows = _config.StartWithWindows;
        _checkForUpdates = _config.CheckForUpdates;
        _selectedLanguage = LanguageOptions.First(o => o.Code == AppLanguage.Normalize(_config.Language));
        _selectedRepeatSpeed = RepeatSpeedOptions.First(o => o.Speed == _config.RepeatSpeed);
        _loading = false;
        RefreshGear();
        RefreshProfiles();
        RefreshSummaries();
        UpdateTelemetryLoop();
    }

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];
    public ObservableCollection<ButtonRowViewModel> ButtonRows { get; } = [];
    public IReadOnlyList<ActionItem> Actions { get; }

    [ObservableProperty] private string _gearText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _bleStatus = "";
    [ObservableProperty] private string _networkStatus = "";
    [ObservableProperty] private bool _enableBle;
    [ObservableProperty] private bool _enableNetwork;
    [ObservableProperty] private bool _verboseFrames;
    partial void OnVerboseFramesChanged(bool value) => _bridge.VerboseFrames = value;
    [ObservableProperty] private bool _enableDirCon;
    [ObservableProperty] private string _dirConStatus = "";
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private bool _controllerVibration = true;
    /// <summary>SRAM AXS en mode avancé (appairage, expérimental) : pris en compte à la prochaine connexion.</summary>
    [ObservableProperty] private bool _sramAxsAdvanced;

    partial void OnSramAxsAdvancedChanged(bool value)
    {
        if (_loading) return;
        _config.SramAxsAdvanced = value;
        SaveConfig();
        RefreshAxsRows();
    }

    [RelayCommand]
    private async Task PairDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null) return;
        await _bridge.PairAxsAsync(row.Device.Address);
        SaveConfig();
    }

    /// <summary>Code PIN (3 caractères) du groupe L-TWOO, lu à la prochaine connexion.</summary>
    [ObservableProperty] private string _ltwooPin = "";

    partial void OnLtwooPinChanged(string value)
    {
        if (_loading) return;
        _config.LtwooPin = value ?? "";
        SaveConfig();
    }

    partial void OnControllerVibrationChanged(bool value)
    {
        if (_loading) return;
        _config.ControllerVibration = value; // lu à chaque vibration par BridgeService (même objet de config)
        SaveConfig();
    }

    partial void OnAutoReconnectChanged(bool value)
    {
        if (_loading) return;
        _bridge.SetAutoReconnect(value); // effet immédiat, et sauvegardé tout de suite (Android peut tuer le processus sans StopAsync)
        SaveConfig();
    }

    public string AppConnectedText { get; private set; } = Strings.App_NoneConnected;

    // ---------- Navigation, mode avancé, résumés de l'accueil ----------

    [ObservableProperty] private string _currentPage = Pages.Home;
    [ObservableProperty] private string _controllersSummary = Strings.Ctrl_None;
    [ObservableProperty] private string _controllersDetail = "";
    [ObservableProperty] private UiState _controllersState;
    [ObservableProperty] private string _buttonsSummary = "";
    [ObservableProperty] private string _buttonsDetail = "";
    [ObservableProperty] private UiState _buttonsState;
    [ObservableProperty] private string _trainerSummary = Strings.Trainer_NotConfigured;
    [ObservableProperty] private string _trainerDetail = "";
    [ObservableProperty] private UiState _trainerState;
    [ObservableProperty] private string _appSummary = Strings.App_Waiting;
    [ObservableProperty] private string _appDetail = "";
    [ObservableProperty] private UiState _appState;
    [ObservableProperty] private string _heartRateSummary = Strings.Hr_Summary_None;
    [ObservableProperty] private string _heartRateDetail = "";
    [ObservableProperty] private UiState _heartRateState;
    [ObservableProperty] private string _gearRatioText = "";
    [ObservableProperty] private string _shiftModeText = "";

    [RelayCommand]
    private void Navigate(string? page) { if (!string.IsNullOrEmpty(page)) CurrentPage = page; }

    /// <summary>Remonte d'un niveau (Diagnostic → Réglages → Accueil). Faux depuis l'Accueil : le bouton retour d'Android quitte alors l'appli.</summary>
    public bool GoBack()
    {
        if (Pages.ParentOf(CurrentPage) is not { } parent) return false;
        CurrentPage = parent;
        return true;
    }

    [RelayCommand]
    private void Back() => GoBack();

    /// <summary>Titre de la page courante (barre du haut d'Android).</summary>
    public string CurrentPageTitle => CurrentPage switch
    {
        Pages.Devices => Strings.Nav_Controllers,
        Pages.Buttons => Strings.Nav_Buttons,
        Pages.Trainer => Strings.Nav_Trainer,
        Pages.Apps => Strings.Nav_App,
        Pages.Settings => Strings.Nav_Settings,
        Pages.Diagnostic => Strings.Nav_Diagnostic,
        _ => "ShiftBridge", // l'Accueil porte le nom de l'application
    };

    /// <summary>La barre du haut propose ← (au lieu de ⚙) sur les pages qui ne sont pas des onglets.</summary>
    public bool ShowBackButton => CurrentPage is Pages.Settings or Pages.Diagnostic;

    private void RefreshSummaries()
    {
        // Manettes
        var connected = _bridge.Controllers.Where(c => c.State == ControllerState.Connected).ToList();
        bool locked = _bridge.Controllers.Any(c => c.State == ControllerState.Locked);
        ControllersSummary = connected.Count switch
        {
            0 => locked ? Strings.Ctrl_Locked : Strings.Ctrl_None,
            _ => Loc.Plural(connected.Count, Strings.Ctrl_Connected_One, Strings.Ctrl_Connected_Many),
        };
        var batteries = connected.Where(c => c.BatteryPercent.HasValue).Select(c => c.BatteryPercent!.Value).ToList();
        bool lowBattery = batteries.Count > 0 && batteries.Min() < BridgeService.LowBatteryPercent;
        ControllersDetail = batteries.Count > 0
            ? Loc.Fmt(Strings.Ctrl_Battery, batteries.Min()) + (lowBattery ? " · " + Strings.Home_LowBattery : "")
            : (connected.Count == 0 ? Strings.Ctrl_AddHint : "");
        ControllersState = connected.Count > 0 ? (lowBattery ? UiState.Warning : UiState.Ok) : locked ? UiState.Warning : _bridge.IsScanning ? UiState.Waiting : UiState.Off;

        // Touches : nombre de touches auxquelles une action est affectée
        int assigned = _bridge.Profile.Bindings.Count;
        ButtonsSummary = assigned == 0
            ? Strings.Keys_None
            : Loc.Plural(assigned, Strings.Keys_Configured_One, Strings.Keys_Configured_Many);
        ButtonsDetail = assigned == 0 ? Strings.Keys_ChooseAction : Strings.Keys_ClickToEdit;
        ButtonsState = assigned > 0 ? UiState.Ok : UiState.Warning;

        // Trainer
        string trainerName = UseBluetooth
            ? (SelectedBleTrainer?.Name ?? "")
            : (SelectedTrainer?.Name ?? TrainerHost);
        bool hasTrainer = !string.IsNullOrWhiteSpace(trainerName);
        if (_bridge.TrainerProxyActive)
        {
            bool recon = _bridge.TrainerLinkState == LinkState.Reconnecting;
            TrainerSummary = recon ? Strings.Trainer_Reconnecting : Strings.Trainer_VsActive;
            TrainerDetail = hasTrainer ? trainerName : "";
            TrainerState = recon ? UiState.Warning : UiState.Ok;
        }
        else
        {
            TrainerSummary = hasTrainer ? Strings.Trainer_ReadyToEnable : Strings.Trainer_NotConfigured;
            TrainerDetail = hasTrainer ? trainerName : Strings.Trainer_ChooseOnPage;
            TrainerState = hasTrainer ? UiState.Waiting : UiState.Off;
        }

        // Application d'entraînement
        IObcTransport?[] transports = [_ble, _tcp, _dircon];
        var withClient = transports.FirstOrDefault(t => t is { HasClient: true });
        if (withClient is not null)
        {
            AppSummary = Strings.App_Connected;
            AppDetail = withClient.CurrentAppInfo is { } i ? $"{i.AppId} {i.AppVersion}".Trim() : "";
            AppState = UiState.Ok;
        }
        else if (_bridge.TrainerProxyHasApp)
        {
            AppSummary = Strings.App_Connected;
            AppDetail = Strings.App_ViaTrainer;
            AppState = UiState.Ok;
        }
        else
        {
            AppSummary = Strings.App_Waiting;
            AppDetail = BleNameHint() ?? Strings.App_ChooseShiftBridge;
            AppState = transports.Any(t => t is not null) || _bridge.TrainerProxyActive ? UiState.Waiting : UiState.Off;
        }

        RefreshHeartRateSummary();

        // Prochaine étape pour quelqu'un qui découvre l'application (bandeau de l'Accueil, masqué une fois tout en place).
        if (ControllersState != UiState.Ok)
            SetNextStep(Strings.Step_1, "devices");
        else if (!hasTrainer)
            SetNextStep(Strings.Step_2Trainer, "trainer");
        else if (!_bridge.TrainerProxyActive)
            SetNextStep(Strings.Step_2Enable, "trainer");
        else if (AppState != UiState.Ok)
            SetNextStep(Strings.Step_3, "apps");
        else
            SetNextStep("", "");

        UpdateTelemetryLoop(); // les mesures en direct suivent l'état du proxy
    }

    /// <summary>Tuile Cardio de l'Accueil. Séparée de <see cref="RefreshSummaries"/> : le bpm change chaque seconde, le reste non.</summary>
    private void RefreshHeartRateSummary()
    {
        if (HeartRateConnected)
        {
            bool recon = _bridge.HeartRateState == LinkState.Reconnecting;
            HeartRateSummary = recon ? Strings.Hr_Summary_Reconnecting
                : _bridge.HeartRateBpm is { } hrBpm ? Loc.Fmt(Strings.Unit_Bpm, hrBpm) : Strings.Hr_Summary_Connected;
            HeartRateDetail = _bridge.HeartRateName;
            HeartRateState = recon ? UiState.Warning : UiState.Ok;
        }
        else
        {
            HeartRateSummary = IsScanningHeartRate ? Strings.Hr_Summary_Searching : Strings.Hr_Summary_None;
            HeartRateDetail = Strings.Hr_AddHint;
            HeartRateState = IsScanningHeartRate ? UiState.Waiting : UiState.Off;
        }
    }

    /// <summary>Permission système refusée (Bluetooth sous Android) : bandeau d'avertissement de l'Accueil avec accès aux réglages.</summary>
    [ObservableProperty] private string _permissionProblem = "";
    [ObservableProperty] private bool _hasPermissionProblem;

    /// <summary>Appelé par l'hôte : un texte signale le problème, null ou vide l'efface.</summary>
    public void SetPermissionProblem(string? text)
    {
        PermissionProblem = text ?? "";
        HasPermissionProblem = !string.IsNullOrEmpty(text);
    }

    [RelayCommand]
    private void OpenAppSettings() => _host.OpenAppSettings();

    [RelayCommand]
    private void OpenBatterySettings() => _host.OpenBatterySettings();

    [RelayCommand]
    private void OpenLocalNetworkSettings() => _host.OpenLocalNetworkSettings();

    [RelayCommand]
    private void OpenReleaseDownloads() => _host.OpenReleaseDownloads();

    /// <summary>Page publique de la politique de confidentialité (exigée par le Play Store).</summary>
    public const string PrivacyPolicyUrl = "https://renoua.github.io/ShiftBridge/privacy.html";

    [RelayCommand]
    private void OpenPrivacyPolicy() => _host.OpenUrl(PrivacyPolicyUrl);

    [ObservableProperty] private string _nextStepText = "";
    [ObservableProperty] private string _nextStepPage = "";
    [ObservableProperty] private bool _hasNextStep;

    private void SetNextStep(string text, string page)
    {
        NextStepText = text;
        NextStepPage = page;
        HasNextStep = text.Length > 0;
    }

    // ---------- Démarrage / arrêt ----------

    public async Task StartAsync()
    {
        AppendLog($"ShiftBridge v{_host.AppVersion}");
        AutoReconnect = _config.AutoReconnect;
        _bridge.AutoConnectKnown();
        await ApplyTransportsAsync();
        if ((EnableNetwork || EnableDirCon) && !_host.IsWifiOn) AppendLog(Strings.Log_NetworkNoWifi);
        StartScan();
        _ = StartSensorsAndProxyAsync();
        StartUpdateLoop();
    }

    private async Task StartSensorsAndProxyAsync()
    {
        await AutoConnectHeartRateAsync();
        if (_config.AutoStartVirtualShifting && HasSavedTrainer()) await AutoStartProxyAsync();
    }

    /// <summary>Un trainer a déjà été utilisé : son adresse (Wi-Fi) ou son identifiant (Bluetooth) est mémorisé.</summary>
    private bool HasSavedTrainer() => UseBluetooth
        ? SelectedBleTrainer is not null
        : !string.IsNullOrWhiteSpace(TrainerHost) && int.TryParse(TrainerPortText, out int port) && port is >= 1 and <= 65535;

    /// <summary>
    /// Démarrage automatique : réactive le virtual shifting avec le dernier trainer. Un trainer éteint ne bloque rien : le message
    /// d'échec s'affiche sur la page Trainer et un clic sur « Activer » relance la connexion.
    /// </summary>
    private async Task AutoStartProxyAsync()
    {
        if (IsProxyBusy || ProxyActive) return;
        IsProxyBusy = true;
        try
        {
            AppendLog(Strings.Log_AutoStarting);
            await StartProxyAsync();
            if (!ProxyActive) AppendLog(Strings.Log_AutoStartOff);
        }
        catch (Exception ex) { AppendLog(Loc.Fmt(Strings.Log_AutoStartFailed, ex.Message)); }
        finally { IsProxyBusy = false; }
    }

    public async Task StopAsync()
    {
        _config.AutoReconnect = AutoReconnect;
        SaveConfig();
        _updateCts.Cancel();
        await _bridge.DisposeAsync();
    }

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
        InspectorDevices.Clear();
        foreach (var d in insp.Devices().Take(40))
            InspectorDevices.Add(new InspectedDeviceRowViewModel(d, insp.IsCapturing && insp.CapturingAddress == d.Address));
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
        if (!await _host.ConfirmAsync(Loc.Fmt(Strings.Dev_ConfirmForget, row.Title))) return;
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
        Devices.Clear();
        foreach (var r in rows) Devices.Add(r);
        // Un nouveau modèle de manette vient d'être connecté : ses touches (et leurs assignations par défaut) apparaissent,
        // et la config (modèle mémorisé) est enregistrée.
        if (AddControllerRows()) { ReloadRows(); SaveConfig(); }
        IsScanning = _bridge.IsScanning;
        RefreshSummaries();
    }

    // ---------- Touches ----------

    // Clés de ressources (groupe, libellé, info-bulle) : résolues à la construction, dans la langue courante.
    private static readonly (PhysicalButton B, string Group, string Label, string? Tip)[] ButtonLayout =
    [
        (PhysicalButton.ClickPlus, "BtnGroup_Click", "Btn_Plus", null),
        (PhysicalButton.ClickMinus, "BtnGroup_Click", "Btn_Minus", null),
        (PhysicalButton.RightShiftUp, "BtnGroup_Right", "Btn_SideOrShiftUp", "Tip_RightShiftUp"),
        (PhysicalButton.RightShiftDown, "BtnGroup_Right", "Btn_ShiftDown", "Tip_ClickV2"),
        (PhysicalButton.RightPaddle, "BtnGroup_Right", "Btn_Paddle", null),
        (PhysicalButton.RightY, "BtnGroup_Right", "Btn_Y", null),
        (PhysicalButton.RightZ, "BtnGroup_Right", "Btn_Z", null),
        (PhysicalButton.RightA, "BtnGroup_Right", "Btn_A", null),
        (PhysicalButton.RightB, "BtnGroup_Right", "Btn_B", null),
        (PhysicalButton.RightPowerUp, "BtnGroup_Right", "Btn_PowerUp", null),
        (PhysicalButton.RightOnOff, "BtnGroup_Right", "Btn_OnOff", null),
        (PhysicalButton.LeftShiftUp, "BtnGroup_Left", "Btn_ShiftUp", "Tip_ClickV2"),
        (PhysicalButton.LeftShiftDown, "BtnGroup_Left", "Btn_SideOrShiftDown", "Tip_LeftShiftDown"),
        (PhysicalButton.LeftPaddle, "BtnGroup_Left", "Btn_Paddle", null),
        (PhysicalButton.LeftUp, "BtnGroup_Left", "Pad_DPadUp", null),
        (PhysicalButton.LeftDown, "BtnGroup_Left", "Pad_DPadDown", null),
        (PhysicalButton.LeftLeft, "BtnGroup_Left", "Pad_DPadLeft", null),
        (PhysicalButton.LeftRight, "BtnGroup_Left", "Pad_DPadRight", null),
        (PhysicalButton.LeftPowerUp, "BtnGroup_Left", "Btn_PowerUp", null),
        (PhysicalButton.LeftOnOff, "BtnGroup_Left", "Btn_OnOff", null),
    ];

    private static string LocalInputGroup => Strings.Btn_GroupLocal;

    private void BuildButtonRows()
    {
        foreach (var (b, group, label, tip) in ButtonLayout)
        {
            ButtonKey key = b;
            var row = new ButtonRowViewModel(key, Loc.Get(group), Loc.Get(label), tip is null ? null : Loc.Get(tip), Actions,
                ItemFor(key), OnRowChanged, RepeatFor(key), TargetFor(key), StepsFor(key));
            _rowByButton[key] = row;
            ButtonRows.Add(row);
        }
        // Touches clavier / manette / joystick déjà assignées dans le profil.
        if (HasLocalInput)
            foreach (var key in _editProfile.Bindings.Keys.Where(k => k.IsLocalInput).OrderBy(k => k.Id, StringComparer.Ordinal))
                AddLocalRow(key);
        AddControllerRows(); // appelle RebuildButtonGroups si besoin
        RebuildButtonGroups();
    }

    private void RebuildButtonGroups() =>
        ButtonGroups = [.. ButtonRows.GroupBy(r => r.Group).Select(g => new ButtonGroupViewModel(g.Key, [.. g]))];

    private IReadOnlyList<ButtonGroupViewModel> _buttonGroups = [];

    /// <summary>Les mêmes lignes regroupées par manette (gauche/droite…), pour les interfaces sans liste groupée native.</summary>
    public IReadOnlyList<ButtonGroupViewModel> ButtonGroups
    {
        get => _buttonGroups;
        private set { _buttonGroups = value; OnPropertyChanged(); }
    }

    // ---- Manettes BLE non Zwift : leurs touches n'apparaissent qu'une fois le modèle connu ----

    private readonly HashSet<ControllerKind> _shownKinds = [];

    private HashSet<ControllerKind> KindsToShow()
    {
        HashSet<ControllerKind> kinds = [];
        lock (_config.SyncRoot) foreach (var k in _config.KnownControllerKinds.Values) kinds.Add(k);
        foreach (var c in _bridge.Controllers) kinds.Add(c.Info.Kind);
        foreach (var key in _editProfile.Bindings.Keys.ToArray())
            if (key.TryAsController(out var kind, out _)) kinds.Add(kind);
        kinds.Remove(ControllerKind.Zwift);
        return kinds;
    }

    /// <summary>Ajoute les lignes de touches des modèles de manette connus (une seule fois par modèle). Vrai si des lignes ont été ajoutées.</summary>
    private bool AddControllerRows()
    {
        bool added = false;
        foreach (var kind in KindsToShow().OrderBy(k => k))
        {
            if (!_shownKinds.Add(kind)) continue;
            foreach (var (name, labelKey) in kind.Buttons())
            {
                if (kind == ControllerKind.SramAxs && !IsAxsButtonForMode(name, _config.SramAxsAdvanced)) continue;
                ButtonKey key = ButtonKey.Controller(kind, name);
                var row = new ButtonRowViewModel(key, Loc.Get("Model_" + kind), Loc.Get(labelKey), null, Actions, ItemFor(key),
                    OnRowChanged, RepeatFor(key), TargetFor(key), StepsFor(key));
                _rowByButton[key] = row;
                ButtonRows.Add(row);
            }
            added = true;
        }
        if (added) RebuildButtonGroups();
        return added;
    }

    /// <summary>
    /// Touches SRAM AXS réellement émises dans le mode choisi : simple/double appui en mode simple, pignon plus petit/plus grand
    /// (Up/Down) en mode avancé. Afficher les autres ne ferait que des lignes qui ne réagissent jamais.
    /// </summary>
    public static bool IsAxsButtonForMode(string name, bool advanced) =>
        advanced ? name is "Up" or "Down" : name is "Single" or "Double";

    /// <summary>Reconstruit les lignes AXS après un changement de mode (les assignations du profil ne sont pas touchées).</summary>
    private void RefreshAxsRows()
    {
        if (!_shownKinds.Remove(ControllerKind.SramAxs)) return;
        foreach (var row in ButtonRows.Where(r => r.Button.TryAsController(out var k, out _) && k == ControllerKind.SramAxs).ToList())
            RemoveRow(row);
        if (!AddControllerRows()) RebuildButtonGroups();
    }

    private ActionItem? ItemFor(ButtonKey b)
    {
        var id = _editProfile.Bindings.TryGetValue(b, out var binding) && binding.Actions.Count > 0
            ? binding.Actions[0] : ObcButtonId.None;
        return Actions.FirstOrDefault(a => a.Id == id) ?? Actions[0];
    }

    private bool RepeatFor(ButtonKey b) =>
        _editProfile.Bindings.TryGetValue(b, out var binding) && binding.RepeatOnHold;

    private int TargetFor(ButtonKey b) =>
        _editProfile.Bindings.TryGetValue(b, out var binding) ? binding.GearTarget : 0;

    private int StepsFor(ButtonKey b) =>
        _editProfile.Bindings.TryGetValue(b, out var binding) ? binding.GearSteps : 0;

    private void OnRowChanged(ButtonRowViewModel row)
    {
        if (_loading) return;
        if (row.Selected is null || row.Selected.Id == ObcButtonId.None)
            _editProfile.Bindings.Remove(row.Button);
        else
        {
            // Les délais par touche ne servent plus (la vitesse de répétition est globale) : on les conserve tels quels.
            var previous = _editProfile.Bindings.GetValueOrDefault(row.Button);
            _editProfile.Bindings[row.Button] = new ButtonBinding
            {
                Actions = [row.Selected.Id],
                RepeatOnHold = row.RepeatOnHold && row.CanRepeat,
                RepeatDelayMs = previous?.RepeatDelayMs ?? 500,
                RepeatIntervalMs = previous?.RepeatIntervalMs ?? 250,
                GearTarget = row.CanTarget ? row.GearTarget : 0,
                GearSteps = row.CanStep ? row.GearSteps : 0,
            };
        }
        SaveConfig();
        _bridge.RefreshLocalInput(); // la liste des touches écoutées / avalées dépend des assignations
        RefreshSummaries();
    }

    [RelayCommand]
    private async Task ResetProfileAsync()
    {
        if (!await _host.ConfirmAsync(Strings.Profile_ConfirmReset)) return;
        _bridge.ResetProfile(_editKey); // un profil neuf remplace l'ancien d'un bloc : le mapping ne voit jamais un état à moitié vidé
        _editProfile = _bridge.GetProfile(_editKey);
        ReloadRows(); // les touches clavier/manette ajoutées disparaissent avec leurs assignations
        SaveConfig();
        _bridge.RefreshLocalInput();
        RefreshSummaries();
    }

    /// <summary>Recharge les lignes de l'onglet Touches d'après le profil édité (changement de profil, réinitialisation).</summary>
    private void ReloadRows()
    {
        foreach (var r in ButtonRows.Where(r => r.CanRemove).ToList()) RemoveRow(r);
        if (HasLocalInput)
            foreach (var key in _editProfile.Bindings.Keys.Where(k => k.IsLocalInput).OrderBy(k => k.Id, StringComparer.Ordinal))
                AddLocalRow(key);
        foreach (var r in ButtonRows.Where(r => !r.CanRemove))
            r.SetSilently(ItemFor(r.Button), RepeatFor(r.Button), TargetFor(r.Button), StepsFor(r.Button));
    }

    // ---------- Profils par appli ----------

    /// <summary>« Par défaut » puis un profil par appli d'entraînement : celui qu'on modifie dans l'onglet Touches.</summary>
    public ObservableCollection<ProfileOption> ProfileOptions { get; } = [];

    [ObservableProperty] private ProfileOption? _selectedProfileOption;
    /// <summary>Profil que ShiftBridge utilise en ce moment (celui de l'appli connectée, ou le profil par défaut).</summary>
    [ObservableProperty] private string _activeProfileText = "";
    [ObservableProperty] private string _createAppProfileText = "";
    /// <summary>L'appli que ShiftBridge a détectée (nom annoncé dans App Info), ou pourquoi aucun profil ne peut lui être proposé.</summary>
    [ObservableProperty] private string _connectedAppText = "";
    /// <summary>Une appli est connectée et n'a pas encore de profil : on propose d'en créer un.</summary>
    [ObservableProperty] private bool _canCreateAppProfile;
    /// <summary>Le profil modifié est celui d'une appli (le profil par défaut ne se supprime pas).</summary>
    [ObservableProperty] private bool _canDeleteProfile;
    private bool _profileRefreshing;

    partial void OnSelectedProfileOptionChanged(ProfileOption? value)
    {
        if (_profileRefreshing || value is null || value.Key == _editKey) return;
        _editKey = value.Key;
        _editProfile = _bridge.GetProfile(_editKey);
        CanDeleteProfile = _editKey.Length > 0;
        ReloadRows();
    }

    private void RefreshProfiles()
    {
        var apps = _bridge.AppProfiles;
        _profileRefreshing = true;
        try
        {
            if (_editKey.Length > 0 && !apps.ContainsKey(_editKey))
            {
                _editKey = ""; // le profil modifié a disparu : retour au profil par défaut
                _editProfile = _bridge.DefaultProfile;
                ReloadRows();
            }
            ProfileOptions.Clear();
            ProfileOptions.Add(new ProfileOption("", Strings.Profile_Default));
            foreach (var (key, profile) in apps.OrderBy(kv => kv.Value.Name, StringComparer.CurrentCultureIgnoreCase))
                ProfileOptions.Add(new ProfileOption(key, profile.Name));
            SelectedProfileOption = ProfileOptions.First(o => o.Key == _editKey);
            CanDeleteProfile = _editKey.Length > 0;
        }
        finally { _profileRefreshing = false; }

        string active = _bridge.ActiveProfileKey;
        ActiveProfileText = Loc.Fmt(Strings.Profile_Active, active.Length > 0 && apps.TryGetValue(active, out var current) ? current.Name : Strings.Profile_Default);
        string connected = _bridge.ConnectedAppId;
        CanCreateAppProfile = connected.Length > 0 && !apps.ContainsKey(_bridge.ConnectedAppKey);
        CreateAppProfileText = Loc.Fmt(Strings.Profile_Create, connected);
        ConnectedAppText = connected.Length > 0 ? Loc.Fmt(Strings.Profile_AppConnected, connected) : Strings.Profile_AppNone;
    }

    [RelayCommand]
    private void CreateAppProfile()
    {
        if (_bridge.CreateAppProfile(_bridge.ConnectedAppId) is not { } key) return;
        _editKey = key;
        _editProfile = _bridge.GetProfile(key);
        ReloadRows();
        SaveConfig();
        RefreshProfiles();
        RefreshSummaries();
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (_editKey.Length == 0) return;
        if (!await _host.ConfirmAsync(Strings.Profile_ConfirmDelete)) return;
        string key = _editKey;
        _editKey = "";
        _editProfile = _bridge.DefaultProfile;
        ReloadRows();
        _bridge.RemoveAppProfile(key);
        SaveConfig();
        RefreshProfiles();
        RefreshSummaries();
    }

    // ---------- Clavier, télécommandes et manettes de jeu ----------

    /// <summary>Faux sur Android : l'interface masque alors tout ce qui touche au clavier et aux manettes de jeu.</summary>
    public bool HasLocalInput { get; }

    public ObservableCollection<LocalDeviceRowViewModel> LocalDevices { get; } = [];

    [ObservableProperty] private bool _keyboardInputEnabled;
    [ObservableProperty] private bool _gamepadInputEnabled;
    [ObservableProperty] private bool _suppressBoundKeys;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _inputCaptureStatus = "";

    partial void OnKeyboardInputEnabledChanged(bool value) => ApplyLocalInputSetting(() => _config.KeyboardInputEnabled = value);
    partial void OnGamepadInputEnabledChanged(bool value) => ApplyLocalInputSetting(() => _config.GamepadInputEnabled = value);
    partial void OnSuppressBoundKeysChanged(bool value) => ApplyLocalInputSetting(() => _config.SuppressBoundKeys = value);

    private void ApplyLocalInputSetting(Action set)
    {
        if (_loading) return;
        set();
        SaveConfig();
        _bridge.RefreshLocalInput();
        if (IsCapturing && !KeyboardInputEnabled && !GamepadInputEnabled) CancelCapture();
    }

    private void RefreshLocalDevices()
    {
        LocalDevices.Clear();
        foreach (var d in _bridge.LocalInput.Devices)
            LocalDevices.Add(new LocalDeviceRowViewModel(d.Name, d.Kind switch
            {
                LocalInputKind.Keyboard => Strings.Input_KindKeyboard,
                LocalInputKind.Gamepad => Strings.Input_KindGamepad,
                _ => Strings.Input_KindJoystick,
            }, d.CanVibrate));
    }

    private ButtonRowViewModel AddLocalRow(ButtonKey key)
    {
        if (_rowByButton.TryGetValue(key, out var existing)) return existing;
        var row = new ButtonRowViewModel(key, LocalInputGroup, ButtonKeyNames.Label(key), null, Actions, ItemFor(key),
            OnRowChanged, RepeatFor(key), TargetFor(key), StepsFor(key)) { CanRemove = true };
        _rowByButton[key] = row;
        ButtonRows.Add(row);
        return row;
    }

    private void RemoveRow(ButtonRowViewModel row)
    {
        _rowByButton.Remove(row.Button);
        ButtonRows.Remove(row);
    }

    private int _captureToken;

    /// <summary>Attend la prochaine touche (clavier, manette ou joystick) pour créer une ligne d'assignation.</summary>
    [RelayCommand]
    private void AddInputKey()
    {
        if (!HasLocalInput) return;
        if (!KeyboardInputEnabled && !GamepadInputEnabled)
        {
            InputCaptureStatus = Strings.Capture_EnableFirst;
            return;
        }
        IsCapturing = true;
        InputCaptureStatus = KeyboardInputEnabled
            ? Strings.Capture_PressKey
            : Strings.Capture_PressPad;
        _bridge.LocalInput.CaptureNext = true;

        // Filet de sécurité : le clavier étant avalé pendant la capture, on ne reste jamais bloqué plus de 15 s.
        int token = ++_captureToken;
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => OnUi(() =>
        {
            if (IsCapturing && token == _captureToken) { CancelCapture(); InputCaptureStatus = Strings.Capture_Timeout; }
        }));
    }

    [RelayCommand]
    private void CancelCapture()
    {
        _captureToken++;
        _bridge.LocalInput.CaptureNext = false;
        IsCapturing = false;
        InputCaptureStatus = "";
    }

    private void OnInputCaptured(ButtonKey key)
    {
        if (!IsCapturing) return;
        _captureToken++;
        IsCapturing = false;
        var row = AddLocalRow(key);
        InputCaptureStatus = row.Selected is { Id: not ObcButtonId.None }
            ? Loc.Fmt(Strings.Capture_AlreadyAssigned, row.Label)
            : Loc.Fmt(Strings.Capture_Added, row.Label);
    }

    [RelayCommand]
    private void RemoveInputKey(ButtonRowViewModel? row)
    {
        if (row is not { CanRemove: true }) return;
        _editProfile.Bindings.Remove(row.Button);
        RemoveRow(row);
        SaveConfig();
        _bridge.RefreshLocalInput();
        RefreshSummaries();
    }

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
        RefreshSummaries();
    }

    /// <summary>Adresses et ports à saisir dans l'appli d'entraînement si elle ne trouve pas ShiftBridge toute seule.</summary>
    [ObservableProperty] private string _localAddressText = "";

    // ---------- Virtual shifting (proxy trainer) ----------

    public ObservableCollection<DiscoveredTrainer> FoundTrainers { get; } = [];
    [ObservableProperty] private DiscoveredTrainer? _selectedTrainer;
    [ObservableProperty] private string _trainerHost = "";
    [ObservableProperty] private string _trainerPortText = "36866";
    [ObservableProperty] private string _trainerStatus = Strings.Trainer_Stopped;
    [ObservableProperty] private bool _proxyActive;
    [ObservableProperty] private bool _isSearchingTrainer;
    // Message ponctuel (recherche, échec…) conservé tant que le proxy est arrêté, au lieu de tester le texte affiché.
    private bool _trainerMessageSticky;

    private void SetTrainerMessage(string text) { _trainerMessageSticky = true; TrainerStatus = text; }

    // Mode natif Zwift : le trainer applique lui-même le rapport (repli FTMS si indisponible)
    [ObservableProperty] private bool _nativeZwiftMode = true;
    [ObservableProperty] private string _nativeStatus = "";
    [ObservableProperty] private string _riderWeightText = "75";
    [ObservableProperty] private string _bikeWeightText = "9";

    // Qui gère les vitesses virtuelles : ShiftBridge (défaut) ou l'appli d'entraînement
    [ObservableProperty] private bool _appHandlesShifting;

    partial void OnAppHandlesShiftingChanged(bool value)
    {
        if (_loading) return;
        _ = _bridge.SetAppHandlesShiftingAsync(value);
        SaveConfig();
    }

    partial void OnNativeZwiftModeChanged(bool value)
    {
        if (_loading) return;
        _ = _bridge.SetNativeZwiftModeAsync(value);
        SaveConfig();
    }

    partial void OnRiderWeightTextChanged(string value) => ApplyWeights();
    partial void OnBikeWeightTextChanged(string value) => ApplyWeights();

    // Les champs numériques sont liés à chaque frappe : sans attente, chaque valeur intermédiaire valide serait envoyée au vrai
    // trainer (la résistance changerait en direct) et écrite sur le disque.
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(600);
    private CancellationTokenSource? _weightsCts, _drivetrainCts, _gearSaveCts, _gearCountCts;

    private void Debounce(ref CancellationTokenSource? slot, Action apply, TimeSpan? delay = null)
    {
        slot?.Cancel();
        var cts = slot = new CancellationTokenSource();
        _ = RunLaterAsync(cts.Token, apply, delay ?? TypingDelay);
    }

    private async Task RunLaterAsync(CancellationToken ct, Action apply, TimeSpan delay)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        _ui.Post(() => { if (!ct.IsCancellationRequested) apply(); });
    }

    private void ApplyWeights()
    {
        if (_loading) return;
        Debounce(ref _weightsCts, ApplyWeightsNow);
    }

    private void ApplyWeightsNow()
    {
        if (TryParseKg(RiderWeightText, out var rider) && TryParseKg(BikeWeightText, out var bike))
        {
            _bridge.SetWeights(rider, bike);
            SaveConfig();
        }
    }

    private static bool TryParseKg(string text, out double kg) =>
        double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out kg) && kg > 0;

    private void UpdateNativeStatus()
    {
        NativeStatus = !_bridge.NativeZwiftMode ? Strings.Native_Off
            : !_bridge.TrainerProxyActive ? Strings.Native_Pending
            : _bridge.TrainerNativeActive ? Strings.Native_Active
            : Strings.Native_Fallback;
    }

    // Nombre de vitesses (curseur 8–24) : la valeur est un double pour les curseurs ; arrondie à l'entier le plus proche
    [ObservableProperty] private double _gearCountValue = 24;
    [ObservableProperty] private string _gearCountText = "";

    partial void OnGearCountValueChanged(double value)
    {
        int count = (int)Math.Clamp(Math.Round(value), 8, 24);
        GearCountText = Loc.Fmt(Strings.Trainer_GearCountValue, count);
        if (_loading) return;
        // Glisser le curseur produit une valeur par cran : on n'applique (et ne sauvegarde) que la dernière.
        Debounce(ref _gearCountCts, () => { _bridge.SetGearCount(count); SaveConfig(); }, TimeSpan.FromMilliseconds(300));
    }

    // Braquet physique (plateau ÷ pignon) : rapport de référence du virtual shifting
    [ObservableProperty] private string _chainringText = "34";
    [ObservableProperty] private string _cogText = "14";
    [ObservableProperty] private string _drivetrainInfo = "";
    [ObservableProperty] private string _measureResult = "";
    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private bool _canUseMeasurement;
    private RatioMeasurement? _lastMeasurement;

    public bool CanMeasure => !IsMeasuring;
    partial void OnIsMeasuringChanged(bool value) => OnPropertyChanged(nameof(CanMeasure));
    partial void OnChainringTextChanged(string value) => ApplyDrivetrainFromText();
    partial void OnCogTextChanged(string value) => ApplyDrivetrainFromText();

    private void ApplyDrivetrainFromText()
    {
        if (_loading) return;
        Debounce(ref _drivetrainCts, ApplyDrivetrainNow);
    }

    private void ApplyDrivetrainNow()
    {
        if (int.TryParse(ChainringText, out int chainring) && int.TryParse(CogText, out int cog))
        {
            var d = new Drivetrain(chainring, cog);
            if (d.IsValid)
            {
                _bridge.SetDrivetrain(d);
                SaveConfig();
                UpdateDrivetrainInfo();
                return;
            }
        }
        DrivetrainInfo = Loc.Fmt(Strings.Drive_Range, Drivetrain.MinChainring, Drivetrain.MaxChainring, Drivetrain.MinCog, Drivetrain.MaxCog);
    }

    private void UpdateDrivetrainInfo()
    {
        var d = _bridge.Drivetrain;
        DrivetrainInfo = Loc.Fmt(Strings.Drive_RealAndVirtual, d.Ratio, d, _bridge.Gear.Gear, _bridge.Gear.GearCount, _bridge.GearFactor);
    }

    [RelayCommand]
    private async Task MeasureDrivetrainAsync()
    {
        IsMeasuring = true;
        CanUseMeasurement = false;
        MeasureResult = Strings.Measure_Running;
        try
        {
            var r = await _bridge.MeasureDrivetrainAsync();
            _lastMeasurement = r;
            if (!r.Success) { MeasureResult = r.Message; return; }

            double entered = _bridge.Drivetrain.Ratio;
            double gap = r.Ratio / entered - 1;
            bool notable = Math.Abs(gap) > 0.03;
            MeasureResult = Loc.Fmt(Strings.Measure_Result, r.Message, _bridge.Drivetrain, entered, gap,
                notable ? Strings.Measure_Notable : Strings.Measure_Consistent);
            CanUseMeasurement = notable;
        }
        catch (Exception ex) { MeasureResult = Loc.Fmt(Strings.Measure_Failed, ex.Message); }
        finally { IsMeasuring = false; }
    }

    [RelayCommand]
    private void UseMeasuredRatio()
    {
        if (_lastMeasurement is not { Success: true } m || !int.TryParse(CogText, out int cog)) return;
        int chainring = Drivetrain.ChainringFor(m.Ratio, cog);
        ChainringText = chainring.ToString(); // applique le nouveau braquet (via OnChainringTextChanged)
        CanUseMeasurement = false;
        MeasureResult += " " + Loc.Fmt(Strings.Measure_Adjusted, chainring, cog);
    }

    [ObservableProperty] private bool _slopeOnlyMode;

    partial void OnSlopeOnlyModeChanged(bool value)
    {
        if (_loading) return;
        _bridge.SlopeOnlyMode = value;
        SaveConfig();
    }

    // Trainers Bluetooth
    public ObservableCollection<DiscoveredBleTrainer> FoundBleTrainers { get; } = [];
    private DiscoveredBleTrainer? _savedBleTrainer; // dernier trainer utilisé : proposé sans nouvelle recherche, conservé dans la liste
    [ObservableProperty] private DiscoveredBleTrainer? _selectedBleTrainer;
    [ObservableProperty] private bool _useBluetooth;
    [ObservableProperty] private bool _isScanningBle;

    public bool UseWifi { get => !UseBluetooth; set => UseBluetooth = !value; }

    partial void OnUseBluetoothChanged(bool value)
    {
        OnPropertyChanged(nameof(UseWifi));
        if (_loading) return;
        _config.TrainerTransport = value ? "ble" : "wifi";
        SaveConfig();
    }

    [RelayCommand]
    private void FindBleTrainers()
    {
        if (!EnsureBluetooth(FindBleTrainers)) { SetTrainerMessage(Strings.Bt_Off); return; }
        // La liste n'est pas vidée : le trainer mémorisé ou sélectionné y reste, les autres s'y ajoutent au fil des annonces.
        _bridge.StartTrainerScan();
        IsScanningBle = true;
        SetTrainerMessage(Strings.Ble_Searching);
    }

    [RelayCommand]
    private void StopBleScan()
    {
        _bridge.StopTrainerScan();
        IsScanningBle = false;
    }

    private void RefreshBleTrainers()
    {
        var selected = SelectedBleTrainer;
        var seen = _bridge.BleTrainers;
        // Mise à jour sur place : plus de clignotement, et le trainer choisi (ou mémorisé) n'est jamais perdu avant d'être revu.
        TrainerListMerge.Apply(FoundBleTrainers, seen, [selected, _savedBleTrainer]);
        if (selected is not null)
            SelectedBleTrainer = FoundBleTrainers.FirstOrDefault(t => t.Address == selected.Address); // la ligne a pu être remplacée
        IsScanningBle = _bridge.TrainerScanning;
        if (IsScanningBle)
            SetTrainerMessage(seen.Count == 0
                ? Strings.Ble_SearchingShort
                : Loc.Plural(seen.Count, Strings.Ble_Found_One, Strings.Ble_Found_Many)); // trouvés réellement, pas la ligne mémorisée
        RefreshSummaries();
    }

    partial void OnSelectedTrainerChanged(DiscoveredTrainer? value)
    {
        if (value is null) return;
        TrainerHost = value.Host;
        TrainerPortText = value.Port.ToString();
        RefreshSummaries();
    }

    [RelayCommand]
    private async Task FindTrainersAsync()
    {
        if (!EnsureWifi()) return;
        IsSearchingTrainer = true;
        SetTrainerMessage(Strings.Wifi_Searching);
        try
        {
            var list = await _bridge.FindTrainersAsync();
            FoundTrainers.Clear();
            // On exclut nos propres annonces (OBC et proxy).
            foreach (var t in list.Where(t => !t.Name.StartsWith("ShiftBridge", StringComparison.OrdinalIgnoreCase)))
                FoundTrainers.Add(t);
            SetTrainerMessage(FoundTrainers.Count == 0
                ? Strings.Wifi_NoneFound
                : Loc.Plural(FoundTrainers.Count, Strings.Wifi_Found_One, Strings.Wifi_Found_Many));
            if (FoundTrainers.Count == 1) SelectedTrainer = FoundTrainers[0];
        }
        catch (Exception ex) { SetTrainerMessage(Loc.Fmt(Strings.Wifi_SearchFailed, ex.Message)); }
        finally { IsSearchingTrainer = false; }
    }

    [RelayCommand]
    private async Task StartProxyAsync()
    {
        TrainerEndpoint endpoint;
        if (UseBluetooth)
        {
            if (!EnsureBluetooth(() => _ = StartProxyAsync())) { SetTrainerMessage(Strings.Bt_Off); return; }
            if (SelectedBleTrainer is not { } ble)
            {
                SetTrainerMessage(Strings.Trainer_ChooseBle);
                CurrentPage = "trainer"; // depuis l'Accueil, on emmène l'utilisateur là où il doit choisir
                return;
            }
            endpoint = new TrainerEndpoint.Ble(ble.Address, ble.Name);
        }
        else
        {
            if (!EnsureWifi()) return;
            if (string.IsNullOrWhiteSpace(TrainerHost) || !int.TryParse(TrainerPortText, out int port) || port is < 1 or > 65535)
            {
                SetTrainerMessage(Strings.Trainer_ChooseWifi);
                CurrentPage = "trainer";
                return;
            }
            // On reprend les attributs mDNS du vrai trainer (liste de services…) pour que l'appli nous reconnaisse comme lui.
            var txt = SelectedTrainer is { } t && t.Host == TrainerHost.Trim() ? t.Txt : null;
            endpoint = new TrainerEndpoint.DirCon(TrainerHost.Trim(), port, txt);
        }

        _trainerMessageSticky = false;
        TrainerStatus = Strings.Trainer_Connecting;
        IsScanningBle = false;
        try
        {
            await _bridge.StartTrainerProxyAsync(endpoint);
            SaveConfig();
        }
        catch (Exception ex) { SetTrainerMessage(Loc.Fmt(Strings.Trainer_ConnectFailed, ex.Message)); }
        RefreshTrainer();
    }

    public string ProxyButtonText => IsProxyBusy ? Strings.Proxy_Connecting : ProxyActive ? Strings.Proxy_Stop : Strings.Proxy_Enable;
    partial void OnProxyActiveChanged(bool value) => OnPropertyChanged(nameof(ProxyButtonText));

    /// <summary>Un démarrage ou un arrêt du proxy est en cours : le bouton est désactivé (pas de double démarrage).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleProxyCommand))]
    private bool _isProxyBusy;
    partial void OnIsProxyBusyChanged(bool value) => OnPropertyChanged(nameof(ProxyButtonText));
    private bool CanToggleProxy() => !IsProxyBusy;

    [RelayCommand(CanExecute = nameof(CanToggleProxy))]
    private async Task ToggleProxyAsync()
    {
        if (IsProxyBusy) return;
        IsProxyBusy = true;
        try
        {
            if (ProxyActive) { await _bridge.StopTrainerProxyAsync(); RefreshTrainer(); }
            else await StartProxyAsync();
        }
        finally { IsProxyBusy = false; }
    }

    // ---------- Cardio (ceinture / montre Bluetooth republiée par le proxy) ----------

    public ObservableCollection<DiscoveredHeartRateSensor> FoundHeartRateSensors { get; } = [];
    private DiscoveredHeartRateSensor? _savedHeartRate;
    [ObservableProperty] private DiscoveredHeartRateSensor? _selectedHeartRateSensor;
    [ObservableProperty] private bool _isScanningHeartRate;
    [ObservableProperty] private bool _heartRateConnected;
    [ObservableProperty] private bool _isHeartRateBusy;
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
            int i = FoundHeartRateSensors.ToList().FindIndex(x => x.Address == s.Address);
            if (i < 0) FoundHeartRateSensors.Add(s); else if (FoundHeartRateSensors[i] != s) FoundHeartRateSensors[i] = s;
        }
        if (selected is not null)
            SelectedHeartRateSensor = FoundHeartRateSensors.FirstOrDefault(t => t.Address == selected.Address);
        IsScanningHeartRate = _bridge.HeartRateScanning;
        if (IsScanningHeartRate && !HeartRateConnected)
            HeartRateStatus = seen.Count == 0 ? Strings.Hr_Searching : Loc.Plural(seen.Count, Strings.Hr_Found_One, Strings.Hr_Found_Many);
    }

    [RelayCommand]
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

    [RelayCommand]
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

    public string BleScanButtonText => IsScanningBle ? Strings.Ble_BtnStop : Strings.Ble_BtnSearch;
    partial void OnIsScanningBleChanged(bool value) => OnPropertyChanged(nameof(BleScanButtonText));

    [RelayCommand]
    private void ToggleBleScan() { if (IsScanningBle) StopBleScan(); else FindBleTrainers(); }

    [RelayCommand]
    private async Task StopProxyAsync()
    {
        await _bridge.StopTrainerProxyAsync();
        RefreshTrainer();
    }

    private void RefreshTrainer()
    {
        UpdateDrivetrainInfo();
        UpdateNativeStatus();
        UpdateOverlay();
        ProxyActive = _bridge.TrainerProxyActive;
        RefreshSummaries();
        if (!ProxyActive) { if (!_trainerMessageSticky) TrainerStatus = Strings.Trainer_Stopped; return; }
        string gear = Loc.Fmt(Strings.Trainer_GearLine, _bridge.Gear.Gear, _bridge.Gear.GearCount, _bridge.GearFactor);
        string recon = _bridge.TrainerReconnectCount > 0
            ? " " + Loc.Plural(_bridge.TrainerReconnectCount, Strings.Trainer_Reconnects_One, Strings.Trainer_Reconnects_Many)
            : "";
        if (_bridge.TrainerLinkState == LinkState.Reconnecting)
        {
            TrainerStatus = Strings.Trainer_ReconnectingWarn + " " + gear;
            return;
        }
        TrainerStatus = _bridge.TrainerProxyHasApp
            ? Loc.Fmt(Strings.Trainer_ActiveApp, gear, recon)
            : Loc.Fmt(Strings.Trainer_ActiveNoApp, _bridge.TrainerProxyPort, gear, recon);
    }

    // ---------- Vitesse de répétition (vitesse + / − tenue) ----------

    public IReadOnlyList<RepeatSpeedOption> RepeatSpeedOptions { get; } =
    [
        new(RepeatSpeed.Slow, Strings.RepeatSpeed_Slow),
        new(RepeatSpeed.Normal, Strings.RepeatSpeed_Normal),
        new(RepeatSpeed.Fast, Strings.RepeatSpeed_Fast),
        new(RepeatSpeed.VeryFast, Strings.RepeatSpeed_VeryFast),
    ];

    [ObservableProperty] private RepeatSpeedOption? _selectedRepeatSpeed;

    partial void OnSelectedRepeatSpeedChanged(RepeatSpeedOption? value)
    {
        if (_loading || value is null) return;
        _config.RepeatSpeed = value.Speed; // lu à chaque appui par BridgeService : effet immédiat
        SaveConfig();
    }

    // ---------- Langue de l'interface ----------

    /// <summary>Automatique (langue du système), français, anglais. Les noms des langues ne sont jamais traduits.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
    [
        new(AppLanguage.Auto, Strings.Lang_Auto),
        new(AppLanguage.French, "Français"),
        new(AppLanguage.English, "English"),
    ];

    [ObservableProperty] private LanguageOption? _selectedLanguage;

    /// <summary>La langue choisie diffère de celle appliquée au démarrage : elle ne sera prise en compte qu'au redémarrage.</summary>
    [ObservableProperty] private bool _languageRestartNeeded;

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_loading || value is null) return;
        _config.Language = value.Code == AppLanguage.Auto ? null : value.Code;
        SaveConfig();
        LanguageRestartNeeded = AppLanguage.Resolve(value.Code, AppLanguage.SystemUiCulture) != AppLanguage.ActiveLanguage;
    }

    // ---------- Application Windows : zone de notification et démarrage ----------
    // Les cases n'ont de sens que sous Windows (HasLocalInput est faux sous Android, où la vue ne les affiche pas) ; c'est
    // MainWindow qui réagit à ces propriétés (icône, registre).

    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartMinimizedChanged(bool value) { if (!_loading) { _config.StartMinimized = value; SaveConfig(); } }
    partial void OnMinimizeToTrayChanged(bool value) { if (!_loading) { _config.MinimizeToTray = value; SaveConfig(); } }
    partial void OnStartWithWindowsChanged(bool value) { if (!_loading) { _config.StartWithWindows = value; SaveConfig(); } }

    // ---------- Mises à jour ----------

    private static readonly TimeSpan UpdateFirstDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    private readonly CancellationTokenSource _updateCts = new();
    private ShiftBridge.Core.Updates.UpdateInfo? _pendingUpdate;
    private bool _updateDismissed;
    private int _updateCheckInProgress;

    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _updateAvailable;
    /// <summary>Une version plus récente existe (même si le bandeau de l'Accueil a été masqué par « Plus tard ») : boutons des Réglages.</summary>
    [ObservableProperty] private bool _hasPendingUpdate;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private bool _isCheckingUpdates;
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
            _ui.Post(() => { IsCheckingUpdates = true; if (manual) UpdateStatus = ""; });
            var update = await checker.CheckAsync(_updateCts.Token);
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
                UpdateText = Loc.Fmt(Strings.Update_Available, update.Version.ToString());
                UpdateStatus = UpdateText;
                if (!_updateDismissed || manual) { _updateDismissed = false; UpdateAvailable = true; }
            });
        }
        catch (OperationCanceledException) when (_updateCts.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppendLog($"Mise à jour : vérification impossible ({ex.Message})");
            if (manual) _ui.Post(() => UpdateStatus = Strings.Update_CheckFailed);
        }
        finally
        {
            _ui.Post(() => IsCheckingUpdates = false);
            Interlocked.Exchange(ref _updateCheckInProgress, 0);
        }
    }

    [RelayCommand]
    private Task CheckUpdatesNow() => IsUpdating || IsCheckingUpdates ? Task.CompletedTask : CheckUpdatesAsync(manual: true);

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

    [RelayCommand]
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
        if (ProxyActive && !_host.Confirm(Strings.Update_ConfirmProxy)) return;

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
    public string TrayTooltip => Loc.Fmt(Strings.Tray_Tooltip, _bridge.Gear.Gear, _bridge.Gear.GearCount);

    // ---------- Divers ----------

    private void RefreshGear()
    {
        GearText = $"{_bridge.Gear.Gear} / {_bridge.Gear.GearCount}";
        UpdateOverlay();
        if (OverlayFlash && _bridge.LastGearChange != GearChange.None) GearFlashRequested?.Invoke(_bridge.LastGearChange);
        // La vitesse est mémorisée (BridgeService.OnGearChanged) ; on attend la fin d'une rafale d'appuis avant d'écrire sur le disque.
        if (!_loading) Debounce(ref _gearSaveCts, SaveConfig, TimeSpan.FromSeconds(2));
    }

    // ---------- Rapport affiché dans l'appli (OBC) et superposition ----------

    [ObservableProperty] private bool _reportGearToApp = true;
    [ObservableProperty] private bool _autoStartVirtualShifting = true;

    partial void OnAutoStartVirtualShiftingChanged(bool value)
    {
        if (_loading) return;
        _config.AutoStartVirtualShifting = value;
        SaveConfig();
    }
    [ObservableProperty] private bool _showOverlay;
    [ObservableProperty] private bool _overlayLocked;
    [ObservableProperty] private double _overlayScale = 1.0;
    [ObservableProperty] private string _overlayGear = "";
    [ObservableProperty] private string _overlayDetail = "";

    partial void OnReportGearToAppChanged(bool value)
    {
        if (_loading) return;
        _bridge.ReportGearToApp = value;
        SaveConfig();
    }

    // Réglages de la superposition (Windows). Elle montre la vitesse virtuelle ; en option, une barre des vitesses, un
    // clignotement à chaque changement (rouge aux butées) et la puissance, la cadence et la vitesse du trainer.
    [ObservableProperty] private double _overlayOpacity = 0.85;
    [ObservableProperty] private bool _overlayShowBar = true;
    [ObservableProperty] private bool _overlayFlash = true;
    [ObservableProperty] private bool _overlayShowTelemetry;
    [ObservableProperty] private string _overlayTelemetry = "";
    [ObservableProperty] private IReadOnlyList<GearSegment> _overlaySegments = [];

    /// <summary>Demande un clignotement de la superposition (changement normal, ou butée atteinte).</summary>
    public event Action<GearChange>? GearFlashRequested;

    partial void OnShowOverlayChanged(bool value)
    {
        UpdateTelemetryLoop();
        if (!_loading) { _config.ShowOverlay = value; SaveConfig(); }
    }
    partial void OnOverlayLockedChanged(bool value) { if (!_loading) { _config.OverlayLocked = value; SaveConfig(); } }
    partial void OnOverlayScaleChanged(double value) { if (!_loading) { _config.OverlayScale = value; Debounce(ref _opacitySaveCts, SaveConfig); } } // une écriture par cran de molette serait inutile
    partial void OnOverlayOpacityChanged(double value) { if (!_loading) { _config.OverlayOpacity = value; Debounce(ref _opacitySaveCts, SaveConfig); } }
    partial void OnOverlayShowBarChanged(bool value) { if (!_loading) { _config.OverlayShowBar = value; SaveConfig(); } }
    partial void OnOverlayFlashChanged(bool value) { if (!_loading) { _config.OverlayFlash = value; SaveConfig(); } }
    partial void OnOverlayShowTelemetryChanged(bool value)
    {
        UpdateTelemetryLoop();
        if (!_loading) { _config.OverlayShowTelemetry = value; SaveConfig(); }
    }

    private CancellationTokenSource? _opacitySaveCts, _telemetryCts;

    /// <summary>Texte puissance · cadence · cardio · vitesse en direct (bloc principal de l'Accueil), vide tant que le proxy est arrêté.</summary>
    [ObservableProperty] private string _liveTelemetry = "";

    private bool OverlayWantsTelemetry => ShowOverlay && OverlayShowTelemetry;

    /// <summary>
    /// Rafraîchit une fois par seconde le texte puissance · cadence · cardio · vitesse, seulement tant que le proxy tourne (Accueil)
    /// ou que la superposition l'affiche. Sans effet si la boucle est déjà dans le bon état : appelée à chaque RefreshSummaries.
    /// </summary>
    private void UpdateTelemetryLoop()
    {
        bool wanted = _bridge.TrainerProxyActive || OverlayWantsTelemetry;
        if (wanted && _telemetryCts is not null) { OverlayTelemetry = OverlayWantsTelemetry ? LiveTelemetry : ""; return; }
        _telemetryCts?.Cancel();
        _telemetryCts = null;
        LiveTelemetry = "";
        OverlayTelemetry = "";
        if (!wanted) return;
        var cts = _telemetryCts = new CancellationTokenSource();
        _ = TelemetryLoopAsync(cts.Token);
    }

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
                _ui.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    LiveTelemetry = BuildTelemetry();
                    OverlayTelemetry = OverlayWantsTelemetry ? LiveTelemetry : "";
                });
        }
        catch (OperationCanceledException) { }
    }

    private string BuildTelemetry()
    {
        if (!_bridge.TrainerProxyActive) return "";
        var parts = new List<string>();
        if (_bridge.TrainerPowerW is { } watts) parts.Add($"{watts} W");
        if (_bridge.TrainerCadenceRpm is { } rpm) parts.Add(Loc.Fmt(Strings.Unit_Rpm, rpm));
        if (_bridge.HeartRateBpm is { } bpm) parts.Add(Loc.Fmt(Strings.Unit_Bpm, bpm));
        double kmh = _bridge.TrainerDisplaySpeedMs * 3.6;
        if (kmh >= 0.1) parts.Add($"{kmh:0.0} km/h");
        return string.Join("  ·  ", parts);
    }

    public double? SavedOverlayLeft => _config.OverlayLeft;
    public double? SavedOverlayTop => _config.OverlayTop;

    public void SaveOverlayPosition(double left, double top)
    {
        _config.OverlayLeft = left;
        _config.OverlayTop = top;
        SaveConfig();
    }

    private void UpdateOverlay()
    {
        string mode = !_bridge.TrainerProxyActive ? Strings.Overlay_ProxyStopped
            : _bridge.AppHandlesShifting ? Strings.Overlay_AppShifts
            : _bridge.TrainerNativeActive ? Strings.Overlay_Native : "FTMS";
        OverlayGear = _bridge.Gear.Gear.ToString();
        int current = _bridge.Gear.Gear;
        OverlaySegments = [.. Enumerable.Range(1, _bridge.Gear.GearCount).Select(i => new GearSegment(i, i == current, i < current))];
        OverlayDetail = $"/ {_bridge.Gear.GearCount}  ·  {_bridge.GearRatio:0.00}  ·  {mode}";
        GearRatioText = Loc.Fmt(Strings.Gear_Ratio, _bridge.GearRatio);
        // Des phrases plutôt que « Natif / Compatible » : le premier mot ne dit rien à qui ne connaît pas les protocoles.
        ShiftModeText = !_bridge.TrainerProxyActive ? Strings.Mode_Stopped
            : _bridge.AppHandlesShifting ? Strings.Mode_AppShifts
            : _bridge.TrainerNativeActive ? Strings.Mode_ByTrainer : Strings.Mode_ByApp;
        IsGearApplied = _bridge.TrainerProxyActive && !_bridge.AppHandlesShifting;
    }

    /// <summary>La vitesse affichée est réellement appliquée au trainer (virtual shifting actif).</summary>
    [ObservableProperty] private bool _isGearApplied;

    // Vitesse + / − hors manette (boutons de la notification Android) : mêmes effets qu'un appui de manette.
    [RelayCommand]
    private void ShiftUp() => _bridge.Shift(ObcButtonId.ShiftUp);

    [RelayCommand]
    private void ShiftDown() => _bridge.Shift(ObcButtonId.ShiftDown);

    [RelayCommand]
    private Task TestShiftUpAsync() => _bridge.SendTestActionAsync(ObcButtonId.ShiftUp);

    [RelayCommand]
    private Task TestShiftDownAsync() => _bridge.SendTestActionAsync(ObcButtonId.ShiftDown);

    [ObservableProperty] private string _noteText = "";

    /// <summary>Insère une note horodatée dans le journal (ex. « j'appuie sur Y gauche ») pour relier gestes et trames.</summary>
    [RelayCommand]
    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(NoteText)) return;
        AppendLog($"NOTE >>> {NoteText.Trim()}");
        NoteText = "";
    }

    [RelayCommand]
    private void CopyLog() => _host.CopyText(string.Join(Environment.NewLine, _logLines)); // le texte affiché peut avoir ~250 ms de retard

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

    private static string? _logFilePath;
    private static bool _logFileBroken;

    /// <summary>Recopie le journal dans %APPDATA%\ShiftBridge\log.txt (tourne à ~1 Mo) pour diagnostiquer après coup.</summary>
    private static void WriteLogFile(string line)
    {
        if (_logFileBroken) return;
        try
        {
            if (_logFilePath is null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftBridge");
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, "log.txt");
                var fi = new FileInfo(_logFilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(_logFilePath, _logFilePath + ".old", true);
            }
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
        catch { _logFileBroken = true; }
    }

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
        if (!_logStale || CurrentPage != "diagnostic") return; // sinon : reconstruit à l'ouverture de la page
        _logStale = false;
        LogText = string.Join(Environment.NewLine, _logLines);
    }

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(ShowBackButton));
        RefreshLogText();
    }

    private void SaveConfig()
    {
        try { ConfigStore.Save(_config); }
        catch (Exception ex) { AppendLog(Loc.Fmt(Strings.Log_SaveFailed, ex.Message)); }
    }

    private void OnUi(Action a) => _ui.Post(a);
}
