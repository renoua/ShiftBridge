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
        _ = ObserveAsync(_bridge.SetAppHandlesShiftingAsync(value), Strings.Log_ShiftingModeFailed);
        SaveConfig();
    }

    partial void OnNativeZwiftModeChanged(bool value)
    {
        if (_loading) return;
        _ = ObserveAsync(_bridge.SetNativeZwiftModeAsync(value), Strings.Log_NativeModeFailed);
        SaveConfig();
    }

    /// <summary>
    /// Attend une opération lancée depuis une case à cocher : une erreur (trainer injoignable…) est écrite au journal au lieu
    /// d'être perdue, et l'état du trainer est rafraîchi pour refléter ce qui a réellement été appliqué.
    /// </summary>
    private async Task ObserveAsync(Task operation, string failureFormat)
    {
        try { await operation; }
        catch (Exception ex)
        {
            ReportError(Loc.Fmt(failureFormat, ex.Message));
            OnUi(RefreshTrainer);
        }
    }

    partial void OnRiderWeightTextChanged(string value) => ApplyWeights();
    partial void OnBikeWeightTextChanged(string value) => ApplyWeights();

    // Les champs numériques sont liés à chaque frappe : sans attente, chaque valeur intermédiaire valide serait envoyée au vrai
    // trainer (la résistance changerait en direct) et écrite sur le disque.
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(600);
    private CancellationTokenSource? _weightsCts, _drivetrainCts, _gearSaveCts, _gearCountCts, _pinCts;

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

    /// <summary>Saisie de poids hors bornes ou illisible : message sous les champs (elle était ignorée sans rien dire).</summary>
    [ObservableProperty] private string _weightsError = "";

    private void ApplyWeightsNow()
    {
        bool riderOk = TryParseKg(RiderWeightText, out var rider) && rider is >= 30 and <= 200;
        bool bikeOk = TryParseKg(BikeWeightText, out var bike) && bike is >= 3 and <= 30;
        WeightsError = riderOk && bikeOk ? "" : Strings.Trainer_WeightsRange;
        if (!riderOk || !bikeOk) return;
        _bridge.SetWeights(rider, bike);
        SaveConfig();
    }

    /// <summary>Adresse manuelle du trainer Wi-Fi vérifiée à la saisie (et non seulement au clic sur « Activer »).</summary>
    [ObservableProperty] private string _trainerAddressError = "";
    partial void OnTrainerHostChanged(string value) => ValidateTrainerAddress();
    partial void OnTrainerPortTextChanged(string value) => ValidateTrainerAddress();

    private void ValidateTrainerAddress()
    {
        if (_loading) return;
        string host = TrainerHost.Trim();
        bool hostOk = host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Unknown;
        bool portOk = int.TryParse(TrainerPortText, out int port) && port is >= 1 and <= 65535;
        TrainerAddressError = !hostOk ? Strings.Trainer_HostInvalid : !portOk ? Strings.Trainer_PortInvalid : "";
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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureDrivetrainCommand))]
    private bool _isMeasuring;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseMeasuredRatioCommand))]
    private bool _canUseMeasurement;
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

    [RelayCommand(CanExecute = nameof(CanMeasure))]
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

    [RelayCommand(CanExecute = nameof(CanUseMeasurement))]
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

    private async Task StartProxyAsync()
    {
        TrainerEndpoint endpoint;
        if (UseBluetooth)
        {
            if (!EnsureBluetooth(() => _ = RetryStartProxyAsync())) { SetTrainerMessage(Strings.Bt_Off); return; }
            if (SelectedBleTrainer is not { } ble)
            {
                SetTrainerMessage(Strings.Trainer_ChooseBle);
                if (!IsOnboarding) CurrentPage = Pages.Trainer; // depuis l'Accueil, on emmène l'utilisateur là où il doit choisir
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
                if (!IsOnboarding) CurrentPage = Pages.Trainer; // l'assistant reste ouvert : le message s'affiche à l'étape Trainer
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
    partial void OnProxyActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ProxyButtonText));
        OnPropertyChanged(nameof(CanStartFromOnboarding));
    }

    /// <summary>Un démarrage ou un arrêt du proxy est en cours : le bouton est désactivé (pas de double démarrage).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleProxyCommand))]
    private bool _isProxyBusy;
    partial void OnIsProxyBusyChanged(bool value) => OnPropertyChanged(nameof(ProxyButtonText));
    private bool CanToggleProxy() => !IsProxyBusy;

    /// <summary>
    /// Relance après l'allumage du Bluetooth : par le même drapeau « occupé » que le bouton, sinon un clic pendant ce temps
    /// démarrait le proxy deux fois.
    /// </summary>
    private async Task RetryStartProxyAsync()
    {
        if (IsProxyBusy || ProxyActive) return;
        IsProxyBusy = true;
        try { await StartProxyAsync(); }
        finally { IsProxyBusy = false; }
    }

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

    public string BleScanButtonText => IsScanningBle ? Strings.Ble_BtnStop : Strings.Ble_BtnSearch;
    partial void OnIsScanningBleChanged(bool value) => OnPropertyChanged(nameof(BleScanButtonText));

    [RelayCommand]
    private void ToggleBleScan() { if (IsScanningBle) StopBleScan(); else FindBleTrainers(); }

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
}
