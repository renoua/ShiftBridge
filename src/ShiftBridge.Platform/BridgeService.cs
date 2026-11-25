using ShiftBridge.Core.Localization;
using System.Collections.Concurrent;
using System.Threading.Channels;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Gears;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;
using ShiftBridge.Platform.Input;
using ShiftBridge.Platform.Sensors;
using ShiftBridge.Platform.Trainer;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Platform;

/// <summary>
/// Chef d'orchestre : manettes → mapping → sorties OBC, avec retour haptique et rapport virtuel.
/// Aucun accès UI : l'application WPF s'abonne aux événements et se charge de la marshalisation vers le thread UI.
/// </summary>
public sealed class BridgeService : IAsyncDisposable
{
    private readonly IBlePlatform _platform;
    private readonly BleAdvertisementHub _hub;
    private readonly ControllerScanner _scanner;
    private readonly ConcurrentDictionary<ulong, DiscoveredController> _seen = new();
    private readonly ConcurrentDictionary<ulong, IController> _controllers = new();
    private readonly List<IObcTransport> _transports = [];
    private readonly object _transportsLock = new(); // _transports est lue par la boucle d'envoi pendant que l'UI en ajoute/retire
    private readonly ConcurrentDictionary<string, byte> _manuallyDisconnected = new(); // manettes que l'utilisateur a déconnectées
    private readonly SemaphoreSlim _proxyGate = new(1, 1); // un seul démarrage/arrêt du proxy à la fois
    private int _scanEval;
    private volatile bool _disposed;
    private readonly MappingEngine _mapping = new();
    private readonly object _dispatchLock = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _sendLoop;

    private readonly ILocalInputProvider _localInput;

    /// <param name="platform">Accès Bluetooth de la plateforme (Windows, Android…). Sans lui, aucun scan ni aucune connexion BLE.</param>
    /// <param name="localInput">Clavier, télécommandes HID et manettes de jeu. Sans lui, seules les manettes Zwift sont écoutées.</param>
    public BridgeService(AppConfig config, IBlePlatform? platform = null, ILocalInputProvider? localInput = null)
    {
        _localInput = localInput ?? new NullLocalInputProvider();
        _platform = platform ?? new NullBlePlatform();
        _hub = new BleAdvertisementHub(_platform.CreateScanner());
        _scanner = new ControllerScanner(_hub);
        Inspector = new BleInspector(_hub, _platform.Central);
        Inspector.Log += m => Log?.Invoke(m);
        _trainerScanner = new BleTrainerScanner(_hub);
        _hrScanner = new BleHeartRateScanner(_hub);
        _sendLoop = Task.Run(SendLoopAsync);
        Config = config;
        _mapping.Profile = _defaultProfile = config.Profile;
        _vsModel.SlopeOnly = config.SlopeOnlyMode;
        var saved = new Drivetrain(config.ChainringTeeth, config.CogTeeth);
        Drivetrain = saved.IsValid ? saved : Drivetrain.Default;
        _vsModel.ReferenceRatio = Drivetrain.Ratio;
        // Dernière vitesse mémorisée (0 = jamais) ; sinon la vitesse centrale.
        Gear = config.LastGear > 0 ? new VirtualGearState(config.GearCount, config.LastGear) : new VirtualGearState(config.GearCount);
        _vsModel.SetGearCount(Gear.GearCount);
        _scanner.DeviceSeen += d =>
        {
            bool isNew = _seen.TryAdd(d.Address, d);
            _seen[d.Address] = d;
            if (isNew) DevicesChanged?.Invoke();
        };
        _scanner.Log += m => Log?.Invoke(m);
        _hub.Log += m => Log?.Invoke(m);
        _trainerScanner.TrainerSeen += t =>
        {
            // Une annonce arrive plusieurs fois par seconde : on ne prévient l'interface que pour un nouveau trainer ou un changement utile.
            bool changed = !_bleTrainers.TryGetValue(t.Address, out var old) || old.Name != t.Name || old.Protocols != t.Protocols;
            _bleTrainers[t.Address] = t;
            if (changed) BleTrainersChanged?.Invoke();
        };
        _hrScanner.SensorSeen += s =>
        {
            bool changed = !_hrSensors.TryGetValue(s.Address, out var old) || old.Name != s.Name;
            _hrSensors[s.Address] = s;
            if (changed) HeartRateSensorsChanged?.Invoke();
        };
        _localInputHandler = ev => InjectEvents(ev, _localInput);
        _localInput.ButtonEvents += _localInputHandler;
        _localInput.Log += m => Log?.Invoke(m);
        _localInput.DevicesChanged += () => LocalInputChanged?.Invoke();
        RefreshLocalInput();
    }

    public AppConfig Config { get; }

    /// <summary>Inspecteur Bluetooth (diagnostic des appareils non reconnus).</summary>
    public BleInspector Inspector { get; }

    /// <summary>Clavier, télécommandes HID et manettes de jeu (jamais null : source vide si la plateforme n'en a pas).</summary>
    public ILocalInputProvider LocalInput => _localInput;
    /// <summary>Un périphérique local est apparu ou a disparu.</summary>
    public event Action? LocalInputChanged;

    /// <summary>
    /// Applique la configuration à la source locale : active clavier/manettes et pousse la liste des touches à avaler.
    /// À rappeler après chaque changement de profil ou de réglage.
    /// </summary>
    public void RefreshLocalInput()
    {
        _localInput.Configure(Config.KeyboardInputEnabled, Config.GamepadInputEnabled);
        HashSet<ButtonKey> bound = [];
        if (Config.KeyboardInputEnabled)
            foreach (var (key, binding) in Profile.Bindings.ToArray())
                if (key.IsKeyboard && binding.Actions.Any(a => a != ObcButtonId.None)) bound.Add(key);
        _localInput.SetBoundKeys(bound, Config.SuppressBoundKeys);
    }
    /// <summary>Accès Bluetooth de la plateforme (p. ex. pour créer la sortie OBC Bluetooth).</summary>
    public IBlePlatform Platform => _platform;
    public VirtualGearState Gear { get; }
    public IReadOnlyCollection<IObcTransport> Transports => TransportsSnapshot();
    private IObcTransport[] TransportsSnapshot() { lock (_transportsLock) return [.. _transports]; }
    public IReadOnlyCollection<DiscoveredController> SeenDevices => _seen.Values.ToList();
    public IReadOnlyCollection<IController> Controllers => _controllers.Values.ToList();
    public bool IsScanning => _scanner.IsScanning;

    private bool _verboseFrames;
    /// <summary>Journal détaillé : toutes les trames de boutons, même au repos.</summary>
    public bool VerboseFrames
    {
        get => _verboseFrames;
        set
        {
            _verboseFrames = value;
            foreach (var c in _controllers.Values) c.VerboseFrames = value;
            foreach (var d in TransportsSnapshot().OfType<DirConServer>()) d.LogWire = value;
            if (_proxy is { } proxy) proxy.LogWire = value;
        }
    }

    // ---------- Profils (par défaut et par appli) ----------

    private Profile _defaultProfile;
    private string _connectedAppKey = "";
    private string _connectedAppId = "";

    /// <summary>Profil ACTIF, celui que le mapping utilise : le profil de l'appli connectée s'il existe, sinon le profil par défaut.</summary>
    public Profile Profile
    {
        get => _mapping.Profile;
        // Remplace le profil par défaut (« Réinitialiser » l'abandonne pour un neuf) ; actif tant qu'aucun profil d'appli ne l'est.
        set
        {
            bool wasActive = ReferenceEquals(_mapping.Profile, _defaultProfile);
            _defaultProfile = value;
            Config.Profile = value;
            if (wasActive) { lock (_dispatchLock) _mapping.Profile = value; }
            RefreshLocalInput();
        }
    }

