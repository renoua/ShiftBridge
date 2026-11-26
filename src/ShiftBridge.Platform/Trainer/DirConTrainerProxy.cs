using ShiftBridge.Core.Localization;
using System.Net;
using System.Net.Sockets;
using ShiftBridge.Core.DirCon;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Obc;
using ShiftBridge.Platform.Sensors;

namespace ShiftBridge.Platform.Trainer;

/// <summary>
/// Proxy de trainer « virtual shifting » : se présente à l'appli d'entraînement comme un trainer DirCon, relaie tout vers
/// le vrai trainer (quel que soit son transport : voir <see cref="ITrainerLink"/>) et ne réécrit que (1) la pente des
/// commandes de simulation FTMS et (2) la vitesse des mesures, selon le rapport virtuel courant. C'est le principe de
/// BikeControl (« BikeControl sets a slope »).
///
/// Le Control Point passe par un <see cref="ControlPointArbiter"/> (une commande en vol, réponse routée à son émetteur)
/// et l'état demandé par l'appli est suivi par un <see cref="FtmsControlTracker"/> (pas de ré-application hors simulation,
/// rejeu après reconnexion).
/// </summary>
public sealed class DirConTrainerProxy : IAsyncDisposable
{
    public const int PreferredPort = 36880;

    private readonly ITrainerLink _upstream;
    private readonly VirtualShiftingModel _model;
    private readonly IReadOnlyDictionary<string, string>? _txtTemplate;
    private readonly string _name;
    private readonly GattRelayPolicy _policy;
    private readonly IMdnsAdvertiser _mdns;
    private readonly List<Client> _clients = [];
    private readonly object _lock = new();
    private readonly object _state = new(); // protège _lastSpeedMs, _appSim, _speedWarned, _readCache
    private readonly Dictionary<Guid, List<(Guid Uuid, byte Props)>> _tree = [];
    private readonly Dictionary<Guid, byte[]> _readCache = [];
    private readonly FtmsControlTracker _tracker = new();
    private ControlPointArbiter? _arbiter;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private RealRatioDetector? _detector; // non nul pendant une mesure du braquet réel
    private SimulationParams? _appSim; // dernière simulation DEMANDÉE PAR L'APPLI (avant réécriture)
    private double _lastSpeedMs;
    private bool _noSpeedWarned;
    private long _lastLogTick;

    private readonly string _trainerLabel;
    private readonly string _transportLabel;
    private bool _firstSampleLogged;

    // ---------- FE-C (Tacx sans FTMS) ----------
    // Protégés par _state. La dernière page 0x33 de l'appli (avant réécriture) est réappliquée à chaque changement de rapport.
    private byte[]? _fecAppTrack;
    private byte _fecLastTarget;            // dernière page de mode (0x30, 0x31 ou 0x33) envoyée par l'appli
    private byte[]? _fecUserConfig;         // page 0x37 : rejouée après une reconnexion
    private double _fecCw = FecCodec.DefaultCw;
    private int? _fecCadence;
    private readonly SemaphoreSlim _fecWriteGate = new(1, 1); // une écriture FE-C à la fois (appli et proxy)

    /// <summary>Protocole de pilotage du trainer, connu après <see cref="StartAsync"/>.</summary>
    public TrainerControl Control { get; private set; } = TrainerControl.Ftms;

    public DirConTrainerProxy(ITrainerLink upstream, VirtualShiftingModel model,
        IReadOnlyDictionary<string, string>? txtTemplate = null, string name = "ShiftBridge Trainer",
        GattRelayPolicy? policy = null, string trainerLabel = "Trainer", string transportLabel = "?", IMdnsAdvertiser? mdns = null)
    {
        _mdns = mdns ?? new MdnsAdvertiser();
        _upstream = upstream;
        _model = model;
        _txtTemplate = txtTemplate;
        _name = name;
        _policy = policy ?? new GattRelayPolicy(full: true); // DirCon : relais complet, comportement validé sur KICKR
        _trainerLabel = trainerLabel;
        _transportLabel = transportLabel;
    }

    /// <summary>Faux : relais pur, sans aucune modification (utile pour comparer avec/sans virtual shifting).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Mode natif Zwift : si le trainer expose le service Zwift et répond au handshake, c'est LUI qui applique le rapport
    /// (commandes natives) ; sinon, ou en cas d'échec, repli sur la mise à l'échelle FTMS.
    /// </summary>
    public bool NativeZwiftMode { get; set; } = true;
    public double RiderWeightKg { get; set; } = 75;
    public double BikeWeightKg { get; set; } = 9;
    public bool NativeActive => _native is { IsActive: true };

    /// <summary>Nouveaux poids : retenus pour la prochaine ouverture du canal natif et, s'il est actif, envoyés aussitôt au trainer.</summary>
    public async Task ApplyWeightsAsync(double riderKg, double bikeKg)
    {
        RiderWeightKg = riderKg;
        BikeWeightKg = bikeKg;
        if (_native is { IsActive: true } native) await native.SendWeightsAsync(riderKg, bikeKg);
    }
    public event Action? NativeStateChanged;

