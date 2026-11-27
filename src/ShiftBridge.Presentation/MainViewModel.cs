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
        // Rafraîchissements regroupés : une rafale d'événements (batterie, vitesses en répétition rapide) = un seul rafraîchissement.
        var devicesRefresh = new CoalescedRefresh(ui, RefreshDevices);
        var trainerRefresh = new CoalescedRefresh(ui, RefreshTrainer);
        _bridge.DevicesChanged += devicesRefresh.Request;
        // L'inspecteur signale chaque annonce reçue (dizaines par seconde) : un seul rafraîchissement par rafale.
        var inspectorRefresh = new CoalescedRefresh(ui, RefreshInspector);
        _bridge.Inspector.DevicesChanged += inspectorRefresh.Request;
        _bridge.Inspector.CaptureChanged += inspectorRefresh.Request;
        _bridge.TransportsChanged += () => OnUi(RefreshTransportStatus);
        _bridge.ActiveProfileChanged += () => OnUi(() => { RefreshProfiles(); RefreshSummaries(); });
        _bridge.GearChanged += () => OnUi(RefreshGear);
        _bridge.TrainerChanged += trainerRefresh.Request;
        // Annonces Bluetooth et mesures cardio : regroupées comme les appareils.
        _bridge.BleTrainersChanged += new CoalescedRefresh(ui, RefreshBleTrainers).Request;
        _bridge.HeartRateChanged += new CoalescedRefresh(ui, RefreshHeartRate).Request;
        _bridge.HeartRateSensorsChanged += new CoalescedRefresh(ui, RefreshHeartRateSensors).Request;
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
        _riderWeightText = _config.RiderWeightKg.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _bikeWeightText = _config.BikeWeightKg.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _gearCountValue = _bridge.Gear.GearCount;
        _gearCountText = Loc.Fmt(Strings.Trainer_GearCountValue, _bridge.Gear.GearCount);
        _chainringText = _bridge.Drivetrain.ChainringTeeth.ToString();
        _cogText = _bridge.Drivetrain.CogTeeth.ToString();
        _drivetrainInfo = Loc.Fmt(Strings.Drive_RealRatio, _bridge.Drivetrain.Ratio, _bridge.Drivetrain);
        // Dernier trainer Bluetooth utilisé : proposé d'emblée, sans nouvelle recherche.
        if (BleDeviceId.TryParseSavedAddress(_config.TrainerBleAddress, out var savedAddress))
        {
            var saved = new DiscoveredBleTrainer(savedAddress, string.IsNullOrWhiteSpace(_config.TrainerBleName) ? Strings.Trainer_Last : _config.TrainerBleName, 0, TrainerProtocols.None) { Remembered = true };
            _savedBleTrainer = saved;
            FoundBleTrainers.Add(saved);
            _selectedBleTrainer = saved;
        }
        // Dernier capteur cardio : proposé d'emblée (reconnecté automatiquement au démarrage).
        if (BleDeviceId.TryParseSavedAddress(_config.HeartRateBleAddress, out var savedHr))
        {
            var hr = new DiscoveredHeartRateSensor(savedHr, string.IsNullOrWhiteSpace(_config.HeartRateBleName) ? Strings.Hr_Title : _config.HeartRateBleName, 0) { Remembered = true };
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
        _includePrereleases = _config.IncludePrereleases;
        _selectedLanguage = LanguageOptions.First(o => o.Code == AppLanguage.Normalize(_config.Language));
        _selectedRepeatSpeed = RepeatSpeedOptions.First(o => o.Speed == _config.RepeatSpeed);
        // Premier lancement : l'assistant de démarrage s'ouvre à la place de l'Accueil.
        _currentPage = _config.OnboardingCompleted ? Pages.Home : Pages.Onboarding;
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
        Debounce(ref _pinCts, SaveConfig); // une écriture sur disque à la fin de la saisie, et non à chaque caractère
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
        // Assistant : étape précédente, puis (depuis la première) sortie de l'assistant, comme « Passer ».
        if (CurrentPage == Pages.Onboarding)
        {
            if (CanOnboardingGoBack) OnboardingBack(); else SkipOnboarding();
            return true;
        }
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
        ButtonsDetail = assigned == 0 ? Strings.Keys_ChooseAction : _host.IsTouch ? Strings.Keys_TapToEdit : Strings.Keys_ClickToEdit;
        ButtonsState = assigned > 0 ? UiState.Ok : UiState.Warning;

        // Trainer
        string trainerName = UseBluetooth
            ? (SelectedBleTrainer?.Name ?? "")
            : (SelectedTrainer?.Name ?? TrainerHost);
        bool hasTrainer = !string.IsNullOrWhiteSpace(trainerName);
        if (_bridge.TrainerProxyActive)
        {
            bool recon = _bridge.TrainerLinkState == LinkState.Reconnecting;
            var control = _bridge.TrainerControlMode;
            bool noControl = control == ShiftBridge.Core.Trainer.TrainerControl.None;
            TrainerSummary = recon ? Strings.Trainer_Reconnecting : noControl ? Strings.Trainer_NoControl : Strings.Trainer_VsActive;
            TrainerDetail = noControl ? Strings.Trainer_NoControlDetail
                : control == ShiftBridge.Core.Trainer.TrainerControl.FeC && hasTrainer ? $"{trainerName} (FE-C)"
                : hasTrainer ? trainerName : "";
            TrainerState = recon || noControl ? UiState.Warning : UiState.Ok;
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

        // Prochaine étape pour quelqu'un qui découvre l'application (bandeau de l'Accueil, masqué une fois tout en place ou à
        // la demande). Un clavier ou une manette de jeu activés comptent comme des manettes : sinon l'étape 1 ne passait jamais.
        bool controllersReady = ControllersState == UiState.Ok || (HasLocalInput && (KeyboardInputEnabled || GamepadInputEnabled));
        if (_config.HideNextStep)
            SetNextStep("", "");
        else if (!controllersReady)
            SetNextStep(Strings.Step_1, Pages.Devices);
        else if (!hasTrainer)
            SetNextStep(Strings.Step_2Trainer, Pages.Trainer);
        else if (!_bridge.TrainerProxyActive)
            SetNextStep(Strings.Step_2Enable, Pages.Trainer);
        else if (AppState != UiState.Ok)
            SetNextStep(Loc.Fmt(Strings.Step_3, ControllerNameForApp()), Pages.Apps);
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
    /// <summary>Appelable depuis n'importe quel thread (rappel de permission Android), comme <see cref="SetBluetoothOff"/>.</summary>
    public void SetPermissionProblem(string? text) => _ui.Post(() =>
    {
        PermissionProblem = text ?? "";
        HasPermissionProblem = !string.IsNullOrEmpty(text);
    });

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
        _bridge.AutoConnectKnown(); // idempotent : un nouvel essai après un échec ne double pas la reconnexion automatique
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
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _config.AutoReconnect = AutoReconnect;
        SaveConfig();
        _updateCts.Cancel();
        // Boucles et temporisations de l'interface : plus rien ne doit être posté vers une interface qui se ferme.
        foreach (var cts in new[] { _telemetryCts, _hrLoopCts, _weightsCts, _drivetrainCts, _gearSaveCts, _gearCountCts, _opacitySaveCts })
            cts?.Cancel();
        await _bridge.DisposeAsync();
        if (LogFileWriter.Default is { } log) await log.CloseAsync(TimeSpan.FromSeconds(1));
    }

    private int _stopped;

    // ---------- Divers ----------

    private void RefreshGear()
    {
        GearText = $"{_bridge.Gear.Gear} / {_bridge.Gear.GearCount}";
        UpdateOverlay();
        if (OverlayFlash && _bridge.LastGearChange != GearChange.None) GearFlashRequested?.Invoke(_bridge.LastGearChange);
        // La vitesse est mémorisée (BridgeService.OnGearChanged) ; on attend la fin d'une rafale d'appuis avant d'écrire sur le disque.
        if (!_loading) Debounce(ref _gearSaveCts, SaveConfig, TimeSpan.FromSeconds(2));
    }

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(IsOnboarding));
        RefreshLogText();
    }

    private void SaveConfig()
    {
        try { ConfigStore.Save(_config); }
        catch (Exception ex) { AppendLog(Loc.Fmt(Strings.Log_SaveFailed, ex.Message)); }
    }

    private void OnUi(Action a) => _ui.Post(a);

    /// <summary>
    /// Poste un rafraîchissement sur le thread UI, sauf s'il y est déjà en attente : il s'exécutera une fois, avec l'état le
    /// plus récent, quel que soit le nombre de demandes reçues entre-temps.
    /// </summary>
    private sealed class CoalescedRefresh(IUiDispatcher ui, Action refresh)
    {
        private int _pending;

        public void Request()
        {
            if (Interlocked.Exchange(ref _pending, 1) != 0) return;
            ui.Post(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                refresh();
            });
        }
    }
}