    public Profile DefaultProfile => _defaultProfile;

    /// <summary>Profils propres à une appli (copie ; clé = AppId normalisé).</summary>
    public IReadOnlyDictionary<string, Profile> AppProfiles
    {
        get { lock (Config.SyncRoot) return new Dictionary<string, Profile>(Config.AppProfiles); }
    }

    /// <summary>AppId de l'appli connectée, tel qu'elle l'annonce (« » si aucune).</summary>
    public string ConnectedAppId => _connectedAppId;
    public string ConnectedAppKey => _connectedAppKey;

    /// <summary>Clé du profil actif (« » = profil par défaut).</summary>
    public string ActiveProfileKey
    {
        get
        {
            lock (Config.SyncRoot) return _connectedAppKey.Length > 0 && Config.AppProfiles.ContainsKey(_connectedAppKey) ? _connectedAppKey : "";
        }
    }

    /// <summary>L'appli connectée, son profil ou le profil actif ont changé (l'onglet Touches se met à jour).</summary>
    public event Action? ActiveProfileChanged;

    /// <summary>Profil de la clé (« » = défaut) ; le profil par défaut si la clé est inconnue.</summary>
    public Profile GetProfile(string key)
    {
        if (key.Length == 0) return _defaultProfile;
        lock (Config.SyncRoot) return Config.AppProfiles.TryGetValue(key, out var p) ? p : _defaultProfile;
    }

    /// <summary>Crée (ou retrouve) le profil d'une appli : une copie du profil par défaut, activée aussitôt si l'appli est connectée.</summary>
    public string? CreateAppProfile(string appId)
    {
        string key = AppProfileKey.Normalize(appId);
        if (key.Length == 0) return null;
        lock (Config.SyncRoot)
        {
            if (!Config.AppProfiles.ContainsKey(key))
            {
                var copy = _defaultProfile.Clone();
                copy.Name = appId.Trim();
                Config.AppProfiles[key] = copy;
            }
        }
        Log?.Invoke($"Profil créé pour l'appli « {appId.Trim()} ».");
        UpdateActiveProfile();
        return key;
    }

    public void RemoveAppProfile(string key)
    {
        bool removed;
        lock (Config.SyncRoot) removed = Config.AppProfiles.Remove(key);
        if (removed) UpdateActiveProfile();
    }

    /// <summary>Remet un profil aux touches d'origine (un profil neuf remplace l'ancien : le mapping ne voit jamais un état à moitié vidé).</summary>
    public void ResetProfile(string key)
    {
        var fresh = Profile.ZwiftLike();
        if (key.Length == 0) { fresh.Name = _defaultProfile.Name; Profile = fresh; return; }
        lock (Config.SyncRoot)
        {
            if (!Config.AppProfiles.TryGetValue(key, out var old)) return;
            fresh.Name = old.Name;
            Config.AppProfiles[key] = fresh;
        }
        UpdateActiveProfile();
    }

    /// <summary>Retient l'appli connectée (null : plus aucune) et en active le profil.</summary>
    private void SetConnectedApp(string? appId)
    {
        _connectedAppId = appId?.Trim() ?? "";
        _connectedAppKey = AppProfileKey.Normalize(appId);
        UpdateActiveProfile();
    }

    private void UpdateActiveProfile()
    {
        Profile target;
        lock (Config.SyncRoot)
            target = _connectedAppKey.Length > 0 && Config.AppProfiles.TryGetValue(_connectedAppKey, out var p) ? p : _defaultProfile;
        if (!ReferenceEquals(target, _mapping.Profile))
        {
            lock (_dispatchLock) _mapping.Profile = target;
            RefreshLocalInput(); // les touches clavier à écouter / avaler dépendent du profil
            Log?.Invoke($"Profil actif : {(ReferenceEquals(target, _defaultProfile) ? "par défaut" : target.Name)}");
        }
        ActiveProfileChanged?.Invoke();
    }

    public event Action<string>? Log;
    public event Action? DevicesChanged;
    public event Action? TransportsChanged;
    public event Action? GearChanged;
    /// <summary>Touche physique appuyée/relâchée (pour l'éclairage en direct de l'onglet Touches).</summary>
    public event Action<ButtonKey, bool>? PhysicalButtonChanged;

    // ---------- Manettes ----------

    /// <summary>
    /// Lance la recherche des manettes (demande de l'utilisateur ou démarrage). Elle s'arrête d'elle-même une fois toutes les
    /// manettes connues connectées (voir <see cref="EvaluateScan"/>) : un scan continu vide la batterie et pénalise les liaisons.
    /// </summary>
    public void StartScan() { _scanner.Start(); DevicesChanged?.Invoke(); EvaluateScan(); }
    public void StopScan() { Interlocked.Increment(ref _scanEval); _scanner.Stop(); DevicesChanged?.Invoke(); }

    /// <summary>Change la reconnexion automatique à chaud : manettes déjà connectées incluses, et la recherche s'adapte aussitôt.</summary>
    public void SetAutoReconnect(bool value)
    {
        Config.AutoReconnect = value;
        foreach (var c in _controllers.Values) c.AutoReconnect = value;
        if (value) EvaluateScan();
    }

    /// <summary>Délai pendant lequel le scan reste actif une fois toutes les manettes connectées (laisse le temps d'en ajouter une, p. ex. la seconde Play).</summary>
    public TimeSpan ScanSettleDelay { get; set; } = TimeSpan.FromSeconds(60);

    private bool IsKnown(string addressText)
    {
        lock (Config.SyncRoot) return Config.KnownDevices.Contains(addressText);
    }

    /// <summary>Toutes les manettes connues (sauf celles déconnectées à la main) sont connectées : plus rien à chercher.</summary>
    private bool IsScanSettled()
    {
        string[] known;
        lock (Config.SyncRoot) known = [.. Config.KnownDevices];
        if (known.Length == 0) return false; // première utilisation : on cherche jusqu'à la première connexion
        var connected = _controllers.Values.Where(c => c.State == ControllerState.Connected).Select(c => c.Info.AddressText).ToHashSet();
        return known.All(k => connected.Contains(k) || _manuallyDisconnected.ContainsKey(k));
    }