    private ZwiftTrainerChannel? _native;
    private bool _nonSimTargetActive;   // la dernière cible de l'appli n'est pas une simulation (ERG…)
    private long _lastNativeLogTick;
    public int Port { get; private set; }
    public bool HasClient { get { lock (_lock) return _clients.Count > 0; } }
    public double LastSpeedMs { get { lock (_state) return _lastSpeedMs; } }
    private int? _lastPowerW;
    private double? _lastCadenceRpm;
    /// <summary>Dernière puissance (W) et cadence (tr/min) mesurées par le trainer (null si absentes) : affichées dans la superposition.</summary>
    public int? LastPowerW { get { lock (_state) return _lastPowerW; } }
    public double? LastCadenceRpm { get { lock (_state) return _lastCadenceRpm; } }
    public FtmsFeatures? Features { get; private set; }

    /// <summary>
    /// Capteur cardio externe à republier à l'appli (service 0x180D synthétique) ; à fixer avant <see cref="StartAsync"/>.
    /// Il remplace le service cardio éventuel du trainer. Son cycle de vie reste à l'appelant.
    /// </summary>
    public HeartRateSource? HeartRate { get; set; }
    private bool _hrInjected;
    public int? LastHeartRateBpm => HeartRate?.LastBpm;

    public event Action<string>? Log;
    public event Action? ClientsChanged;
    /// <summary>Réponse du trainer à une commande qu'aucun de nos clients n'a émise : un autre client écrit sur le trainer.</summary>
    public event Action? ForeignControlDetected;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // 1) Copie (filtrée) de l'arbre de services du vrai trainer et abonnement à ses notifications.
        // Sans FTMS, le canal FE-C (Tacx) est le seul moyen de piloter la résistance : il est alors relayé, et réécrit.
        var upstreamServices = await _upstream.DiscoverServicesAsync();
        bool hasFtms = upstreamServices.Contains(FtmsCodec.FitnessMachineService);
        List<Guid> skipped = [];
        foreach (var svc in upstreamServices)
        {
            bool fecFallback = !hasFtms && svc == FecCodec.Service;
            if (!fecFallback && !_policy.ShouldRelayService(svc)) { skipped.Add(svc); continue; }
            var chars = (await _upstream.DiscoverCharacteristicsAsync(svc))
                .Where(c => fecFallback || _policy.ShouldRelayCharacteristic(svc, c.Uuid)).ToList();
            if (chars.Count > 0) _tree[svc] = chars;
        }
        Control = TrainerReport.DetectControl(_tree);

        // Cardio externe : le service du capteur remplace celui du trainer (rien n'est lu ni abonné côté trainer).
        if (HeartRate is { } hr)
        {
            _tree.Remove(HeartRateCodec.Service);
            _tree[HeartRateCodec.Service] =
            [
                (HeartRateCodec.Measurement, DirConProps.Notify),
                (HeartRateCodec.SensorLocation, DirConProps.Read),
            ];
            _hrInjected = true;
            hr.Measurement += OnHeartRateMeasurement;
            Log?.Invoke($"Cardio : capteur « {hr.Name} » republié à l'appli (service 0x180D).");
        }

        _upstream.Notification += OnUpstreamNotification;
        foreach (var (uuid, props) in _tree.Where(kv => kv.Key != HeartRateCodec.Service || !_hrInjected).SelectMany(kv => kv.Value))
            if ((props & (DirConProps.Notify | DirConProps.Indicate)) != 0)
            {
                try { await _upstream.EnableNotificationsAsync(uuid); }
                catch (Exception ex) { Log?.Invoke($"Abonnement trainer {uuid} impossible : {ex.Message}"); }
            }
        Log?.Invoke($"Trainer relayé : {_tree.Count} service(s), {_tree.Values.Sum(c => c.Count)} caractéristique(s)");

        // 2) Capacités du trainer : avertir si la simulation n'est pas déclarée (le virtual shifting en dépend).
        await ReadFeaturesAsync();
        Log?.Invoke(TrainerReport.Build(_trainerLabel, _transportLabel, _tree, Features, skipped, Control));
        if (Control == TrainerControl.None)
            Log?.Invoke("ATTENTION : ce trainer n'expose ni FTMS ni FE-C : ShiftBridge ne peut pas régler sa résistance. " +
                        "Le virtual shifting est inopérant ; les vitesses sont laissées à l'appli. Connectez l'appli directement au trainer.");

        // 3) Arbitre du Control Point.
        _arbiter = new ControlPointArbiter((cp, _) => _upstream.WriteAsync(FtmsCodec.ControlPoint, cp));
        _arbiter.ResponseForApp += ForwardControlResponse;
        _arbiter.ForeignResponse += _ => { Log?.Invoke("Réponse du trainer à une commande inconnue : une autre appli pilote-t-elle ce trainer ?"); ForeignControlDetected?.Invoke(); };
        _arbiter.Timeout += op => Log?.Invoke($"Le trainer n'a pas répondu à la commande 0x{op:X2}");
        _arbiter.Log += m => Log?.Invoke(m);
        _arbiter.ResponseLatency += ms => Latency.Add("FTMS", ms);

        if (_upstream is IReconnectingLink rl)
        {
            rl.Dropped += () => Log?.Invoke("Trainer déconnecté, reconnexion en cours…");
            rl.Reconnected += OnUpstreamReconnected;
        }

        // Mode natif Zwift : tenté avant d'ouvrir le serveur ; en cas d'échec, rien n'a été modifié côté résistance.
        await TryStartNativeAsync();

        // 4) Serveur DirCon pour l'appli d'entraînement.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int port = PreferredPort; port < PreferredPort + 20; port++)
        {
            try { _listener = new TcpListener(IPAddress.Any, port); _listener.Start(); Port = port; break; }
            catch (SocketException) { _listener = null; }
        }
        if (_listener is null) throw new IOException(Strings.Err_NoFreePortProxy);

        var (mac, serial) = DirConServer.NicIdentity(variant: 1);
        var txt = new Dictionary<string, string>(_txtTemplate ?? new Dictionary<string, string>());
        txt["mac-address"] = mac;
        txt["serial-number"] = serial;
        if (!txt.ContainsKey("ble-service-uuids"))
            txt["ble-service-uuids"] = string.Join(",", _tree.Keys.Select(ShortOrFull));
        else if (_hrInjected && !txt["ble-service-uuids"].Contains("180d", StringComparison.OrdinalIgnoreCase))
            txt["ble-service-uuids"] += ",0x180D";
        try { _mdns.Advertise($"{_name} {serial[^4..]}", "_wahoo-fitness-tnp._tcp", (ushort)Port, txt, m => Log?.Invoke(m)); }
        catch (Exception ex) { Log?.Invoke($"mDNS indisponible : {ex.Message}"); }

        Log?.Invoke($"Proxy trainer en écoute sur le port {Port}");
        _ = AcceptLoopAsync(_cts.Token);
        _ = LatencyReportLoopAsync(_cts.Token);
    }

    /// <summary>Mesures de latence (natif : envoi → confirmation ; FTMS : commande → réponse), résumées dans le journal.</summary>
    public LatencyTracker Latency { get; } = new();
    /// <summary>Journalise en hexadécimal les requêtes de l'appli et les réponses du proxy (option « trames détaillées »).</summary>
    public bool LogWire { get; set; }
    /// <summary>Flux de mesures (watts, vitesse, cadence) relayé à l'appli : nombre, plus long silence, dernière puissance.</summary>
    public BikeDataMonitor BikeData { get; } = new();
    public TimeSpan LatencyReportInterval { get; set; } = TimeSpan.FromSeconds(30);

    private async Task LatencyReportLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LatencyReportInterval, ct);
                if (Latency.TakeSummary() is { } line) Log?.Invoke(line);
                if (BikeData.TakeSummary(Environment.TickCount64) is { } flow) Log?.Invoke(flow);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadFeaturesAsync()
    {
        if (!_tree.Values.Any(c => c.Any(x => x.Uuid == FtmsCodec.FitnessMachineFeature))) return;
        try
        {
            var data = await _upstream.ReadAsync(FtmsCodec.FitnessMachineFeature);
            lock (_state) _readCache[FtmsCodec.FitnessMachineFeature] = data;
            if (FtmsFeatures.TryParse(data, out var f))
            {
                Features = f;
                Log?.Invoke($"Fonctions du trainer (FTMS) : {f.Describe()}");
                if (!f.SupportsIndoorBikeSimulation)
                    Log?.Invoke("ATTENTION : ce trainer ne déclare pas la simulation de vélo d'intérieur (0x11) : le virtual shifting risque de ne pas fonctionner.");
            }
        }
        catch (Exception ex) { Log?.Invoke($"Lecture des fonctions FTMS impossible : {ex.Message}"); }
    }

    private static string ShortOrFull(Guid g)
    {
        var s = g.ToString();
        return s.StartsWith("0000", StringComparison.Ordinal) && s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal)
            ? "0x" + s.Substring(4, 4) : s;
    }

    // ---------- Mesure du braquet réel ----------

    /// <summary>Circonférence de roue (m) supposée par la mesure du rapport réel.</summary>
    public double WheelCircumferenceM { get; set; } = 2.105;

    /// <summary>
    /// Mesure le rapport réel (plateau ÷ pignon) d'après la vitesse et la cadence que le trainer rapporte. L'utilisateur doit
    /// pédaler à cadence régulière (60–110 tr/min) sur le braquet réel. Retourne dès que la mesure est stable, ou au délai.
    /// </summary>
    public async Task<RatioMeasurement> MeasureRealRatioAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var detector = new RealRatioDetector(WheelCircumferenceM);
        lock (_state) _detector = detector;
        try
        {
            long end = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < end)
            {
                await Task.Delay(200, ct);
                if (detector.TryGetResult(out var ratio, out var samples, out var spread))
                    return RatioMeasurement.Ok(ratio, samples, spread);
            }
            return RatioMeasurement.Failure(ExplainFailure(detector));
        }
        finally
        {
            lock (_state) { if (ReferenceEquals(_detector, detector)) _detector = null; }
        }
    }

    private static string ExplainFailure(RealRatioDetector d)
    {
        if (d.TotalSamples == 0) return Strings.Measure_NoData;
        if (!d.SawCadence) return Strings.Measure_NoCadence;
        if (d.AcceptedSamples == 0)
            return Loc.Fmt(Strings.Measure_OutOfRange, RealRatioDetector.MinRpm, RealRatioDetector.MaxRpm, d.LastRpm);
        if (d.AcceptedSamples < RealRatioDetector.MinAccepted) return Strings.Measure_TooShort;
        return Loc.Fmt(Strings.Measure_Irregular, d.CurrentSpread);
    }

    // ---------- Gear changes ----------

    /// <summary>
    /// À appeler après un changement de rapport. Ne touche au trainer QUE si la dernière cible de l'appli est une simulation
    /// (en ERG, le rapport est sans effet). Les ré-applications sont fusionnées et espacées par l'arbitre.
    /// </summary>
    public Task ReapplyAsync()
    {
        if (!Enabled) return Task.CompletedTask;
        if (Control == TrainerControl.FeC) return ReapplyFecAsync();

        // Mode natif : le trainer applique le rapport. On lui envoie le nouveau rapport de la table, même sans simulation en cours
        // (il le retient pour la suite) ; la résistance suit sans toucher à la commande FTMS de l'appli.
        if (_native is { IsActive: true } native)
        {
            _ = native.SendGearAsync(CurrentRatio());
            return Task.CompletedTask;
        }

        if (_arbiter is null || !_tracker.CanReapplySimulation) return Task.CompletedTask;
        var sim = _tracker.LastTarget;
        if (sim is not null) _arbiter.SubmitFromProxy(Adjust(sim));
        return Task.CompletedTask;
    }

    // ---------- FE-C ----------

    /// <summary>Page 0x33 à envoyer au trainer pour le rapport courant (pente seule : la norme FE-C met la traînée ailleurs).</summary>
    private byte[] AdjustFec(byte[] track)
    {
        if (!FecCodec.TryParseTrack(track, out short grade, out double crr)) return track;
        double speed, cw;
        lock (_state) { speed = _lastSpeedMs; cw = _fecCw; }
        var sim = new SimulationParams(0, grade,
            (byte)Math.Clamp(Math.Round(crr / 0.0001), 0, 255), (byte)Math.Clamp(Math.Round(cw / 0.01), 0, 255));
        short adjusted = _model.AdjustGrade(sim, speed);
        if (adjusted == grade) return track;

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLogTick) > 1000)
        {
            Interlocked.Exchange(ref _lastLogTick, now);
            Log?.Invoke($"Virtual shifting (FE-C) : rapport {_model.Gear} (×{_model.Factor:0.00}) pente {grade * 0.01:0.0} % → {adjusted * 0.01:0.0} %");
        }
        return FecCodec.WithGrade(track, adjusted);
    }

    private async Task WriteFecAsync(byte[] value)
    {
        await _fecWriteGate.WaitAsync();
        try { await _upstream.WriteAsync(FecCodec.Tx, value); }
        finally { _fecWriteGate.Release(); }
    }

    /// <summary>Changement de rapport : la dernière simulation de l'appli, réajustée. Rien en ERG ou en résistance fixe.</summary>
    private async Task ReapplyFecAsync()
    {
        byte[]? track; byte target;
        lock (_state) { track = _fecAppTrack; target = _fecLastTarget; }
        if (track is null || target != FecCodec.PageTrackResistance || !_upstream.IsConnected) return;
        try { await WriteFecAsync(AdjustFec(track)); }
        catch (Exception ex) { Log?.Invoke($"Virtual shifting (FE-C) : envoi au trainer impossible ({ex.Message})"); }
    }

    /// <summary>Écriture de l'appli sur FEC3 : mémorise l'état demandé, réécrit la pente des pages 0x33, relaie le reste tel quel.</summary>
    private async Task<DirConPacket> HandleFecWriteAsync(DirConPacket p, Guid ch, byte[] value)
    {
        byte[] toTrainer = value;
        if (FecCodec.TrySplit(value, out var messages))
        {
            var output = new List<byte>(value.Length);
            foreach (var m in messages)
            {
                byte page = FecCodec.Page(m);
                if (LogWire) Log?.Invoke($"Appli → trainer : {FecCodec.Describe(m)}");
                lock (_state)
                {
                    if (FecCodec.IsTargetPage(page)) _fecLastTarget = page;
                    if (page == FecCodec.PageTrackResistance) _fecAppTrack = m;
                    else if (page == FecCodec.PageUserConfiguration) _fecUserConfig = m;
                    else if (page == FecCodec.PageWindResistance && FecCodec.TryParseWindCw(m, out var cw)) _fecCw = cw;
                }
                output.AddRange(Enabled && page == FecCodec.PageTrackResistance ? AdjustFec(m) : m);
            }
            toTrainer = [.. output];
        }

        if (!_upstream.IsConnected) return DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch); // rejoué à la reconnexion
        try { await WriteFecAsync(toTrainer); }
        catch (Exception) { return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.WriteFailed); }
        return DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch);
    }

    /// <summary>Notification FEC2 : mesures (vitesse, cadence, puissance) lues, vitesse remplacée par la vitesse virtuelle.</summary>
    private byte[] OnFecNotification(byte[] value)
    {
        if (!FecCodec.TrySplit(value, out var messages)) return value;
        bool changed = false;
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (FecCodec.TryGetTrainerData(m, out var cadence, out var power))
            {
                BikeData.Add(Environment.TickCount64, power);
                lock (_state) { _lastPowerW = power; _lastCadenceRpm = cadence; _fecCadence = cadence; }
            }
            else if (FecCodec.TryGetSpeedMs(m, out var v))
            {
                if (!_firstSampleLogged) { _firstSampleLogged = true; Log?.Invoke($"Premier échantillon FE-C : {Convert.ToHexString(m)} (vitesse {v * 3.6:0.0} km/h)"); }
                RealRatioDetector? detector; int? rpm;
                lock (_state) { _lastSpeedMs = v; detector = _detector; rpm = _fecCadence; }
                detector?.AddSample(v, rpm ?? 0);
                if (Enabled && Math.Abs(_model.Factor - 1.0) > 1e-9) { messages[i] = FecCodec.ScaleSpeed(m, _model.Factor); changed = true; }
            }
        }
        return changed ? [.. messages.SelectMany(m => m)] : value;
    }

    private byte[] Adjust(byte[] cp)
    {
        if (!FtmsCodec.TryParseSimulation(cp, out var sim)) return cp;
        double speed;
        lock (_state) speed = _lastSpeedMs;
        var adjusted = _model.AdjustSimulation(sim, speed);
        if (adjusted == sim) return cp;

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLogTick) > 1000)
        {
            Interlocked.Exchange(ref _lastLogTick, now);
            Log?.Invoke($"Virtual shifting : rapport {_model.Gear} (×{_model.Factor:0.00}) pente {sim.GradePercent:0.0} % → {adjusted.GradePercent:0.0} %, " +
                        $"roulement {sim.Crr:0.0000} → {adjusted.Crr:0.0000}, traînée {sim.Cw:0.00} → {adjusted.Cw:0.00}");
        }
        return FtmsCodec.WithSimulation(cp, adjusted);
    }

    // ---------- Mode natif Zwift ----------

    private double CurrentRatio() => _model.RatioFor(_model.Gear);

    private async Task TryStartNativeAsync()
    {
        if (!Enabled) return; // l'appli gère les vitesses : le trainer ne doit recevoir aucun rapport de ShiftBridge
        if (!NativeZwiftMode) { Log?.Invoke("Mode natif Zwift désactivé : mise à l'échelle FTMS."); return; }

        SimulationParams? current; lock (_state) current = _appSim;
        var channel = await ZwiftTrainerChannel.TryOpenAsync(_upstream, CurrentRatio(), RiderWeightKg, BikeWeightKg, current,
            m => Log?.Invoke($"[Natif] {m}"));
        if (channel is null)
        {
            Log?.Invoke("Mode natif indisponible : repli sur la mise à l'échelle FTMS.");
            NativeStateChanged?.Invoke();
            return;
        }
        channel.Failed += OnNativeFailed;
        channel.Latency = Latency;
        _native = channel;
        Log?.Invoke("Mode natif Zwift actif : le trainer applique lui-même le rapport.");
        NativeStateChanged?.Invoke();
    }

    private void OnNativeFailed()
    {
        Log?.Invoke("Canal natif Zwift perdu : repli sur la mise à l'échelle FTMS.");
        NativeStateChanged?.Invoke();
        _ = ReapplyAsync(); // la résistance repasse aussitôt par la commande FTMS ajustée
    }

    private async Task DisposeNativeAsync(bool release)
    {
        var n = _native;
        _native = null;
        if (n is null) return;
        n.Failed -= OnNativeFailed;
        if (release) await n.ReleaseAsync();
        await n.DisposeAsync();
    }

    /// <summary>Active ou désactive le mode natif à chaud (case de l'interface).</summary>
    public async Task SetNativeModeAsync(bool enabled)
    {
        NativeZwiftMode = enabled;
        if (enabled && !NativeActive)
        {
            await DisposeNativeAsync(release: false);
            await TryStartNativeAsync();
            if (NativeActive && _arbiter is not null && _tracker.CanReapplySimulation && _tracker.LastTarget is { } sim)
            {
                // Le trainer garde la dernière commande FTMS ajustée : on la neutralise (pente 0) avant que le natif ne reprenne.
                _arbiter.SubmitProxySequence([FtmsCodec.WithGrade(sim, 0)]);
                if (_native is { } n && FtmsCodec.TryParseSimulation(sim, out var raw)) await n.SendSimulationAsync(raw);
            }
        }
        else if (!enabled && _native is not null)
        {
            await DisposeNativeAsync(release: true);
            NativeStateChanged?.Invoke();
            await ReapplyAsync();
        }
    }

    /// <summary>
    /// Active ou coupe le virtual shifting à chaud. Coupé, le proxy devient un relais pur : l'appli gère elle-même ses vitesses
    /// et le trainer retrouve aussitôt la simulation demandée par l'appli, sans réécriture. <paramref name="native"/> : mode
    /// natif à reprendre au réarmement.
    /// </summary>
    public async Task SetShiftingEnabledAsync(bool enabled, bool native)
    {
        if (Enabled == enabled) return;
        if (!enabled)
        {
            Enabled = false;
            await DisposeNativeAsync(release: true);
            NativeStateChanged?.Invoke();
            if (Control == TrainerControl.FeC)
            {
                byte[]? track; byte target;
                lock (_state) { track = _fecAppTrack; target = _fecLastTarget; }
                if (track is not null && target == FecCodec.PageTrackResistance && _upstream.IsConnected)
                {
                    try { await WriteFecAsync(track); } catch (Exception ex) { Log?.Invoke($"FE-C : pente de l'appli non rétablie ({ex.Message})"); }
                }
                return;
            }
            // Le trainer garde la dernière simulation ajustée : on lui renvoie celle de l'appli telle quelle.
            if (_arbiter is not null && _tracker.CanReapplySimulation && _tracker.LastTarget is { } sim) _arbiter.SubmitFromProxy(sim);
            return;
        }
        Enabled = true;
        NativeZwiftMode = native;
        if (native) await SetNativeModeAsync(true);
        await ReapplyAsync();
    }

    /// <summary>Après une reconnexion : nouvelle session native, puis le rapport et la simulation en cours.</summary>
    private async Task ReopenNativeAsync()
    {
        var n = _native;
        if (n is null) return;
        if (!await n.ReopenAsync())
        {
            Log?.Invoke("Canal natif Zwift non rétabli après la reconnexion : repli sur la mise à l'échelle FTMS.");
            await DisposeNativeAsync(release: false);
            NativeStateChanged?.Invoke();
            await ReapplyAsync();
            return;
        }
        NativeStateChanged?.Invoke();
        await n.SendGearAsync(CurrentRatio());
        SimulationParams? sim; lock (_state) sim = _appSim;
        if (_tracker.CanReapplySimulation && sim is { } s) await n.SendSimulationAsync(s);
    }

    /// <summary>Après une reconnexion le contrôle FTMS est perdu : on rejoue ce que l'appli avait demandé.</summary>
    private void OnUpstreamReconnected()
    {
        if (Control == TrainerControl.FeC) { _ = ReplayFecAsync(); return; }
        if (_arbiter is null) return;
        var sequence = _tracker.ReplaySequence(cp => Enabled ? Adjust(cp) : cp);
        if (_native is not null)
        {
            // En natif la simulation ne passe pas par FTMS : on ne rejoue que le contrôle, le démarrage et les autres cibles (ERG…).
            sequence = sequence.Where(s => s[0] != FtmsCodec.OpSetSimulation).ToList();
            _ = ReopenNativeAsync();
        }
        if (sequence.Count == 0) return;
        Log?.Invoke($"Trainer reconnecté : rejeu de {sequence.Count} commande(s) de contrôle");
        _arbiter.SubmitProxySequence(sequence);
    }

    /// <summary>Après une reconnexion FE-C : configuration du cycliste, puis le dernier mode demandé par l'appli.</summary>
    private async Task ReplayFecAsync()
    {
        byte[]? config, track; byte target;
        lock (_state) { config = _fecUserConfig; track = _fecAppTrack; target = _fecLastTarget; }
        try
        {
            if (config is not null) await WriteFecAsync(config);
            if (track is not null && target == FecCodec.PageTrackResistance) await WriteFecAsync(Enabled ? AdjustFec(track) : track);
            if (config is not null || track is not null) Log?.Invoke("Trainer reconnecté : configuration et simulation FE-C rejouées");
        }
        catch (Exception ex) { Log?.Invoke($"Rejeu FE-C impossible : {ex.Message}"); }
    }

    // ---------- Trainer → appli ----------

    /// <summary>Mesure du capteur cardio externe : envoyée aux clients abonnés (comme une notification du trainer).</summary>
    private void OnHeartRateMeasurement(byte[] value)
    {
        var packet = DirConPacket.NotificationPacket(HeartRateCodec.Measurement, value).Encode();
        List<Client> targets;
        lock (_lock) targets = _clients.Where(c => c.IsSubscribed(HeartRateCodec.Measurement)).ToList();
        foreach (var c in targets) _ = c.SendAsync(packet);
    }

    private void OnUpstreamNotification(Guid ch, byte[] value)
    {
        if (_hrInjected && ch == HeartRateCodec.Measurement) return; // le capteur externe fait foi

        if (ch == FtmsCodec.ControlPoint)
        {
            _arbiter?.OnResponse(value); // routé par l'arbitre (propriétaire de la commande en vol)
            return;
        }

        if (ch == FtmsCodec.IndoorBikeData)
        {
            if (!_firstSampleLogged) { _firstSampleLogged = true; Log?.Invoke(TrainerReport.DescribeBikeData(value)); }
            bool hasPower = FtmsCodec.TryGetPowerW(value, out int watts);
            BikeData.Add(Environment.TickCount64, hasPower ? watts : null);
            bool hasCadence = FtmsCodec.TryGetCadenceRpm(value, out double cadence);
            lock (_state) { _lastPowerW = hasPower ? watts : null; _lastCadenceRpm = hasCadence ? cadence : null; }
            if (FtmsCodec.TryGetSpeedMs(value, out var v))
            {
                RealRatioDetector? detector;
                lock (_state) { _lastSpeedMs = v; detector = _detector; }
                // Mesure du braquet réel en cours : vitesse BRUTE du trainer (avant mise à l'échelle) et cadence (0 si absente).
                detector?.AddSample(v, FtmsCodec.TryGetCadenceRpm(value, out var rpm) ? rpm : 0);
            }
            else
            {
                bool warn;
                lock (_state) { warn = !_noSpeedWarned; _noSpeedWarned = true; }
                if (warn) Log?.Invoke("Ce trainer n'envoie pas de vitesse instantanée : la pente sera calculée sans terme de traînée.");
            }
            // Mode natif : le trainer possède le rapport, sa vitesse est laissée telle quelle (à valider sur matériel).
            if (Enabled && !NativeActive && Math.Abs(_model.Factor - 1.0) > 1e-9) value = FtmsCodec.ScaleSpeed(value, _model.Factor);
        }
        else if (ch == FecCodec.Rx && Control == TrainerControl.FeC)
        {
            value = OnFecNotification(value);
        }
        else if (ch == FtmsCodec.FitnessMachineStatus && Enabled && !NativeActive && value.Length > 0 && value[0] == FtmsCodec.StatusSimulationChanged)
        {
            // Le statut renverrait à l'appli la pente RÉÉCRITE : on lui rend celle qu'elle a demandée.
            SimulationParams? app; lock (_state) app = _appSim;
            if (app is { } a) value = FtmsCodec.RewriteStatusGrade(value, a.GradeRaw);
        }

        var packet = DirConPacket.NotificationPacket(ch, value).Encode();
        List<Client> targets;
        lock (_lock) targets = _clients.Where(c => c.IsSubscribed(ch)).ToList();
        foreach (var c in targets) _ = c.SendAsync(packet);
    }

    /// <summary>Réponse du Control Point : envoyée à tous les clients (même sans abonnement), APRÈS l'acquittement d'écriture.</summary>
    private void ForwardControlResponse(byte[] indication)
    {
        var packet = DirConPacket.NotificationPacket(FtmsCodec.ControlPoint, indication).Encode();
        List<Client> targets;
        lock (_lock) targets = [.. _clients];
        foreach (var c in targets) _ = SendAfterAckAsync(c, packet);
    }

    private static async Task SendAfterAckAsync(Client c, byte[] packet)
    {
        await c.AckGate;
        await c.SendAsync(packet);
    }

    // ---------- Appli → trainer ----------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener is not null)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); } catch { break; }
            tcp.NoDelay = true;
            var client = new Client(tcp);
            lock (_lock) _clients.Add(client);
            Log?.Invoke($"Appli connectée au proxy trainer : {tcp.Client.RemoteEndPoint}");
            ClientsChanged?.Invoke();
            _ = ReadLoopAsync(client, ct);
        }
    }

    private async Task ReadLoopAsync(Client client, CancellationToken ct)
    {
        var parser = new DirConStreamParser();
        var buf = new byte[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await client.Stream.ReadAsync(buf, ct);
                if (n == 0) break;
                foreach (var p in parser.Feed(buf.AsSpan(0, n)))
                {
                    try
                    {
                        if (LogWire) Log?.Invoke($"Proxy < {Convert.ToHexString(p.Encode())}");
                        var response = await HandleAsync(client, p);
                        if (response is not null)
                        {
                            var bytes = response.Encode();
                            if (LogWire) Log?.Invoke($"Proxy > {Convert.ToHexString(bytes)}");
                            await client.SendAsync(bytes);
                        }
                    }
                    finally { client.EndAck(); } // les indications retenues peuvent partir
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_lock) _clients.Remove(client);
            client.Dispose();
            Log?.Invoke("Appli déconnectée du proxy trainer");
            ClientsChanged?.Invoke();
        }
    }

    private async Task<DirConPacket?> HandleAsync(Client client, DirConPacket p)
    {
        if (p.ResponseCode != DirConResponse.Success) return null;

        switch ((DirConId)p.Id)
        {
            case DirConId.DiscoverServices:
                return DirConPacket.ServicesResponse(p.Sequence, _tree.Keys);

            case DirConId.DiscoverCharacteristics:
            {
                if (p.Payload.Length < 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                var svc = DirConPacket.ReadUuid(p.Payload);
                return _tree.TryGetValue(svc, out var chars)
                    ? DirConPacket.CharacteristicsResponse(p.Sequence, svc, chars)
                    : DirConPacket.Error(p.Id, p.Sequence, DirConResponse.ServiceNotFound);
            }

            case DirConId.ReadCharacteristic:
            {
                if (p.Payload.Length < 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                var ch = DirConPacket.ReadUuid(p.Payload);
                if (!IsKnown(ch)) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.CharacteristicNotFound);
                if (IsInjected(ch))
                {
                    // Cardio externe : valeur locale (position du capteur).
                    return ch == HeartRateCodec.SensorLocation && HeartRate is { } src
                        ? DirConPacket.ReadResponse(p.Sequence, ch, src.SensorLocation)
                        : DirConPacket.Error(p.Id, p.Sequence, DirConResponse.OperationNotSupported);
                }
                try
                {
                    var data = await _upstream.ReadAsync(ch);
                    lock (_state) _readCache[ch] = data;
                    return DirConPacket.ReadResponse(p.Sequence, ch, data);
                }
                catch (Exception)
                {
                    // Pendant une coupure, on sert la dernière valeur connue plutôt qu'une erreur.
                    byte[]? cached; lock (_state) _readCache.TryGetValue(ch, out cached);
                    return cached is not null
                        ? DirConPacket.ReadResponse(p.Sequence, ch, cached)
                        : DirConPacket.Error(p.Id, p.Sequence, DirConResponse.OperationNotSupported);
                }
            }

            case DirConId.WriteCharacteristic:
            {
                if (p.Payload.Length <= 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                var ch = DirConPacket.ReadUuid(p.Payload);
                if (!IsKnown(ch)) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.CharacteristicNotFound);
                byte[] value = p.Payload[16..];

                if (IsInjected(ch)) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.OperationNotSupported);
                if (ch == FtmsCodec.ControlPoint) return await HandleControlPointWriteAsync(client, p, ch, value);
                if (ch == FecCodec.Tx && Control == TrainerControl.FeC) return await HandleFecWriteAsync(p, ch, value);

                try { await _upstream.WriteAsync(ch, value); }
                catch (Exception) { return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.WriteFailed); }
                return DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch);
            }

            case DirConId.EnableNotifications:
            {
                if (p.Payload.Length < 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                var ch = DirConPacket.ReadUuid(p.Payload);
                if (!IsKnown(ch)) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.CharacteristicNotFound);
                bool enable = p.Payload.Length < 17 || p.Payload[16] != 0; // TPV envoie l'UUID seul
                client.SetSubscribed(ch, enable);
                return DirConPacket.AckResponse(DirConId.EnableNotifications, p.Sequence, ch);
            }

            default:
                return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnknownMessageType);
        }
    }

    private async Task<DirConPacket> HandleControlPointWriteAsync(Client client, DirConPacket p, Guid ch, byte[] value)
    {
        // État demandé par l'appli (avant toute réécriture) : sert à ne pas ré-appliquer hors simulation, et au rejeu.
        _tracker.OnAppCommand(value);
        bool isSim = FtmsCodec.TryParseSimulation(value, out var appSim);
        if (isSim) lock (_state) _appSim = appSim;

        bool wasNonSimTarget;
        lock (_state)
        {
            wasNonSimTarget = _nonSimTargetActive;
            if (value.Length > 0 && FtmsCodec.IsTargetCommand(value[0])) _nonSimTargetActive = !isSim;
        }

        client.BeginAck(); // la réponse du trainer ne doit pas dépasser notre acquittement
        if (_arbiter is null) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.WriteFailed);

        // Mode natif : la simulation de l'appli ne va PAS au trainer en FTMS (le rapport est appliqué par le trainer via le
        // canal Zwift) ; on la transmet en natif avec ses valeurs brutes et on répond « succès » à l'appli.
        if (_native is { IsActive: true } native && _upstream.IsConnected)
        {
            if (isSim && Enabled)
            {
                // Sortie d'ERG : une simulation FTMS neutre fait quitter le mode puissance cible (comme serpentine).
                if (wasNonSimTarget) _arbiter.SubmitProxySequence([FtmsCodec.WithGrade(value, 0)]);
                if (await native.SendSimulationAsync(appSim))
                {
                    LogNativeSimulation(appSim);
                    ForwardControlResponse([FtmsCodec.OpResponse, FtmsCodec.OpSetSimulation, 0x01]);
                    return DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch);
                }
                // Canal natif en échec : on retombe sur le chemin FTMS ci-dessous.
            }
            else if (!isSim && value.Length > 0 && FtmsCodec.IsTargetCommand(value[0])) native.SetSimulationActive(false); // ERG : plus de maintien
        }

        byte[] toTrainer = value;
        if (Enabled && isSim && !NativeActive) toTrainer = Adjust(value);

        if (!_upstream.IsConnected)
        {
            // Coupure en cours : on acquitte, on répond « succès » à l'appli, et l'état sera rejoué à la reconnexion.
            ForwardControlResponse([FtmsCodec.OpResponse, value[0], 0x01]);
            return DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch);
        }

        bool sent = await _arbiter.SubmitFromAppAsync(toTrainer);
        return sent
            ? DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch)
            : DirConPacket.Error(p.Id, p.Sequence, DirConResponse.WriteFailed);
    }

    private bool IsKnown(Guid ch) => _tree.Values.Any(c => c.Any(x => x.Uuid == ch));

    /// <summary>Caractéristique servie localement (cardio externe) et non relayée au trainer.</summary>
    private bool IsInjected(Guid ch) => _hrInjected && (ch == HeartRateCodec.Measurement || ch == HeartRateCodec.SensorLocation);

    private void LogNativeSimulation(SimulationParams sim)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastNativeLogTick) < 2000) return;
        Interlocked.Exchange(ref _lastNativeLogTick, now);
        Log?.Invoke($"Mode natif : simulation de l'appli transmise au trainer (pente {sim.GradePercent:0.0} %), rapport {CurrentRatio():0.00}");
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeNativeAsync(release: true); // rend au trainer son virtual shifting (rapport 0)
        _cts?.Cancel();
        _upstream.Notification -= OnUpstreamNotification;
        if (HeartRate is { } hr) hr.Measurement -= OnHeartRateMeasurement;
        _arbiter?.Dispose();
        _mdns.Dispose();
        _listener?.Stop();
        lock (_lock) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
        await Task.CompletedTask;
    }

    private sealed class Client(TcpClient tcp) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private volatile TaskCompletionSource? _ack;
        public NetworkStream Stream { get; } = tcp.GetStream();
        private readonly HashSet<Guid> _subscribed = [];

        // Lu par le thread des notifications du trainer, modifié par la boucle de lecture TCP : accès sous verrou.
        public bool IsSubscribed(Guid ch) { lock (_subscribed) return _subscribed.Contains(ch); }
        public void SetSubscribed(Guid ch, bool on) { lock (_subscribed) { if (on) _subscribed.Add(ch); else _subscribed.Remove(ch); } }

        /// <summary>Complété quand l'acquittement de l'écriture en cours a été envoyé à ce client.</summary>
        public Task AckGate => _ack?.Task ?? Task.CompletedTask;
        public void BeginAck() => _ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void EndAck() => _ack?.TrySetResult();

        public async Task SendAsync(byte[] data)
        {
            await _writeLock.WaitAsync();
            try { await TcpSend.WriteAsync(Stream, data, Dispose); } // client bloqué : coupé, la boucle de lecture nettoie
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            finally { _writeLock.Release(); }
        }

        public void Dispose() { try { tcp.Dispose(); } catch { } }
    }
}