    private void EvaluateScan()
    {
        if (_disposed || !Config.AutoReconnect) return;
        int version = Interlocked.Increment(ref _scanEval);
        if (!IsScanSettled())
        {
            // Une manette connue manque (coupure, extinction) : il faut la voir réapparaître.
            if (!_scanner.IsScanning) { _scanner.Start(); DevicesChanged?.Invoke(); }
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ScanSettleDelay); } catch { return; }
            // Un autre événement (connexion, déconnexion, nouvelle recherche) a relancé l'évaluation : c'est elle qui décide.
            if (_disposed || version != Volatile.Read(ref _scanEval) || !IsScanSettled()) return;
            if (_scanner.IsScanning)
            {
                _scanner.Stop();
                Log?.Invoke("Recherche arrêtée : toutes les manettes connues sont connectées (bouton « Rechercher » pour en ajouter).");
                DevicesChanged?.Invoke();
            }
        });
    }

    public void Connect(DiscoveredController device)
    {
        if (_controllers.ContainsKey(device.Address)) return;
        var c = CreateController(device);
        if (c is null) { Log?.Invoke($"Modèle de manette non pris en charge : {device.Name}"); return; }
        c.ButtonEvents += ev => InjectEvents(ev, c); // la source sert à ne faire vibrer que la manette appuyée
        c.StateChanged += st =>
        {
            if (st != ControllerState.Connected) _lowBatteryAlerted.TryRemove(device.Address, out _); // une alerte par connexion
            DevicesChanged?.Invoke();
            EvaluateScan();
        };
        c.InfoChanged += () => DevicesChanged?.Invoke();
        c.BatteryChanged += pct =>
        {
            // Avec plusieurs manettes, l'appli voit la batterie la plus faible (et non celle de la dernière qui a parlé).
            int lowest = _controllers.Values.Where(x => x.BatteryPercent.HasValue).Select(x => x.BatteryPercent!.Value).DefaultIfEmpty(pct).Min();
            foreach (var t in TransportsSnapshot()) t.SetBattery(lowest);
            if (pct < LowBatteryPercent && _lowBatteryAlerted.TryAdd(device.Address, 0))
                Log?.Invoke(Loc.Fmt(Strings.Log_LowBattery, DisplayName(device), pct));
            DevicesChanged?.Invoke();
        };
        c.Frame += m => Log?.Invoke(m);
        c.Log += m => Log?.Invoke(m);
        if (_controllers.TryAdd(device.Address, c))
        {
            string key = device.AddressText;
            _manuallyDisconnected.TryRemove(key, out _); // l'utilisateur la veut à nouveau
            lock (Config.SyncRoot) { if (!Config.KnownDevices.Contains(key)) Config.KnownDevices.Add(key); }
            if (device.Kind != ControllerKind.Zwift) RegisterControllerKind(key, device.Kind);
            c.Start();
            DevicesChanged?.Invoke();
        }
    }

    private IController? CreateController(DiscoveredController device)
    {
        if (device is DiscoveredZwiftDevice zwift)
            return new ZwiftController(_platform.Central, zwift, Config.AutoReconnect) { VerboseFrames = _verboseFrames };
        if (device.Kind == ControllerKind.SramAxs && Config.SramAxsAdvanced)
        {
            SeedAxsAdvancedDefaults();
            return new SramAxsAdvancedController(_platform.Central, device, new ConfigSramKeyStore(Config), Config.AutoReconnect) { VerboseFrames = _verboseFrames };
        }
        return ControllerSpec.For(device.Kind, Config.LtwooPin) is { } spec
            ? new BleButtonController(_platform.Central, device, spec, Config.AutoReconnect) { VerboseFrames = _verboseFrames }
            : null;
    }

    /// <summary>Ajoute une seule fois les assignations par défaut des touches Haut/Bas de l'AXS avancé (sans toucher aux autres).</summary>
    private void SeedAxsAdvancedDefaults()
    {
        if (Config.SramAxsAdvancedSeeded) return;
        Config.SramAxsAdvancedSeeded = true;
        string[] only = ["Up", "Down"];
        Profile[] appProfiles;
        lock (Config.SyncRoot) appProfiles = [.. Config.AppProfiles.Values];
        lock (_dispatchLock)
        {
            _defaultProfile.ApplyControllerDefaults(ControllerKind.SramAxs, only);
            foreach (var p in appProfiles) p.ApplyControllerDefaults(ControllerKind.SramAxs, only);
        }
    }

    /// <summary>Lance l'appairage d'un dérailleur AXS connecté en mode avancé (il doit être en mode appairage). Vrai si la clé a été obtenue.</summary>
    public async Task<bool> PairAxsAsync(ulong address)
    {
        if (!_controllers.TryGetValue(address, out var c) || c is not SramAxsAdvancedController axs) return false;
        bool ok = await axs.PairAsync();
        if (ok) { try { ConfigStore.Save(Config); } catch { /* la clé reste en mémoire pour cette session */ } }
        DevicesChanged?.Invoke();
        return ok;
    }

    /// <summary>
    /// Mémorise le modèle d'une manette. À la toute première connexion d'un modèle, ses assignations par défaut sont ajoutées
    /// au profil par défaut et aux profils d'appli (sans toucher aux assignations existantes) ; ensuite, ce que l'utilisateur
    /// a retiré ne revient pas.
    /// </summary>
    private void RegisterControllerKind(string addressText, ControllerKind kind)
    {
        bool firstOfKind;
        lock (Config.SyncRoot)
        {
            firstOfKind = !Config.KnownControllerKinds.ContainsValue(kind);
            Config.KnownControllerKinds[addressText] = kind;
        }
        if (!firstOfKind) return;
        Profile[] appProfiles;
        lock (Config.SyncRoot) appProfiles = [.. Config.AppProfiles.Values]; // jamais SyncRoot sous _dispatchLock : l'ordre inverse existe ailleurs
        lock (_dispatchLock) // le mapping lit les assignations pendant ce temps
        {
            _defaultProfile.ApplyControllerDefaults(kind);
            foreach (var p in appProfiles) p.ApplyControllerDefaults(kind);
        }
    }

    public async Task DisconnectAsync(ulong address, bool forget = false)
    {
        var addressText = BleAddress.ToText(address);
        if (_controllers.TryRemove(address, out var c))
        {
            // Sans cette mémoire, AutoConnectKnown reconnecterait la manette à sa prochaine annonce (moins d'une seconde).
            _manuallyDisconnected[c.Info.AddressText] = 0;
            if (forget && c is SramAxsAdvancedController axs) axs.ForgetKey(); // la clé d'appairage est indexée par numéro de série
            await c.DisposeAsync();
        }
        else if (forget) _manuallyDisconnected[addressText] = 0;
        if (forget) ForgetKnown(addressText);
        DevicesChanged?.Invoke();
        EvaluateScan();
    }

    /// <summary>Seuil (en %) sous lequel une manette est signalée en batterie faible.</summary>
    public const int LowBatteryPercent = 15;
    private readonly ConcurrentDictionary<ulong, byte> _lowBatteryAlerted = new();

    /// <summary>Nom à afficher : le nom donné par l'utilisateur, sinon celui annoncé en Bluetooth.</summary>
    public string DisplayName(DiscoveredController device)
    {
        lock (Config.SyncRoot) return Config.DeviceNames.TryGetValue(device.AddressText, out var n) ? n : device.Name;
    }

    /// <summary>Nom personnalisé d'une manette (null si aucun).</summary>
    public string? CustomName(string addressText)
    {
        lock (Config.SyncRoot) return Config.DeviceNames.TryGetValue(addressText, out var n) ? n : null;
    }

    /// <summary>Donne un nom à une manette ; un nom vide rétablit le nom d'origine.</summary>
    public void SetDeviceName(string addressText, string? name)
    {
        name = name?.Trim();
        if (name is { Length: > 40 }) name = name[..40];
        lock (Config.SyncRoot)
        {
            if (string.IsNullOrEmpty(name)) Config.DeviceNames.Remove(addressText);
            else Config.DeviceNames[addressText] = name;
        }
        DevicesChanged?.Invoke();
    }

    /// <summary>Retire une manette de la mémoire (liste connue, modèle, nom personnalisé), qu'elle soit connectée ou non.</summary>
    private void ForgetKnown(string addressText)
    {
        lock (Config.SyncRoot)
        {
            Config.KnownDevices.Remove(addressText);
            Config.KnownControllerKinds.Remove(addressText);
            Config.DeviceNames.Remove(addressText);
        }
    }

    /// <summary>Reconnecte au démarrage les manettes déjà connues, dès qu'elles réapparaissent dans le scan.</summary>
    public void AutoConnectKnown()
    {
        _scanner.DeviceSeen += d =>
        {
            if (Config.AutoReconnect && IsKnown(d.AddressText) && !_manuallyDisconnected.ContainsKey(d.AddressText)) Connect(d);
        };
    }

    // ---------- Sorties OBC ----------

    public async Task AddTransportAsync(IObcTransport t)
    {
        t.Log += m => Log?.Invoke($"[{t.Name}] {m}");
        t.ClientsChanged += () => { ResetSupportedActionsIfNoApp(); TransportsChanged?.Invoke(); };
        t.AppInfoReceived += OnAppInfo;
        t.HapticReceived += OnAppHaptic;
        if (t is DirConServer dc) dc.LogWire = _verboseFrames;
        lock (_transportsLock) _transports.Add(t);
        _pumps[t] = new TransportPump(t, m => Log?.Invoke(m));
        try { await t.StartAsync(); }
        catch (Exception ex)
        {
            Log?.Invoke($"[{t.Name}] démarrage impossible : {ex.Message}");
            lock (_transportsLock) _transports.Remove(t);
            await t.DisposeAsync();
            await ClosePumpAsync(t);
        }
        TransportsChanged?.Invoke();
    }

    public async Task RemoveTransportAsync(IObcTransport t)
    {
        lock (_transportsLock) _transports.Remove(t);
        await t.DisposeAsync(); // d'abord : ses clients sont coupés, un envoi bloqué échoue aussitôt
        await ClosePumpAsync(t);
        ResetSupportedActionsIfNoApp();
        TransportsChanged?.Invoke();
    }

    // ---------- Envoi vers les transports ----------

    private readonly ConcurrentDictionary<IObcTransport, TransportPump> _pumps = new();

    private async Task ClosePumpAsync(IObcTransport t)
    {
        if (_pumps.TryRemove(t, out var pump)) await pump.CompleteAsync();
    }

    /// <summary>
    /// File d'envoi propre à un transport : l'ordre est conservé pour lui, et un client bloqué (écriture TCP jusqu'à 3 s) ne
    /// retarde plus les boutons destinés aux autres transports.
    /// </summary>
    private sealed class TransportPump
    {
        private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _task;

        public TransportPump(IObcTransport transport, Action<string> log)
        {
            _task = Task.Run(async () =>
            {
                await foreach (var msg in _queue.Reader.ReadAllAsync())
                {
                    try { await transport.SendButtonStateAsync(msg); }
                    catch (Exception ex) { log($"Envoi {transport.Name} : {ex.Message}"); }
                }
            });
        }

        public void Post(byte[] message) => _queue.Writer.TryWrite(message);

        public Task CompleteAsync() { _queue.Writer.TryComplete(); return _task; }
    }

    /// <summary>
    /// Les boutons déclarés par une appli (App Info) ne valent que pour sa connexion. Quand plus aucune appli n'est connectée,
    /// on les oublie : sinon la liste de l'appli A filtrerait encore les touches pour l'appli B, qui n'a rien déclaré.
    /// </summary>
    private void ResetSupportedActionsIfNoApp()
    {
        if (TransportsSnapshot().Any(t => t.HasClient)) return;
        lock (_dispatchLock) _mapping.SupportedActions = null;
        SetConnectedApp(null); // plus d'appli : retour au profil par défaut
    }

    private void OnAppInfo(ObcAppInfo info)
    {
        string declared = info.ButtonIds.Count == 0
            ? "tous"
            : string.Join(", ", info.ButtonIds.Select(b => Enum.IsDefined((ObcButtonId)b) ? ((ObcButtonId)b).ToString() : $"0x{b:X2}"));
        Log?.Invoke($"{info.AppId} déclare les boutons : {declared}");

        // Convention de la spec : liste vide = tous les boutons supportés.
        lock (_dispatchLock)
        {
            _mapping.SupportedActions = info.ButtonIds.Count == 0
                ? null
                : info.ButtonIds.Select(b => (ObcButtonId)b).ToHashSet();
        }
        SetConnectedApp(info.AppId); // son profil s'il en a un
        TransportsChanged?.Invoke();

        // Nouvelle connexion de l'appli : elle ignore tout du rapport courant.
        ReportGear(force: true);
    }

    // ---------- Rapport virtuel vers l'appli ----------

    private int _reportedGearState = -1;

    /// <summary>
    /// Informe l'appli du rapport virtuel (OBC « Gear Set », index direct 1-based avec décalage +1 : 0x02 = rapport 1), seulement
    /// si le proxy trainer est actif (sans lui, l'appli applique elle-même les vitesses), si l'option est cochée et si l'appli
    /// n'a pas exclu ce bouton.
    /// </summary>
    private void ReportGear(bool force = false)
    {
        if (!Config.ReportGearToApp || !ProxyShifts) return;
        if (_mapping.SupportedActions is { Count: > 0 } supported && !supported.Contains(ObcButtonId.GearSet)) return;

        int state = Math.Clamp(Gear.Gear, 1, Gear.GearCount) + 1;
        if (!force && state == _reportedGearState) return;
        _reportedGearState = state;
        foreach (var m in ObcMessages.EncodeButtonState([(ObcButtonId.GearSet, (byte)state)])) _outbox.Writer.TryWrite(m);
        Log?.Invoke($"Vitesse {Gear.Gear}/{Gear.GearCount} envoyée à l'appli (OBC Gear Set).");
    }

    /// <summary>Relâche le « Gear Set » (valeur 0) quand le proxy s'arrête, si un rapport avait été annoncé.</summary>
    private void ReleaseReportedGear()
    {
        if (_reportedGearState < 0) return;
        _reportedGearState = -1;
        foreach (var m in ObcMessages.EncodeButtonState([(ObcButtonId.GearSet, (byte)0)])) _outbox.Writer.TryWrite(m);
    }

    public bool ReportGearToApp
    {
        get => Config.ReportGearToApp;
        set
        {
            Config.ReportGearToApp = value;
            if (value) ReportGear(force: true); else ReleaseReportedGear();
        }
    }

    private void OnAppHaptic(ObcHaptic h)
    {
        if (h.Pattern == ObcHapticPattern.Stop || !Config.ControllerVibration) return;
        // Écho d'une vibration que ShiftBridge vient déjà de jouer à l'appui : l'ignorer évite la double vibration décalée.
        if (Environment.TickCount64 - Interlocked.Read(ref _lastLocalBuzzTick) < AppEchoWindowMs)
        {
            if (_verboseFrames) Log?.Invoke("Écho haptique de l'appli ignoré (vibration déjà jouée à l'appui).");
            return;
        }
        // En parallèle : pas de retard cumulé d'une manette à l'autre.
        foreach (var c in _controllers.Values.Where(c => c.CanVibrate)) _ = c.VibrateAsync();
        if (_localInput.CanVibrate) _ = BuzzOneAsync(_localInput.VibrateAsync, limit: false);
    }

    // ---------- Proxy trainer (virtual shifting) ----------

    private readonly VirtualShiftingModel _vsModel = new();
    private ReconnectingTrainerLink? _trainerLink;
    private DirConTrainerProxy? _proxy;
    private readonly BleTrainerScanner _trainerScanner;
    private readonly ConcurrentDictionary<ulong, DiscoveredBleTrainer> _bleTrainers = new();

    public bool TrainerProxyActive => _proxy is not null;
    public int TrainerProxyPort => _proxy?.Port ?? 0;
    public bool TrainerProxyHasApp => _proxy?.HasClient == true;
    public double TrainerSpeedMs => _proxy?.LastSpeedMs ?? 0;
    public int? TrainerPowerW => _proxy?.LastPowerW;
    public double? TrainerCadenceRpm => _proxy?.LastCadenceRpm;
    /// <summary>Vitesse que voit l'appli (m/s) : mise à l'échelle par la vitesse virtuelle en mode FTMS, brute en mode natif.</summary>
    public double TrainerDisplaySpeedMs => TrainerSpeedMs * (TrainerNativeActive ? 1.0 : _vsModel.Factor);

    /// <summary>Nature du dernier changement de vitesse (normal ou butée), lue par l'interface au moment de <see cref="GearChanged"/>.</summary>
    public GearChange LastGearChange { get; private set; }
    public double GearFactor => _vsModel.Factor;
    /// <summary>Rapport de la table Zwift pour le rapport virtuel courant (p. ex. 2,40 au rapport 12).</summary>
    public double GearRatio => _vsModel.RatioFor(Gear.Gear);

    /// <summary>
    /// Change le nombre de vitesses (8 à 24). La table de rapports garde la même plage (mêmes extrêmes) et le rapport courant est
    /// recalé proportionnellement : on reste au même endroit de l'échelle. La résistance suit aussitôt.
    /// </summary>
    public void SetGearCount(int count)
    {
        count = Math.Clamp(count, 8, 24);
        lock (_gearLock)
        {
            int old = Gear.GearCount;
            if (count == old) return;
            int gear = old > 1 ? (int)Math.Round((Gear.Gear - 1) * (count - 1.0) / (old - 1)) + 1 : (count + 1) / 2;
            Gear.Reconfigure(count, gear);
            _vsModel.SetGearCount(count);
            Config.GearCount = count;
            LastGearChange = GearChange.None; // pas de clignotement ni de vibration pour un réglage
        }
        Log?.Invoke($"Nombre de vitesses : {count} (vitesse {Gear.Gear}/{count}, rapport {GearRatio:0.00}).");
        GearChanged?.Invoke();
        OnGearChanged();
    }

    /// <summary>
    /// ShiftBridge applique lui-même les vitesses au trainer (proxy actif, option « l'appli gère les vitesses » décochée).
    /// Sinon + et − partent à l'appli par OpenBikeControl, comme sans proxy.
    /// </summary>
    private bool ProxyShifts => _proxy is not null && !Config.AppHandlesShifting;

    public bool AppHandlesShifting => Config.AppHandlesShifting;

    /// <summary>
    /// Choisit qui gère les vitesses virtuelles : ShiftBridge (proxy trainer, défaut) ou l'appli d'entraînement. Appliqué à
    /// chaud : le proxy devient un relais pur (ou reprend le virtual shifting) sans couper la connexion de l'appli.
    /// </summary>
    public async Task SetAppHandlesShiftingAsync(bool value)
    {
        if (Config.AppHandlesShifting == value) return;
        lock (_dispatchLock)
        {
            Config.AppHandlesShifting = value;
            _mapping.AlwaysAllowed = _proxy is not null && !value ? ProxyShiftActions : NoActions;
        }
        if (value) ReleaseReportedGear(); // l'appli a ses propres vitesses : ne plus lui imposer celle de ShiftBridge
        if (_proxy is { } p) await p.SetShiftingEnabledAsync(!value, Config.NativeZwiftMode);
        if (!value) ReportGear(force: true);
        Log?.Invoke(value
            ? "Vitesses gérées par l'appli : + et − lui sont transmis, le trainer est relayé sans modification."
            : "Vitesses gérées par ShiftBridge (virtual shifting du proxy trainer).");
        TrainerChanged?.Invoke();
    }

    /// <summary>Le trainer applique lui-même le rapport (protocole natif Zwift), au lieu de la mise à l'échelle FTMS.</summary>
    public bool TrainerNativeActive => _proxy?.NativeActive == true;
    public bool NativeZwiftMode => Config.NativeZwiftMode;

    /// <summary>Active/désactive le mode natif ; appliqué aussitôt si le proxy tourne (repli FTMS si le trainer ne le supporte pas).</summary>
    public async Task SetNativeZwiftModeAsync(bool enabled)
    {
        Config.NativeZwiftMode = enabled;
        if (_proxy is { } p) await p.SetNativeModeAsync(enabled);
        TrainerChanged?.Invoke();
    }

    /// <summary>Poids envoyés au trainer en mode natif : appliqués aussitôt si le proxy tourne, sinon à la prochaine activation.</summary>
    public void SetWeights(double riderKg, double bikeKg)
    {
        Config.RiderWeightKg = Math.Clamp(riderKg, 30, 200);
        Config.BikeWeightKg = Math.Clamp(bikeKg, 3, 30);
        if (_proxy is { } p) _ = p.ApplyWeightsAsync(Config.RiderWeightKg, Config.BikeWeightKg);
    }

    /// <summary>Braquet physique du vélo : sert de rapport de référence au virtual shifting (k = rapport virtuel ÷ rapport réel).</summary>
    public Drivetrain Drivetrain { get; private set; } = Drivetrain.Default;

    public void SetDrivetrain(Drivetrain d)
    {
        if (!d.IsValid) return;
        Drivetrain = d;
        _vsModel.ReferenceRatio = d.Ratio;
        Config.ChainringTeeth = d.ChainringTeeth;
        Config.CogTeeth = d.CogTeeth;
        Log?.Invoke($"Braquet réel {d} : rapport réel {d.Ratio:0.00} → rapport {Gear.Gear} : ×{_vsModel.Factor:0.00}");
        if (_proxy is { } p) _ = p.ReapplyAsync(); // la résistance suit aussitôt
        TrainerChanged?.Invoke();
    }

    /// <summary>Mesure le braquet réel d'après la vitesse et la cadence du trainer (le proxy doit être actif).</summary>
    public Task<RatioMeasurement> MeasureDrivetrainAsync()
    {
        if (_proxy is not { } p)
            return Task.FromResult(RatioMeasurement.Failure(Strings.Err_EnableVsFirst));
        p.WheelCircumferenceM = Config.WheelCircumferenceM;
        return p.MeasureRealRatioAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Mise à l'échelle par la seule pente (au lieu de pente + roulement + traînée). Appliquée aussitôt si le proxy tourne.</summary>
    public bool SlopeOnlyMode
    {
        get => _vsModel.SlopeOnly;
        set
        {
            _vsModel.SlopeOnly = value;
            Config.SlopeOnlyMode = value;
            if (_proxy is { } p) _ = p.ReapplyAsync();
        }
    }
    // ---------- Cardio (ceinture / montre Bluetooth republiée par le proxy) ----------

    private readonly BleHeartRateScanner _hrScanner;
    private readonly ConcurrentDictionary<ulong, DiscoveredHeartRateSensor> _hrSensors = new();
    private HeartRateSource? _hrSource;
    private TrainerEndpoint? _proxyEndpoint; // pour redémarrer le proxy quand le capteur change

    public bool HeartRateConnected => _hrSource is not null;
    public string HeartRateName => _hrSource?.Name ?? "";
    public LinkState? HeartRateState => _hrSource?.State;
    public int? HeartRateBpm => _hrSource?.LastBpm;
    public bool HeartRateScanning => _hrScanner.IsScanning;
    public IReadOnlyCollection<DiscoveredHeartRateSensor> HeartRateSensors => _hrSensors.Values.OrderBy(s => s.Name).ToList();
    public event Action? HeartRateChanged;
    public event Action? HeartRateSensorsChanged;

    public void StartHeartRateScan()
    {
        _hrSensors.Clear();
        _hrScanner.Start();
        HeartRateSensorsChanged?.Invoke();
    }

    public void StopHeartRateScan()
    {
        _hrScanner.Stop();
        HeartRateSensorsChanged?.Invoke();
    }

    /// <summary>
    /// Se connecte au capteur cardio (la première connexion doit réussir ; ensuite reconnexion automatique). Si le proxy tourne,
    /// il redémarre pour republier le service cardio (l'appli d'entraînement se reconnecte).
    /// </summary>
    public async Task ConnectHeartRateAsync(ulong address, string name)
    {
        await _proxyGate.WaitAsync();
        try
        {
            StopHeartRateScan();
            await DropHeartRateCoreAsync();
            var source = new HeartRateSource(_platform.Central, address, name);
            source.Log += m => Log?.Invoke($"[Cardio] {m}");
            source.StateChanged += _ => HeartRateChanged?.Invoke();
            await source.ConnectAsync();
            _hrSource = source;
            Config.HeartRateBleAddress = address.ToString();
            Config.HeartRateBleName = name;
            Log?.Invoke($"Capteur cardio connecté : {name}");
            await RestartProxyForHeartRateAsync();
        }
        finally { _proxyGate.Release(); }
        HeartRateChanged?.Invoke();
    }

    public async Task DisconnectHeartRateAsync()
    {
        await _proxyGate.WaitAsync();
        try
        {
            if (_hrSource is null) return;
            await DropHeartRateCoreAsync();
            Config.HeartRateBleAddress = ""; Config.HeartRateBleName = "";
            await RestartProxyForHeartRateAsync();
        }
        finally { _proxyGate.Release(); }
        HeartRateChanged?.Invoke();
    }

    private async Task DropHeartRateCoreAsync()
    {
        var s = _hrSource;
        _hrSource = null;
        if (s is not null) { try { await s.DisposeAsync(); } catch { } }
    }

    private async Task RestartProxyForHeartRateAsync()
    {
        if (_proxy is null || _proxyEndpoint is not { } endpoint) return;
        Log?.Invoke("Cardio : redémarrage du proxy trainer pour republier le capteur.");
        await StartTrainerProxyCoreAsync(endpoint);
    }

    public LinkState? TrainerLinkState => _trainerLink?.State;
    public int TrainerReconnectCount => _trainerLink?.ReconnectCount ?? 0;
    public bool TrainerScanning => _trainerScanner.IsScanning;
    public IReadOnlyCollection<DiscoveredBleTrainer> BleTrainers => _bleTrainers.Values.OrderBy(t => t.Name).ToList();
    public event Action? TrainerChanged;
    public event Action? BleTrainersChanged;

    public Task<List<DiscoveredTrainer>> FindTrainersAsync() => TrainerDiscovery.BrowseAsync(TimeSpan.FromSeconds(4));

    public void StartTrainerScan()
    {
        _bleTrainers.Clear();
        _trainerScanner.Start();
        BleTrainersChanged?.Invoke();
    }

    public void StopTrainerScan()
    {
        _trainerScanner.Stop();
        BleTrainersChanged?.Invoke();
    }

    /// <summary>
    /// Se connecte au trainer (Wi-Fi ou Bluetooth), avec reconnexion automatique, et le republie à l'appli d'entraînement
    /// avec le virtual shifting. La première connexion doit réussir (l'erreur est remontée) ; les coupures suivantes sont gérées.
    /// </summary>
    public async Task StartTrainerProxyAsync(TrainerEndpoint endpoint)
    {
        // Un double-clic ne doit pas lancer deux démarrages en parallèle (le premier proxy fuirait son port et son annonce mDNS).
        await _proxyGate.WaitAsync();
        try { await StartTrainerProxyCoreAsync(endpoint); }
        finally { _proxyGate.Release(); }
    }

    private async Task StartTrainerProxyCoreAsync(TrainerEndpoint endpoint)
    {
        await StopTrainerProxyCoreAsync();
        StopTrainerScan(); // le scan actif pénalise les liaisons déjà ouvertes (manettes, trainer)

        Func<CancellationToken, Task<ITrainerLink>> factory;
        GattRelayPolicy policy;
        IReadOnlyDictionary<string, string>? txt = null;
        string label, transport;

        switch (endpoint)
        {
            case TrainerEndpoint.DirCon d:
                label = d.Host; transport = "Wi-Fi (DirCon)"; txt = d.Txt;
                policy = new GattRelayPolicy(full: true); // comportement validé sur KICKR
                factory = async ct =>
                {
                    var c = new DirConClient();
                    c.Log += m => Log?.Invoke($"[Trainer] {m}");
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(8));
                        await c.ConnectAsync(d.Host, d.Port, timeout.Token);
                    }
                    catch { await c.DisposeAsync(); throw; }
                    return c;
                };
                Config.TrainerTransport = "wifi"; Config.TrainerHost = d.Host; Config.TrainerPort = d.Port;
                break;

            case TrainerEndpoint.Ble b:
                label = b.Name; transport = "Bluetooth";
                policy = new GattRelayPolicy(Config.FullRelay);
                factory = async ct =>
                {
                    var l = new BleTrainerLink(_platform.Central, b.Address, b.Name);
                    l.Log += m => Log?.Invoke($"[Trainer] {m}");
                    try { await l.ConnectAsync(ct); }
                    catch { await l.DisposeAsync(); throw; }
                    return l;
                };
                Config.TrainerTransport = "ble"; Config.TrainerBleAddress = b.Address.ToString(); Config.TrainerBleName = b.Name;
                break;

            default: throw new ArgumentException("Type de trainer inconnu.", nameof(endpoint));
        }

        var link = new ReconnectingTrainerLink(factory);
        link.Log += m => Log?.Invoke($"[Trainer] {m}");
        link.StateChanged += _ => TrainerChanged?.Invoke();
        link.Reconnected += () => TrainerChanged?.Invoke();
        DirConTrainerProxy? proxy = null;
        try
        {
            await link.ConnectAsync();

            proxy = new DirConTrainerProxy(link, _vsModel, txt, policy: policy, trainerLabel: label, transportLabel: transport)
            {
                Enabled = !Config.AppHandlesShifting, // l'appli gère les vitesses : relais pur
                NativeZwiftMode = Config.NativeZwiftMode,
                RiderWeightKg = Config.RiderWeightKg,
                BikeWeightKg = Config.BikeWeightKg,
                HeartRate = _hrSource,
                LogWire = _verboseFrames,
            };
            proxy.Log += m => Log?.Invoke($"[Proxy trainer] {m}");
            proxy.ClientsChanged += () => TrainerChanged?.Invoke();
            proxy.NativeStateChanged += () => TrainerChanged?.Invoke();
            await proxy.StartAsync();

            _vsModel.Gear = Gear.Gear;
            _trainerLink = link;
            _proxy = proxy;
            _proxyEndpoint = endpoint;
            // ShiftBridge applique lui-même les vitesses : l'appli ne doit pas pouvoir les filtrer (voir MappingEngine.AlwaysAllowed).
            lock (_dispatchLock) _mapping.AlwaysAllowed = Config.AppHandlesShifting ? NoActions : ProxyShiftActions;
            ReportGear(force: true);
            Log?.Invoke($"Proxy trainer actif ({transport}) : appairez votre appli au trainer « ShiftBridge Trainer » (port {proxy.Port}).");
        }
        catch
        {
            // Un proxy à moitié démarré garde son abonnement au trainer et peut avoir ouvert le canal natif : on le libère aussi.
            if (proxy is not null) { try { await proxy.DisposeAsync(); } catch { } }
            await link.DisposeAsync();
            throw;
        }
        TrainerChanged?.Invoke();
    }

    private static readonly IReadOnlySet<ObcButtonId> ProxyShiftActions = new HashSet<ObcButtonId> { ObcButtonId.ShiftUp, ObcButtonId.ShiftDown };
    private static readonly IReadOnlySet<ObcButtonId> NoActions = new HashSet<ObcButtonId>();

    public async Task StopTrainerProxyAsync()
    {
        await _proxyGate.WaitAsync();
        try { await StopTrainerProxyCoreAsync(); }
        finally { _proxyGate.Release(); }
    }

    private async Task StopTrainerProxyCoreAsync()
    {
        var proxy = _proxy; var link = _trainerLink;
        _proxy = null; _trainerLink = null; _proxyEndpoint = null;
        lock (_dispatchLock) _mapping.AlwaysAllowed = NoActions;
        if (proxy is not null) ReleaseReportedGear();
        if (proxy is not null) await proxy.DisposeAsync();
        if (link is not null) await link.DisposeAsync();
        if (proxy is not null || link is not null) TrainerChanged?.Invoke();
    }

    private void OnGearChanged()
    {
        _vsModel.Gear = Gear.Gear;
        Config.LastGear = Gear.Gear; // sauvegardée par l'interface (voir MainViewModel) pour être restaurée au prochain lancement
        if (_proxy is { } p) _ = p.ReapplyAsync();
        ReportGear();
        TrainerChanged?.Invoke();
    }

    // ---------- Flux principal ----------

    private readonly Action<IReadOnlyList<ButtonEvent>> _localInputHandler;

    /// <summary>
    /// Point d'entrée du flux (appelé par les manettes ; public pour les tests et le mode simulation).
    /// <paramref name="source"/> : la manette (ZwiftController) ou le fournisseur local à l'origine de l'appui, seul à vibrer ;
    /// null = pas de source précise, toutes les manettes vibrent.
    /// </summary>
    public void InjectEvents(IReadOnlyList<ButtonEvent> events, object? source = null)
    {
        List<(ObcButtonId Id, byte State)> changes, toSend;
        lock (_dispatchLock)
        {
            changes = _mapping.Translate(events);
            // Avec le proxy trainer actif, c'est lui qui applique les vitesses au trainer : on ne les envoie PAS à l'appli,
            // sinon elles seraient comptées deux fois (virtual shifting du proxy + celui de l'appli).
            // Les actions internes (vitesse centrale, aller au rapport) ne sortent jamais de ShiftBridge.
            bool proxyShifts = ProxyShifts;
            toSend = changes.Where(c => !ObcButtonCatalog.IsInternal(c.Id)
                                        && !(proxyShifts && c.Id is ObcButtonId.ShiftUp or ObcButtonId.ShiftDown)).ToList();
            // File unique, remplie sous le même verrou que la traduction : l'ordre appui/relâchement est strictement conservé
            // même quand deux sources (deux manettes, clavier…) injectent en même temps.
            if (toSend.Count > 0)
                foreach (var msg in ObcMessages.EncodeButtonState(toSend)) _outbox.Writer.TryWrite(msg);
        }

        foreach (var e in events)
        {
            PhysicalButtonChanged?.Invoke(e.Button, e.Pressed);
            Log?.Invoke($"Touche {e.Button} {(e.Pressed ? "appuyée" : "relâchée")}");
        }
        foreach (var (id, state) in changes)
            Log?.Invoke(ObcButtonCatalog.IsInternal(id)
                ? $"→ ShiftBridge {id} {(state == 1 ? "appuyé" : "relâché")}"
                : $"→ OBC {id} {(state == 1 ? "appuyé" : "relâché")}");
        foreach (var e in events.Where(e => !e.Pressed)) StopRepeater(e.Button); // touche relâchée (ou manette coupée)
        if (changes.Count == 0)
        {
            if (events.Any(e => e.Pressed))
                Log?.Invoke("Aucune action OBC : touche non associée, ou action non déclarée par l'appli.");
            return;
        }

        // Rapport virtuel + retour haptique sur appui uniquement.
        foreach (var (id, state) in changes.Where(c => c.State == 1))
            ApplyGearAction(id, GearParamFor(events, id), source);
        StartRepeaters(events, changes, source);

        if (ProxyShifts && changes.Any(c => c.Id is ObcButtonId.ShiftUp or ObcButtonId.ShiftDown or ObcButtonId.ShiftUpBy or ObcButtonId.ShiftDownBy))
            Log?.Invoke("Vitesse appliquée par le proxy trainer (non envoyée à l'appli).");
    }

    /// <summary>
    /// Un changement de vitesse demandé hors manette (boutons de la notification Android) : même effet qu'un appui sur
    /// « vitesse + » ou « vitesse − ». Sans proxy trainer, l'appli applique elle-même ses vitesses : elle voit un appui bref.
    /// </summary>
    public void Shift(ObcButtonId action)
    {
        if (action is not (ObcButtonId.ShiftUp or ObcButtonId.ShiftDown)) return;
        Log?.Invoke($"Vitesse demandée hors manette : {action}");
        ApplyGearAction(action);
        lock (_dispatchLock)
        {
            bool appSupportsIt = _mapping.SupportedActions is not { Count: > 0 } supported || supported.Contains(action);
            if (ProxyShifts || !appSupportsIt) return;
            // Sous le même verrou que le flux des touches : l'ordre appui/relâchement reste strict.
            foreach (var m in ObcMessages.EncodeButtonState([(action, (byte)1), (action, (byte)0)])) _outbox.Writer.TryWrite(m);
        }
    }

    /// <summary>Une action de vitesse (appui) : met à jour le rapport virtuel, l'interface, le trainer et la vibration.</summary>
    private void ApplyGearAction(ObcButtonId id, int target = 0, object? source = null)
    {
        GearChange change;
        int before, after;
        lock (_gearLock) { before = Gear.Gear; change = Gear.Apply(id, target); after = Gear.Gear; }
        Log?.Invoke($"Vitesse : {id} {before}→{after} ({change})");
        LastGearChange = change;
        if (change != GearChange.None) GearChanged?.Invoke();
        if (change == GearChange.Changed) OnGearChanged();
        _ = BuzzAsync(change, source);

        // « Monter / Descendre de X vitesses » : sans proxy, l'appli applique elle-même ses vitesses, elle reçoit donc
        // autant d'appuis brefs que de crans réellement franchis.
        if (id is ObcButtonId.ShiftUpBy or ObcButtonId.ShiftDownBy && after != before)
            _ = PulseAppAsync(id == ObcButtonId.ShiftUpBy ? ObcButtonId.ShiftUp : ObcButtonId.ShiftDown, Math.Abs(after - before));
    }

    /// <summary>Pause entre deux appuis brefs envoyés à l'appli pour « Monter / Descendre de X vitesses ».</summary>
    private const int StepPulseGapMs = 60;
    private readonly SemaphoreSlim _pulseGate = new(1, 1);

    private async Task PulseAppAsync(ObcButtonId action, int count)
    {
        // Une seule rafale à la fois : deux appuis rapprochés ne mélangent pas leurs impulsions.
        await _pulseGate.WaitAsync();
        try
        {
            for (int i = 0; i < count; i++)
            {
                lock (_dispatchLock)
                {
                    bool appSupportsIt = _mapping.SupportedActions is not { Count: > 0 } supported || supported.Contains(action);
                    if (ProxyShifts || !appSupportsIt) return; // le proxy a déjà appliqué le rapport au trainer
                    // Appui et relâchement dans deux messages distincts : l'appli voit un vrai appui à chaque fois.
                    foreach (var m in ObcMessages.EncodeButtonState([(action, (byte)1)])) _outbox.Writer.TryWrite(m);
                    foreach (var m in ObcMessages.EncodeButtonState([(action, (byte)0)])) _outbox.Writer.TryWrite(m);
                }
                if (i < count - 1) await Task.Delay(StepPulseGapMs);
            }
        }
        catch (Exception ex) { Log?.Invoke($"Rafale de vitesses interrompue : {ex.Message}"); }
        finally { _pulseGate.Release(); }
    }

    /// <summary>
    /// Paramètre de l'action de vitesse qui vient d'être appuyée, réglé dans l'onglet Touches : le rapport visé pour
    /// « Aller au rapport », le nombre de vitesses pour « Monter / Descendre de X vitesses ».
    /// </summary>
    private int GearParamFor(IReadOnlyList<ButtonEvent> events, ObcButtonId id)
    {
        if (id is not (ObcButtonId.GoToGear or ObcButtonId.ShiftUpBy or ObcButtonId.ShiftDownBy)) return 0;
        foreach (var e in events.Where(e => e.Pressed))
            if (Profile.Bindings.TryGetValue(e.Button, out var binding) && binding.Actions.Contains(id))
                return id == ObcButtonId.GoToGear ? binding.GearTarget : binding.GearSteps;
        return 0;
    }

    // ---------- Répétition sur appui long (vitesse + / −) ----------

    private readonly object _gearLock = new();
    private readonly ConcurrentDictionary<ButtonKey, CancellationTokenSource> _repeaters = new();

    /// <summary>Délai minimal d'attente et d'intervalle de répétition (protège le trainer et l'appli d'une valeur absurde).</summary>
    private const int MinRepeatMs = 30;

    /// <summary>
    /// Démarre la répétition pour les touches qui viennent d'être appuyées, ont l'option « répéter » et déclenchent une vitesse.
    /// Seules ShiftUp et ShiftDown se répètent : répéter « Menu » ou « Select » n'aurait aucun sens.
    /// </summary>
    private void StartRepeaters(IReadOnlyList<ButtonEvent> events, List<(ObcButtonId Id, byte State)> changes, object? source)
    {
        foreach (var e in events.Where(e => e.Pressed))
        {
            if (!Profile.Bindings.TryGetValue(e.Button, out var binding) || !binding.RepeatOnHold) continue;
            var action = binding.Actions.FirstOrDefault(a => a is ObcButtonId.ShiftUp or ObcButtonId.ShiftDown);
            if (action == ObcButtonId.None || !changes.Contains((action, (byte)1))) continue;

            StopRepeater(e.Button);
            var cts = new CancellationTokenSource();
            _repeaters[e.Button] = cts;
            // Réglage global (onglet Touches) : lu à chaque appui, donc pris en compte sans redémarrage.
            var (delayMs, intervalMs) = RepeatSpeeds.Timings(Config.RepeatSpeed);
            _ = RepeatLoopAsync(action, Math.Max(delayMs, MinRepeatMs), Math.Max(intervalMs, MinRepeatMs), source, cts.Token);
        }
    }

    private void StopRepeater(ButtonKey button)
    {
        if (_repeaters.TryRemove(button, out var cts)) cts.Cancel();
    }

    private async Task RepeatLoopAsync(ObcButtonId action, int delayMs, int intervalMs, object? source, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            while (!ct.IsCancellationRequested)
            {
                if (!RepeatShift(action, source)) return; // butée atteinte : inutile d'insister
                await Task.Delay(intervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"Répétition interrompue : {ex.Message}"); }
    }

    /// <summary>Une répétition : même effet qu'un nouvel appui (vitesse, retour haptique), sauf que la touche reste tenue.</summary>
    private bool RepeatShift(ObcButtonId action, object? source)
    {
        GearChange change;
        int before, after;
        lock (_gearLock) { before = Gear.Gear; change = Gear.Apply(action); after = Gear.Gear; }
        Log?.Invoke($"Vitesse (répétition) : {action} {before}→{after} ({change})");
        LastGearChange = change;
        if (change != GearChange.None) GearChanged?.Invoke();
        if (change == GearChange.Changed) OnGearChanged();
        _ = BuzzAsync(change, source);
        if (change != GearChange.Changed) return false;

        // Sans proxy, l'appli applique elle-même les vitesses : elle doit voir un nouvel appui. La touche est toujours tenue
        // (état 1) : on envoie donc relâché puis appuyé, et le vrai relâchement partira avec la touche.
        bool appSupportsIt = _mapping.SupportedActions is not { Count: > 0 } supported || supported.Contains(action);
        if (!ProxyShifts && appSupportsIt)
        {
            foreach (var m in ObcMessages.EncodeButtonState([(action, (byte)0)])) _outbox.Writer.TryWrite(m);
            foreach (var m in ObcMessages.EncodeButtonState([(action, (byte)1)])) _outbox.Writer.TryWrite(m);
        }
        return true;
    }

    /// <summary>
    /// Envoie un appui bref d'une action OBC aux applications, sans passer par les manettes.
    /// Permet de vérifier la partie « ShiftBridge → appli » indépendamment des manettes.
    /// </summary>
    public async Task SendTestActionAsync(ObcButtonId id)
    {
        Log?.Invoke($"Test : → OBC {id}");
        foreach (var m in ObcMessages.EncodeButtonState([(id, (byte)1)])) _outbox.Writer.TryWrite(m);
        await Task.Delay(100);
        foreach (var m in ObcMessages.EncodeButtonState([(id, (byte)0)])) _outbox.Writer.TryWrite(m);
    }

    private async Task SendLoopAsync()
    {
        // Ne fait que répartir : chaque transport a sa propre file (voir TransportPump).
        await foreach (var msg in _outbox.Reader.ReadAllAsync())
            foreach (var pump in _pumps.Values) pump.Post(msg);
    }

    /// <summary>
    /// Fenêtre pendant laquelle une demande haptique de l'appli est considérée comme l'écho d'une vibration déjà jouée par
    /// ShiftBridge (l'appli vibre à son tour après avoir appliqué la même vitesse) et ignorée : sinon double vibration, la seconde
    /// en retard de l'aller-retour appli.
    /// </summary>
    private const int AppEchoWindowMs = 500;
    private long _lastLocalBuzzTick = long.MinValue / 2;

    /// <summary>
    /// Vibre sur un changement de vitesse. Seule la manette à l'origine de l'appui vibre (<paramref name="source"/>), toutes
    /// si la source est inconnue (notification Android, simulation). Les cibles vibrent en parallèle : pas de retard cumulé.
    /// </summary>
    private Task BuzzAsync(GearChange change, object? source)
    {
        if (change == GearChange.None || !Config.ControllerVibration) return Task.CompletedTask;
        bool limit = change is GearChange.AtTopLimit or GearChange.AtBottomLimit;
        Interlocked.Exchange(ref _lastLocalBuzzTick, Environment.TickCount64); // avant l'écriture : l'écho ne peut pas passer entre-temps

        var tasks = new List<Task>();
        if (source is IController { CanVibrate: true } one)
        {
            tasks.Add(BuzzOneAsync(() => one.VibrateAsync(), limit));
        }
        else if (!ReferenceEquals(source, _localInput))
        {
            // Source inconnue, ou manette qui ne vibre pas (Click) : on retombe sur celles qui savent vibrer.
            foreach (var c in _controllers.Values.Where(c => c.CanVibrate)) tasks.Add(BuzzOneAsync(() => c.VibrateAsync(), limit));
        }
        if ((source is null || ReferenceEquals(source, _localInput)) && _localInput.CanVibrate)
            tasks.Add(BuzzOneAsync(_localInput.VibrateAsync, limit));
        return Task.WhenAll(tasks);
    }

    private async Task BuzzOneAsync(Func<Task> vibrate, bool limit)
    {
        try
        {
            await vibrate();
            if (limit)
            {
                await Task.Delay(150); // double vibration = butée atteinte
                await vibrate();
            }
        }
        catch (Exception ex) { Log?.Invoke($"Vibration manette : {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Interlocked.Increment(ref _scanEval); // annule tout arrêt de scan différé
        foreach (var button in _repeaters.Keys) StopRepeater(button);
        _localInput.ButtonEvents -= _localInputHandler;
        await _localInput.DisposeAsync();
        await StopTrainerProxyAsync();
        await DropHeartRateCoreAsync();
        _hrScanner.Dispose();
        _trainerScanner.Dispose();
        _scanner.Dispose();
        Inspector.Dispose();
        _hub.Dispose();
        foreach (var c in _controllers.Values) await c.DisposeAsync();
        _controllers.Clear();
        _outbox.Writer.TryComplete();
        await _sendLoop;
        // On laisse partir ce qui est en file, mais un client bloqué ne doit pas retenir l'arrêt de l'application.
        try { await Task.WhenAll(_pumps.Values.Select(p => p.CompleteAsync())).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _pumps.Clear();
        foreach (var t in TransportsSnapshot()) await t.DisposeAsync();
        lock (_transportsLock) _transports.Clear();
    }
}
